using System;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Unity-side transport for the Banxia Flutter overlay bridge. It owns the
    /// envelope encode/decode and dispatch loop, and has no compile-time
    /// Flutter dependency: everything is plain JSON handed off through a small
    /// host seam. See <see cref="FlutterMessageProtocol"/> for the wire format.
    ///
    /// Flow
    ///   Flutter → MethodChannel「banxia.bridge」→ Android plugin
    ///            → UnitySendMessage("BanxiaFlutterBridge", "ReceiveFromNative", json)
    ///            → <see cref="ReceiveFromNative"/> → <see cref="FlutterUiFacade.HandleCommand"/>
    ///   engine  → <see cref="PublishEvent"/> → Android host onUnityEvent(json)
    ///            → EventChannel「banxia.events」→ Flutter.
    ///
    /// The Android plugin contract (implemented outside this repository):
    ///   com.lingxi.banxia.flutter.BanxiaFlutterHost
    ///     static BanxiaFlutterHost instance()
    ///     void onUnityEvent(String json)          // event envelope
    ///     void onUnityReply(long id, String json) // reply envelope
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BanxiaFlutterBridge : MonoBehaviour
    {
        public static BanxiaFlutterBridge Instance { get; private set; }

        [SerializeField] private FlutterUiFacade facade;

#if UNITY_ANDROID
        // Canonical Java class/method names for the Flutter host plugin. These
        // live in the Android plugin (not edited here) and are resolved lazily;
        // a missing host degrades to the managed sinks below without crashing.
        private const string NativeHostClassName = "com.lingxi.banxia.flutter.BanxiaFlutterHost";
        private const string NativeHostAccessorMethod = "instance";
        private const string NativeEventMethod = "onUnityEvent";
        private const string NativeReplyMethod = "onUnityReply";
        private const string NativeConfigureUnityMethod = "configureUnity";
        private const string NativeInitializeMethod = "initialize";
        private const string NativeInitializeAndAttachMethod = "initializeAndAttach";
        private const string NativeShutdownMethod = "shutdown";
        private const string NativeStateMethod = "getStateJson";
        private const string NativePreflightMethod = "preflight";
        private AndroidJavaObject nativeHost;
        private bool nativeHostResolved;
        private bool nativeHostConfigured;
        private float nextNativeHostResolveAt;
#endif

        /// <summary>Receives a serialized <c>event</c> envelope when no Android host is attached.</summary>
        public Action<string> EventSink { get; set; }

        /// <summary>Receives (command id, serialized <c>reply</c> envelope) when no Android host is attached.</summary>
        public Action<long, string> ReplySink { get; set; }

        public FlutterUiFacade Facade => facade;

        /// <summary>Whether the Android host class could be resolved.</summary>
        public bool NativeHostAvailable
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return TryResolveNativeHost();
#else
                return false;
#endif
            }
        }

        /// <summary>Initializes the Android Flutter engine and registers Unity as its receiver.</summary>
        public bool InitializeNativeHost(bool attachPhoneView)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!TryResolveNativeHost() || nativeHost == null)
            {
                return false;
            }
            try
            {
                if (!nativeHostConfigured)
                {
                    nativeHost.Call(NativeConfigureUnityMethod, gameObject.name, nameof(ReceiveFromNative));
                    nativeHostConfigured = true;
                }
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    if (activity == null)
                    {
                        return false;
                    }
                    if (attachPhoneView)
                    {
                        return nativeHost.Call<bool>(NativeInitializeAndAttachMethod, activity);
                    }
                    return nativeHost.Call<bool>(NativeInitializeMethod, activity);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[BanxiaFlutterBridge] Native host initialization failed: " + exception.Message);
                return false;
            }
#else
            return false;
#endif
        }

        /// <summary>Returns the host's JSON capability snapshot for QA/diagnostics.</summary>
        public string NativeStateJson()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!TryResolveNativeHost() || nativeHost == null)
            {
                return "{\"available\":false,\"reason\":\"host unavailable\"}";
            }
            try
            {
                return nativeHost.Call<string>(NativeStateMethod);
            }
            catch (Exception exception)
            {
                return "{\"available\":false,\"reason\":\"" + exception.Message.Replace("\"", "'") + "\"}";
            }
#else
            return "{\"available\":false,\"reason\":\"Android host not active\"}";
#endif
        }

        /// <summary>Stops the Android Flutter engine during application teardown.</summary>
        public void ShutdownNativeHost()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (nativeHost == null)
            {
                return;
            }
            try
            {
                nativeHost.Call(NativeShutdownMethod);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[BanxiaFlutterBridge] Native host shutdown failed: " + exception.Message);
            }
