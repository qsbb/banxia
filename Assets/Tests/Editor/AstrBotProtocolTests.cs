#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NUnit.Framework;

namespace QuestMmdPlayer.Tests
{
    public sealed class AstrBotProtocolTests
    {
        [TestCase(0, false)]
        [TestCase(199, false)]
        [TestCase(200, true)]
        [TestCase(204, true)]
        [TestCase(299, true)]
        [TestCase(300, false)]
        [TestCase(401, false)]
        public void SseBecomesReadyFromSuccessfulResponseHeaders(int status, bool expected)
        {
            Assert.That(AstrBotBridge.IsSseHandshakeReady(status), Is.EqualTo(expected));
        }

        [Test]
        public void ExistingSessionConflictCanReconnectButCapacityConflictCannot()
        {
            const string existing =
                "{\"status\":\"error\",\"message\":\"session already exists\",\"data\":{\"code\":\"session_conflict\"}}";
            const string capacity =
                "{\"status\":\"error\",\"message\":\"session limit reached\",\"data\":{\"code\":\"session_conflict\"}}";

            Assert.That(AstrBotBridge.CanRecoverExistingSession(409, existing), Is.True);
            Assert.That(AstrBotBridge.IsSessionCapacityConflict(409, existing), Is.False);
            Assert.That(AstrBotBridge.CanRecoverExistingSession(409, capacity), Is.False);
            Assert.That(AstrBotBridge.IsSessionCapacityConflict(409, capacity), Is.True);
            Assert.That(AstrBotBridge.CanRecoverExistingSession(500, existing), Is.False);
        }

        [Test]
        public void SessionResponseExposesEventBusEligibilityWithoutIdentifiers()
        {
            Assert.That(AstrBotBridge.ParseSessionChainStatus(
                "{\"status\":\"ok\",\"data\":{\"protected_context\":{\"authorized\":true,\"reason\":\"authorized\"}}}"),
                Is.EqualTo("EventBus eligible"));
            Assert.That(AstrBotBridge.ParseSessionChainStatus(
                "{\"status\":\"ok\",\"data\":{\"protected_context\":{\"authorized\":false,\"reason\":\"owner_not_configured\"}}}"),
                Is.EqualTo("owner_not_configured"));
            Assert.That(AstrBotBridge.ParseSessionChainStatus(
                "{\"status\":\"ok\",\"data\":{\"protected_context\":{\"authorized\":false,\"reason\":\"internal-secret\"}}}"),
                Is.EqualTo("protected_context_denied"));
            Assert.That(AstrBotBridge.ParseSessionChainStatus("not json"), Is.EqualTo("chain unknown"));
            Assert.That(AstrBotBridge.ResolveBackendChainStatus("EventBus eligible", "ready"), Is.EqualTo("EventBus ready"));
            Assert.That(AstrBotBridge.ResolveBackendChainStatus("EventBus eligible", "unavailable"), Is.EqualTo("direct provider fallback"));
        }

        [Test]
        public void StableSessionIdentifierHasStrictShape()
        {
            Assert.That(AstrBotBridge.IsStableSessionId("q3-0123456789abcdef0123456789abcdef"), Is.True);
            Assert.That(AstrBotBridge.IsStableSessionId("q3-0123456789ABCDEF0123456789ABCDEF"), Is.False);
            Assert.That(AstrBotBridge.IsStableSessionId("q3-short"), Is.False);
        }

        [Test]
        public void SseParserHandlesSplitUtf8AndComments()
        {
            var parser = new SseEventStreamParser();
            var frames = new List<SseEventFrame>();
            parser.EventReceived += frames.Add;
            var source = ": connected\n\nevent: reply.text.delta\ndata: {\"text\":\"你好\"}\n\n";
            var bytes = Encoding.UTF8.GetBytes(source);

            for (var index = 0; index < bytes.Length; index++)
            {
                parser.Push(new[] { bytes[index] }, 1);
            }

            Assert.That(frames, Has.Count.EqualTo(1));
            Assert.That(frames[0].EventName, Is.EqualTo("reply.text.delta"));
            Assert.That(frames[0].Data, Does.Contain("你好"));
        }

