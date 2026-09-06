using System;
using System.Collections;
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

        [SerializeField] private int requestTimeoutSeconds = 15;

        private AstrBotBridge bridge;
        private IPairingCodeScanner scanner = new UnsupportedPairingCodeScanner();
        private UnityWebRequest activeRequest;
        private Coroutine pairingRoutine;
        // Local/private deployments commonly expose the bridge over plain HTTP.
        // Explicit https:// endpoints remain HTTPS regardless of this default.
        private bool allowPrivateHttp = true;

        public event Action StatusChanged;

        public bool IsBusy => pairingRoutine != null;
        public bool ScannerAvailable => scanner != null && scanner.IsAvailable;
        public bool PrivateHttpAllowed => allowPrivateHttp;
        public string PairingServerEndpoint { get; private set; } = string.Empty;
        public string Status { get; private set; } = "Enter pairing server and 6-digit code";

        private void Awake()
        {
            bridge = GetComponent<AstrBotBridge>();
            RestorePairingServer();
        }

        public void Initialize(AstrBotBridge astrBotBridge)
        {
            bridge = astrBotBridge;
            RestorePairingServer();
        }

        public void SetPrivateHttpAllowed(bool allowed)
        {
            if (allowPrivateHttp == allowed)
            {
                return;
            }

            allowPrivateHttp = allowed;
            if (!allowed && Uri.TryCreate(PairingServerEndpoint, UriKind.Absolute, out var current) &&
                current.Scheme == Uri.UriSchemeHttp)
            {
                PairingServerEndpoint = string.Empty;
            }
            RestorePairingServer();
            SetStatus(allowed
                ? "Plain-HTTP pairing enabled (server policy still applies)"
                : "HTTPS pairing required");
        }

        public bool TrySetPairingServer(string value, out string reason)
        {
            if (!BackendPairingProtocol.TryBuildExchangeEndpoint(value, out var endpoint, out reason, allowPrivateHttp))
            {
                SetStatus(reason);
                return false;
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
            PlayerPrefs.Save();
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
                out var reason,
                allowPrivateHttp))
            {
                SetStatus(reason);
                return;
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
            SetStatus("Exchanging one-time pairing credential...");
            var payload = new PairingExchangeRequest { token = token, code = code };
            var body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));
            var request = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = Mathf.Clamp(requestTimeoutSeconds, 3, 60)
            };
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
                var detail = string.IsNullOrWhiteSpace(request.error) ? string.Empty : ": " + request.error;
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
            if (settings != null &&
                Uri.TryCreate(endpoint, UriKind.Absolute, out var pairingUri) &&
                pairingUri.Scheme == Uri.UriSchemeHttp &&
                allowPrivateHttp &&
                AstrBotProtocol.IsPrivateNetworkHost(pairingUri.Host))
            {
                settings.allow_insecure_http = true;
            }
            if (response == null || response.status != "ok" || response.data == null ||
                response.data.pairing_protocol_version != BackendPairingProtocol.Version || settings == null)
            {
                SetStatus("Pairing response is invalid or incompatible");
                pairingRoutine = null;
                yield break;
            }
            if (!BackendPairingProtocol.TryWriteSettingsAtomically(
                bridge == null ? string.Empty : bridge.ConfigurationPath,
                settings,
                out var reason,
                allowPrivateHttp))
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

        private void RestorePairingServer()
        {
            var saved = PlayerPrefs.GetString(PairingServerPreference, string.Empty);
            var restoredLegacyPreference = false;
            if (string.IsNullOrWhiteSpace(saved) && PlayerPrefs.HasKey(LegacyPairingServerPreference))
            {
                saved = PlayerPrefs.GetString(LegacyPairingServerPreference, string.Empty);
                restoredLegacyPreference = true;
            }
            if (BackendPairingProtocol.TryBuildExchangeEndpoint(saved, out var endpoint, out _, allowPrivateHttp))
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
                allowPrivateHttp))
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
