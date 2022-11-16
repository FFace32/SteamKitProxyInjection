using System;
using System.Collections.Generic;
using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.NLog;
using ArchiSteamFarm.Plugins.Interfaces;
using HarmonyLib;
using JetBrains.Annotations;
using Newtonsoft.Json.Linq;
using SteamKit2;

namespace SteamKitProxyInjection {
	[Export(typeof(IPlugin))]
	[UsedImplicitly]
	public class SteamKitProxyInjectionPlugin : IASF {
		public Task OnLoaded() {
			ASF.ArchiLogger.LogGenericInfo($"{Name} by ezhevita | Support & source code: https://github.com/ezhevita/{Name}");

			return Task.CompletedTask;
		}

		public string Name => nameof(SteamKitProxyInjection);
		public Version Version => Assembly.GetExecutingAssembly().GetName().Version ?? throw new InvalidOperationException();

		// static so it can be accessed from the patches
		private static Dictionary<string, WebProxy> BotProxies = new();

		public Task OnASFInit(IReadOnlyDictionary<string, JToken>? additionalConfigProperties = null) {
			if (ASF.GlobalConfig?.SteamProtocols != ProtocolTypes.WebSocket) {
				ASF.ArchiLogger.LogGenericInfo($"{Name} is currently disabled according to your configuration. If you'd like to enable it, set SteamProtocols to WebSocket.");

				return Task.CompletedTask;
			}

			ASF.ArchiLogger.LogGenericTrace("Loading proxies...");
			LoadBotProxies();

			ASF.ArchiLogger.LogGenericInfo("Injecting...");
			Harmony harmony = new("dev.ezhevita.SteamKitProxyInjection");

			ASF.ArchiLogger.LogGenericTrace("Retrieving WebSocketConnection and WebSocketContext types...");
			Type webSocketConnectionType = AccessTools.TypeByName("SteamKit2.WebSocketConnection");
			Type webSocketContextType = AccessTools.TypeByName("SteamKit2.WebSocketConnection+WebSocketContext");

			ASF.ArchiLogger.LogGenericTrace("Retrieving WebSocketContext constructor...");
			ConstructorInfo constructor = AccessTools.Constructor(webSocketContextType, new[] { webSocketConnectionType, typeof(EndPoint) });
			ASF.ArchiLogger.LogGenericTrace("Patching...");
			harmony.Patch(constructor, postfix: new HarmonyMethod(AccessTools.Method(typeof(SteamKitProxyInjectionPlugin), nameof(WebSocketContextConstructor))));

			ASF.ArchiLogger.LogGenericTrace("Retrieving WebBrowser constructor...");
			ConstructorInfo constructor2 = AccessTools.Constructor(typeof(ArchiSteamFarm.Web.WebBrowser), new[] { typeof(ArchiLogger), typeof(IWebProxy), typeof(bool) });
			ASF.ArchiLogger.LogGenericTrace("Patching...");
			harmony.Patch(constructor2, prefix: new HarmonyMethod(AccessTools.Method(typeof(SteamKitProxyInjectionPlugin), nameof(WebBrowserConstructor))));

			ASF.ArchiLogger.LogGenericInfo("Successfully injected!");

			return Task.CompletedTask;
		}