        [Test]
        public void AvatarIntentIsMappedAndUnknownValuesAreSafe()
        {
            const string json = "{\"type\":\"avatar.intent\",\"protocol_version\":\"1.0\",\"session_id\":\"s1\",\"turn_id\":\"i:e9\",\"in_reply_to_event_id\":\"e9\",\"emotion\":\"angry\",\"gesture\":\"delete_avatar\",\"look_at\":\"camera_object\",\"intensity\":1.4,\"duration_ms\":45000}";

            var mapped = AstrBotProtocol.TryMapSseEvent(
                "s1", "avatar.intent", json, out var message, out var error);

            Assert.That(mapped, Is.True, error);
            Assert.That(message.Type, Is.EqualTo(ConversationEventType.AvatarIntent));
            Assert.That(message.TurnId, Is.EqualTo("i:e9"));
            Assert.That(message.InReplyToEventId, Is.EqualTo("e9"));
            Assert.That(message.Emotion, Is.EqualTo("neutral"));
            Assert.That(message.Gesture, Is.EqualTo("idle"));
            Assert.That(message.LookAt, Is.EqualTo("none"));
            Assert.That(message.Intensity, Is.EqualTo(1f));
            Assert.That(message.DurationMs, Is.EqualTo(30000));
        }

        [Test]
        public void AvatarIntentMapsOptionalServerActionIdWithoutInventingLegacyIds()
        {
            const string current = "{\"type\":\"avatar.intent\",\"protocol_version\":\"1.0\",\"session_id\":\"s1\",\"turn_id\":\"t1\",\"action_id\":\"action-wave-1\",\"emotion\":\"happy\",\"gesture\":\"wave\",\"look_at\":\"user\",\"intensity\":0.5,\"duration_ms\":2000}";
            const string legacy = "{\"type\":\"avatar.intent\",\"protocol_version\":\"1.0\",\"session_id\":\"s1\",\"turn_id\":\"t1\",\"emotion\":\"happy\",\"gesture\":\"wave\",\"look_at\":\"user\",\"intensity\":0.5,\"duration_ms\":2000}";

            Assert.That(AstrBotProtocol.TryMapSseEvent(
                "s1", "avatar.intent", current, out var currentMessage, out var currentError),
                Is.True,
                currentError);
            Assert.That(currentMessage.ActionId, Is.EqualTo("action-wave-1"));
            Assert.That(AstrBotProtocol.TryMapSseEvent(
                "s1", "avatar.intent", legacy, out var legacyMessage, out var legacyError),
                Is.True,
                legacyError);
            Assert.That(legacyMessage.ActionId, Is.Empty);
        }

        [Test]
        public void StructuredAvatarActionMapsBoundedMethodParametersAndTransition()
        {
            const string json = "{\"type\":\"avatar.intent\",\"protocol_version\":\"1.0\",\"session_id\":\"s1\",\"turn_id\":\"t1\",\"action_id\":\"action-crouch-1\",\"method\":\"crouch\",\"parameters\":{\"depth\":1.4,\"hold_ms\":9000,\"style\":\"gentle\"},\"transition\":{\"enter_ms\":550,\"exit_ms\":650,\"easing\":\"ease_in_out\"},\"source\":\"explicit_request\",\"emotion\":\"happy\",\"gesture\":\"idle\",\"look_at\":\"user\",\"intensity\":0.5,\"duration_ms\":2100}";

            Assert.That(AstrBotProtocol.TryMapSseEvent(
                "s1", "avatar.intent", json, out var message, out var error),
                Is.True,
                error);
            Assert.That(message.Gesture, Is.EqualTo("crouch"));
            Assert.That(message.ActionMethod, Is.EqualTo("crouch"));
            Assert.That(message.ActionParameters.Depth, Is.EqualTo(1f));
            Assert.That(message.ActionParameters.HoldMs, Is.EqualTo(5000));
            Assert.That(message.ActionParameters.Style, Is.EqualTo("gentle"));
            Assert.That(message.ActionTransition.EnterMs, Is.EqualTo(550));
            Assert.That(message.ActionTransition.ExitMs, Is.EqualTo(650));
            Assert.That(message.ActionTransition.Easing, Is.EqualTo("ease_in_out"));
            Assert.That(message.ActionSource, Is.EqualTo("explicit_request"));
        }

