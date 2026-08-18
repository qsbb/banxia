using System;
using System.Collections.Generic;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Lightweight frontend VAD gate. It does not try to replace server STT;
    /// it only decides when a continuously monitored microphone should open a
    /// Protocol 1.0 audio turn. Attack/release hysteresis and a bounded ambient
    /// noise estimate avoid creating turns from isolated room noise.
    /// </summary>
    public sealed class VoiceActivityGate
    {
        private readonly float minimumThreshold;
        private readonly float maximumThreshold;
        private readonly float activationSeconds;
        private readonly float calibrationSeconds;
        private float noiseFloor;
        private float observedSeconds;
        private float activeSeconds;

        public VoiceActivityGate(
            float minimumThreshold,
            float maximumThreshold,
            float activationSeconds,
            float calibrationSeconds = .32f)
        {
            this.minimumThreshold = Math.Max(.0001f, minimumThreshold);
            this.maximumThreshold = Math.Max(this.minimumThreshold, maximumThreshold);
            this.activationSeconds = Math.Max(.04f, activationSeconds);
            this.calibrationSeconds = Math.Max(0f, calibrationSeconds);
            noiseFloor = this.minimumThreshold * .5f;
        }

        public float Threshold => Math.Max(
            minimumThreshold,
            Math.Min(maximumThreshold, noiseFloor * 1.45f));

        public float ActivationProgress => Math.Min(1f, activeSeconds / activationSeconds);

        public bool Observe(float rms, float durationSeconds, bool allowActivation)
        {
            var duration = Math.Max(0f, durationSeconds);
            var level = Math.Max(0f, rms);
            if (!allowActivation)
            {
                activeSeconds = 0f;
                return false;
            }

            if (observedSeconds + .0001f < calibrationSeconds)
            {
                AdaptNoise(level, duration, true);
                observedSeconds += duration;
                activeSeconds = 0f;
                return false;
            }

            var speech = level >= Threshold;
            if (speech)
            {
                activeSeconds += duration;
            }
            else
            {
                // Keep a short amount of syllable energy across a brief
                // consonant or word gap. Isolated spikes still expire before
                // a later sound can open a turn.
                activeSeconds = Math.Max(0f, activeSeconds - duration * .5f);
                AdaptNoise(level, duration, false);
            }
            return activeSeconds >= activationSeconds;
        }

        public bool IsSpeech(float rms)
        {
            return Math.Max(0f, rms) >= Threshold;
        }

        public void ResetActivation()
        {
            activeSeconds = 0f;
        }

        public void Reset()
        {
            noiseFloor = minimumThreshold * .5f;
            observedSeconds = 0f;
            activeSeconds = 0f;
        }

        private void AdaptNoise(float level, float duration, bool calibrating)
        {
            var bounded = Math.Min(level, maximumThreshold / 1.8f);
            var timeConstant = calibrating ? .18f : 2.5f;
            var alpha = duration <= 0f ? 0f : 1f - (float)Math.Exp(-duration / timeConstant);
            noiseFloor += (bounded - noiseFloor) * alpha;
        }
    }

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
            return ResampleAndEncode(
                monoSamples,
                0,
                monoSamples == null ? 0 : monoSamples.Count,
                sourceSampleRate,
                targetSampleRate);
        }

        public static byte[] ResampleAndEncode(
            IReadOnlyList<float> monoSamples,
            int sourceOffset,
            int sourceCount,
            int sourceSampleRate,
            int targetSampleRate)
        {
            if (monoSamples == null || sourceCount <= 0 ||
                sourceOffset < 0 || sourceOffset >= monoSamples.Count ||
                sourceOffset + sourceCount > monoSamples.Count ||
                sourceSampleRate <= 0 || targetSampleRate <= 0)
            {
                return Array.Empty<byte>();
            }

            var outputSamples = Math.Max(
                1,
                (int)Math.Floor(sourceCount * targetSampleRate / (double)sourceSampleRate));
            var bytes = new byte[outputSamples * 2];
            for (var index = 0; index < outputSamples; index++)
            {
                var sourcePosition = index * sourceSampleRate / (double)targetSampleRate;
                var left = Math.Min((int)Math.Floor(sourcePosition), sourceCount - 1);
                var right = Math.Min(left + 1, sourceCount - 1);
                var fraction = (float)(sourcePosition - left);
                var sample = monoSamples[sourceOffset + left] +
                    (monoSamples[sourceOffset + right] - monoSamples[sourceOffset + left]) * fraction;
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