		[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
		[SuppressMessage("ReSharper", "InconsistentNaming")]
#pragma warning disable CA1707
		public static void WebSocketContextConstructor(object ___connection, ClientWebSocket ___socket) {
			if (___connection == null) {
				throw new ArgumentNullException(nameof(___connection));
			}

			if (___socket == null) {
				throw new ArgumentNullException(nameof(___socket));
			}

			SteamClient? client = GetSteamClientFromConnection(___connection);
			if (client == null) {
				ASF.ArchiLogger.LogNullError(client);

				return;
			}

			ArchiLogger archiLogger = new(client.ID);
			archiLogger.LogGenericTrace("Retrieving WebProxy config value...");

			WebProxy? webProxy = BotProxies.GetValueOrDefault(client.ID);
			if (webProxy == null) {
				webProxy = ASF.GlobalConfig?.WebProxy;

				if (webProxy == null) {
					return;
				}
			}

			archiLogger.LogGenericTrace($"Setting proxy to {FormatWebProxy(webProxy)} ...");
			___socket.Options.Proxy = webProxy;
		}

		public static void WebBrowserConstructor(ArchiLogger archiLogger, ref IWebProxy webProxy, bool extendedTimeout) {
			if (archiLogger == null) {
				throw new ArgumentNullException(nameof(archiLogger));
			}

			string? loggerName = GetArchiLoggerName(archiLogger);
			if (loggerName == null) {
				archiLogger.LogNullError(loggerName);

				return;
			}

			archiLogger.LogGenericTrace("Retrieving WebProxy config value...");

			WebProxy? newWebProxy = BotProxies.GetValueOrDefault(loggerName);
			if (newWebProxy == null) {
				return;
			}

			archiLogger.LogGenericTrace($"Setting proxy to {FormatWebProxy(newWebProxy)} ...");
			webProxy = newWebProxy;
		}
#pragma warning restore CA1707

		// TO DO: Call this on config reload/new config
		static private void LoadBotProxies() {
			try {
				foreach (string botConfigPath in Directory.EnumerateFiles("config", "*.json")) {
					string? webProxy = null;
					string? webProxyUsername = null;
					string? webProxyPassword = null;

					JObject config = JObject.Parse(File.ReadAllText(botConfigPath));
					if (config.ContainsKey("WebProxy")) {
						webProxy = config["WebProxy"]?.Value<string>();
					}

					if (string.IsNullOrEmpty(webProxy))
						continue;

					if (config.ContainsKey("WebProxyUsername")) {
						webProxyUsername = config["WebProxyUsername"]?.Value<string>();
					}

					if (config.ContainsKey("WebProxyPassword")) {
						webProxyPassword = config["WebProxyPassword"]?.Value<string>();
					}

					WebProxy proxy = new() {
						Address = new Uri(webProxy),
						BypassProxyOnLocal = true
					};

					if (!string.IsNullOrEmpty(webProxyUsername) || !string.IsNullOrEmpty(webProxyPassword)) {
						NetworkCredential credentials = new();

						if (!string.IsNullOrEmpty(webProxyUsername)) {
							credentials.UserName = webProxyUsername;
						}

						if (!string.IsNullOrEmpty(webProxyPassword)) {
							credentials.Password = webProxyPassword;
						}

						proxy.Credentials = credentials;
					}

					BotProxies[Path.GetFileNameWithoutExtension(botConfigPath)] = proxy;
				}
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);

				return;
			}
		}

		private static string FormatWebProxy(WebProxy webProxy) {
			string formatted = webProxy.Address!.Scheme;
			formatted += "://";

			if (webProxy.Credentials != null) {
				NetworkCredential credentials = (NetworkCredential) webProxy.Credentials;

				if (!string.IsNullOrEmpty(credentials.UserName)) {
					formatted += credentials.UserName;
					formatted += '@';
				}

				// Don't include the password
			}

			formatted += webProxy.Address.Host;
			formatted += ':';
			formatted += webProxy.Address.Port;
			return formatted;
		}

		private static SteamClient? GetSteamClientFromConnection(object connection) {
			FieldInfo logField = AccessTools.Field(connection.GetType(), "log");
			return (SteamClient?) logField.GetValue(connection);
		}

		private static string? GetArchiLoggerName(ArchiLogger archiLogger) {
			FieldInfo loggerField = AccessTools.Field(archiLogger.GetType(), "Logger");
			object? logger = loggerField.GetValue(archiLogger);
			if (logger == null) {
				return null;
			}

			PropertyInfo nameProperty = AccessTools.Property(logger.GetType(), "Name");
			return (string?) nameProperty.GetValue(logger);
		}
	}
}