        [Test]
        public void ClientAdvertisesOnlyExecutableActionMethods()
        {
            var actions = AstrBotProtocol.SupportedActions();

            Assert.That(actions, Does.Contain("crouch"));
            Assert.That(actions, Does.Contain("dance_next"));
            Assert.That(actions, Does.Not.Contain("idle"));
            Assert.That(actions, Does.Not.Contain("talk"));
            Assert.That(actions, Is.Unique);
        }

        [Test]
        public void SpeechTimelineMapsBoundedSortedVisemeCues()
        {
            const string json = "{\"type\":\"reply.speech.timeline\",\"protocol_version\":\"1.0\",\"session_id\":\"s1\",\"turn_id\":\"t1\",\"visemes\":[{\"symbol\":\"A\",\"start_ms\":0,\"end_ms\":120,\"weight\":0.75},{\"symbol\":\"I\",\"start_ms\":100,\"end_ms\":220,\"weight\":1.0}]}";

            Assert.That(AstrBotProtocol.TryMapSseEvent(
                "s1", "reply.speech.timeline", json, out var message, out var error),
                Is.True,
                error);
            Assert.That(message.Type, Is.EqualTo(ConversationEventType.SpeechTimeline));
            Assert.That(message.VisemeTimeline, Has.Length.EqualTo(2));
            Assert.That(message.VisemeTimeline[0].Symbol, Is.EqualTo("A"));
            Assert.That(message.VisemeTimeline[0].Weight, Is.EqualTo(.75f).Within(.001f));
            Assert.That(message.VisemeTimeline[1].StartMs, Is.EqualTo(100));
        }

        [Test]
        public void SpeechTimelineRejectsUnsortedOrUnboundedCues()
        {
            const string json = "{\"type\":\"reply.speech.timeline\",\"protocol_version\":\"1.0\",\"session_id\":\"s1\",\"turn_id\":\"t1\",\"visemes\":[{\"symbol\":\"A\",\"start_ms\":100,\"end_ms\":200},{\"symbol\":\"I\",\"start_ms\":50,\"end_ms\":250}]}";

            Assert.That(AstrBotProtocol.TryMapSseEvent(
                "s1", "reply.speech.timeline", json, out _, out var error),
                Is.False);
            Assert.That(error, Does.Contain("invalid or unsorted"));
        }

        [Test]
        public void ActionResultJsonMatchesStrictBackendContract()
        {
            var receipt = new AvatarActionReceipt
            {
                TurnId = "turn-1",
                ActionId = "action-1",
                ReceiptId = "receipt-1",
                Action = "wave",
                Phase = AvatarActionReceiptPhase.Accepted,
                ReasonCode = "accepted",
                DurationMs = 0
            };

            var json = AstrBotBridge.CreateActionResultJson("session-1", receipt);
            var retryJson = AstrBotBridge.CreateActionResultJson("session-1", receipt);

            Assert.That(json, Is.EqualTo(
                "{\"type\":\"action.result\",\"protocol_version\":\"1.0\",\"session_id\":\"session-1\",\"turn_id\":\"turn-1\",\"action_id\":\"action-1\",\"receipt_id\":\"receipt-1\",\"action\":\"wave\",\"status\":\"accepted\",\"reason_code\":\"accepted\",\"duration_ms\":0}"));
            Assert.That(json, Does.Not.Contain("gesture"));
            Assert.That(json, Does.Not.Contain("phase"));
            Assert.That(json, Does.Not.Contain("source"));
            Assert.That(json, Does.Not.Contain("elapsed_ms"));
            Assert.That(retryJson, Is.EqualTo(json), "Retries must preserve the exact receipt_id and body.");
        }

