#if UNITY_EDITOR
using NUnit.Framework;

namespace QuestMmdPlayer.Tests
{
    public sealed class PlaybackReceiptTransportTests
    {
        [Test]
        public void ReceiptClampsDurationsAndBoundsReason()
        {
            var receipt = new PlaybackReceipt(
                "turn", "speech", PlaybackReceiptKind.Progress,
                -1, 9000000, -5, new string('x', 65));

            Assert.AreEqual(0, receipt.PlayedMs);
            Assert.AreEqual(3600000, receipt.BufferedMs);
            Assert.AreEqual(0, receipt.UnderflowCount);
            Assert.AreEqual(64, receipt.ReasonCode.Length);
        }

        [Test]
        public void ReceiptRetainsOnlyBoundedPlaybackFacts()
        {
            var receipt = new PlaybackReceipt(
                "turn-a", "speech-a", PlaybackReceiptKind.Interrupted,
                320, 80, 2, "local_stop");

            Assert.AreEqual("turn-a", receipt.TurnId);
            Assert.AreEqual("speech-a", receipt.SpeechId);
            Assert.AreEqual(PlaybackReceiptKind.Interrupted, receipt.Kind);
            Assert.AreEqual("local_stop", receipt.ReasonCode);
        }
    }
}
#endif
