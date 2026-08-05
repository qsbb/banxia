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
