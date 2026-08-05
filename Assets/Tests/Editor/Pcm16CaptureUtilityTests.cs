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
    }
}
#endif
