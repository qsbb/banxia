#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UMT;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class VoiceConversationControllerTests
    {
        private GameObject owner;
        private Mesh generatedMesh;

        [TearDown]
        public void TearDown()
        {
            if (owner != null)
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
            if (generatedMesh != null)
            {
                UnityEngine.Object.DestroyImmediate(generatedMesh);
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
        public void ExplicitActionTextStillStartsTheBackendTurn()
        {
            owner = new GameObject("Action text transport test");
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);

            controller.StartConversation("请挥手");

            Assert.AreEqual(1, transport.TextStartCount);
            Assert.AreEqual(controller.TurnId, transport.StartedTextTurnId);
            Assert.AreEqual("请挥手", transport.LastText);
            Assert.AreEqual(ConversationState.Listening, controller.State);
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
        public void BargeInWritesStructuredVoiceInterruptionLog()
        {
            owner = new GameObject("Voice interruption diagnostics test");
            var diagnostics = owner.AddComponent<RuntimeDebugLog>();
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);
            controller.StartConversation("hello");

            Assert.IsTrue(controller.BeginVoiceInput());

            Assert.That(diagnostics.GetRecentText(30), Does.Contain("Voice interrupted"));
            Assert.That(diagnostics.GetRecentText(30), Does.Contain("刚刚又被打断了"));
            Assert.That(diagnostics.GetRecentText(30), Does.Contain("reason=barge_in"));
            Assert.That(diagnostics.GetRecentTimelineText(30), Does.Contain("voice_interrupted_barge_in"));
        }

        [Test]
        public void AutomaticCaptureIsDiscardedOnlyDuringTtsWithoutExplicitRecording()
        {
            Assert.IsTrue(QuestMicrophoneInput.ShouldDiscardUnrecordedCapture(
                ConversationState.Speaking,
                false));
            Assert.IsFalse(QuestMicrophoneInput.ShouldDiscardUnrecordedCapture(
                ConversationState.Speaking,
                true));
            Assert.IsFalse(QuestMicrophoneInput.ShouldDiscardUnrecordedCapture(
                ConversationState.Thinking,
                false));
        }

        [Test]
        public void PipelineStopWritesStructuredVoiceInterruptionLog()
        {
            owner = new GameObject("Pipeline interruption diagnostics test");
            var diagnostics = owner.AddComponent<RuntimeDebugLog>();
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);
            Assert.IsTrue(controller.BeginVoiceInput());
            Assert.IsTrue(controller.EndVoiceInput());

            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.Error,
                TurnId = controller.TurnId,
                ErrorCode = "astrbot_pipeline_event_stopped"
            });

            Assert.That(diagnostics.GetRecentText(30), Does.Contain("Voice interrupted"));
            Assert.That(diagnostics.GetRecentText(30), Does.Contain("reason=pipeline_stopped"));
            Assert.That(diagnostics.GetRecentTimelineText(30), Does.Contain("voice_interrupted_pipeline_stopped"));
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
        public void AudioUploadBatchKeepsChunkOrderWhenMergingOwnedBuffers()
        {
            var chunks = new System.Collections.Generic.Queue<byte[]>();
            chunks.Enqueue(new byte[] { 1, 2 });
            chunks.Enqueue(new byte[] { 3, 4 });

            var batch = AstrBotBridge.DequeueAudioBatch(chunks, 4);

            Assert.That(batch, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            Assert.That(chunks, Is.Empty);
        }

        [TestCase("turn/start", 0, true, 1, true)]
        [TestCase("audio/end", 20, true, 1, true)]
        [TestCase("audio/chunk", 0, true, 1, true)]
        [TestCase("audio/chunk", 1, false, 1, true)]
        [TestCase("audio/chunk", 1, true, 249, false)]
        [TestCase("audio/chunk", 1, true, 250, true)]
        public void AudioUploadDiagnosticsKeepOnlyFirstSlowFailedAndTerminalRequests(
            string endpoint,
            int sequence,
            bool succeeded,
            int elapsedMs,
            bool expected)
        {
            Assert.That(
                AstrBotBridge.ShouldRecordAudioRequestStage(
                    endpoint,
                    sequence,
                    succeeded,
                    elapsedMs),
                Is.EqualTo(expected));
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
        public void PcmTimelineClockStartsAtAudibleBoundaryAndResetsWithStream()
        {
            owner = new GameObject("PCM audible timeline clock test");
            var player = owner.AddComponent<Pcm16StreamAudioPlayer>();
            var privateInstance = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            typeof(Pcm16StreamAudioPlayer).GetMethod("Awake", privateInstance)?.Invoke(player, null);
            player.BeginStream();
            player.Enqueue(new short[] { 1200, -1200, 600, -600 }, 24000);
            typeof(Pcm16StreamAudioPlayer)
                .GetMethod("ReadAudio", privateInstance)
                ?.Invoke(player, new object[] { new float[1024] });
            var startedAt = (double)typeof(Pcm16StreamAudioPlayer)
                .GetField("audiblePlaybackStartedAtDspTime", privateInstance)
                ?.GetValue(player);

            Assert.That(startedAt, Is.GreaterThanOrEqualTo(0d));
            Assert.That(player.AudiblePlaybackSeconds, Is.GreaterThanOrEqualTo(0f));
            player.StopAndClear();
            Assert.That((double)typeof(Pcm16StreamAudioPlayer)
                .GetField("audiblePlaybackStartedAtDspTime", privateInstance)
                ?.GetValue(player), Is.EqualTo(-1d));
            Assert.That(player.AudiblePlaybackSeconds, Is.Zero);
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
        public void AvatarIntentStartsBackendWatchdogButRequiresTerminalEvent()
        {
            Assert.That(ConversationController.ShouldTimeoutResponse(
                true, false, true, false, 34f, 0f, .1f, -1f, 35f, 30f, out _), Is.False);
            Assert.That(ConversationController.ShouldTimeoutResponse(
                true, false, true, false, 35f, 0f, .1f, -1f, 35f, 30f, out var terminalCode), Is.True);
            Assert.That(terminalCode, Is.EqualTo("response_terminal_event_missing_timeout"));
            Assert.That(ConversationController.ShouldTimeoutResponse(
                true, false, true, true, 100f, 0f, .1f, -1f, 35f, 30f, out _), Is.False);
        }

        [Test]
        public void ReplyProgressAfterAvatarIntentStillUsesStallTimeout()
        {
            Assert.That(ConversationController.ShouldTimeoutResponse(
                true, false, true, false, 34f, 0f, .1f, 5f, 35f, 30f, out _), Is.False);
            Assert.That(ConversationController.ShouldTimeoutResponse(
                true, false, true, false, 35f, 0f, .1f, 5f, 35f, 30f, out var stallCode), Is.True);
            Assert.That(stallCode, Is.EqualTo("response_event_stall_timeout"));
        }

        [Test]
        public void StaleAvatarIntentDoesNotRefreshCurrentTurnWatchdog()
        {
            owner = new GameObject("Stale avatar intent watchdog test");
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);
            controller.StartConversation("hello");

            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AvatarIntent,
                TurnId = "turn-stale",
                Gesture = "wave"
            });

            var flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            Assert.That((bool)typeof(ConversationController)
                .GetField("validAvatarIntentReceived", flags)?.GetValue(controller), Is.False);
            Assert.That((float)typeof(ConversationController)
                .GetField("firstBackendEventAt", flags)?.GetValue(controller), Is.EqualTo(-1f));

            var currentTurn = controller.TurnId;
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AvatarIntent,
                TurnId = currentTurn,
                Gesture = "wave"
            });
            var firstAt = (float)typeof(ConversationController)
                .GetField("firstBackendEventAt", flags)?.GetValue(controller);

            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AvatarIntent,
                TurnId = "turn-stale",
                Gesture = "bow"
            });

            Assert.That((bool)typeof(ConversationController)
                .GetField("validAvatarIntentReceived", flags)?.GetValue(controller), Is.True);
            Assert.That((float)typeof(ConversationController)
                .GetField("firstBackendEventAt", flags)?.GetValue(controller), Is.EqualTo(firstAt));
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
        public void ActionOnlyReplyEndIsAcceptedAfterAvatarIntent()
        {
            Assert.IsTrue(ConversationController.AcceptActionOnlyReplyEnd(true, string.Empty, 0));
            Assert.IsTrue(ConversationController.AcceptActionOnlyReplyEnd(true, "   ", 0));
            Assert.IsFalse(ConversationController.AcceptActionOnlyReplyEnd(false, string.Empty, 0));
            Assert.IsFalse(ConversationController.AcceptActionOnlyReplyEnd(true, string.Empty, 1));
            Assert.IsFalse(ConversationController.AcceptActionOnlyReplyEnd(true, "done", 0));
        }

        [Test]
        public void AcceptedBackendIntentCompletesAnActionOnlyVoiceTurnExactlyOnce()
        {
            owner = new GameObject("Accepted backend action-only test");
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            var avatar = owner.AddComponent<AvatarController>();
            avatar.Initialize(owner.transform);
            controller.SetTransport(transport);
            controller.Bind(avatar, null);

            var actionCount = 0;
            avatar.ActionChanged += _ => actionCount++;
            controller.StartConversation("请挥手");
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AvatarIntent,
                TurnId = controller.TurnId,
                Emotion = "happy",
                Gesture = "wave",
                LookAt = "user"
            });
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.ReplyEnd,
                TurnId = controller.TurnId,
                TextSent = false,
                AudioSent = false
            });
            typeof(ConversationController)
                .GetMethod(
                    "Update",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(controller, null);

            Assert.That(controller.State, Is.EqualTo(ConversationState.Idle));
            Assert.That(controller.LastErrorCode, Is.Empty);
            Assert.That(avatar.CurrentAction, Is.EqualTo("wave"));
            Assert.That(avatar.CurrentActionSource, Is.EqualTo(AvatarActionSource.Backend));
            Assert.That(actionCount, Is.EqualTo(1));
            Assert.That(transport.InterruptCount, Is.EqualTo(0));
        }

        [Test]
        public void RejectedBackendIntentFallsBackOnceInsteadOfFailingAnEmptyReply()
        {
            owner = new GameObject("Rejected backend action fallback test");
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            var avatar = owner.AddComponent<AvatarController>();
            avatar.Initialize(owner.transform);
            controller.SetTransport(transport);
            controller.Bind(avatar, null);

            var presenter = owner.GetComponent<AvatarConversationPresenter>();
            Assert.That(presenter.ApplyIntent("happy", "wave", "user"), Is.True);

            var fallbackActionCount = 0;
            avatar.ActionChanged += _ => fallbackActionCount++;
            controller.StartConversation("wave");
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AvatarIntent,
                TurnId = controller.TurnId,
                Emotion = "happy",
                Gesture = "wave",
                LookAt = "user"
            });
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.ReplyEnd,
                TurnId = controller.TurnId,
                TextSent = false,
                AudioSent = false
            });

            Assert.That(controller.State, Is.Not.EqualTo(ConversationState.Error));
            Assert.That(controller.LastErrorCode, Is.Empty);
            Assert.That(avatar.CurrentAction, Is.EqualTo("wave"));
            Assert.That(avatar.CurrentActionSource, Is.EqualTo(AvatarActionSource.Manual));
            Assert.That(fallbackActionCount, Is.EqualTo(1));
            Assert.That(transport.InterruptCount, Is.EqualTo(0));
        }

        [Test]
        public void FastActionNoActionSuppressesLegacyKeywordFallback()
        {
            owner = new GameObject("Fast action no-action test");
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            var avatar = owner.AddComponent<AvatarController>();
            avatar.Initialize(owner.transform);
            controller.SetTransport(transport);
            controller.Bind(avatar, null);

            var actions = new System.Collections.Generic.List<string>();
            avatar.ActionChanged += action => actions.Add(action);
            controller.StartConversation("请挥手");
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AvatarIntent,
                TurnId = controller.TurnId,
                Emotion = "neutral",
                Gesture = "talk",
                LookAt = "user",
                ReasonCode = "fast_action_no_action"
            });
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.ReplyTextDelta,
                TurnId = controller.TurnId,
                Text = "这次先不挥手。"
            });
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.ReplyEnd,
                TurnId = controller.TurnId,
                TextSent = true,
                AudioSent = false
            });

            Assert.That(actions, Does.Not.Contain("wave"));
            Assert.That(avatar.CurrentAction, Is.Not.EqualTo("wave"));
            Assert.That(transport.InterruptCount, Is.EqualTo(0));
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
        public void ForestBerryMorphCatalogOnlyBindsCanonicalVowels()
        {
            owner = new GameObject("Forest berry mouth classification test");
            var renderer = owner.AddComponent<SkinnedMeshRenderer>();
            generatedMesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 }
            };
            var delta = new Vector3[3];
            var names = new[]
            {
                "あ", "あ2", "あ3", "あ4", "い", "い2", "い3", "い4",
                "う", "え", "え2", "お", "ん", "抿嘴", "口横広げ", "口上",
                "口下", "口角上げ", "口角下げ", "笑い"
            };
            for (var index = 0; index < names.Length; index++)
            {
                generatedMesh.AddBlendShapeFrame(names[index], 100f, delta, delta, delta);
            }
            renderer.sharedMesh = generatedMesh;
            var avatar = owner.AddComponent<AvatarController>();
            avatar.Initialize(owner.transform);
            var presenter = owner.AddComponent<AvatarConversationPresenter>();

            presenter.Bind(avatar, null, null);

            Assert.That(presenter.MatchedVisemeCount, Is.EqualTo(5));
            Assert.That(owner.GetComponent<AvatarMouthLatePass>(), Is.Not.Null);
            Assert.That(AvatarConversationPresenter.GetVisemeGroup("あ2"), Is.EqualTo(-1));
            Assert.That(AvatarConversationPresenter.GetVisemeGroup("口上"), Is.EqualTo(-1));
            Assert.That(AvatarConversationPresenter.GetVisemeGroup("笑い"), Is.EqualTo(-1));
        }

        [Test]
        public void OptionalRealForestBerryPmxMapsExactlyOneMorphPerCanonicalVowel()
        {
            var pmxPath = Environment.GetEnvironmentVariable(
                "BANXIA_TEST_FOREST_BERRY_PMX");
            if (string.IsNullOrWhiteSpace(pmxPath) || !File.Exists(pmxPath))
            {
                Assert.Ignore(
                    "BANXIA_TEST_FOREST_BERRY_PMX is not configured for this run.");
            }

            PMXModel model = null;
            try
            {
                using (var stream = File.OpenRead(pmxPath))
                {
                    model = PMXReader.Read(stream, true);
                }
                var groups = new HashSet<int>();
                var accepted = 0;
                for (var index = 0; index < model.morphs.Length; index++)
                {
                    var morph = model.morphs[index];
                    if (morph.type != PMXMorph.Type.Vertex)
                    {
                        continue;
                    }
                    var group = AvatarConversationPresenter.GetVisemeGroup(
                        morph.originalName.ToString());
                    if (group < 0)
                    {
                        continue;
                    }
                    accepted++;
                    groups.Add(group);
                }

                Assert.That(accepted, Is.EqualTo(5));
                Assert.That(groups, Is.EquivalentTo(new[] { 0, 1, 2, 3, 4 }));
            }
            finally
            {
                if (model != null)
                {
                    UnityEngine.Object.DestroyImmediate(model);
                }
            }
        }

        [Test]
        public void ForestBerryHappyExpressionPrefersOneAuthoredCalmMorph()
        {
            Assert.That(
                AvatarConversationPresenter.GetExpressionPriority("なごみ", "happy"),
                Is.GreaterThan(AvatarConversationPresenter.GetExpressionPriority("口角上げ", "happy")));
            Assert.That(
                AvatarConversationPresenter.GetExpressionPriority("口角上げ", "happy"),
                Is.GreaterThan(AvatarConversationPresenter.GetExpressionPriority("笑い", "happy")));
            Assert.That(AvatarConversationPresenter.GetExpressionPriority("まばたき", "happy"), Is.Zero);
        }

        [Test]
        public void SpeechMouthLayerRunsAfterVmdAndBeforeTouchExpressions()
        {
            var mouthOrder = ((DefaultExecutionOrder)Attribute.GetCustomAttribute(
                typeof(AvatarMouthLatePass), typeof(DefaultExecutionOrder))).order;
            var vmdOrder = ((DefaultExecutionOrder)Attribute.GetCustomAttribute(
                typeof(VmdActionLibrary), typeof(DefaultExecutionOrder))).order;
            var touchOrder = ((DefaultExecutionOrder)Attribute.GetCustomAttribute(
                typeof(AvatarHumanInteraction), typeof(DefaultExecutionOrder))).order;

            Assert.That(mouthOrder, Is.GreaterThan(vmdOrder));
            Assert.That(mouthOrder, Is.LessThan(touchOrder));
        }

        [Test]
        public void ManualExpressionNamesNormalizeToSafePresets()
        {
            Assert.That(AvatarConversationPresenter.NormalizeExpression("happy"), Is.EqualTo("happy"));
            Assert.That(AvatarConversationPresenter.NormalizeExpression("unknown"), Is.EqualTo("neutral"));
            Assert.That(AvatarConversationPresenter.NormalizeExpression(""), Is.EqualTo("neutral"));
        }

        [Test]
        public void ActionReceiptTrackerRequiresServerActionIdAndEmitsStrictLifecycle()
        {
            var tracker = new AvatarActionReceiptTracker();
            tracker.Reset("turn-1");
            Assert.That(tracker.TryPlan("turn-1", string.Empty, "wave", out _), Is.False);
            Assert.That(tracker.TryPlan("turn-1", "action-1", "wave", out var context), Is.True);

            Assert.That(tracker.TryAdvance(new AvatarActionExecutionUpdate
            {
                Context = context,
                Phase = AvatarActionReceiptPhase.Accepted,
                ReasonCode = "ignored"
            }, out var accepted), Is.True);
            Assert.That(accepted.Action, Is.EqualTo("wave"));
            Assert.That(accepted.ReasonCode, Is.EqualTo("accepted"));
            Assert.That(accepted.ReceiptId, Does.StartWith("receipt-"));

            Assert.That(tracker.TryAdvance(new AvatarActionExecutionUpdate
            {
                Context = context,
                Phase = AvatarActionReceiptPhase.Started,
                ReasonCode = "ignored"
            }, out var started), Is.True);
            Assert.That(started.ReasonCode, Is.EqualTo("started"));
            Assert.That(started.ReceiptId, Is.Not.EqualTo(accepted.ReceiptId));

            Assert.That(tracker.TryAdvance(new AvatarActionExecutionUpdate
            {
                Context = context,
                Phase = AvatarActionReceiptPhase.Completed,
                ReasonCode = "ignored",
                ElapsedMs = 700000
            }, out var completed), Is.True);
            Assert.That(completed.ReasonCode, Is.EqualTo("completed"));
            Assert.That(completed.DurationMs, Is.EqualTo(600000));
            Assert.That(tracker.TryAdvance(new AvatarActionExecutionUpdate
            {
                Context = context,
                Phase = AvatarActionReceiptPhase.Completed
            }, out _), Is.False);
        }

        [Test]
        public void BackendActionReceiptsFollowActualAvatarActionLifecycle()
        {
            owner = new GameObject("Action receipt lifecycle test");
            var avatar = owner.AddComponent<AvatarController>();
            avatar.Initialize(owner.transform);
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);
            controller.Bind(avatar, null);
            controller.StartConversation("wave");

            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AvatarIntent,
                TurnId = controller.TurnId,
                ActionId = "action-wave-1",
                Emotion = "happy",
                Gesture = "wave",
                LookAt = "user",
                DurationMs = 2000
            });

            Assert.That(transport.ActionReceipts, Has.Count.EqualTo(2));
            Assert.That(transport.ActionReceipts[0].Phase, Is.EqualTo(AvatarActionReceiptPhase.Accepted));
            Assert.That(transport.ActionReceipts[1].Phase, Is.EqualTo(AvatarActionReceiptPhase.Started));
            Assert.That(transport.ActionReceipts[0].Action, Is.EqualTo("wave"));
            avatar.PlayActionFromSource("idle", AvatarActionSource.System);
            Assert.That(transport.ActionReceipts, Has.Count.EqualTo(3));
            Assert.That(transport.ActionReceipts[2].Phase, Is.EqualTo(AvatarActionReceiptPhase.Completed));
        }

        [Test]
        public void CrouchWithoutRequiredLegRigIsRejectedAsAssetMissing()
        {
            owner = new GameObject("Missing crouch rig receipt test");
            var avatar = owner.AddComponent<AvatarController>();
            avatar.Initialize(owner.transform);
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);
            controller.Bind(avatar, null);
            controller.StartConversation("下蹲");

            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AvatarIntent,
                TurnId = controller.TurnId,
                ActionId = "action-crouch-missing-rig",
                Gesture = "crouch",
                ActionMethod = "crouch",
                LookAt = "user"
            });

            Assert.That(transport.ActionReceipts, Has.Count.EqualTo(1));
            Assert.That(transport.ActionReceipts[0].Phase, Is.EqualTo(AvatarActionReceiptPhase.Rejected));
            Assert.That(transport.ActionReceipts[0].ReasonCode, Is.EqualTo("asset_missing"));
            Assert.That(avatar.CurrentAction, Is.EqualTo("idle"));
        }

        [Test]
        public void SameTurnCannotStartASecondWholeBodyAction()
        {
            owner = new GameObject("Single whole-body action per turn test");
            var avatar = owner.AddComponent<AvatarController>();
            avatar.Initialize(owner.transform);
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);
            controller.Bind(avatar, null);
            controller.StartConversation("挥手然后鞠躬");
            var turnId = controller.TurnId;

            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AvatarIntent,
                TurnId = turnId,
                ActionId = "action-wave-first",
                Gesture = "wave",
                LookAt = "user"
            });
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AvatarIntent,
                TurnId = turnId,
                ActionId = "action-bow-second",
                Gesture = "bow",
                LookAt = "user"
            });

            Assert.That(avatar.CurrentAction, Is.EqualTo("wave"));
            Assert.That(transport.ActionReceipts, Has.Count.EqualTo(3));
            Assert.That(transport.ActionReceipts[2].Action, Is.EqualTo("bow"));
            Assert.That(transport.ActionReceipts[2].Phase, Is.EqualTo(AvatarActionReceiptPhase.Rejected));
            Assert.That(transport.ActionReceipts[2].ReasonCode, Is.EqualTo("superseded"));
        }

        [Test]
        public void LegacyAvatarIntentStillExecutesWithoutActionReceipt()
        {
            owner = new GameObject("Legacy avatar intent test");
            var avatar = owner.AddComponent<AvatarController>();
            avatar.Initialize(owner.transform);
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);
            controller.Bind(avatar, null);
            controller.StartConversation("wave");

            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AvatarIntent,
                TurnId = controller.TurnId,
                Emotion = "happy",
                Gesture = "wave",
                LookAt = "user"
            });

            Assert.That(avatar.CurrentAction, Is.EqualTo("wave"));
            Assert.That(transport.ActionReceipts, Is.Empty);
        }

        [Test]
        public void UnavailableAvatarRejectsTrackedActionWithoutFalseAcceptance()
        {
            owner = new GameObject("Unavailable avatar action test");
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);
            controller.StartConversation("wave");

            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AvatarIntent,
                TurnId = controller.TurnId,
                ActionId = "action-wave-rejected",
                Gesture = "wave",
                LookAt = "user"
            });

            Assert.That(transport.ActionReceipts, Has.Count.EqualTo(1));
            Assert.That(transport.ActionReceipts[0].Phase, Is.EqualTo(AvatarActionReceiptPhase.Rejected));
            Assert.That(transport.ActionReceipts[0].ReasonCode, Is.EqualTo("invalid_state"));
        }

        [Test]
        public void MotionArbiterRejectionDoesNotEmitFalseAcceptedOrStartedReceipts()
        {
            owner = new GameObject("Motion arbiter rejection receipt test");
            var avatar = owner.AddComponent<AvatarController>();
            avatar.Initialize(owner.transform);
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);
            controller.Bind(avatar, null);
            Assert.That(avatar.PlayActionFromSource("vmd", AvatarActionSource.Imported), Is.True);
            controller.StartConversation("wave");

            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AvatarIntent,
                TurnId = controller.TurnId,
                ActionId = "action-wave-busy",
                Gesture = "wave",
                LookAt = "user"
            });

            Assert.That(transport.ActionReceipts, Has.Count.EqualTo(1));
            Assert.That(transport.ActionReceipts[0].Phase, Is.EqualTo(AvatarActionReceiptPhase.Rejected));
            Assert.That(transport.ActionReceipts[0].ReasonCode, Is.EqualTo("busy"));
            Assert.That(avatar.CurrentAction, Is.EqualTo("vmd"));
        }

        [Test]
        public void UserInterruptReportsTheStartedActionAsInterrupted()
        {
            owner = new GameObject("Action interruption receipt test");
            var avatar = owner.AddComponent<AvatarController>();
            avatar.Initialize(owner.transform);
            var controller = owner.AddComponent<ConversationController>();
            var transport = owner.AddComponent<RecordingVoiceTransport>();
            controller.SetTransport(transport);
            controller.Bind(avatar, null);
            controller.StartConversation("wave");
            transport.Raise(new ConversationEvent
            {
                Type = ConversationEventType.AvatarIntent,
                TurnId = controller.TurnId,
                ActionId = "action-wave-interrupted",
                Gesture = "wave",
                LookAt = "user"
            });

            controller.Interrupt();

            Assert.That(transport.ActionReceipts, Has.Count.EqualTo(3));
            Assert.That(transport.ActionReceipts[2].Phase, Is.EqualTo(AvatarActionReceiptPhase.Interrupted));
            Assert.That(transport.ActionReceipts[2].ReasonCode, Is.EqualTo("user_interrupted"));
            Assert.That(avatar.CurrentAction, Is.EqualTo("idle"));
        }

        [Test]
        public void VmdPlaybackLifecycleDrivesStartedAndCompletedUpdates()
        {
            owner = new GameObject("VMD action receipt lifecycle test");
            var avatar = owner.AddComponent<AvatarController>();
            avatar.Initialize(owner.transform);
            var vmd = owner.AddComponent<VmdActionLibrary>();
            var presenter = owner.AddComponent<AvatarConversationPresenter>();
            presenter.Bind(avatar, null, null);
            var updates = new System.Collections.Generic.List<AvatarActionExecutionUpdate>();
            presenter.ActionExecutionChanged += updates.Add;
            var context = new AvatarActionExecutionContext("turn-1", "action-dance-1", "dance");
            var flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            typeof(AvatarConversationPresenter)
                .GetMethod("ActivateTrackedAction", flags)
                ?.Invoke(presenter, new object[] { context });

            typeof(VmdActionLibrary).GetProperty("PlaybackPhase")
                ?.SetValue(vmd, VmdPlaybackPhase.Playing);
            typeof(AvatarConversationPresenter)
                .GetMethod("HandleVmdPlaybackChanged", flags)
                ?.Invoke(presenter, null);
            typeof(VmdActionLibrary).GetProperty("PlaybackPhase")
                ?.SetValue(vmd, VmdPlaybackPhase.Idle);
            typeof(AvatarConversationPresenter)
                .GetMethod("HandleVmdPlaybackChanged", flags)
                ?.Invoke(presenter, null);

            Assert.That(updates.ConvertAll(update => update.Phase), Is.EqualTo(new[]
            {
                AvatarActionReceiptPhase.Accepted,
                AvatarActionReceiptPhase.Started,
                AvatarActionReceiptPhase.Completed
            }));
        }

        [Test]
        public void SpeechTimelineUsesNaturalVowelGroupsAndTransitionEnvelope()
        {
            Assert.That(AvatarConversationPresenter.GetVisemeGroup("A"), Is.EqualTo(0));
            Assert.That(AvatarConversationPresenter.GetVisemeGroup("vrc.v_ih"), Is.EqualTo(1));
            Assert.That(AvatarConversationPresenter.GetVisemeGroup("\u53e3\u304a"), Is.EqualTo(4));
            Assert.That(AvatarConversationPresenter.GetVisemeGroup("sil"), Is.EqualTo(-1));
            var cue = new SpeechVisemeCue { Symbol = "A", StartMs = 100, EndMs = 200, Weight = .8f };
            Assert.That(AvatarConversationPresenter.SpeechCueEnvelope(cue, 35f), Is.Zero);
            Assert.That(AvatarConversationPresenter.SpeechCueEnvelope(cue, 67.5f), Is.EqualTo(.4f).Within(.001f));
            Assert.That(AvatarConversationPresenter.SpeechCueEnvelope(cue, 150f), Is.EqualTo(.8f).Within(.001f));
            Assert.That(AvatarConversationPresenter.SpeechCueEnvelope(cue, 232.5f), Is.EqualTo(.4f).Within(.001f));
            Assert.That(AvatarConversationPresenter.SpeechCueEnvelope(cue, 265f), Is.Zero);
        }

        [Test]
        public void SpeechVisemeCrossfadeKeepsCombinedMorphWeightBounded()
        {
            Assert.That(
                AvatarConversationPresenter.NormalizeVisemeInfluence(.8f, 1.6f),
                Is.EqualTo(.5f).Within(.001f));
            Assert.That(
                AvatarConversationPresenter.NormalizeVisemeInfluence(.8f, .8f),
                Is.EqualTo(.8f).Within(.001f));
            Assert.That(
                AvatarConversationPresenter.NormalizeVisemeInfluence(-1f, 2f),
                Is.Zero);
        }

        [Test]
        public void SpeechVisemeCrossfadeMovesBothVowelsWithoutInstantSwap()
        {
            var outgoing = AvatarConversationPresenter.SmoothVisemeInfluence(
                1f, 0f, .016f, 14f);
            var incoming = AvatarConversationPresenter.SmoothVisemeInfluence(
                0f, 1f, .016f, 14f);

            Assert.That(outgoing, Is.GreaterThan(0f).And.LessThan(1f));
            Assert.That(incoming, Is.GreaterThan(0f).And.LessThan(1f));
            Assert.That(outgoing + incoming, Is.EqualTo(1f).Within(.001f));
        }

        [Test]
        public void ClearedTimelineReleasesLastVowelWithoutJumpingToFallback()
        {
            owner = new GameObject("Mouth release test");
            var avatarObject = new GameObject("Mouth release avatar");
            avatarObject.transform.SetParent(owner.transform, false);
            var renderer = CreateMorphRenderer(avatarObject, "あ", "い");
            renderer.SetBlendShapeWeight(0, 3f);
            renderer.SetBlendShapeWeight(1, 7f);
            var avatar = avatarObject.AddComponent<AvatarController>();
            avatar.Initialize(avatarObject.transform);
            var presenter = owner.AddComponent<AvatarConversationPresenter>();
            presenter.Bind(avatar, null, null);
            presenter.SetSpeechTimeline(new[]
            {
                new SpeechVisemeCue
                {
                    Symbol = "I",
                    StartMs = 0,
                    EndMs = 100,
                    Weight = 1f
                }
            });
            presenter.ClearSpeechTimeline();

            var flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            var influences = (float[])typeof(AvatarConversationPresenter)
                .GetField("visemeInfluences", flags)?.GetValue(presenter);
            Assert.That(influences, Is.Not.Null);
            influences[1] = 1f;
            typeof(AvatarConversationPresenter)
                .GetField("smoothedMouthAmount", flags)?.SetValue(presenter, .8f);
            typeof(AvatarConversationPresenter)
                .GetField("mouthWasActive", flags)?.SetValue(presenter, true);
            var applyMouth = typeof(AvatarConversationPresenter).GetMethod(
                "ApplyMouth",
                flags,
                null,
                new[] { typeof(float), typeof(float) },
                null);
            Assert.That(applyMouth, Is.Not.Null);

            applyMouth.Invoke(presenter, new object[] { 0f, .02f });

            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(3f).Within(.001f));
            Assert.That(renderer.GetBlendShapeWeight(1), Is.GreaterThan(7f));

            applyMouth.Invoke(presenter, new object[] { 0f, 1f });

            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(3f).Within(.001f));
            Assert.That(renderer.GetBlendShapeWeight(1), Is.EqualTo(7f).Within(.001f));
            Assert.That(influences[0], Is.Zero);
            Assert.That(influences[1], Is.Zero);
        }

        [Test]
        public void FreshVmdMorphSurvivesSpeechReleaseAfterActionSwitch()
        {
            owner = new GameObject("VMD mouth action switch test");
            var avatarObject = new GameObject("VMD mouth avatar");
            avatarObject.transform.SetParent(owner.transform, false);
            var renderer = CreateMorphRenderer(avatarObject, "あ");
            renderer.SetBlendShapeWeight(0, 5f);
            var avatar = avatarObject.AddComponent<AvatarController>();
            avatar.Initialize(avatarObject.transform);
            var presenter = owner.AddComponent<AvatarConversationPresenter>();
            presenter.Bind(avatar, null, null);
            var flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            var applyMouth = typeof(AvatarConversationPresenter).GetMethod(
                "ApplyMouth",
                flags,
                null,
                new[] { typeof(float), typeof(float) },
                null);
            Assert.That(applyMouth, Is.Not.Null);

            renderer.SetBlendShapeWeight(0, 42f);
            applyMouth.Invoke(presenter, new object[] { 1f, .1f });
            Assert.That(renderer.GetBlendShapeWeight(0), Is.GreaterThan(42f));

            renderer.SetBlendShapeWeight(0, 55f);
            applyMouth.Invoke(presenter, new object[] { 0f, 1f });

            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(55f).Within(.001f));
        }

        [Test]
        public void ModelRebindRestoresOldMouthAndClearsSpeechBlendState()
        {
            owner = new GameObject("Mouth model rebind test");
            var firstObject = new GameObject("First mouth avatar");
            var secondObject = new GameObject("Second mouth avatar");
            firstObject.transform.SetParent(owner.transform, false);
            secondObject.transform.SetParent(owner.transform, false);
            var firstRenderer = CreateMorphRenderer(firstObject, "あ");
            var secondRenderer = secondObject.AddComponent<SkinnedMeshRenderer>();
            secondRenderer.sharedMesh = generatedMesh;
            firstRenderer.SetBlendShapeWeight(0, 12f);
            secondRenderer.SetBlendShapeWeight(0, 4f);
            var firstAvatar = firstObject.AddComponent<AvatarController>();
            var secondAvatar = secondObject.AddComponent<AvatarController>();
            firstAvatar.Initialize(firstObject.transform);
            secondAvatar.Initialize(secondObject.transform);
            var presenter = owner.AddComponent<AvatarConversationPresenter>();
            presenter.Bind(firstAvatar, null, null);
            var flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            var applyMouth = typeof(AvatarConversationPresenter).GetMethod(
                "ApplyMouth",
                flags,
                null,
                new[] { typeof(float), typeof(float) },
                null);
            Assert.That(applyMouth, Is.Not.Null);
            applyMouth.Invoke(presenter, new object[] { 1f, .1f });
            Assert.That(firstRenderer.GetBlendShapeWeight(0), Is.GreaterThan(12f));

            presenter.Bind(secondAvatar, null, null);

            Assert.That(firstRenderer.GetBlendShapeWeight(0), Is.EqualTo(12f).Within(.001f));
            Assert.That(secondRenderer.GetBlendShapeWeight(0), Is.EqualTo(4f).Within(.001f));
            Assert.That(presenter.SmoothedMouthAmount, Is.Zero);
            var influences = (float[])typeof(AvatarConversationPresenter)
                .GetField("visemeInfluences", flags)?.GetValue(presenter);
            Assert.That(influences, Is.All.Zero);
        }

        [Test]
        public void SpeechAndExpressionLayersPreserveFreshAuthoredMorphWeights()
        {
            var authored = AvatarConversationPresenter.ResolveMorphLayerBaseWeight(
                35f,
                0f,
                0f,
                false,
                5f);
            var composite = AvatarConversationPresenter.ComposeMorphLayerWeight(authored, 10f);
            var restored = AvatarConversationPresenter.ResolveMorphLayerBaseWeight(
                composite,
                composite,
                composite - authored,
                true,
                5f);
            var freshVmdFrame = AvatarConversationPresenter.ResolveMorphLayerBaseWeight(
                62f,
                composite,
                composite - authored,
                true,
                5f);

            Assert.That(authored, Is.EqualTo(35f).Within(.001f));
            Assert.That(composite, Is.EqualTo(45f).Within(.001f));
            Assert.That(restored, Is.EqualTo(35f).Within(.001f));
            Assert.That(freshVmdFrame, Is.EqualTo(62f).Within(.001f));

            var saturated = AvatarConversationPresenter.ComposeMorphLayerWeight(90f, 20f);
            Assert.That(saturated, Is.EqualTo(100f));
            Assert.That(
                AvatarConversationPresenter.ResolveMorphLayerBaseWeight(
                    saturated,
                    saturated,
                    saturated - 90f,
                    true,
                    0f),
                Is.EqualTo(90f).Within(.001f));
        }

        private SkinnedMeshRenderer CreateMorphRenderer(
            GameObject target,
            params string[] morphNames)
        {
            generatedMesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 }
            };
            var delta = new Vector3[3];
            foreach (var morphName in morphNames)
            {
                generatedMesh.AddBlendShapeFrame(
                    morphName,
                    100f,
                    delta,
                    delta,
                    delta);
            }
            var renderer = target.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = generatedMesh;
            return renderer;
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
            public int TextStartCount;
            public string StartedTextTurnId = string.Empty;
            public string LastText = string.Empty;
            public string InteractionEventId = string.Empty;
            public string LastInteractionName = string.Empty;
            public readonly System.Collections.Generic.List<AvatarActionReceipt> ActionReceipts =
                new System.Collections.Generic.List<AvatarActionReceipt>();
            public bool IsConnected => Connected;
            public string Status => Connected ? "connected" : "offline";

            public void StartTurn(string turnId, string userText)
            {
                TextStartCount++;
                StartedTextTurnId = turnId;
                LastText = userText;
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

            public bool SendActionResult(AvatarActionReceipt receipt)
            {
                if (receipt == null) return false;
                ActionReceipts.Add(receipt);
                return true;
            }

            public void Raise(ConversationEvent message)
            {
                EventReceived?.Invoke(message);
            }
        }
    }
}
#endif
