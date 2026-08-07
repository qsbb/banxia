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
        public void VoiceInputCannotImplicitlyInterruptAReply()
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
            Assert.IsFalse(controller.CanStartVoiceInput);
            Assert.IsFalse(controller.BeginVoiceInput());
            Assert.AreEqual(0, transport.InterruptCount);
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
        public void VoiceActivationRmsSeparatesSpeechFromSilence()
        {
            var silence = new System.Collections.Generic.List<float> { 0f, .001f, -.001f, 0f };
            var speech = new System.Collections.Generic.List<float> { .1f, -.1f, .1f, -.1f };

            Assert.That(QuestMicrophoneInput.CalculateRms(silence, silence.Count), Is.LessThan(.008f));
            Assert.That(QuestMicrophoneInput.CalculateRms(speech, speech.Count), Is.GreaterThan(.08f));
        }

        [Test]
        public void ExpressionMappingIsEmotionSpecificAndBounded()
        {
            Assert.That(
                AvatarConversationPresenter.GetExpressionWeight("smile", "happy", .5f),
                Is.EqualTo(31f).Within(.001f));
            Assert.That(
                AvatarConversationPresenter.GetExpressionWeight("smile", "sad", 1f),
                Is.EqualTo(0f));
            Assert.That(
                AvatarConversationPresenter.GetExpressionWeight("照れ", "shy", 2f),
                Is.EqualTo(43.4f).Within(.001f));
            Assert.That(
                AvatarConversationPresenter.GetExpressionWeight("笑い", "happy", 1f),
                Is.EqualTo(62f).Within(.001f));
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
