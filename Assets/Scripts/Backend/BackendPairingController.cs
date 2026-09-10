using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace QuestMmdPlayer
{
    public readonly struct PairingScanResult
    {
        public PairingScanResult(bool succeeded, string payload, string error)
        {
            Succeeded = succeeded;
            Payload = payload ?? string.Empty;
            Error = error ?? string.Empty;
        }

        public bool Succeeded { get; }
        public string Payload { get; }
        public string Error { get; }
    }

    public interface IPairingCodeScanner
    {
        bool IsAvailable { get; }
        string AvailabilityReason { get; }
        void BeginScan(Action<PairingScanResult> completed);
    }

    public sealed class UnsupportedPairingCodeScanner : IPairingCodeScanner
    {
        public bool IsAvailable => false;
        public string AvailabilityReason => "Camera scan requires the Unity 6 and MRUK 81+ camera stack; use host, port, and the 6-digit code.";

        public void BeginScan(Action<PairingScanResult> completed)
        {
            completed?.Invoke(new PairingScanResult(false, string.Empty, AvailabilityReason));
        }
    }

    [DisallowMultipleComponent]
    public sealed class BackendPairingController : MonoBehaviour
    {
        private const string PairingServerPreference = "embodiment_bridge_pairing_server_v1";
        private const string LegacyPairingServerPreference = "quest_avatar_pairing_server_v1";
        private const string CertificatePinPreference = "embodiment_bridge_certificate_pin_sha256_v1";
        private const string PrivateHttpPreference = "embodiment_bridge_private_http_v1";
        private const string RemoteHttpPreference = "embodiment_bridge_remote_http_v1";

        [SerializeField] private int requestTimeoutSeconds = 15;

        private AstrBotBridge bridge;
        private IPairingCodeScanner scanner = new UnsupportedPairingCodeScanner();
        private UnityWebRequest activeRequest;
        private Coroutine pairingRoutine;
        // The pin is intentionally owned by pairing, not the Flutter shell. It
        // can be entered before a pairing exchange and is persisted separately
        // so the first HTTPS request can be pinned before AstrBotBridge reloads.
        private string certificatePinSha256 = string.Empty;

        [Serializable]
        private sealed class PairingQrCertificatePinPayload
        {
            public string certificate_pin_sha256 = string.Empty;
            public string certificatePinSha256 = string.Empty;
            public string sha256 = string.Empty;
        }

        // Plain HTTP is opt-in per scope. The private-LAN switch must not
        // implicitly authorize public hosts.
        private bool allowPrivateHttp = false;
        private bool allowRemoteHttp = false;

        public event Action StatusChanged;

        public bool IsBusy => pairingRoutine != null;
        public bool ScannerAvailable => scanner != null && scanner.IsAvailable;
        public bool PrivateHttpAllowed => allowPrivateHttp;
        public bool RemoteHttpAllowed => allowRemoteHttp;
        public string PairingServerEndpoint { get; private set; } = string.Empty;
        public string Status { get; private set; } = "Enter pairing server and 6-digit code";

        /// <summary>
        /// The pin entered on the pairing surface. It is deliberately kept
        /// separate from AstrBotBridge's loaded configuration so a pin can be
        /// attached to the very first HTTPS exchange.
        /// </summary>
        public string CertificatePinSha256 => certificatePinSha256;

        /// <summary>
        /// Whether a pending fingerprint is configured, or a validated pin is
        /// present in the still-bound AstrBot configuration. Once the binding is
        /// removed, the loaded-config fallback disappears after reload.
        /// </summary>
        public bool CertificatePinConfigured => !string.IsNullOrEmpty(certificatePinSha256) ||
            (bridge != null && bridge.IsConfigured &&
             !string.IsNullOrEmpty(bridge.CertificatePinSha256));

        /// <summary>
        /// A deliberately short, non-secret display value. Never publish the
        /// complete fingerprint through the Flutter status event.
        /// </summary>
        public string CertificatePinSummary => SummarizeCertificatePin(
            !string.IsNullOrEmpty(certificatePinSha256)
                ? certificatePinSha256
                : (bridge != null && bridge.IsConfigured ? bridge.CertificatePinSha256 : string.Empty));

        private void Awake()
        {
            bridge = GetComponent<AstrBotBridge>();
            RestoreCertificatePin();
            RestoreHttpPreferences();
            RestorePairingServer();
        }

        public void Initialize(AstrBotBridge astrBotBridge)
        {
            bridge = astrBotBridge;
            RestoreCertificatePin();
            RestoreHttpPreferences();
            RestorePairingServer();
        }

        /// <summary>
        /// Validates and stores the optional HTTPS leaf-certificate SHA-256
        /// fingerprint used by the next pairing exchange. The wire form is
        /// exactly 64 hexadecimal characters; prefixes and colon-separated
        /// fingerprints are intentionally rejected so the same value is used
        /// by Unity and the Bridge configuration.
        /// </summary>
        public bool TrySetCertificatePinSha256(string value, out string reason)
        {
            if (!TryNormalizeCertificatePin(value, out var normalized, out reason))
            {
                SetStatus(reason);
                return false;
            }
            // An entered pin must never silently turn a plain-HTTP pairing into
            // a falsely "pinned" connection. An empty value is the explicit
            // clear operation and remains valid for either transport.
            if (!string.IsNullOrEmpty(normalized) && IsPlainHttpEndpoint(PairingServerEndpoint))
            {
                reason = "certificate pin is only valid for HTTPS";
                SetStatus(reason);
                return false;
            }
            StoreCertificatePin(normalized);
            SetStatus(string.IsNullOrEmpty(normalized)
                ? "HTTPS certificate pin cleared"
                : "HTTPS certificate pin ready");
            return true;
        }

        /// <summary>Convenience wrapper for native/legacy callers.</summary>
        public bool SetCertificatePinSha256(string value)
        {
            return TrySetCertificatePinSha256(value, out _);
        }

        private void StoreCertificatePin(string normalized)
        {
            certificatePinSha256 = normalized ?? string.Empty;
            if (string.IsNullOrEmpty(certificatePinSha256))
            {
                PlayerPrefs.DeleteKey(CertificatePinPreference);
            }
            else
            {
                PlayerPrefs.SetString(CertificatePinPreference, certificatePinSha256);
            }
            PlayerPrefs.Save();
        }

        public void SetPrivateHttpAllowed(bool allowed)
        {
            if (allowPrivateHttp == allowed)
            {
                return;
            }

            allowPrivateHttp = allowed;
            if (allowed)
            {
                PlayerPrefs.SetInt(PrivateHttpPreference, 1);
            }
            else
            {
                PlayerPrefs.DeleteKey(PrivateHttpPreference);
            }
            PlayerPrefs.Save();
            if (!allowed && Uri.TryCreate(PairingServerEndpoint, UriKind.Absolute, out var current) &&
                current.Scheme == Uri.UriSchemeHttp &&
                AstrBotProtocol.IsPrivateNetworkHost(current.Host))
            {
                PairingServerEndpoint = string.Empty;
                // A pin cannot apply to the endpoint that was just discarded.
                StoreCertificatePin(string.Empty);
            }
            RestorePairingServer();
            SetStatus(allowed
                ? "Private-LAN HTTP pairing enabled"
                : "Private-LAN HTTP disabled");
        }

        public void SetRemoteHttpAllowed(bool allowed)
        {
            if (allowRemoteHttp == allowed)
            {
                return;
            }
            allowRemoteHttp = allowed;
            if (allowed)
            {
                PlayerPrefs.SetInt(RemoteHttpPreference, 1);
            }
            else
            {
                PlayerPrefs.DeleteKey(RemoteHttpPreference);
                if (Uri.TryCreate(PairingServerEndpoint, UriKind.Absolute, out var current) &&
                    current.Scheme == Uri.UriSchemeHttp &&
                    !AstrBotProtocol.IsPrivateNetworkHost(current.Host))
                {
                    PairingServerEndpoint = string.Empty;
                    StoreCertificatePin(string.Empty);
                }
            }
            PlayerPrefs.Save();
            RestorePairingServer();
            SetStatus(allowed ? "Public HTTP explicitly enabled" : "Public HTTP disabled");
        }

        public bool TrySetPairingServer(string value, out string reason)
        {
            if (!BackendPairingProtocol.TryBuildExchangeEndpoint(
                    value,
                    out var endpoint,
                    out reason,
                    allowPrivateHttp,
                    allowRemoteHttp))
            {
                SetStatus(reason);
                return false;
            }
            if (!string.IsNullOrEmpty(certificatePinSha256) && IsPlainHttpEndpoint(endpoint))
            {
                reason = "certificate pin is only valid for HTTPS";
                SetStatus(reason);
                return false;
            }
            if (!string.IsNullOrEmpty(certificatePinSha256) &&
                !string.IsNullOrEmpty(PairingServerEndpoint) &&
                !AstrBotProtocol.HasSameEndpointAuthority(PairingServerEndpoint, endpoint))
            {
                // A pin is bound to one HTTPS authority. Changing the server
                // must not leave a stale pin armed for the next host.
                StoreCertificatePin(string.Empty);
            }
            PairingServerEndpoint = endpoint;
            PlayerPrefs.SetString(PairingServerPreference, endpoint);
            PlayerPrefs.Save();
            SetStatus("Pairing server ready");
            return true;
        }

        /// <summary>
        /// Clears the pairing server from memory and both current/legacy PlayerPrefs
        /// keys so a successful unbind cannot resurrect the old endpoint on restart.
        /// </summary>
        public void ClearPairingServer()
        {
            CancelPairingRequest();
            PairingServerEndpoint = string.Empty;
            PlayerPrefs.DeleteKey(PairingServerPreference);
            PlayerPrefs.DeleteKey(LegacyPairingServerPreference);
            StoreCertificatePin(string.Empty);
            SetStatus("Pairing server cleared");
        }

        public void PairWithCode(string code)
        {
            var normalized = BackendPairingProtocol.NormalizeShortCode(code);
            if (normalized.Length != 6)
            {
                SetStatus("Enter all 6 pairing digits");
                return;
            }
            if (string.IsNullOrEmpty(PairingServerEndpoint))
            {
                SetStatus("Set the pairing server first");
                return;
            }
            BeginExchange(PairingServerEndpoint, string.Empty, normalized);
        }

        public void PairWithQrPayload(string payload)
        {
            if (!BackendPairingProtocol.TryParseQrPayload(
                payload,
                out var endpoint,
                out var token,
                out var qrPin,
                out var reason,
                allowPrivateHttp,
                allowRemoteHttp))
            {
                SetStatus(reason);
                return;
            }
            if (!string.IsNullOrEmpty(qrPin) && IsPlainHttpEndpoint(endpoint))
            {
                SetStatus("certificate pin is only valid for HTTPS");
                return;
            }
            if (!string.IsNullOrEmpty(qrPin))
            {
                // Set the endpoint first so the HTTPS-only guard in the setter
                // evaluates the QR exchange URL rather than a stale server.
                PairingServerEndpoint = endpoint;
                if (!SetCertificatePinSha256(qrPin))
                {
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(certificatePinSha256) &&
                !string.IsNullOrEmpty(PairingServerEndpoint) &&
                !AstrBotProtocol.HasSameEndpointAuthority(PairingServerEndpoint, endpoint))
            {
                // A QR payload without a pin for a different authority must
                // not inherit the previous host's pending fingerprint.
                StoreCertificatePin(string.Empty);
            }
            PairingServerEndpoint = endpoint;
            PlayerPrefs.SetString(PairingServerPreference, endpoint);
            PlayerPrefs.Save();
            BeginExchange(endpoint, token, string.Empty);
        }

        public void BeginQrScan()
        {
            if (IsBusy)
            {
                SetStatus("Pairing request is already running");
                return;
            }
            if (scanner == null)
            {
                SetStatus("QR scanner is unavailable");
                return;
            }
            scanner.BeginScan(result =>
            {
                if (result.Succeeded) PairWithQrPayload(result.Payload);
                else SetStatus(string.IsNullOrEmpty(result.Error) ? scanner.AvailabilityReason : result.Error);
            });
        }

        public void SetScanner(IPairingCodeScanner value)
        {
            scanner = value ?? new UnsupportedPairingCodeScanner();
        }

        private void BeginExchange(string endpoint, string token, string code)
        {
            if (IsBusy)
            {
                SetStatus("Pairing request is already running");
                return;
            }
            pairingRoutine = StartCoroutine(Exchange(endpoint, token, code));
        }

        private IEnumerator Exchange(string endpoint, string token, string code)
        {
            // Snapshot the user-entered pin for this exchange. Do not fall back
            // to AstrBotBridge's loaded pin here: pairing must use the explicit
            // pending value so the first HTTPS request is pinned deterministically.
            var pendingPin = certificatePinSha256 ?? string.Empty;
            if (!string.IsNullOrEmpty(pendingPin) && !IsHttpsEndpoint(endpoint))
            {
                SetStatus("certificate pin is only valid for HTTPS");
                pairingRoutine = null;
                yield break;
            }
            SetStatus("Exchanging one-time pairing credential...");
            var payload = new PairingExchangeRequest { token = token, code = code };
            var body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));
            var request = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = Mathf.Clamp(requestTimeoutSeconds, 3, 60)
            };
            if (IsHttpsEndpoint(endpoint) && !string.IsNullOrEmpty(pendingPin))
                request.certificateHandler = new CertificatePinningHandler(pendingPin);
            // Pairing carries a one-time bearer credential. Refuse all redirects
            // so it cannot be replayed at another authority or downgraded to HTTP.
            request.redirectLimit = 0;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "application/json");
            request.SetRequestHeader("Cache-Control", "no-store");
            activeRequest = request;

            yield return request.SendWebRequest();
            if (!ReferenceEquals(activeRequest, request))
            {
                request.Dispose();
                pairingRoutine = null;
                yield break;
            }
            activeRequest = null;

            if (request.result != UnityWebRequest.Result.Success ||
                request.responseCode < 200 || request.responseCode >= 300)
            {
                var pinHandler = request.certificateHandler as CertificatePinningHandler;
                if ((pinHandler != null && pinHandler.IsTerminalFailure) ||
                    AstrBotProtocol.IsTerminalTransportFailure(request.error))
                {
                    // A pin mismatch or terminal TLS trust failure must not
                    // remain armed for an unrelated retry/authority.
                    StoreCertificatePin(string.Empty);
                }
                var detail = string.IsNullOrWhiteSpace(request.error) ? string.Empty : ": " + request.error;
                // TLS 握手失败（HTTP 0 + SSL 文案）几乎总是"对纯 HTTP 服务器
                // 用了 https:// 前缀"。给出可操作的下一步，而不是只抛引擎原文。
                if (request.responseCode == 0 && !string.IsNullOrEmpty(request.error) &&
                    request.error.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    detail += " (server may be plain HTTP - only retry with an explicit http:// URL after enabling the matching HTTP option)";
                }
                SetStatus("Pairing exchange failed (HTTP " + request.responseCode + ")" + detail);
                request.Dispose();
                pairingRoutine = null;
                yield break;
            }

            PairingExchangeEnvelope response;
            try
            {
                response = JsonUtility.FromJson<PairingExchangeEnvelope>(request.downloadHandler.text);
            }
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "pairing.response-parse");
                if (QuestDebugMode.Enabled)
                {
                    pairingRoutine = null;
                }
                QuestDebugMode.RethrowIfEnabled(exception, "pairing.response-parse");
                response = null;
            }
            finally
            {
                request.Dispose();
            }

            var settings = response == null || response.data == null ? null : response.data.configuration;
            if (response == null || response.status != "ok" || response.data == null ||
                response.data.pairing_protocol_version != BackendPairingProtocol.Version || settings == null)
            {
                SetStatus("Pairing response is invalid or incompatible");
                pairingRoutine = null;
                yield break;
            }

            if (!TryValidateExchangeCertificatePin(
                endpoint,
                pendingPin,
                settings,
                out var responsePin,
                out var pinReason))
            {
                SetStatus(pinReason);
                pairingRoutine = null;
                yield break;
            }
            // Keep the persisted configuration canonical even when the server
            // returned lower-case hexadecimal. Never save a malformed or
            // HTTP-associated pin.
            settings.certificate_pin_sha256 = responsePin;

            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var pairingUri) &&
                pairingUri.Scheme == Uri.UriSchemeHttp)
            {
                if (AstrBotProtocol.IsPrivateNetworkHost(pairingUri.Host) && allowPrivateHttp)
                {
                    settings.allow_insecure_http = true;
                }
                else if (!AstrBotProtocol.IsPrivateNetworkHost(pairingUri.Host) && allowRemoteHttp)
                {
                    settings.allow_insecure_remote_http = true;
                }
                else
                {
                    SetStatus("This HTTP pairing target is not enabled on the device");
                    pairingRoutine = null;
                    yield break;
                }
            }
            // Never carry candidates (and therefore credentials) from the old
            // binding into a newly issued configuration. Endpoint authorities
            // are user-controlled trust boundaries; a fresh pair starts with
            // only the server-provided primary URL.
            settings.endpoint_urls = new List<string>
            {
                AstrBotProtocol.NormalizeBaseUrl(settings.base_url)
            };
            if (!BackendPairingProtocol.TryWriteSettingsAtomically(
                bridge == null ? string.Empty : bridge.ConfigurationPath,
                settings,
                out var reason,
                allowPrivateHttp,
                allowRemoteHttp))
            {
                SetStatus(reason);
                pairingRoutine = null;
                yield break;
            }
            if (bridge == null || !bridge.ReloadConfiguration())
            {
                SetStatus("Configuration saved, but AstrBot reconnect could not start");
                pairingRoutine = null;
                yield break;
            }

            SetStatus("Backend paired; AstrBot is connecting");
            pairingRoutine = null;
        }

        private static bool TryValidateExchangeCertificatePin(
            string exchangeEndpoint,
            string pendingPin,
            AstrBotBridgeSettings settings,
            out string responsePin,
            out string reason)
        {
            responsePin = string.Empty;
            reason = string.Empty;
            if (settings == null || !Uri.TryCreate(exchangeEndpoint, UriKind.Absolute, out var exchangeUri))
            {
                reason = "Pairing response target is invalid";
                return false;
            }

            var endpointIsHttps = exchangeUri.Scheme == Uri.UriSchemeHttps;
            var returnedPin = settings.certificate_pin_sha256 ?? string.Empty;
            if (!string.IsNullOrEmpty(returnedPin))
            {
                if (!TryNormalizeCertificatePin(returnedPin, out responsePin, out reason))
                {
                    return false;
                }
                if (!endpointIsHttps)
                {
                    reason = "certificate pin is only valid for HTTPS";
                    return false;
                }
                if (!BackendPairingProtocol.IsPinnedPairingTargetAllowed(
                        exchangeEndpoint,
                        settings.base_url,
                        responsePin))
                {
                    reason = "certificate pin target authority does not match pairing server";
                    return false;
                }
            }
            else if (!string.IsNullOrEmpty(pendingPin))
            {
                reason = "HTTPS pairing response is missing certificate pin";
                return false;
            }
            else if (!endpointIsHttps && !string.IsNullOrEmpty(returnedPin))
            {
                reason = "certificate pin is only valid for HTTPS";
                return false;
            }

            if (!string.IsNullOrEmpty(pendingPin))
            {
                if (!endpointIsHttps || !TryNormalizeCertificatePin(pendingPin, out var normalizedPending, out reason))
                {
                    if (string.IsNullOrEmpty(reason))
                    {
                        reason = "certificate pin is only valid for HTTPS";
                    }
                    return false;
                }
                if (string.IsNullOrEmpty(responsePin) ||
                    !ConstantTimeEquals(normalizedPending, responsePin))
                {
                    reason = "HTTPS pairing certificate pin does not match the configured pin";
                    return false;
                }
            }

            // A pin-less HTTP v1 response remains compatible; a pin-less HTTPS
            // response relies on the platform's normal certificate validation.
            if (!string.IsNullOrEmpty(responsePin) &&
                !BackendPairingProtocol.IsPinnedPairingTargetAllowed(
                    exchangeEndpoint,
                    settings.base_url,
                    responsePin))
            {
                reason = "certificate pin target authority does not match pairing server";
                return false;
            }
            return true;
        }

        private static bool ConstantTimeEquals(string first, string second)
        {
            if (first == null || second == null || first.Length != second.Length)
            {
                return false;
            }
            var difference = 0;
            for (var index = 0; index < first.Length; index++)
            {
                difference |= first[index] ^ second[index];
            }
            return difference == 0;
        }

        private void RestoreHttpPreferences()
        {
            allowPrivateHttp = PlayerPrefs.GetInt(PrivateHttpPreference, 0) == 1;
            allowRemoteHttp = PlayerPrefs.GetInt(RemoteHttpPreference, 0) == 1;
        }

        private void RestoreCertificatePin()
        {
            var saved = PlayerPrefs.GetString(CertificatePinPreference, string.Empty);
            if (TryNormalizeCertificatePin(saved, out var normalized, out _))
            {
                certificatePinSha256 = normalized;
                return;
            }
            certificatePinSha256 = string.Empty;
            if (!string.IsNullOrEmpty(saved))
            {
                PlayerPrefs.DeleteKey(CertificatePinPreference);
                PlayerPrefs.Save();
            }
        }

        private static bool TryNormalizeCertificatePin(string value, out string normalized, out string reason)
        {
            normalized = string.Empty;
            reason = string.Empty;
            var candidate = value ?? string.Empty;
            if (candidate.Length == 0)
            {
                return true;
            }
            if (candidate.Length != 64)
            {
                reason = "certificate pin must be exactly 64 hexadecimal characters";
                return false;
            }
            for (var index = 0; index < candidate.Length; index++)
            {
                var c = candidate[index];
                var valid = (c >= '0' && c <= '9') ||
                    (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!valid)
                {
                    reason = "certificate pin must be exactly 64 hexadecimal characters";
                    return false;
                }
            }
            normalized = candidate.ToUpperInvariant();
            return true;
        }

        private static bool IsHttpsEndpoint(string endpoint)
        {
            return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps;
        }

        private static bool IsPlainHttpEndpoint(string endpoint)
        {
            return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttp;
        }

        private static string SummarizeCertificatePin(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            var normalized = value.Trim().ToUpperInvariant();
            if (normalized.Length <= 16)
            {
                return normalized;
            }
            return normalized.Substring(0, 8) + "…" + normalized.Substring(normalized.Length - 8, 8);
        }

        private static bool TryReadQrCertificatePin(string json, out string pin, out string reason)
        {
            pin = string.Empty;
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(json))
            {
                return true;
            }
            PairingQrCertificatePinPayload payload;
            try
            {
                payload = JsonUtility.FromJson<PairingQrCertificatePinPayload>(json);
            }
            catch (Exception exception)
            {
                // The base QR parser already reports malformed JSON. Keep this
                // optional field parser fail-closed without exposing the input.
                QuestDebugMode.Report(exception, "pairing.parse-qr-pin");
                return true;
            }
            if (payload == null)
            {
                return true;
            }
            var candidate = !string.IsNullOrEmpty(payload.certificate_pin_sha256)
                ? payload.certificate_pin_sha256
                : !string.IsNullOrEmpty(payload.certificatePinSha256)
                    ? payload.certificatePinSha256
                    : payload.sha256;
            if (!TryNormalizeCertificatePin(candidate, out pin, out reason))
            {
                return false;
            }
            return true;
        }

        private void RestorePairingServer()
        {
            var saved = PlayerPrefs.GetString(PairingServerPreference, string.Empty);
            var restoredLegacyPreference = false;
            if (string.IsNullOrWhiteSpace(saved) && PlayerPrefs.HasKey(LegacyPairingServerPreference))
            {
                saved = PlayerPrefs.GetString(LegacyPairingServerPreference, string.Empty);
                restoredLegacyPreference = true;
            }
            if (BackendPairingProtocol.TryBuildExchangeEndpoint(
                saved,
                out var endpoint,
                out _,
                allowPrivateHttp,
                allowRemoteHttp))
            {
                PairingServerEndpoint = endpoint;
                if (restoredLegacyPreference || !string.Equals(saved, endpoint, StringComparison.Ordinal))
                {
                    PlayerPrefs.SetString(PairingServerPreference, endpoint);
                    PlayerPrefs.Save();
                }
                return;
            }
            if (bridge != null && BackendPairingProtocol.TryBuildExchangeEndpoint(
                bridge.ConfiguredBaseUrl,
                out endpoint,
                out _,
                allowPrivateHttp,
                allowRemoteHttp))
            {
                PairingServerEndpoint = endpoint;
            }
        }

        private void SetStatus(string value)
        {
            Status = string.IsNullOrWhiteSpace(value) ? "Pairing status unavailable" : value;
            StatusChanged?.Invoke();
        }

        private void CancelPairingRequest()
        {
            if (pairingRoutine != null)
            {
                StopCoroutine(pairingRoutine);
                pairingRoutine = null;
            }
            if (activeRequest != null)
            {
                activeRequest.Abort();
                activeRequest.Dispose();
                activeRequest = null;
            }
        }

        private void OnDisable()
        {
            CancelPairingRequest();
        }
    }
}
