using System;
using System.Collections.Generic;

namespace QuestMmdPlayer
{
    public static class Pcm16CaptureUtility
    {
        public static int FramesForDuration(int sampleRate, int milliseconds)
        {
            if (sampleRate <= 0 || milliseconds <= 0)
            {
                return 0;
            }

            return Math.Max(1, (int)Math.Round(sampleRate * milliseconds / 1000.0));
        }

        public static byte[] ResampleAndEncode(
            IReadOnlyList<float> monoSamples,
            int sourceSampleRate,
            int targetSampleRate)
        {
            if (monoSamples == null || monoSamples.Count == 0 ||
                sourceSampleRate <= 0 || targetSampleRate <= 0)
            {
                return Array.Empty<byte>();
            }

            var outputSamples = Math.Max(
                1,
                (int)Math.Floor(monoSamples.Count * targetSampleRate / (double)sourceSampleRate));
            var bytes = new byte[outputSamples * 2];
            for (var index = 0; index < outputSamples; index++)
            {
                var sourcePosition = index * sourceSampleRate / (double)targetSampleRate;
                var left = Math.Min((int)Math.Floor(sourcePosition), monoSamples.Count - 1);
                var right = Math.Min(left + 1, monoSamples.Count - 1);
                var fraction = (float)(sourcePosition - left);
                var sample = monoSamples[left] + (monoSamples[right] - monoSamples[left]) * fraction;
                var pcm = FloatToPcm16(sample);
                bytes[index * 2] = (byte)(pcm & 0xff);
                bytes[index * 2 + 1] = (byte)((pcm >> 8) & 0xff);
            }

            return bytes;
        }

        public static short FloatToPcm16(float sample)
        {
            if (sample <= -1f)
            {
                return short.MinValue;
            }
            if (sample >= 1f)
            {
                return short.MaxValue;
            }

            return (short)Math.Round(sample * short.MaxValue);
        }
    }
}
