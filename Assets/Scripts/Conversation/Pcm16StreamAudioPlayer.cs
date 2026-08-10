using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace QuestMmdPlayer
{
    public readonly struct PlaybackTelemetry
    {
        public PlaybackTelemetry(int bufferedMs, int callbackDelayMs, int underflowCount)
        {
            BufferedMs = bufferedMs;
            CallbackDelayMs = callbackDelayMs;
            UnderflowCount = underflowCount;
        }

        public int BufferedMs { get; }
        public int CallbackDelayMs { get; }
        public int UnderflowCount { get; }
    }

    /// <summary>
    /// Small mono PCM16 queue for streaming TTS. Audio callbacks consume the
    /// queue without touching Unity objects, while enqueue/reset stay on the
    /// main thread.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class Pcm16StreamAudioPlayer : MonoBehaviour
    {
        private readonly object gate = new object();
        private readonly Queue<float[]> buffers = new Queue<float[]>();

        private AudioSource audioSource;
        private AudioClip streamClip;
        private float[] currentBuffer;
        private int currentOffset;
        private int queuedSamples;
        private int sampleRate = 24000;
        private float latestRms;
        private int dspBufferLength = 1024;
        private int dspBufferCount = 4;
        private double audibleUntilDspTime;
        private bool streamCompleted = true;
        private bool playbackStarted;
        private int underflowCount;
        private long playbackRequestedAtTicks;
        private long firstAudioCallbackAtTicks;
        private bool playbackTelemetryReported;
        private int playbackStartBufferedMs;
        [SerializeField, Range(.04f, .4f)] private float startupBufferSeconds = .12f;
        [SerializeField, Range(.02f, .5f)] private float outputTailSafetySeconds = .22f;

        public bool IsDrained
        {
            get
            {
                lock (gate)
                {
                    return queuedSamples <= 0 && AudioSettings.dspTime >= audibleUntilDspTime;
                }
            }
        }

        public bool StreamCompleted => streamCompleted;
        public bool PlaybackStarted => playbackStarted;
        public int UnderflowCount => underflowCount;
        public int QueuedChunkCount
        {
            get
            {
                lock (gate) return buffers.Count + (currentBuffer == null ? 0 : 1);
            }
        }
        public string DiagnosticStatus => $"buffer {BufferedSeconds:F2}s | started {playbackStarted} | underflows {underflowCount}";

        public event Action<PlaybackTelemetry> PlaybackTelemetryReady;

        public float BufferedSeconds
        {
            get
            {
                lock (gate) return sampleRate <= 0 ? 0f : (float)queuedSamples / sampleRate;
            }
        }

        public float LatestRms
        {
            get
            {
                lock (gate) return latestRms;
            }
        }

        private void Awake()
        {
            audioSource = GetComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = true;
            audioSource.spatialBlend = 0f;
            AudioSettings.GetDSPBufferSize(out dspBufferLength, out dspBufferCount);
            dspBufferLength = Mathf.Max(1, dspBufferLength);
            dspBufferCount = Mathf.Max(1, dspBufferCount);
        }

        private void Update()
        {
            TryStartPlayback();
            ReportPlaybackTelemetry();
            if (audioSource != null && audioSource.isPlaying && streamCompleted && IsDrained)
            {
                audioSource.Stop();
                lock (gate) latestRms = 0f;
            }
        }

        public void BeginStream()
        {
            StopAndClear();
            streamCompleted = false;
            underflowCount = 0;
        }

        public void MarkStreamCompleted()
        {
            streamCompleted = true;
            TryStartPlayback();
        }
        public void Enqueue(short[] pcm16, int sourceSampleRate)
        {
            if (pcm16 == null || pcm16.Length == 0 || sourceSampleRate <= 0)
            {
                return;
            }

            EnsureStream(sourceSampleRate);
            var converted = new float[pcm16.Length];
            for (var i = 0; i < pcm16.Length; i++)
            {
                converted[i] = pcm16[i] / 32768f;
            }

            lock (gate)
            {
                buffers.Enqueue(converted);
                queuedSamples += converted.Length;
            }
            TryStartPlayback();
        }

        public void StopAndClear()
        {
            if (audioSource != null)
            {
                audioSource.Stop();
            }
            lock (gate)
            {
                buffers.Clear();
                currentBuffer = null;
                currentOffset = 0;
                queuedSamples = 0;
                latestRms = 0f;
                audibleUntilDspTime = 0d;
                streamCompleted = true;
                playbackStarted = false;
                playbackRequestedAtTicks = 0L;
                firstAudioCallbackAtTicks = 0L;
                playbackTelemetryReported = false;
                playbackStartBufferedMs = 0;
            }
        }

        private void EnsureStream(int sourceSampleRate)
        {
            if (audioSource == null)
            {
                audioSource = GetComponent<AudioSource>();
                if (audioSource == null)
                {
                    audioSource = gameObject.AddComponent<AudioSource>();
                }
                audioSource.playOnAwake = false;
                audioSource.loop = true;
                audioSource.spatialBlend = 0f;
            }
            if (streamClip != null && sampleRate == sourceSampleRate)
            {
                return;
            }

            var wasCompleted = streamCompleted;
            StopAndClear();
            streamCompleted = wasCompleted;
            if (streamClip != null)
            {
                Destroy(streamClip);
            }

            sampleRate = sourceSampleRate;
            streamClip = AudioClip.Create("Conversation PCM Stream", sampleRate * 2, 1, sampleRate, true, ReadAudio, SetPosition);
            audioSource.clip = streamClip;
        }

        private void ReadAudio(float[] data)
        {
            var sumSquares = 0f;
            lock (gate)
            {
                var write = 0;
                while (write < data.Length)
                {
                    if (currentBuffer == null || currentOffset >= currentBuffer.Length)
                    {
                        if (buffers.Count == 0)
                        {
                            currentBuffer = null;
                            break;
                        }
                        currentBuffer = buffers.Dequeue();
                        currentOffset = 0;
                    }

                    var count = Mathf.Min(data.Length - write, currentBuffer.Length - currentOffset);
                    for (var i = 0; i < count; i++)
                    {
                        var sample = currentBuffer[currentOffset + i];
                        data[write + i] = sample;
                        sumSquares += sample * sample;
                    }
                    write += count;
                    currentOffset += count;
                    queuedSamples = Mathf.Max(0, queuedSamples - count);
                }

                for (var i = write; i < data.Length; i++)
                {
                    data[i] = 0f;
                }
                if (write < data.Length && playbackStarted && !streamCompleted)
                {
                    underflowCount++;
                }
                latestRms = data.Length == 0 ? 0f : Mathf.Sqrt(sumSquares / data.Length);
                if (write > 0 && sampleRate > 0)
                {
                    var callbackSeconds = (double)data.Length / sampleRate;
                    var outputLatencySeconds = (double)dspBufferLength * dspBufferCount / sampleRate;
                    audibleUntilDspTime = Math.Max(
                        audibleUntilDspTime,
                        AudioSettings.dspTime + callbackSeconds + outputLatencySeconds + outputTailSafetySeconds);
                }
            }
        }

        public static bool ShouldStartPlayback(
            int bufferedSamples,
            int outputSampleRate,
            bool completed,
            float targetBufferSeconds)
        {
            if (bufferedSamples <= 0 || outputSampleRate <= 0)
            {
                return false;
            }
            return completed || bufferedSamples >= outputSampleRate * Mathf.Max(.02f, targetBufferSeconds);
        }

        private void TryStartPlayback()
        {
            if (audioSource == null || audioSource.isPlaying)
            {
                return;
            }
            int buffered;
            lock (gate) buffered = queuedSamples;
            if (!ShouldStartPlayback(buffered, sampleRate, streamCompleted, startupBufferSeconds))
            {
                return;
            }
            playbackStarted = true;
            playbackRequestedAtTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            firstAudioCallbackAtTicks = 0L;
            playbackTelemetryReported = false;
            playbackStartBufferedMs = Mathf.RoundToInt(buffered * 1000f / Mathf.Max(1, sampleRate));
            audioSource.Play();
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!playbackStarted || data == null || data.Length == 0)
            {
                return;
            }
            Interlocked.CompareExchange(
                ref firstAudioCallbackAtTicks,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                0L);
        }

        private void ReportPlaybackTelemetry()
        {
            if (!playbackStarted || playbackTelemetryReported || playbackRequestedAtTicks <= 0L)
            {
                return;
            }

            var callbackAt = Interlocked.Read(ref firstAudioCallbackAtTicks);
            if (callbackAt <= 0L)
            {
                return;
            }

            playbackTelemetryReported = true;
            var elapsed = (callbackAt - playbackRequestedAtTicks) * 1000d /
                System.Diagnostics.Stopwatch.Frequency;
            PlaybackTelemetryReady?.Invoke(new PlaybackTelemetry(
                playbackStartBufferedMs,
                Mathf.Clamp((int)Math.Round(elapsed), 0, 3600000),
                underflowCount));
        }

        private static void SetPosition(int position)
        {
        }

        private void OnDestroy()
        {
            StopAndClear();
            if (streamClip != null)
            {
                Destroy(streamClip);
            }
        }
    }
}
