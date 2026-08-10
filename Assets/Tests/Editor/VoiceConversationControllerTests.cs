#if UNITY_EDITOR
using System;
using NUnit.Framework;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class VoiceConversationControllerTests
    {
        private GameObject owner;

        [TearDown]
        public void TearDown()
        {
            if (owner != null)
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void MissingTransportDoesNotSilentlyCreateMock()
        {
            owner = new GameObject("No implicit mock transport test");
            var controller = owner.AddComponent<ConversationController>();

            Assert.IsFalse(controller.IsUsingMockTransport);
            Assert.IsFalse(controller.IsRealBackendConnected);
            Assert.That(controller.TransportStatus, Does.Contain("No conversation transport"));
        }

        [Test]
        public void VoiceTurnUsesOneTurnIdAndForwardsPcmBeforeEnd()
        {
            owner = new GameObject("Voice conversation test");
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);
            var pcm16 = new byte[] { 0x00, 0x00, 0xff, 0x7f };

            Assert.IsTrue(controller.BeginVoiceInput());
            Assert.AreEqual(ConversationState.Listening, controller.State);
            Assert.AreEqual(controller.TurnId, transport.StartedTurnId);
            Assert.IsTrue(controller.PushVoiceAudio(pcm16));
            Assert.AreSame(pcm16, transport.LastChunk);
            Assert.AreEqual(controller.TurnId, transport.ChunkTurnId);
            Assert.IsTrue(controller.EndVoiceInput());
            Assert.AreEqual(controller.TurnId, transport.EndedTurnId);
            CollectionAssert.AreEqual(new[] { "start", "chunk", "end" }, transport.Calls);
        }

        [Test]
        public void DuplicateVoiceActivationWhileCapturingDoesNotCreateAnotherTurn()
        {
            owner = new GameObject("Duplicate voice activation guard test");
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);

            Assert.IsTrue(controller.BeginVoiceInput());
            var firstTurnId = controller.TurnId;

            Assert.IsFalse(controller.BeginVoiceInput());
            Assert.AreEqual(firstTurnId, controller.TurnId);
            Assert.AreEqual(1, transport.AudioStartCount);
            Assert.AreEqual(0, transport.InterruptCount);
            CollectionAssert.AreEqual(new[] { "start" }, transport.Calls);
        }

        [Test]
        public void DisconnectedTransportCannotStartVoiceTurn()
        {
            owner = new GameObject("Offline voice conversation test");
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            transport.Connected = false;
            controller.SetTransport(transport);

            Assert.IsFalse(controller.BeginVoiceInput());
            Assert.AreEqual(ConversationState.Idle, controller.State);
            Assert.IsEmpty(transport.Calls);
        }

        [Test]
        public void VoiceInputCanInterruptAnActiveReplyForBargeIn()
        {
            owner = new GameObject("No accidental barge-in test");
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);
            controller.StartConversation("hello");
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AudioChunk,
                TurnId = controller.TurnId,
                Pcm16 = new short[] { 10, -10 },
                SampleRate = 24000
            });

            Assert.AreEqual(ConversationState.Speaking, controller.State);
            Assert.IsTrue(controller.CanStartVoiceInput);
            Assert.IsTrue(controller.BeginVoiceInput());
            Assert.AreEqual(ConversationState.Listening, controller.State);
            Assert.AreEqual(1, transport.InterruptCount);
        }

        [Test]
        public void VoiceInputCanBargeInWhileWaitingForFirstBackendEvent()
        {
            owner = new GameObject("Waiting response barge-in test");
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);

            Assert.IsTrue(controller.BeginVoiceInput());
            Assert.IsTrue(controller.PushVoiceAudio(new byte[] { 0, 0 }));
            Assert.IsTrue(controller.EndVoiceInput());
            Assert.IsTrue(controller.AwaitingBackendResponse);
            var firstTurnId = controller.TurnId;

            Assert.IsTrue(controller.BeginVoiceInput());
            Assert.AreNotEqual(firstTurnId, controller.TurnId);
            Assert.AreEqual(1, transport.InterruptCount);
            Assert.AreEqual(2, transport.AudioStartCount);
            Assert.AreEqual(ConversationState.Listening, controller.State);
        }

        [Test]
        public void SyntheticTransportAckDoesNotCountAsFirstBackendEvent()
        {
            owner = new GameObject("Synthetic transport timing test");
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);
            controller.StartConversation("hello");

            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.Thinking,
                TurnId = controller.TurnId,
                IsSyntheticTransportEvent = true
            });
            Assert.That(controller.TurnTimingStatus, Does.Contain("firstEvent=-ms"));

            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AsrFinal,
                TurnId = controller.TurnId,
                Text = "hello"
            });
            Assert.That(controller.TurnTimingStatus, Does.Not.Contain("firstEvent=-ms"));
        }

        [Test]
        public void BackendTimingIsLoggedAgainForEachCompletedTurn()
        {
            owner = new GameObject("Backend timing reset test");
            var diagnostics = owner.AddComponent<RuntimeDebugLog>();
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);

            controller.StartConversation("first");
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.ReplyTextDelta,
                TurnId = controller.TurnId,
                Text = "one"
            });
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.ReplyEnd,
                TurnId = controller.TurnId,
                TextSent = true,
                BackendTiming = new BackendTimingSnapshot
                {
                    SchemaVersion = 1,
                    SttMs = 111,
                    DecisionMs = 112,
                    TtsFirstChunkMs = 113,
                    TtsTotalMs = 114,
                    TurnTotalMs = 115,
                    DecisionPath = "astrbot_event_bus"
                }
            });
            var firstTimeline = diagnostics.GetRecentTimelineText();
            Assert.That(firstTimeline, Does.Contain("后端语音识别"));
            Assert.That(firstTimeline, Does.Contain("111ms"));
            Assert.That(firstTimeline, Does.Contain("AstrBot/EventBus"));

            controller.StartConversation("second");
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.ReplyTextDelta,
                TurnId = controller.TurnId,
                Text = "two"
            });
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.ReplyEnd,
                TurnId = controller.TurnId,
                TextSent = true,
                BackendTiming = new BackendTimingSnapshot
                {
                    SchemaVersion = 1,
                    SttMs = 221,
                    DecisionMs = 222,
                    TtsFirstChunkMs = 223,
                    TtsTotalMs = 224,
                    TurnTotalMs = 225,
                    DecisionPath = "direct_provider"
                }
            });

            var secondTimeline = diagnostics.GetRecentTimelineText();
            Assert.That(secondTimeline, Does.Contain("后端语音识别"));
            Assert.That(secondTimeline, Does.Contain("221ms"));
            Assert.That(secondTimeline, Does.Contain("直接模型"));
        }

        [Test]
        public void LocalTouchReactionsRemainImmediateWhenBackendConnectsOrDisconnects()
        {
            owner = new GameObject("Conversation fallback test");
            var avatar = owner.AddComponent<AvatarController>();
            avatar.Initialize(owner.transform);
            owner.AddComponent<AvatarTouchInteraction>();
            var interaction = owner.AddComponent<AvatarHumanInteraction>();
            interaction.Bind(avatar);
            var controller = owner.AddComponent<ConversationController>();
            controller.Bind(avatar, interaction);

            var connected = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(connected);
            Assert.IsTrue(interaction.LocalReactionsEnabled);
            Assert.IsTrue(controller.IsRealBackendConnected);

            var disconnected = owner.AddComponent<RecordingVoiceTransport>();
            disconnected.Connected = false;
            controller.SetTransport(disconnected);
            Assert.IsTrue(interaction.LocalReactionsEnabled);
            Assert.IsFalse(controller.IsRealBackendConnected);
        }

        [Test]
        public void TouchDoesNotInterruptAnActiveVoiceTurn()
        {
            owner = new GameObject("Concurrent touch and voice test");
            var avatar = owner.AddComponent<AvatarController>();
            avatar.Initialize(owner.transform);
            owner.AddComponent<AvatarTouchInteraction>();
            var interaction = owner.AddComponent<AvatarHumanInteraction>();
            interaction.Bind(avatar);
            var controller = owner.AddComponent<ConversationController>();
            controller.Bind(avatar, interaction);
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            transport.InteractionEventId = "touch-1";
            controller.SetTransport(transport);

            controller.StartConversation("hello");
            interaction.SimulateInteraction(HumanInteractionKind.HeadPat);

            Assert.AreEqual(ConversationState.Listening, controller.State);
            Assert.AreEqual(0, transport.InterruptCount);
            Assert.IsEmpty(transport.LastInteractionName);
        }

        [Test]
        public void OnlyPhysicalContactIsForwardedToBackend()
        {
            owner = new GameObject("Physical interaction forwarding test");
            var avatar = owner.AddComponent<AvatarController>();
            avatar.Initialize(owner.transform);
            owner.AddComponent<AvatarTouchInteraction>();
            var interaction = owner.AddComponent<AvatarHumanInteraction>();
            interaction.Bind(avatar);
            var controller = owner.AddComponent<ConversationController>();
            controller.Bind(avatar, interaction);
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);

            interaction.SimulateInteraction(HumanInteractionKind.HeadPat);
            Assert.IsEmpty(transport.LastInteractionName);

            interaction.SetInputEnabled(false);
            interaction.SetInputEnabled(true);
            typeof(AvatarHumanInteraction)
                .GetField("simulationUntil", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(interaction, 0f);
            interaction.ReportTrackedHandContact(AvatarContactRegion.Head, false, Vector3.zero);
            typeof(AvatarHumanInteraction)
                .GetField("stateTime", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(interaction, 1f);
            typeof(AvatarHumanInteraction)
                .GetMethod("Update", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(interaction, null);

            Assert.AreEqual("head_pat", transport.LastInteractionName);
        }

        [Test]
        public void InteractionAudioCannotTakeOverAnActiveVoiceTurn()
        {
            owner = new GameObject("Interaction audio isolation test");
            var avatar = owner.AddComponent<AvatarController>();
            avatar.Initialize(owner.transform);
            owner.AddComponent<AvatarTouchInteraction>();
            var interaction = owner.AddComponent<AvatarHumanInteraction>();
            interaction.Bind(avatar);
            var controller = owner.AddComponent<ConversationController>();
            controller.Bind(avatar, interaction);
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            transport.InteractionEventId = "touch-2";
            controller.SetTransport(transport);

            controller.StartConversation("keep speaking");
            interaction.SimulateInteraction(HumanInteractionKind.HeadPat);
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AudioChunk,
                TurnId = "i:touch-2",
                InReplyToEventId = "touch-2",
                Pcm16 = new short[] { 0 },
                SampleRate = 24000
            });

            Assert.AreEqual(ConversationState.Listening, controller.State);
            Assert.AreNotEqual("i:touch-2", controller.TurnId);
            Assert.AreEqual(0, transport.InterruptCount);
        }

        [Test]
        public void PcmPlayerWaitsForDspTailAfterQueueHasBeenRead()
        {
            owner = new GameObject("PCM DSP tail test");
            var player = owner.AddComponent<Pcm16StreamAudioPlayer>();
            var privateInstance = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            var awake = typeof(Pcm16StreamAudioPlayer).GetMethod("Awake", privateInstance);
            Assert.IsNotNull(awake);
            awake.Invoke(player, null);
            player.Enqueue(new short[] { 1200, -1200, 600, -600 }, 24000);

            var readAudio = typeof(Pcm16StreamAudioPlayer).GetMethod(
                "ReadAudio",
                privateInstance);
            Assert.IsNotNull(readAudio);
            readAudio.Invoke(player, new object[] { new float[1024] });

            Assert.IsFalse(
                player.IsDrained,
                "DSP output still owns the final audible buffer.");
            player.StopAndClear();
            Assert.IsTrue(player.IsDrained);
        }

        [Test]
        public void AudioUploadBatchCombinesCaptureChunksWithoutExceedingLimit()
        {
            var chunks = new System.Collections.Generic.Queue<byte[]>();
            for (var index = 0; index < 7; index++)
            {
                chunks.Enqueue(new byte[2560]);
            }

            var batch = AstrBotBridge.DequeueAudioBatch(chunks, 16000);

            Assert.That(batch.Length, Is.EqualTo(15360));
            Assert.That(chunks.Count, Is.EqualTo(1));
        }

        [Test]
        public void SilenceTimeoutWaitsForSpeechThenUsesTrailingSilence()
        {
            Assert.That(QuestMicrophoneInput.ShouldStopForSilence(
                false, 1.2f, 1.2f, .45f, 1.15f, 4f), Is.False);
            Assert.That(QuestMicrophoneInput.ShouldStopForSilence(
                false, 4.1f, 4.1f, .45f, 1.15f, 4f), Is.True);
            Assert.That(QuestMicrophoneInput.ShouldStopForSilence(
                true, 2f, .8f, .45f, 1.15f, 4f), Is.False);
            Assert.That(QuestMicrophoneInput.ShouldStopForSilence(
                true, 2.4f, 1.2f, .45f, 1.15f, 4f), Is.True);
        }

        [Test]
        public void PcmStreamWaitsForExplicitReplyEndBeforeStopping()
        {
            owner = new GameObject("PCM stream completion test");
            var player = owner.AddComponent<Pcm16StreamAudioPlayer>();
            player.BeginStream();
            player.Enqueue(new short[480], 24000);
            Assert.IsFalse(player.StreamCompleted);
            player.MarkStreamCompleted();
            Assert.IsTrue(player.StreamCompleted);
        }

        [Test]
        public void PcmStreamUsesStartupBufferButShortCompletedRepliesCanPlay()
        {
            Assert.IsFalse(Pcm16StreamAudioPlayer.ShouldStartPlayback(1200, 24000, false, .12f));
            Assert.IsTrue(Pcm16StreamAudioPlayer.ShouldStartPlayback(2880, 24000, false, .12f));
            Assert.IsTrue(Pcm16StreamAudioPlayer.ShouldStartPlayback(1200, 24000, true, .12f));
        }

        [Test]
        public void PcmStreamGenerationChangesAcrossCancellationAndRestart()
        {
            owner = new GameObject("PCM generation test");
            var player = owner.AddComponent<Pcm16StreamAudioPlayer>();

            var first = player.BeginStream();
            player.StopAndClear();
            var second = player.BeginStream();

            Assert.That(second, Is.GreaterThan(first));
        }
        [Test]
        public void VoiceActivationRmsSeparatesSpeechFromSilence()
        {
            var silence = new System.Collections.Generic.List<float> { 0f, .001f, -.001f, 0f };
            var speech = new System.Collections.Generic.List<float> { .1f, -.1f, .1f, -.1f };

            Assert.That(QuestMicrophoneInput.CalculateRms(silence, silence.Count), Is.LessThan(.008f));
            Assert.That(QuestMicrophoneInput.CalculateRms(speech, speech.Count), Is.GreaterThan(.08f));
        }

        [Test]
        public void ResponseWatchdogDistinguishesNoEventFromStalledStream()
        {
            Assert.That(ConversationController.ShouldTimeoutResponse(
                true, false, 34f, 0f, -1f, 35f, 30f, out _), Is.False);
            Assert.That(ConversationController.ShouldTimeoutResponse(
                true, false, 35f, 0f, -1f, 35f, 30f, out var firstCode), Is.True);
            Assert.That(firstCode, Is.EqualTo("response_first_event_timeout"));

            Assert.That(ConversationController.ShouldTimeoutResponse(
                true, false, 39f, 0f, 10f, 35f, 30f, out _), Is.False);
            Assert.That(ConversationController.ShouldTimeoutResponse(
                true, false, 40f, 0f, 10f, 35f, 30f, out var stallCode), Is.True);
            Assert.That(stallCode, Is.EqualTo("response_event_stall_timeout"));
        }

        [Test]
        public void ResponseWatchdogStopsAfterReplyEndOrCancellation()
        {
            Assert.That(ConversationController.ShouldTimeoutResponse(
                true, true, 100f, 0f, -1f, 35f, 30f, out _), Is.False);
            Assert.That(ConversationController.ShouldTimeoutResponse(
                false, false, 100f, 0f, -1f, 35f, 30f, out _), Is.False);
        }

        [Test]
        public void EmptyReplyEndBecomesVisibleErrorInsteadOfSilentSuccess()
        {
            owner = new GameObject("Empty backend reply test");
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);
            controller.StartConversation("hello");

            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.Thinking,
                TurnId = controller.TurnId
            });
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.ReplyEnd,
                TurnId = controller.TurnId,
                TextSent = false,
                AudioSent = false
            });

            Assert.That(controller.State, Is.EqualTo(ConversationState.Error));
            Assert.That(controller.LastErrorCode, Is.EqualTo("empty_backend_reply"));
            Assert.That(transport.InterruptCount, Is.EqualTo(1));
        }

        [Test]
        public void ExpressionMappingIsEmotionSpecificAndBounded()
        {
            Assert.That(
                AvatarConversationPresenter.GetExpressionWeight("smile", "happy", .5f),
                Is.EqualTo(19f).Within(.001f));
            Assert.That(
                AvatarConversationPresenter.GetExpressionWeight("smile", "sad", 1f),
                Is.EqualTo(0f));
            Assert.That(
                AvatarConversationPresenter.GetExpressionWeight("照れ", "shy", 2f),
                Is.EqualTo(26.6f).Within(.001f));
            Assert.That(
                AvatarConversationPresenter.GetExpressionWeight("笑い", "happy", 1f),
                Is.EqualTo(38f).Within(.001f));
        }

        [Test]
        public void ManualExpressionNamesNormalizeToSafePresets()
        {
            Assert.That(AvatarConversationPresenter.NormalizeExpression("happy"), Is.EqualTo("happy"));
            Assert.That(AvatarConversationPresenter.NormalizeExpression("unknown"), Is.EqualTo("neutral"));
            Assert.That(AvatarConversationPresenter.NormalizeExpression(""), Is.EqualTo("neutral"));
        }
        private sealed class RecordingVoiceTransport : MonoBehaviour, IConversationTransport
        {
            public event Action<ConversationEvent> EventReceived;

            public readonly System.Collections.Generic.List<string> Calls =
                new System.Collections.Generic.List<string>();

            public bool Connected = true;
            public string StartedTurnId;
            public string ChunkTurnId;
            public string EndedTurnId;
            public byte[] LastChunk;
            public int InterruptCount;
            public int AudioStartCount;
            public string InteractionEventId = string.Empty;
            public string LastInteractionName = string.Empty;
            public bool IsConnected => Connected;
            public string Status => Connected ? "connected" : "offline";

            public void StartTurn(string turnId, string userText)
            {
            }

            public void Interrupt(string turnId)
            {
                InterruptCount++;
            }

            public bool BeginAudioTurn(string turnId)
            {
                Calls.Add("start");
                AudioStartCount++;
                StartedTurnId = turnId;
                return true;
            }

            public bool QueueAudioChunk(string turnId, byte[] pcm16)
            {
                Calls.Add("chunk");
                ChunkTurnId = turnId;
                LastChunk = pcm16;
                return true;
            }

            public bool EndAudioTurn(string turnId)
            {
                Calls.Add("end");
                EndedTurnId = turnId;
                return true;
            }

            public string SendInteraction(
                string interactionName,
                string phase,
                float strength,
                int durationMs = 0,
                string hand = "none")
            {
                LastInteractionName = interactionName;
                return InteractionEventId;
            }

            public void Raise(ConversationEvent message)
            {
                EventReceived?.Invoke(message);
            }
        }
    }
}
#endif