        [Test]
        public void ActionResultDeliveryQueuePreservesLifecycleOrderWithOneWorker()
        {
            var openType = typeof(AstrBotBridge).Assembly.GetType(
                "QuestMmdPlayer.OrderedDeliveryQueue`1");
            Assert.That(openType, Is.Not.Null);
            var queueType = openType.MakeGenericType(typeof(string));
            var queue = Activator.CreateInstance(queueType, true);
            var enqueue = queueType.GetMethod(
                "Enqueue",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var dequeue = queueType.GetMethod(
                "TryDequeue",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var complete = queueType.GetMethod(
                "CompleteWorker",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(enqueue, Is.Not.Null);
            Assert.That(dequeue, Is.Not.Null);
            Assert.That(complete, Is.Not.Null);

            Assert.That(enqueue.Invoke(queue, new object[] { "accepted" }), Is.True);
            Assert.That(enqueue.Invoke(queue, new object[] { "started" }), Is.False);
            Assert.That(enqueue.Invoke(queue, new object[] { "completed" }), Is.False);

            var actual = new List<string>();
            while (true)
            {
                var arguments = new object[] { null };
                if (!(bool)dequeue.Invoke(queue, arguments)) break;
                actual.Add((string)arguments[0]);
            }
            CollectionAssert.AreEqual(
                new[] { "accepted", "started", "completed" },
                actual);

            complete.Invoke(queue, null);
            Assert.That(enqueue.Invoke(queue, new object[] { "next-action" }), Is.True);
        }

        [TestCase("dance_next")]
        [TestCase("raise_hand")]
        [TestCase("raise_leg")]
        [TestCase("turn_half")]
        [TestCase("crouch")]
        [TestCase("sit")]
        [TestCase("lie")]
        [TestCase("lie_down")]
        public void ExecutableAvatarGesturesSurviveProtocolSanitization(string gesture)
        {
            Assert.That(AstrBotProtocol.SanitizeGesture(gesture), Is.EqualTo(gesture));
        }

        [Test]
        public void Pcm16AudioIsDecodedLittleEndian()
        {
            const string json = "{\"type\":\"reply.audio.chunk\",\"protocol_version\":\"1.0\",\"session_id\":\"s1\",\"turn_id\":\"t1\",\"format\":\"pcm16\",\"sample_rate\":24000,\"channels\":1,\"data\":\"NBLM/w==\"}";

            var mapped = AstrBotProtocol.TryMapSseEvent(
                "s1", "reply.audio.chunk", json, out var message, out var error);

            Assert.That(mapped, Is.True, error);
            Assert.That(message.Pcm16, Is.EqualTo(new short[] { 0x1234, -52 }));
            Assert.That(message.SampleRate, Is.EqualTo(24000));
        }

        [Test]
        public void StaleSessionEventIsRejected()
        {
            const string json = "{\"type\":\"reply.end\",\"protocol_version\":\"1.0\",\"session_id\":\"old\",\"turn_id\":\"t1\"}";

            Assert.That(
                AstrBotProtocol.TryMapSseEvent("current", "reply.end", json, out _, out var error),
                Is.False);
            Assert.That(error, Does.Contain("stale session"));
        }

        [Test]
        public void ReplyEndCarriesServerDeliverySummary()
        {
            const string json = "{\"type\":\"reply.end\",\"protocol_version\":\"1.0\",\"session_id\":\"s1\",\"turn_id\":\"t1\",\"text_sent\":true,\"audio_sent\":false}";

            Assert.That(
                AstrBotProtocol.TryMapSseEvent("s1", "reply.end", json, out var message, out var error),
                Is.True,
                error);
            Assert.That(message.TextSent, Is.True);
            Assert.That(message.AudioSent, Is.False);
        }

        [Test]
        public void OptionalServerTimingIsMappedAndBounded()
        {
            const string json = "{\"type\":\"reply.end\",\"protocol_version\":\"1.0\",\"session_id\":\"s1\",\"turn_id\":\"t1\",\"server_timing\":{\"schema_version\":1,\"stt_ms\":820,\"decision_ms\":1510,\"decision_path\":\"astrbot_event_bus\",\"tts_first_chunk_ms\":620,\"tts_total_ms\":2380,\"turn_total_ms\":4725}}";

            Assert.That(
                AstrBotProtocol.TryMapSseEvent("s1", "reply.end", json, out var message, out var error),
                Is.True,
                error);
            Assert.That(message.BackendTiming, Is.Not.Null);
            Assert.That(message.BackendTiming.SttMs, Is.EqualTo(820));
            Assert.That(message.BackendTiming.DecisionMs, Is.EqualTo(1510));
            Assert.That(message.BackendTiming.TtsFirstChunkMs, Is.EqualTo(620));
            Assert.That(message.BackendTiming.SafeDecisionPath(), Is.EqualTo("astrbot_event_bus"));
        }

        [Test]
        public void ContractNamedServerTimingIsMappedForCurrentBridge()
        {
            const string json = "{\"type\":\"reply.end\",\"protocol_version\":\"1.0\",\"session_id\":\"s1\",\"turn_id\":\"t1\",\"server_timing\":{\"contract\":\"server_timing@1.0\",\"stt_ms\":820,\"decision_ms\":1510,\"decision_path\":\"direct_provider\",\"tts_first_chunk_ms\":620,\"tts_total_ms\":2380,\"turn_total_ms\":4725}}";

            Assert.That(
                AstrBotProtocol.TryMapSseEvent("s1", "reply.end", json, out var message, out var error),
                Is.True,
                error);
            Assert.That(message.BackendTiming, Is.Not.Null);
            Assert.That(message.BackendTiming.SchemaVersion, Is.EqualTo(1));
            Assert.That(message.BackendTiming.SttMs, Is.EqualTo(820));
            Assert.That(message.BackendTiming.DecisionMs, Is.EqualTo(1510));
            Assert.That(message.BackendTiming.SafeDecisionPath(), Is.EqualTo("direct_provider"));
        }

        [Test]
        public void ContractNamedServerTimingClampsDurationsAndUnknownPath()
        {
            const string json = "{\"type\":\"reply.end\",\"protocol_version\":\"1.0\",\"session_id\":\"s1\",\"turn_id\":\"t1\",\"server_timing\":{\"contract\":\"server_timing@1.0\",\"stt_ms\":86400000,\"decision_ms\":-5,\"decision_path\":\"unexpected\",\"tts_first_chunk_ms\":0,\"tts_total_ms\":1,\"turn_total_ms\":86400000}}";

            Assert.That(
                AstrBotProtocol.TryMapSseEvent("s1", "reply.end", json, out var message, out var error),
                Is.True,
                error);
            Assert.That(message.BackendTiming.SttMs, Is.EqualTo(3600000));
            Assert.That(message.BackendTiming.DecisionMs, Is.EqualTo(-1));
            Assert.That(message.BackendTiming.TtsFirstChunkMs, Is.EqualTo(-1));
            Assert.That(message.BackendTiming.TtsTotalMs, Is.EqualTo(1));
            Assert.That(message.BackendTiming.TurnTotalMs, Is.EqualTo(3600000));
            Assert.That(message.BackendTiming.SafeDecisionPath(), Is.EqualTo("unknown"));
        }

        [Test]
        public void InvalidOrMissingServerTimingIsIgnored()
        {
            const string missing = "{\"type\":\"reply.end\",\"protocol_version\":\"1.0\",\"session_id\":\"s1\",\"turn_id\":\"t1\"}";
            const string invalid = "{\"type\":\"reply.end\",\"protocol_version\":\"1.0\",\"session_id\":\"s1\",\"turn_id\":\"t1\",\"server_timing\":{\"schema_version\":9,\"stt_ms\":99999999}}";
            const string invalidContract = "{\"type\":\"reply.end\",\"protocol_version\":\"1.0\",\"session_id\":\"s1\",\"turn_id\":\"t1\",\"server_timing\":{\"contract\":\"server_timing@2.0\",\"stt_ms\":820}}";

            Assert.That(AstrBotProtocol.TryMapSseEvent("s1", "reply.end", missing, out var missingMessage, out _), Is.True);
            Assert.That(missingMessage.BackendTiming, Is.Null);
            Assert.That(AstrBotProtocol.TryMapSseEvent("s1", "reply.end", invalid, out var invalidMessage, out _), Is.True);
            Assert.That(invalidMessage.BackendTiming, Is.Null);
            Assert.That(AstrBotProtocol.TryMapSseEvent("s1", "reply.end", invalidContract, out var invalidContractMessage, out _), Is.True);
            Assert.That(invalidContractMessage.BackendTiming, Is.Null);
        }

        [Test]
        public void RuntimePolicyAllowsOnlyExplicitPrivateLanHttp()
        {
            var settings = ValidSettings();
            settings.base_url = "http://192.168.1.10:6185/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge";

            Assert.That(AstrBotProtocol.TryValidateSettings(settings, out var error), Is.False);
            Assert.That(error, Does.Contain("allow_insecure_http"));

            settings.allow_insecure_http = true;
            Assert.That(AstrBotProtocol.TryValidateSettings(settings, out error), Is.True, error);

            settings.base_url = "http://api.example.com/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge";
            Assert.That(AstrBotProtocol.TryValidateSettings(settings, out error), Is.False);
            Assert.That(error, Does.Contain("private-network IP"));

            settings.base_url = "http://nas.local/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge";
            Assert.That(AstrBotProtocol.TryValidateSettings(settings, out error), Is.False);
        }

        [Test]
        public void SpatialContextPayloadIsBoundedAndContainsNoRoomGeometry()
        {
            var semantic = new RoomSemanticSnapshot
            {
                FloorCount = 2,
                SeatCount = 3,
                BedCount = 1,
                TableCount = 4,
                WallCount = 100,
                DoorCount = -2,
                WindowCount = 5
            };
            var capabilities = new SpatialCapabilitySnapshot
            {
                MetaOpenXr = SpatialCapabilityState.Available,
                Occlusion = SpatialCapabilityState.Fallback
            };

            var payload = AstrBotBridge.CreateSpatialContextRequest(
                "session-1", 7, semantic, capabilities);
            var json = UnityEngine.JsonUtility.ToJson(payload);

            Assert.That(payload.wall_count, Is.EqualTo(64));
            Assert.That(payload.door_count, Is.Zero);
            Assert.That(payload.scene_capture_available, Is.True);
            Assert.That(payload.occlusion_available, Is.False);
            Assert.That(json, Does.Contain("\"bed_count\":1"));
            Assert.That(json, Does.Not.Contain("position"));
            Assert.That(json, Does.Not.Contain("rotation"));
            Assert.That(json, Does.Not.Contain("anchor"));
            Assert.That(json, Does.Not.Contain("mesh"));
            Assert.That(json, Does.Not.Contain("path"));
        }

        [Test]
        public void SpatialContextSignatureIgnoresSessionAndRevisionForDedupe()
        {
            var left = new SpatialContextRequest
            {
                session_id = "session-a",
                revision = 3,
                floor_count = 1,
                scene_capture_available = true
            };
            var right = new SpatialContextRequest
            {
                session_id = "session-b",
                revision = 42,
                floor_count = 1,
                scene_capture_available = true
            };

            Assert.That(left.ContentSignature(), Is.EqualTo(right.ContentSignature()));
            right.floor_count = 2;
            Assert.That(left.ContentSignature(), Is.Not.EqualTo(right.ContentSignature()));
        }

        private static AstrBotBridgeSettings ValidSettings()
        {
            return new AstrBotBridgeSettings
            {
                base_url = "https://astrbot.example.com/api/v1/plugins/extensions/astrbot_plugin_embodiment_bridge",
                astrbot_api_key = "astrbot-api-key",
                bridge_api_key = "0123456789abcdef0123456789abcdef",
                client_id = "quest3-living-room",
                user_id = "user-1",
                bot_id = "bot-main"
            };
        }
    }
}
#endif
