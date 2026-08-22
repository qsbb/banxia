#if UNITY_EDITOR
using NUnit.Framework;

namespace QuestMmdPlayer.Tests
{
    public sealed class Pcm16CaptureUtilityTests
    {
        [Test]
        public void FloatSamplesEncodeAsLittleEndianPcm16()
        {
            var encoded = Pcm16CaptureUtility.ResampleAndEncode(
                new[] { -1f, 0f, 1f },
                16000,
                16000);

            CollectionAssert.AreEqual(
                new byte[] { 0x00, 0x80, 0x00, 0x00, 0xff, 0x7f },
                encoded);
        }

        [Test]
        public void EightyMillisecondsAtFortyEightKhzBecomesValidSixteenKhzChunk()
        {
            var sourceFrames = Pcm16CaptureUtility.FramesForDuration(48000, 80);
            var source = new float[sourceFrames];
            for (var index = 0; index < source.Length; index++)
            {
                source[index] = index / (float)source.Length;
            }

            var encoded = Pcm16CaptureUtility.ResampleAndEncode(source, 48000, 16000);

            Assert.AreEqual(3840, sourceFrames);
            Assert.AreEqual(1280 * 2, encoded.Length);
            Assert.AreEqual(0, encoded.Length & 1);
        }

        [Test]
        public void OffsetEncodingMatchesTheSelectedCaptureWindow()
        {
            var source = new[] { -1f, -0.5f, 0f, 0.5f, 1f };

            var encoded = Pcm16CaptureUtility.ResampleAndEncode(
                source,
                1,
                3,
                16000,
                16000);

            CollectionAssert.AreEqual(
                new byte[] { 0x00, 0xc0, 0x00, 0x00, 0x00, 0x40 },
                encoded);
        }

        [Test]
        public void VoiceGateCalibratesThenRequiresSustainedSpeech()
        {
            var gate = new VoiceActivityGate(.008f, .024f, .16f, .24f);

            Assert.IsFalse(gate.Observe(.004f, .08f, true));
            Assert.IsFalse(gate.Observe(.005f, .08f, true));
            Assert.IsFalse(gate.Observe(.004f, .08f, true));
            Assert.IsFalse(gate.Observe(.06f, .08f, true));
            Assert.IsTrue(gate.Observe(.06f, .08f, true));
        }

        [Test]
        public void VoiceGateCannotActivateWhileConversationOwnsAudio()
        {
            var gate = new VoiceActivityGate(.008f, .024f, .16f, 0f);

            Assert.IsFalse(gate.Observe(.08f, .2f, false));
            Assert.That(gate.ActivationProgress, Is.EqualTo(0f));
        }

        [TestCase(ConversationState.Speaking, true, false)]
        [TestCase(ConversationState.Thinking, true, true)]
        [TestCase(ConversationState.Listening, true, true)]
        [TestCase(ConversationState.Idle, true, true)]
        [TestCase(ConversationState.Idle, false, false)]
        public void AutomaticVadCannotBargeInOnTheAppsOwnTts(
            ConversationState state,
            bool canStartVoiceInput,
            bool expected)
        {
            Assert.That(
                QuestMicrophoneInput.ShouldAllowAutomaticVoiceActivation(
                    state,
                    canStartVoiceInput),
                Is.EqualTo(expected));
        }

        [Test]
        public void AutomaticVadAllowsUserVoiceAboveAudibleTts()
        {
            Assert.IsTrue(
                QuestMicrophoneInput.ShouldAllowAutomaticVoiceActivation(
                    ConversationState.Speaking,
                    true,
                    microphoneRms: .12f,
                    playbackRms: .02f,
                    playbackMultiplier: 2.25f,
                    minimumRms: .018f));
        }

        [Test]
        public void AutomaticVadRejectsMicrophoneSignalThatMatchesAudibleTts()
        {
            Assert.IsFalse(
                QuestMicrophoneInput.ShouldAllowAutomaticVoiceActivation(
                    ConversationState.Speaking,
                    true,
                    microphoneRms: .04f,
                    playbackRms: .02f,
                    playbackMultiplier: 2.25f,
                    minimumRms: .018f));
        }

        [Test]
        public void VoiceGateKeepsShortCommandSyllablesAcrossOneQuietChunk()
        {
            var gate = new VoiceActivityGate(.006f, .018f, .12f, 0f);

            Assert.IsFalse(gate.Observe(.02f, .08f, true));
            Assert.IsFalse(gate.Observe(.002f, .08f, true));
            Assert.IsTrue(gate.Observe(.02f, .08f, true));
        }

        [Test]
        public void VoiceGateLetsAnIsolatedSpikeExpire()
        {
            var gate = new VoiceActivityGate(.006f, .018f, .12f, 0f);

            Assert.IsFalse(gate.Observe(.02f, .08f, true));
            Assert.IsFalse(gate.Observe(.002f, .08f, true));
            Assert.IsFalse(gate.Observe(.002f, .08f, true));
            Assert.IsFalse(gate.Observe(.02f, .08f, true));
        }

        [TestCase(2559, 1280, false)]
        [TestCase(2560, 1280, true)]
        [TestCase(3840, 1920, true)]
        public void CaptureBacklogDiagnosticRequiresTwoChunks(
            int pendingFrames,
            int chunkFrames,
            bool expected)
        {
            Assert.That(
                QuestMicrophoneInput.ShouldReportCaptureBacklog(pendingFrames, chunkFrames),
                Is.EqualTo(expected));
        }
    }
}
#endif