#endif
        }

        private float nextNoSinkWarningAt;

        private void Awake()
        {
            Instance = this;
            EnsureFacade();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
#if UNITY_ANDROID
            if (nativeHost != null)
            {
                nativeHost.Dispose();
                nativeHost = null;
            }
#endif
        }

        /// <summary>Binds the command handler. Called explicitly when the facade is not a sibling component.</summary>
        public void Bind(FlutterUiFacade uiFacade)
        {
            facade = uiFacade;
        }

        /// <summary>
        /// Dispatches a serialized command envelope and returns the serialized
        /// reply envelope (or an error reply when parsing fails). Synchronous;
        /// asynchronous work is reported back through events / <see cref="PublishReply"/>.
        /// </summary>
        public string DispatchCommand(string commandJson)
        {
            var reply = BuildReply(commandJson);
            return reply == null ? string.Empty : FlutterMessageProtocol.Serialize(reply);
        }

        /// <summary>
        /// Entry point invoked from the Android plugin via
        /// <c>UnitySendMessage("BanxiaFlutterBridge", "ReceiveFromNative", json)</c>.
        /// Dispatches the command and delivers the reply to the native host.
        /// </summary>
        public void ReceiveFromNative(string commandJson)
        {
            var reply = BuildReply(commandJson);
            if (reply == null)
            {
                return;
            }
            DeliverReply(reply.id, FlutterMessageProtocol.Serialize(reply));
        }

        /// <summary>Publishes an engine → Flutter event as a serialized envelope.</summary>
        public void PublishEvent(string eventName, string payloadJson)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                return;
            }
            DeliverEvent(FlutterMessageProtocol.Serialize(FlutterMessageProtocol.Event(eventName, payloadJson)));
        }

        /// <summary>Publishes a typed event payload (serialized with JsonUtility).</summary>
        public void PublishEvent<T>(string eventName, T payload)
        {
            if (!FlutterMessageProtocol.TrySerializePayload(payload, out var payloadJson, out var error))
            {
                Debug.LogWarning("[BanxiaFlutterBridge] Event payload for '" + eventName + "' could not be serialized: " + error);
                return;
            }
            PublishEvent(eventName, payloadJson);
        }

        /// <summary>Delivers an asynchronous reply for a previously-received command id.</summary>
        public void PublishReply(long id, string name, bool ok, string dataJson, string error)
        {
            DeliverReply(id, FlutterMessageProtocol.Serialize(FlutterMessageProtocol.Reply(id, name, dataJson, error)));
        }

        /// <summary>Publishes a toast convenience event.</summary>
        public void PublishToast(string message)
        {
            PublishEvent(FlutterEvents.Toast, new FlutterToastPayload { message = message ?? string.Empty });
        }

        private FlutterEnvelope BuildReply(string commandJson)
        {
            if (!FlutterMessageProtocol.TryParse(commandJson, out var envelope, out var parseError))
            {
                return FlutterMessageProtocol.Reply(0, string.Empty, null, parseError);
            }
            if (!envelope.IsCommand)
            {
                return FlutterMessageProtocol.Reply(envelope.id, envelope.name, null, "Expected a cmd envelope, got " + envelope.type);
            }
            EnsureFacade();
            if (facade == null)
            {
                return FlutterMessageProtocol.Reply(envelope.id, envelope.name, null, "No FlutterUiFacade is bound to the bridge");
            }
            try
            {
                var result = facade.HandleCommand(envelope.name, envelope.payload);
                return FlutterMessageProtocol.Reply(envelope.id, envelope.name, result.DataJson, result.Error);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[BanxiaFlutterBridge] Command failed: " + exception.Message);
                return FlutterMessageProtocol.Reply(envelope.id, envelope.name, null, "Command handling failed: " + exception.Message);
            }
        }

        private void EnsureFacade()
        {
            if (facade == null)
            {
                facade = GetComponent<FlutterUiFacade>();
            }
        }

        private void DeliverEvent(string json)
        {
#if UNITY_ANDROID
            if (TryResolveNativeHost() && nativeHost != null)
            {
                try
                {
                    nativeHost.Call(NativeEventMethod, json);
                    return;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[BanxiaFlutterBridge] Native event delivery failed: " + exception.Message);
                }
            }
#endif
            if (EventSink != null)
            {
                EventSink(json);
                return;
            }
            WarnNoSink("event");
        }

        private void DeliverReply(long id, string json)
        {
#if UNITY_ANDROID
            if (TryResolveNativeHost() && nativeHost != null)
            {
                try
                {
                    nativeHost.Call(NativeReplyMethod, id, json);
                    return;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[BanxiaFlutterBridge] Native reply delivery failed: " + exception.Message);
                }
            }
#endif
            if (ReplySink != null)
            {
                ReplySink(id, json);
                return;
            }
            WarnNoSink("reply");
        }

#if UNITY_ANDROID
        private bool TryResolveNativeHost()
        {
            if (nativeHost != null)
            {
                return true;
            }
            // Unity can invoke Awake before the Android plugin classes are visible.
            // Retry after startup instead of permanently caching that transient miss.
            if (nativeHostResolved && Time.unscaledTime < nextNativeHostResolveAt)
            {
                return false;
            }
            nativeHostResolved = true;
            nextNativeHostResolveAt = Time.unscaledTime + 2f;
            try
            {
                using (var hostClass = new AndroidJavaClass(NativeHostClassName))
                {
                    nativeHost = hostClass.CallStatic<AndroidJavaObject>(NativeHostAccessorMethod);
                }
                if (nativeHost != null)
                {
                    nativeHostConfigured = false;
                }
            }
            catch (Exception exception)
            {
                Debug.Log("[BanxiaFlutterBridge] Flutter native host unavailable (" + exception.Message + "); falling back to managed sinks.");
                nativeHost = null;
            }
            return nativeHost != null;
        }
#endif

        private void WarnNoSink(string kind)
        {
            if (Time.unscaledTime < nextNoSinkWarningAt)
            {
                return;
            }
            nextNoSinkWarningAt = Time.unscaledTime + 5f;
            Debug.LogWarning("[BanxiaFlutterBridge] No " + kind + " sink is attached; message dropped. Attach EventSink/ReplySink or the Android Flutter host.");
        }
    }
}
