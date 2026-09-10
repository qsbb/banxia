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
        public string certificate_pin_sha256;
        public string certificatePinSha256;
        public string sha256;
    }

    public static class BackendPairingProtocol
    {
        public const string Version = "1.0";
        public const string PluginId = "astrbot_plugin_embodiment_bridge";
        public const string LegacyPluginId = "astrbot_plugin_quest_avatar_bridge";
        public const string PayloadType = "astrbot.quest.pair";
        public const string PluginApiPath = "/api/v1/plugins/extensions/" + PluginId;
        public const string ExchangePath = PluginApiPath + "/pairing/exchange";
        public const string LegacyPluginApiPath = "/api/v1/plugins/extensions/" + LegacyPluginId;
        public const string LegacyExchangePath = LegacyPluginApiPath + "/pairing/exchange";
        public const int MaxServerInputLength = 512;

        public static bool TryBuildExchangeEndpoint(
            string serverOrEndpoint,
            out string endpoint,
            out string reason,
            bool allowPrivateHttp = false,
            bool allowRemoteHttp = false)
        {
            endpoint = string.Empty;
            reason = string.Empty;
            var value = serverOrEndpoint ?? string.Empty;
            if (AstrBotProtocol.ContainsWhitespace(value))
            {
                reason = "Pairing server input must not contain whitespace";
                return false;
            }
            if (value.Length > MaxServerInputLength)
            {
                reason = "Pairing server input exceeds the length limit";
                return false;
            }
            if (string.IsNullOrEmpty(value))
            {
                reason = "Pairing server is required";
                return false;
            }
            if (!value.Contains("://"))
            {
                // Scheme-less input is always upgraded to HTTPS. Callers must
                // type http:// explicitly to opt into plaintext transport.
                value = "https://" + value;
            }
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                string.IsNullOrEmpty(uri.Host) ||
                !AstrBotProtocol.HasValidAuthorityPortSyntax(value, uri) ||
                !AstrBotProtocol.HasSafeUrlPathSyntax(value, uri) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                reason = "Pairing server must be an absolute URL without credentials, query, or fragment";
                return false;
            }

            var isHttps = uri.Scheme == Uri.UriSchemeHttps;
            // A bare host is intentionally HTTPS by default. Plain HTTP is only
            // selected when the caller explicitly supplies http:// and the
            // corresponding opt-in is enabled.
            var isPrivateHttp = uri.Scheme == Uri.UriSchemeHttp &&
                allowPrivateHttp && AstrBotProtocol.IsPrivateNetworkHost(uri.Host);
            var isRemoteHttp = uri.Scheme == Uri.UriSchemeHttp &&
                allowRemoteHttp && !AstrBotProtocol.IsPrivateNetworkHost(uri.Host);
            var isPlainHttp = isPrivateHttp || isRemoteHttp;
            if (!isHttps && !isPlainHttp)
            {
                reason = "Pairing requires HTTPS, or enabling plain-HTTP connections";
                return false;
            }

            var path = uri.AbsolutePath.TrimEnd('/');
            if (string.IsNullOrEmpty(path))
            {
                path = ExchangePath;
            }
            else if (string.Equals(path, PluginApiPath, StringComparison.Ordinal) ||
                     string.Equals(path, LegacyPluginApiPath, StringComparison.Ordinal))
            {
                path = ExchangePath;
            }
            else if (string.Equals(path, LegacyExchangePath, StringComparison.Ordinal))
            {
                path = ExchangePath;
            }
            else if (!string.Equals(path, ExchangePath, StringComparison.Ordinal))
            {
                reason = "Pairing server path is not an Embodiment Bridge endpoint";
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

        /// <summary>
        /// Normalize user input into a Bridge plugin base URL (…/api/v1/plugins/
        /// extensions/&lt;plugin&gt;) for the endpoint failover list. Transport gate
        /// mirrors AstrBotProtocol.TryValidateSettings: HTTPS everywhere, plain
        /// HTTP only for literal private-network IPs with the local opt-in.
        /// </summary>
        public static bool TryBuildBridgeBaseUrl(string serverOrBaseUrl, out string baseUrl, out string reason, bool allowPrivateHttp = false, bool allowRemoteHttp = false)
        {
            baseUrl = string.Empty;
            reason = string.Empty;
            var value = serverOrBaseUrl ?? string.Empty;
            if (AstrBotProtocol.ContainsWhitespace(value))
            {
                reason = "Endpoint input must not contain whitespace";
                return false;
            }
            if (value.Length > MaxServerInputLength)
            {
                reason = "Endpoint input exceeds the length limit";
                return false;
            }
            if (string.IsNullOrEmpty(value))
            {
                reason = "Endpoint is required";
                return false;
            }
            if (!value.Contains("://"))
            {
                // Keep endpoint-entry semantics identical to manual pairing:
                // bare authorities are HTTPS; plaintext requires http://.
                value = "https://" + value;
            }
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                string.IsNullOrEmpty(uri.Host) ||
                !AstrBotProtocol.HasValidAuthorityPortSyntax(value, uri) ||
                !AstrBotProtocol.HasSafeUrlPathSyntax(value, uri) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                reason = "Endpoint must be an absolute URL without credentials, query, or fragment";
                return false;
            }

            var isHttps = uri.Scheme == Uri.UriSchemeHttps;
            var isPrivateHttp = uri.Scheme == Uri.UriSchemeHttp &&
                allowPrivateHttp && AstrBotProtocol.IsPrivateNetworkHost(uri.Host);
            // 公网明文：仅在用户经明文开关显式 opt-in 时放行（密钥/音频明文传输，
            // 仅限自有服务器）；否则公网主机必须显式 https://。
            var isRemoteHttp = uri.Scheme == Uri.UriSchemeHttp &&
                allowRemoteHttp && !AstrBotProtocol.IsPrivateNetworkHost(uri.Host);
            if (!isHttps && !isPrivateHttp && !isRemoteHttp)
            {
                reason = "Public endpoints require an explicit https:// URL, or enabling the plaintext-HTTP switch";
                return false;
            }

            var path = uri.AbsolutePath.TrimEnd('/');
            if (string.IsNullOrEmpty(path) ||
                string.Equals(path, PluginApiPath, StringComparison.Ordinal) ||
                string.Equals(path, ExchangePath, StringComparison.Ordinal))
            {
                path = PluginApiPath;
            }
            else if (string.Equals(path, LegacyPluginApiPath, StringComparison.Ordinal) ||
                     string.Equals(path, LegacyExchangePath, StringComparison.Ordinal))
            {
                path = PluginApiPath;
            }
            else
            {
                reason = "Endpoint path is not an Embodiment Bridge endpoint";
                return false;
            }

            var builder = new UriBuilder(uri)
            {
                Path = path,
                Query = string.Empty,
                Fragment = string.Empty
            };
            baseUrl = builder.Uri.AbsoluteUri.TrimEnd('/');
            return true;
        }

        public static bool TryBuildHealthEndpoint(
            string serverOrBaseUrl,
            out string healthEndpoint,
            out string reason,
            bool allowPrivateHttp = false,
            bool allowRemoteHttp = false)
        {
            healthEndpoint = string.Empty;
            if (!TryBuildBridgeBaseUrl(
                    serverOrBaseUrl,
                    out var baseUrl,
                    out reason,
                    allowPrivateHttp,
                    allowRemoteHttp))
            {
                return false;
            }

            healthEndpoint = baseUrl + "/health";
            return true;
        }

        public static string GetServerEntry(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                string.IsNullOrEmpty(uri.Host))
            {
                return endpoint ?? string.Empty;
            }

            var host = uri.HostNameType == UriHostNameType.IPv6
                ? "[" + uri.Host + "]"
                : uri.Host;
            var authority = uri.IsDefaultPort ? host : host + ":" + uri.Port;
            // Always retain the explicitly selected scheme in the UI. This
            // makes a plaintext opt-in visible and prevents a bare host from
            // being mistaken for an HTTPS authority.
            return uri.Scheme + "://" + authority;
        }
        // Legacy v1 shape. Keep this overload's parameter list intact; the
        // additional remote-HTTP gate is available only to callers that opt in
        // explicitly through the extended overload below.
        public static bool TryParseQrPayload(
            string json,
            out string exchangeEndpoint,
            out string token,
            out string reason,
            bool allowPrivateHttp = false)
        {
            return TryParseQrPayload(
                json,
                out exchangeEndpoint,
                out token,
                out _,
                out reason,
                allowPrivateHttp,
                false);
        }

        // Extended legacy overload used by the pairing controller when the
        // operator has separately enabled public plaintext HTTP.
        public static bool TryParseQrPayload(
            string json,
            out string exchangeEndpoint,
            out string token,
            out string reason,
            bool allowPrivateHttp,
            bool allowRemoteHttp)
        {
            return TryParseQrPayload(
                json,
                out exchangeEndpoint,
                out token,
                out _,
                out reason,
                allowPrivateHttp,
                allowRemoteHttp);
        }

        /// <summary>
        /// Parses the additive v1 QR fingerprint field. Legacy payloads without
        /// a pin remain valid; a supplied pin is only meaningful for HTTPS and
        /// is normalized to lower-case bare hexadecimal.
        /// </summary>
        public static bool TryParseQrPayload(
            string json,
            out string exchangeEndpoint,
            out string token,
            out string certificatePinSha256,
            out string reason,
            bool allowPrivateHttp = false)
        {
            return TryParseQrPayload(
                json,
                out exchangeEndpoint,
                out token,
                out certificatePinSha256,
                out reason,
                allowPrivateHttp,
                false);
        }

        // Extended pin-aware overload with an explicit public HTTP gate.
        public static bool TryParseQrPayload(
            string json,
            out string exchangeEndpoint,
            out string token,
            out string certificatePinSha256,
            out string reason,
            bool allowPrivateHttp,
            bool allowRemoteHttp)
        {
            exchangeEndpoint = string.Empty;
            token = string.Empty;
            certificatePinSha256 = string.Empty;
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
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "pairing.parse-qr");
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
            if (!TryBuildExchangeEndpoint(
                    payload.exchange_url,
                    out exchangeEndpoint,
                    out reason,
                    allowPrivateHttp,
                    allowRemoteHttp))
            {
                return false;
            }

            var first = payload.certificate_pin_sha256 ?? string.Empty;
            var second = payload.certificatePinSha256 ?? string.Empty;
            var third = payload.sha256 ?? string.Empty;
            if (!string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(second) &&
                !string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
            {
                reason = "QR certificate pin aliases conflict";
                return false;
            }
            if (!string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(third) &&
                !string.Equals(first, third, StringComparison.OrdinalIgnoreCase))
            {
                reason = "QR certificate pin aliases conflict";
                return false;
            }
            if (!string.IsNullOrEmpty(second) && !string.IsNullOrEmpty(third) &&
                !string.Equals(second, third, StringComparison.OrdinalIgnoreCase))
            {
                reason = "QR certificate pin aliases conflict";
                return false;
            }
            var candidate = !string.IsNullOrEmpty(first) ? first :
                (!string.IsNullOrEmpty(second) ? second : third);
            if (!string.IsNullOrEmpty(candidate))
            {
                if (!CertificatePinningHandler.IsValidPin(candidate))
                {
                    reason = "certificate pin must be exactly 64 hexadecimal characters";
                    return false;
                }
                if (!IsHttpsEndpoint(exchangeEndpoint))
                {
                    reason = "certificate pin is only valid for HTTPS";
                    return false;
                }
                certificatePinSha256 = candidate.ToLowerInvariant();
            }
            token = payload.token;
            return true;
        }

        public static bool TryGetEffectiveAuthority(
            string value,
            out string scheme,
            out string host,
            out int port)
        {
            scheme = string.Empty;
            host = string.Empty;
            port = 0;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                string.IsNullOrEmpty(uri.Host) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                return false;
            }
            scheme = uri.Scheme.ToLowerInvariant();
            host = uri.Host.ToLowerInvariant();
            port = uri.IsDefaultPort || uri.Port <= 0
                ? (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80)
                : uri.Port;
            return port > 0 && port <= 65535;
        }

        public static bool HasSameAuthority(string firstUrl, string secondUrl)
        {
            return TryGetEffectiveAuthority(firstUrl, out var firstScheme, out var firstHost, out var firstPort) &&
                TryGetEffectiveAuthority(secondUrl, out var secondScheme, out var secondHost, out var secondPort) &&
                string.Equals(firstScheme, secondScheme, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(firstHost, secondHost, StringComparison.OrdinalIgnoreCase) &&
                firstPort == secondPort;
        }

        public static bool IsPinnedPairingTargetAllowed(
            string exchangeEndpoint,
            string targetBaseUrl,
            string certificatePinSha256)
        {
            if (string.IsNullOrEmpty(certificatePinSha256))
            {
                return true;
            }
            return CertificatePinningHandler.IsValidPin(certificatePinSha256) &&
                IsHttpsEndpoint(exchangeEndpoint) &&
                IsHttpsEndpoint(targetBaseUrl) &&
                HasSameAuthority(exchangeEndpoint, targetBaseUrl);
        }

        private static bool IsHttpsEndpoint(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps;
        }

        private static bool IsSafeExchangePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 256 || path[0] != '/')
            {
                return false;
            }
            for (var index = 0; index < path.Length; index++)
            {
                var value = path[index];
                if (value < 0x20 || value == '\\' || value == '?'
                    || value == '#' || value == ':' || value == '%')
                {
                    // Percent-encoded separators/dot segments are ambiguous
                    // across proxy stacks; reject all encoded path bytes here.
                    return false;
                }
            }
            return path.IndexOf("//", StringComparison.Ordinal) < 0 &&
                path.IndexOf("..", StringComparison.Ordinal) < 0;
        }

        public static bool TryUpgradeLegacyPluginBaseUrl(string value, out string upgraded)
        {
            upgraded = value ?? string.Empty;
            if (!TryGetExactPluginUri(value, LegacyPluginApiPath, out var uri))
            {
                return false;
            }

            var builder = new UriBuilder(uri)
            {
                Path = PluginApiPath,
                Query = string.Empty,
                Fragment = string.Empty
            };
            upgraded = builder.Uri.AbsoluteUri.TrimEnd('/');
            return true;
        }

        public static bool TryMigrateLegacyConfiguration(
            string legacyPath,
            string currentPath,
            out bool migrated,
            out string reason)
        {
            migrated = false;
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(legacyPath) || string.IsNullOrWhiteSpace(currentPath))
            {
                reason = "Configuration migration paths are missing";
                return false;
            }

            try
            {
                var legacyFullPath = Path.GetFullPath(legacyPath);
                var currentFullPath = Path.GetFullPath(currentPath);
                if (File.Exists(currentFullPath) || !File.Exists(legacyFullPath))
                {
                    return true;
                }
                var settings = JsonUtility.FromJson<AstrBotBridgeSettings>(
                    File.ReadAllText(legacyFullPath, Encoding.UTF8));
                if (settings == null)
                {
                    reason = "Legacy configuration is empty";
                    return false;
                }
                if (TryUpgradeLegacyPluginBaseUrl(settings.base_url, out var upgradedBaseUrl))
                {
                    settings.base_url = upgradedBaseUrl;
                }
                else if (!TryGetExactPluginUri(settings.base_url, PluginApiPath, out _))
                {
                    reason = "Legacy configuration endpoint is not a recognized bridge path";
                    return false;
                }
                if (!TryWriteSettingsAtomically(
                    currentFullPath,
                    settings,
                    out reason,
                    settings.allow_insecure_http,
                    settings.allow_insecure_remote_http))
                {
                    return false;
                }
                migrated = true;
                return true;
            }
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "pairing.migrate-configuration");
                QuestDebugMode.RethrowIfEnabled(exception, "pairing.migrate-configuration");
                reason = "Legacy configuration could not be migrated: " + exception.GetType().Name;
                return false;
            }
        }

        private static bool TryGetExactPluginUri(string value, string pluginPath, out Uri uri)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                   !string.IsNullOrEmpty(uri.Host) &&
                   !string.IsNullOrEmpty(uri.Scheme) &&
                   string.IsNullOrEmpty(uri.UserInfo) &&
                   string.IsNullOrEmpty(uri.Query) &&
                   string.IsNullOrEmpty(uri.Fragment) &&
                   string.Equals(uri.AbsolutePath.TrimEnd('/'), pluginPath, StringComparison.Ordinal);
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
            bool allowPrivateHttp = false,
            bool allowRemoteHttp = false)
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
            var remoteHttpAllowed = uri.Scheme == Uri.UriSchemeHttp &&
                                    allowRemoteHttp &&
                                    settings.allow_insecure_remote_http &&
                                    !AstrBotProtocol.IsPrivateNetworkHost(uri.Host);
            if (uri.Scheme != Uri.UriSchemeHttps && !privateHttpAllowed && !remoteHttpAllowed)
            {
                reason = "Paired configuration must use HTTPS unless the matching HTTP opt-in was explicitly enabled";
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
                catch (Exception cleanupException)
                {
                    // Preserve the original failure and leave cleanup for the next attempt.
                    QuestDebugMode.Report(cleanupException, "pairing.configuration-cleanup");
                }
                QuestDebugMode.Report(exception, "pairing.save-configuration");
                QuestDebugMode.RethrowIfEnabled(exception, "pairing.save-configuration");
                return false;
            }
        }
    }
}
