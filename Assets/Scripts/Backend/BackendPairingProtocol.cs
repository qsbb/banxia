using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace QuestMmdPlayer
{
    [Serializable]
    internal sealed class PairingExchangeRequest
    {
        public string protocol_version = BackendPairingProtocol.Version;
        public string token = string.Empty;
        public string code = string.Empty;
    }

    [Serializable]
    internal sealed class PairingExchangeEnvelope
    {
        public string status;
        public string message;
        public PairingExchangeData data;
    }

    [Serializable]
    internal sealed class PairingExchangeData
    {
        public string pairing_protocol_version;
        public string pairing_id;
        public AstrBotBridgeSettings configuration;
    }

    [Serializable]
    internal sealed class PairingQrPayload
    {
        public string type;
        public string version;
        public string exchange_url;
        public string token;
    }

    public static class BackendPairingProtocol
    {
        public const string Version = "1.0";
        public const string PayloadType = "astrbot.quest.pair";
        public const string PluginApiPath = "/api/v1/plugins/extensions/astrbot_plugin_quest_avatar_bridge";
        public const string ExchangePath = PluginApiPath + "/pairing/exchange";

        public static bool TryBuildExchangeEndpoint(string serverOrEndpoint, out string endpoint, out string reason, bool allowPrivateHttp = false)
        {
            endpoint = string.Empty;
            reason = string.Empty;
            var value = (serverOrEndpoint ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(value))
            {
                reason = "Pairing server is required";
                return false;
            }
            if (!value.Contains("://"))
            {
                var probeValue = "http://" + value;
                var isLiteralPrivateIp = Uri.TryCreate(probeValue, UriKind.Absolute, out var privateProbe) &&
                    AstrBotProtocol.IsPrivateNetworkHost(privateProbe.Host);
                if (isLiteralPrivateIp && !allowPrivateHttp)
                {
                    reason = "Enable private-LAN HTTP before using a private IP address";
                    return false;
                }
                value = (isLiteralPrivateIp ? "http://" : "https://") + value;
            }
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                string.IsNullOrEmpty(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                reason = "Pairing server must be an absolute URL without credentials, query, or fragment";
                return false;
            }

            var isHttps = uri.Scheme == Uri.UriSchemeHttps;
            var isPrivateHttp = uri.Scheme == Uri.UriSchemeHttp &&
                                allowPrivateHttp &&
                                AstrBotProtocol.IsPrivateNetworkHost(uri.Host);
            if (!isHttps && !isPrivateHttp)
            {
                reason = "Pairing requires HTTPS, or explicit private-LAN HTTP with a literal private IP";
                return false;
            }

            var path = uri.AbsolutePath.TrimEnd('/');
            if (string.IsNullOrEmpty(path))
            {
                path = ExchangePath;
            }
            else if (string.Equals(path, PluginApiPath, StringComparison.Ordinal))
            {
                path = ExchangePath;
            }
            else if (!string.Equals(path, ExchangePath, StringComparison.Ordinal))
            {
                reason = "Pairing server path is not a Quest Avatar Bridge endpoint";
                return false;
            }

            var builder = new UriBuilder(uri)
            {
                Path = path,
                Query = string.Empty,
                Fragment = string.Empty
            };
            endpoint = builder.Uri.AbsoluteUri.TrimEnd('/');
            return true;
        }

        public static bool TryParseQrPayload(
            string json,
            out string exchangeEndpoint,
            out string token,
            out string reason,
            bool allowPrivateHttp = false)
        {
            exchangeEndpoint = string.Empty;
            token = string.Empty;
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(json))
            {
                reason = "QR payload is empty";
                return false;
            }

            PairingQrPayload payload;
            try
            {
                payload = JsonUtility.FromJson<PairingQrPayload>(json);
            }
            catch (Exception)
            {
                reason = "QR payload is not valid JSON";
                return false;
            }
            if (payload == null || payload.type != PayloadType || payload.version != Version)
            {
                reason = "QR payload type or version is unsupported";
                return false;
            }
            if (string.IsNullOrEmpty(payload.token) || payload.token.Length < 32 || payload.token.Length > 128)
            {
                reason = "QR pairing token is invalid";
                return false;
            }
            if (!TryBuildExchangeEndpoint(payload.exchange_url, out exchangeEndpoint, out reason, allowPrivateHttp))
            {
                return false;
            }
            token = payload.token;
            return true;
        }

        public static string NormalizeShortCode(string value)
        {
            var source = value ?? string.Empty;
            var builder = new StringBuilder(6);
            for (var index = 0; index < source.Length && builder.Length < 6; index++)
            {
                if (source[index] >= '0' && source[index] <= '9')
                {
                    builder.Append(source[index]);
                }
            }
            return builder.ToString();
        }

        public static bool TryWriteSettingsAtomically(
            string path,
            AstrBotBridgeSettings settings,
            out string reason,
            bool allowPrivateHttp = false)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                reason = "Configuration path is missing";
                return false;
            }
            if (!AstrBotProtocol.TryValidateSettings(settings, out reason))
            {
                return false;
            }
            if (!Uri.TryCreate(settings.base_url, UriKind.Absolute, out var uri))
            {
                reason = "Paired configuration URL is invalid";
                return false;
            }
            var privateHttpAllowed = uri.Scheme == Uri.UriSchemeHttp &&
                                     allowPrivateHttp &&
                                     settings.allow_insecure_http &&
                                     AstrBotProtocol.IsPrivateNetworkHost(uri.Host);
            if (uri.Scheme != Uri.UriSchemeHttps && !privateHttpAllowed)
            {
                reason = "Paired configuration must use HTTPS unless private-LAN HTTP was explicitly enabled";
                return false;
            }

            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory))
            {
                reason = "Configuration directory is invalid";
                return false;
            }
            var temporaryPath = fullPath + ".pairing.tmp";
            var backupPath = fullPath + ".pairing.bak";

            try
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(settings, true));
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }

                if (File.Exists(fullPath))
                {
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                    File.Replace(temporaryPath, fullPath, backupPath, true);
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                }
                else
                {
                    File.Move(temporaryPath, fullPath);
                }
                return true;
            }
            catch (Exception exception)
            {
                reason = "Configuration could not be saved atomically: " + exception.GetType().Name;
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (Exception)
                {
                    // Preserve the original failure and leave cleanup for the next attempt.
                }
                return false;
            }
        }
    }
}
