using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestMmdPlayer
{
    // ------------------------------------------------------------------
    // Banxia Flutter bridge — wire protocol (Unity side).
    //
    // The bridge exchanges *pure JSON* with the Flutter overlay. Both ends
    // share one versioned envelope (see docs/plans/flutter-ui-module-design.md
    // section 6):
    //
    //   { "v": 1, "id": 12, "type": "cmd|reply|event",
    //     "name": "<cmdOrEvent>", "payload": "<raw JSON string>", "error": "" }
    //
    // IMPORTANT (IL2CPP / JsonUtility constraint): Unity's JsonUtility cannot
    // serialize or deserialize `object`, `Dictionary<string,object>`, or
    // polymorphic payloads. To keep the bridge reflection-free and AOT-safe,
    // `payload` is an opaque JSON *string*: the producer serializes its typed
    // DTO with JsonUtility, embeds the result as a string, and the consumer
    // deserializes it with the matching DTO. The Flutter side must JSON-
    // stringify its payload before embedding it and JSON-parse it on receipt.
    //
    // `error` is empty for success and non-empty for failure, so a `reply` is
    // "ok" exactly when `error` is empty. `type` distinguishes the three kinds
    // of message; there is no separate `ok` field on the wire.
    // ------------------------------------------------------------------

    /// <summary>Command names (Flutter → engine). Mirror of design §6.1.</summary>
    public static class FlutterCommands
    {
        public const string ModelDiscover = "model.discover";
        public const string ModelLoad = "model.load";
        public const string ModelDelete = "model.delete";
        public const string ModelImport = "model.import";

        public const string ActionRefresh = "action.refresh";
        public const string ActionPlay = "action.play";
        public const string ActionStop = "action.stop";
        public const string ActionDelete = "action.delete";

        public const string IdleCycle = "idle.cycle";
        public const string ExpressionCycle = "expression.cycle";
        public const string AvatarCommand = "avatar.command";

        public const string ConversationSend = "conversation.send";
        public const string ConversationSendWithCamera = "conversation.sendWithCamera";
        public const string ConversationInterrupt = "conversation.interrupt";

        public const string VoiceToggleListen = "voice.toggleListen";
        public const string VoiceToggleRecord = "voice.toggleRecord";
        public const string VoiceRestart = "voice.restart";
        public const string VoiceCancel = "voice.cancel";

        public const string PairingSetServer = "pairing.setServer";
        public const string PairingSetPrivateHttp = "pairing.setPrivateHttp";
        public const string PairingDigit = "pairing.digit";
        public const string PairingPair = "pairing.pair";
        public const string PairingReconnect = "pairing.reconnect";
        public const string PairingClearBinding = "pairing.clearBinding";

        public const string QualityApplyPreset = "quality.applyPreset";
        public const string QualityApplyPhysics = "quality.applyPhysics";
        public const string QualityReset = "quality.reset";

        public const string SettingsTargetFps = "settings.targetFps";
        public const string SettingsVolume = "settings.volume";
        public const string SettingsToggle = "settings.toggle";

        public const string CopresenceEnterScene = "copresence.enterScene";
        public const string CopresenceReturnToMenu = "copresence.returnToMenu";
        public const string CopresenceSwitchMode = "copresence.switchMode";
        public const string CopresenceSwitchEnvironment = "copresence.switchEnvironment";
        public const string CopresenceSetChromeInsets = "copresence.setChromeInsets";
        public const string CopresenceArPlace = "copresence.arPlace";

        public const string SceneMoveMode = "scene.moveMode";
        public const string SceneReframe = "scene.reframe";
        public const string SceneHud = "scene.hud";

        public const string UpdateCheck = "update.check";
        public const string UpdateInstall = "update.install";

        public const string LogRefresh = "log.refresh";
        public const string LogClear = "log.clear";

        public const string QaCommand = "qa.command";
    }

    /// <summary>Event names (engine → Flutter). Mirror of design §6.2.</summary>
    public static class FlutterEvents
    {
        public const string ConnectionChanged = "connection.changed";
        public const string PairingStatus = "pairing.status";
        public const string ConversationState = "conversation.state";
        public const string ConversationTranscript = "conversation.transcript";
        public const string ConversationReply = "conversation.reply";
        public const string ConversationSuggestions = "conversation.suggestions";
        public const string ModelUpdated = "model.updated";
        public const string ModelImportStatus = "model.importStatus";
        public const string ActionUpdated = "action.updated";
        public const string ActionPlaybackChanged = "action.playbackChanged";
        public const string QualityChanged = "quality.changed";
        public const string CopresenceMode = "copresence.mode";
        public const string CopresenceCallTimer = "copresence.callTimer";
        public const string CopresenceChromeInsetsNeeded = "copresence.chromeInsetsNeeded";
        public const string CopresencePlacementChanged = "copresence.placementChanged";
        public const string FramingAnchors = "framing.anchors";
        public const string VoiceStatus = "voice.status";
        public const string UpdateStatus = "update.status";
        public const string LogUpdated = "log.updated";
        public const string PerformanceSnapshot = "performance.snapshot";
        public const string Toast = "toast";
    }

    /// <summary>Envelope type string constants (wire values, not an enum).</summary>
    public static class FlutterEnvelopeTypes
    {
        public const string Command = "cmd";
        public const string Reply = "reply";
        public const string Event = "event";
    }

    /// <summary>QA command names folded into <c>qa.command</c> (design §6.3).</summary>
    public static class FlutterQaCommands
    {
        public const string ToggleMenu = "toggle_menu";
        public const string OpenModelList = "open_model_list";
        public const string LoadFirstModel = "load_first_model";
        public const string CaptureFirstModel = "capture_first_model";
        public const string OpenImport = "open_import";
        public const string SimulateContactForQa = "SimulateContactForQa";
        public const string OpenWorldUi = "open_world_ui";
        public const string OpenTextInput = "open_text_input";
        public const string SendText = "send_text";
        public const string RunVmdQa = "run_vmd_qa";
        public const string RunPerformanceQa = "run_performance_qa";
    }

    /// <summary>
    /// The single versioned envelope shared by commands, replies and events.
    /// Field names intentionally match the JSON schema (§6) so JsonUtility
    /// needs no property-mapping layer.
    /// </summary>
    [Serializable]
    public sealed class FlutterEnvelope
    {
        public int v = FlutterMessageProtocol.Version;
        public long id;
        public string type = FlutterEnvelopeTypes.Event;
        public string name = string.Empty;
        public string payload = string.Empty;
        public string error = string.Empty;

        /// <summary>A reply is successful exactly when <c>error</c> is empty.</summary>
        public bool IsOk => string.IsNullOrEmpty(error);

        public bool IsCommand => string.Equals(type, FlutterEnvelopeTypes.Command, StringComparison.Ordinal);
        public bool IsReply => string.Equals(type, FlutterEnvelopeTypes.Reply, StringComparison.Ordinal);
        public bool IsEvent => string.Equals(type, FlutterEnvelopeTypes.Event, StringComparison.Ordinal);
    }

    /// <summary>
    /// Versioned envelope construction, JSON (de)serialization and validation,
    /// implemented exclusively on JsonUtility so it is safe under IL2CPP.
    /// </summary>
    public static class FlutterMessageProtocol
    {
        public const int Version = 1;
        // One megabyte is generous for any bridge message and stops a hostile
        // or buggy peer from forcing a huge allocation on the engine thread.
        public const int MaxJsonLength = 1 << 20;

        private static readonly HashSet<string> RecognizedCommands = BuildCommandSet();
        private static readonly HashSet<string> RecognizedEvents = BuildEventSet();

        public static FlutterEnvelope Command(long id, string name, string payloadJson)
        {
            return new FlutterEnvelope
            {
                v = Version,
                id = id,
                type = FlutterEnvelopeTypes.Command,
                name = name ?? string.Empty,
                payload = payloadJson ?? string.Empty,
                error = string.Empty
            };
        }

        public static FlutterEnvelope Reply(long id, string name, string dataJson, string error)
        {
            return new FlutterEnvelope
            {
                v = Version,
                id = id,
                type = FlutterEnvelopeTypes.Reply,
                name = name ?? string.Empty,
                payload = dataJson ?? string.Empty,
                error = error ?? string.Empty
            };
        }

        public static FlutterEnvelope Event(string name, string payloadJson)
        {
            return new FlutterEnvelope
            {
                v = Version,
                id = 0,
                type = FlutterEnvelopeTypes.Event,
                name = name ?? string.Empty,
                payload = payloadJson ?? string.Empty,
                error = string.Empty
            };
        }

        /// <summary>Serializes an envelope; returns null when the envelope is null.</summary>
        public static string Serialize(FlutterEnvelope envelope)
        {
            return envelope == null ? null : JsonUtility.ToJson(envelope);
        }

        /// <summary>
        /// Parses and validates a bridge message. Returns false with a reason in
        /// <paramref name="error"/> for empty, oversized, malformed, wrong-version
        /// or structurally-invalid messages. Unknown (future) names are accepted.
        /// </summary>
        public static bool TryParse(string json, out FlutterEnvelope envelope, out string error)
        {
            envelope = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Flutter envelope JSON is empty";
                return false;
            }
            if (json.Length > MaxJsonLength)
            {
                error = "Flutter envelope JSON exceeds the size limit";
                return false;
            }

            try
            {
                envelope = JsonUtility.FromJson<FlutterEnvelope>(json);
            }
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "flutter.protocol");
                if (!(exception is ArgumentException))
                {
                    QuestDebugMode.RethrowIfEnabled(exception, "flutter.protocol");
                }
                error = "Flutter envelope JSON is malformed: " + exception.Message;
                envelope = null;
                return false;
            }

            if (envelope == null)
            {
                error = "Flutter envelope JSON did not deserialize to an envelope";
                return false;
            }
            if (envelope.v != Version)
            {
                error = "Unsupported Flutter envelope version: " + envelope.v;
                return false;
            }
            if (!envelope.IsCommand && !envelope.IsReply && !envelope.IsEvent)
            {
                error = "Unknown Flutter envelope type: " + envelope.type;
                return false;
            }
            if (!envelope.IsReply && string.IsNullOrWhiteSpace(envelope.name))
            {
                error = "Flutter envelope is missing a name";
                return false;
            }
            if (envelope.id < 0)
            {
                error = "Flutter envelope id must be non-negative";
                return false;
            }
            return true;
        }

        /// <summary>JsonUtility-based typed payload serialization (payload is embedded as a string).</summary>
        public static bool TrySerializePayload<T>(T payload, out string json, out string error)
        {
            json = null;
            error = null;
            if (payload == null)
            {
                error = "Flutter payload is null";
                return false;
            }
            try
            {
                json = JsonUtility.ToJson(payload);
                return true;
            }
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "flutter.payload-serialize");
                QuestDebugMode.RethrowIfEnabled(exception, "flutter.payload-serialize");
                error = "Flutter payload serialization failed: " + exception.Message;
                return false;
            }
        }

        /// <summary>JsonUtility-based typed payload deserialization; empty input yields default(T).</summary>
        public static bool TryDeserializePayload<T>(string json, out T value, out string error)
        {
            value = default;
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return true; // Missing payload is a valid "no data" case.
            }
            if (json.Length > MaxJsonLength)
            {
                error = "Flutter payload exceeds the size limit";
                return false;
            }
            try
            {
                value = JsonUtility.FromJson<T>(json);
                return true;
            }
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "flutter.protocol");
                if (!(exception is ArgumentException))
                {
                    QuestDebugMode.RethrowIfEnabled(exception, "flutter.protocol");
                }
                error = "Flutter payload deserialization failed: " + exception.Message;
                return false;
            }
        }

        public static T DeserializePayload<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return default;
            }
            if (json.Length > MaxJsonLength)
            {
                throw new ArgumentException("Flutter payload exceeds the size limit", nameof(json));
            }
            try
            {
                return JsonUtility.FromJson<T>(json);
            }
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "flutter.protocol");
                if (!(exception is ArgumentException))
                {
                    QuestDebugMode.RethrowIfEnabled(exception, "flutter.protocol");
                }
                throw new ArgumentException("Flutter payload JSON is malformed", nameof(json), exception);
            }
        }

        public static bool IsRecognizedCommand(string name)
        {
            return !string.IsNullOrEmpty(name) && RecognizedCommands.Contains(name);
        }

        public static bool IsRecognizedEvent(string name)
        {
            return !string.IsNullOrEmpty(name) && RecognizedEvents.Contains(name);
        }

        private static HashSet<string> BuildCommandSet()
        {
            return new HashSet<string>(StringComparer.Ordinal)
            {
                FlutterCommands.ModelDiscover,
                FlutterCommands.ModelLoad,
                FlutterCommands.ModelDelete,
                FlutterCommands.ModelImport,
                FlutterCommands.ActionRefresh,
                FlutterCommands.ActionPlay,
                FlutterCommands.ActionStop,
                FlutterCommands.ActionDelete,
                FlutterCommands.IdleCycle,
                FlutterCommands.ExpressionCycle,
                FlutterCommands.AvatarCommand,
                FlutterCommands.ConversationSend,
                FlutterCommands.ConversationSendWithCamera,
                FlutterCommands.ConversationInterrupt,
                FlutterCommands.VoiceToggleListen,
                FlutterCommands.VoiceToggleRecord,
                FlutterCommands.VoiceRestart,
                FlutterCommands.VoiceCancel,
                FlutterCommands.PairingSetServer,
                FlutterCommands.PairingSetPrivateHttp,
                FlutterCommands.PairingDigit,
                FlutterCommands.PairingPair,
                FlutterCommands.PairingReconnect,
                FlutterCommands.PairingClearBinding,
                FlutterCommands.QualityApplyPreset,
                FlutterCommands.QualityApplyPhysics,
                FlutterCommands.QualityReset,
                FlutterCommands.SettingsTargetFps,
                FlutterCommands.SettingsVolume,
                FlutterCommands.SettingsToggle,
                FlutterCommands.CopresenceEnterScene,
                FlutterCommands.CopresenceReturnToMenu,
                FlutterCommands.CopresenceSwitchMode,
                FlutterCommands.CopresenceSwitchEnvironment,
                FlutterCommands.CopresenceSetChromeInsets,
                FlutterCommands.CopresenceArPlace,
                FlutterCommands.SceneMoveMode,
                FlutterCommands.SceneReframe,
                FlutterCommands.SceneHud,
                FlutterCommands.UpdateCheck,
                FlutterCommands.UpdateInstall,
                FlutterCommands.LogRefresh,
                FlutterCommands.LogClear,
                FlutterCommands.QaCommand
            };
        }

        private static HashSet<string> BuildEventSet()
        {
            return new HashSet<string>(StringComparer.Ordinal)
            {
                FlutterEvents.ConnectionChanged,
                FlutterEvents.PairingStatus,
                FlutterEvents.ConversationState,
                FlutterEvents.ConversationTranscript,
                FlutterEvents.ConversationReply,
                FlutterEvents.ConversationSuggestions,
                FlutterEvents.ModelUpdated,
                FlutterEvents.ModelImportStatus,
                FlutterEvents.ActionUpdated,
                FlutterEvents.ActionPlaybackChanged,
                FlutterEvents.QualityChanged,
                FlutterEvents.CopresenceMode,
                FlutterEvents.CopresenceCallTimer,
                FlutterEvents.CopresenceChromeInsetsNeeded,
                FlutterEvents.CopresencePlacementChanged,
                FlutterEvents.FramingAnchors,
                FlutterEvents.VoiceStatus,
                FlutterEvents.UpdateStatus,
                FlutterEvents.LogUpdated,
                FlutterEvents.PerformanceSnapshot,
                FlutterEvents.Toast
            };
        }
    }

    // ------------------------------------------------------------------
    // Payload DTOs. Field names match the camelCase JSON keys in design §6.2
    // so JsonUtility round-trips them without a mapping layer.
    // ------------------------------------------------------------------

    [Serializable] public sealed class FlutterConnectionChangedPayload { public bool connected; public string bridgeStatus = string.Empty; }

    [Serializable] public sealed class FlutterPairingStatusPayload
    {
        public string status = string.Empty;
        public string server = string.Empty;
        public bool privateHttp;
        public int codeLen;
    }

    [Serializable] public sealed class FlutterConversationStatePayload { public string state = string.Empty; public string transportStatus = string.Empty; public string lastError = string.Empty; }

    [Serializable] public sealed class FlutterTextPayload { public string text = string.Empty; }

    [Serializable] public sealed class FlutterConversationSuggestionsPayload
    {
        public string[] suggestions = new string[0];
    }

    [Serializable] public sealed class FlutterModelInfoDto
    {
        public string displayName = string.Empty;
        public string path = string.Empty;
        public string packageRoot = string.Empty;
        public string size = string.Empty;
        public bool inUse;
    }

    [Serializable] public sealed class FlutterModelListPayload
    {
        public FlutterModelInfoDto[] models = new FlutterModelInfoDto[0];
    }

    [Serializable] public sealed class FlutterModelUpdatedPayload
    {
        public FlutterModelInfoDto[] models = new FlutterModelInfoDto[0];
        public string currentPath = string.Empty;
    }

    [Serializable] public sealed class FlutterImportStatusPayload { public string status = string.Empty; }

    [Serializable] public sealed class FlutterVmdActionInfoDto { public string id = string.Empty; public string displayName = string.Empty; public long byteLength; public int keyframeCount; public long lastFrame; public float durationSeconds; public bool hasFacialTrack; }

    [Serializable] public sealed class FlutterActionUpdatedPayload { public FlutterVmdActionInfoDto[] actions = new FlutterVmdActionInfoDto[0]; }

    [Serializable] public sealed class FlutterPlaybackChangedPayload { public string playingId = string.Empty; }

    [Serializable] public sealed class FlutterQualityChangedPayload
    {
        public string renderPreset = string.Empty;
        public string physicsPreset = string.Empty;
        public string status = string.Empty;
        public int targetFps;
        public float volume;
        public bool debugMode;
    }

    [Serializable] public sealed class FlutterCopresenceModePayload
    {
        public string mode = string.Empty;
        public string environment = string.Empty;
        public bool videoCallActive;
        public bool arAvailable;
        public bool arPlaced;
    }

    [Serializable] public sealed class FlutterCallTimerPayload { public string durationText = string.Empty; }
    [Serializable] public sealed class FlutterPlacementChangedPayload { public bool arPlaced; }

    [Serializable] public sealed class FlutterFramingAnchorDto
    {
        public float x;
        public float y;
    }

    [Serializable] public sealed class FlutterFramingAnchorSetDto
    {
        public FlutterFramingAnchorDto headTop = new FlutterFramingAnchorDto();
        public FlutterFramingAnchorDto eye = new FlutterFramingAnchorDto();
        public FlutterFramingAnchorDto waist = new FlutterFramingAnchorDto();
        public FlutterFramingAnchorDto feet = new FlutterFramingAnchorDto();
    }

    [Serializable] public sealed class FlutterFramingAnchorsPayload
    {
        public bool valid;
        public float screenWidthPx;
        public float screenHeightPx;
        public float topPx;
        public float bottomPx;
        public string anchorKind = string.Empty;
        public float eyeLinePct;
        public float d;
        public float h;
        public float distance;
        public float cameraY;
        public bool headAnchor;
        public bool degraded;
        public FlutterFramingAnchorSetDto anchors = new FlutterFramingAnchorSetDto();
    }

    [Serializable] public sealed class FlutterVoiceStatusPayload { public bool monitoring; public bool alwaysListening; public bool recording; public float level; }

    [Serializable] public sealed class FlutterUpdateStatusPayload
    {
        public string phase = string.Empty;
        public float progress;
        public string version = string.Empty;
        public bool hasUpdate;
        public string error = string.Empty;
    }

    [Serializable] public sealed class FlutterLogUpdatedPayload { public string[] lines = new string[0]; }

    [Serializable] public sealed class FlutterPerformanceSnapshotPayload { public float fps5s; public float fps30s; public float frameP50Ms; public float frameP95Ms; public float physicsDropS; public int poseSrcFlip; }

    [Serializable] public sealed class FlutterToastPayload { public string message = string.Empty; }
}
