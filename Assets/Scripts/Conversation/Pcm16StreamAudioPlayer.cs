using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace QuestMmdPlayer
{
    public readonly struct PlaybackTelemetry
    {
        public PlaybackTelemetry(int generation, int bufferedMs, int callbackDelayMs, int underflowCount)
            : this(generation, bufferedMs, callbackDelayMs, underflowCount, 0, false)
        {
        }

        public PlaybackTelemetry(
            int generation,
            int bufferedMs,
            int callbackDelayMs,
            int underflowCount,
            int playedMs,
            bool progress)
        {
            Generation = generation;
            BufferedMs = bufferedMs;
            CallbackDelayMs = callbackDelayMs;
            UnderflowCount = underflowCount;
            PlayedMs = playedMs;
            IsProgress = progress;
        }

        public int Generation { get; }
        public int BufferedMs { get; }
        public int CallbackDelayMs { get; }
        public int UnderflowCount { get; }
        public int PlayedMs { get; }
        public bool IsProgress { get; }
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
        private readonly Queue<AudioBuffer> buffers = new Queue<AudioBuffer>();
        private readonly Queue<ScheduledRms> scheduledRms = new Queue<ScheduledRms>();

        private readonly struct ScheduledRms
        {
            public ScheduledRms(double audibleAtDspTime, float value)
            {
                AudibleAtDspTime = audibleAtDspTime;
                Value = value;
            }

            public double AudibleAtDspTime { get; }
            public float Value { get; }
        }

        private readonly struct AudioBuffer
        {
            public AudioBuffer(float[] samples, int length)
            {
                Samples = samples;
                Length = length;
            }

            public float[] Samples { get; }
            public int Length { get; }
        }

        private AudioSource audioSource;
        private AudioClip streamClip;
        private AudioBuffer currentBuffer;
        private bool hasCurrentBuffer;
        private int currentOffset;
        private int queuedSamples;
        private int sampleRate = 24000;
        private float latestRms;
        private float audibleRms;
        private int dspBufferLength = 1024;
        private int dspBufferCount = 4;
        private double audibleUntilDspTime;
        private double audiblePlaybackStartedAtDspTime = -1d;
        private int streamCompletedFlag = 1;
        private int playbackStartedFlag;
        private int underflowCount;
        private int streamGeneration;
        private int playbackGeneration;
        private long playbackRequestedAtTicks;
        private long firstAudioCallbackAtTicks;
        private bool playbackTelemetryReported;
        private int playbackStartBufferedMs;
        private int enqueuedChunkCount;
        private int peakQueuedChunkCount;
        private float maximumEnqueueMs;
        private float nextSlowEnqueueLogAt;
        private double nextProgressReportDspTime;
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

        public bool StreamCompleted => Volatile.Read(ref streamCompletedFlag) != 0;
        public bool PlaybackStarted => Volatile.Read(ref playbackStartedFlag) != 0;
        public int UnderflowCount => Volatile.Read(ref underflowCount);
        public int QueuedChunkCount
        {
            get
            {
                lock (gate) return buffers.Count +
                    (hasCurrentBuffer && currentOffset < currentBuffer.Length ? 1 : 0);
            }
        }
        public string DiagnosticStatus => $"buffer {BufferedSeconds:F2}s | started {PlaybackStarted} | underflows {UnderflowCount}";

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

        /// <summary>
        /// RMS aligned to the estimated hardware output time. Procedural clip
        /// callbacks run ahead of what the user hears, so lip motion uses this
        /// value instead of the raw callback RMS.
        /// </summary>
        public float AudibleRms
        {
            get
            {
                lock (gate) return audibleRms;
            }
        }

        /// <summary>
        /// Position of the stream at the hardware output boundary. This clock
        /// starts when the first non-empty PCM callback is expected to become
        /// audible, so optional viseme timelines do not lead the voice.
        /// </summary>
        public float AudiblePlaybackSeconds
        {
            get
            {
                lock (gate)
                {
                    if (audiblePlaybackStartedAtDspTime < 0d)
                    {
                        return 0f;
                    }
                    return Mathf.Max(0f, (float)(AudioSettings.dspTime - audiblePlaybackStartedAtDspTime));
                }
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
            UpdateAudibleRms();
            ReportPlaybackTelemetry();
            ReportPlaybackProgress();
            if (audioSource != null && audioSource.isPlaying && StreamCompleted && IsDrained)
            {
                audioSource.Stop();
                lock (gate)
                {
                    latestRms = 0f;
                    audibleRms = 0f;
                    scheduledRms.Clear();
                }
            }
        }

        public int BeginStream()
        {
            StopAndClear();
            Volatile.Write(ref streamCompletedFlag, 0);
            Interlocked.Exchange(ref underflowCount, 0);
            return Volatile.Read(ref streamGeneration);
        }

        public void MarkStreamCompleted()
        {
            Volatile.Write(ref streamCompletedFlag, 1);
            Debug.Log(
                $"[PcmStream] Stream summary chunks={enqueuedChunkCount} " +
                $"peak_queue={peakQueuedChunkCount} max_enqueue_ms={maximumEnqueueMs:F2} " +
                $"underflows={UnderflowCount}",
                this);
            TryStartPlayback();
        }
        public void Enqueue(short[] pcm16, int sourceSampleRate, int sampleCount = -1)
        {
            var length = sampleCount > 0 ? Mathf.Min(sampleCount, pcm16 == null ? 0 : pcm16.Length) :
                pcm16 == null ? 0 : pcm16.Length;
            if (length == 0 || sourceSampleRate <= 0)
            {
                return;
            }

            EnsureStream(sourceSampleRate);
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var converted = ArrayPool<float>.Shared.Rent(length);
            for (var i = 0; i < length; i++)
            {
                converted[i] = pcm16[i] / 32768f;
            }

            lock (gate)
            {
                buffers.Enqueue(new AudioBuffer(converted, length));
                queuedSamples += length;
                enqueuedChunkCount++;
                peakQueuedChunkCount = Mathf.Max(peakQueuedChunkCount, buffers.Count +
                    (hasCurrentBuffer ? 1 : 0));
            }
            var enqueueMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) *
                1000d / System.Diagnostics.Stopwatch.Frequency);
            maximumEnqueueMs = Mathf.Max(maximumEnqueueMs, enqueueMs);
            if (enqueueMs >= 4f && Time.unscaledTime >= nextSlowEnqueueLogAt)
            {
                nextSlowEnqueueLogAt = Time.unscaledTime + 1f;
                Debug.LogWarning(
                    $"[PcmStream] Slow PCM enqueue: elapsed_ms={enqueueMs:F2} samples={pcm16.Length} " +
                    $"queue_depth={QueuedChunkCount}",
                    this);
            }
            TryStartPlayback();
        }

        public void StopAndClear()
        {
            Interlocked.Increment(ref streamGeneration);
            ClearForFormatChange();
        }

        private void ClearForFormatChange()
        {
            if (audioSource != null)
            {
                audioSource.Stop();
            }
            lock (gate)
            {
                ReturnQueuedBuffers();
                currentBuffer = default;
                hasCurrentBuffer = false;
                currentOffset = 0;
                queuedSamples = 0;
                latestRms = 0f;
                audibleRms = 0f;
                scheduledRms.Clear();
                audibleUntilDspTime = 0d;
                audiblePlaybackStartedAtDspTime = -1d;
                Volatile.Write(ref streamCompletedFlag, 1);
                Volatile.Write(ref playbackStartedFlag, 0);
                Volatile.Write(ref playbackGeneration, 0);
                Interlocked.Exchange(ref playbackRequestedAtTicks, 0L);
                Interlocked.Exchange(ref firstAudioCallbackAtTicks, 0L);
                playbackTelemetryReported = false;
                nextProgressReportDspTime = 0d;
                playbackStartBufferedMs = 0;
                enqueuedChunkCount = 0;
                peakQueuedChunkCount = 0;
                maximumEnqueueMs = 0f;
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

            var wasCompleted = StreamCompleted;
            ClearForFormatChange();
            Volatile.Write(ref streamCompletedFlag, wasCompleted ? 1 : 0);
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
                    if (!hasCurrentBuffer || currentOffset >= currentBuffer.Length)
                    {
                        ReturnCurrentBuffer();
                        if (buffers.Count == 0)
                        {
                            break;
                        }
                        currentBuffer = buffers.Dequeue();
                        hasCurrentBuffer = true;
                        currentOffset = 0;
                    }

                    var count = Mathf.Min(data.Length - write, currentBuffer.Length - currentOffset);
                    for (var i = 0; i < count; i++)
                    {
                        var sample = currentBuffer.Samples[currentOffset + i];
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
                if (write < data.Length && PlaybackStarted && !StreamCompleted)
                {
                    Interlocked.Increment(ref underflowCount);
                }
                latestRms = data.Length == 0 ? 0f : Mathf.Sqrt(sumSquares / data.Length);
                var outputLatencySeconds = CalculateOutputLatencySeconds(
                    dspBufferLength,
                    dspBufferCount,
                    AudioSettings.outputSampleRate);
                scheduledRms.Enqueue(new ScheduledRms(
                    AudioSettings.dspTime + outputLatencySeconds,
                    latestRms));
                if (write > 0 && sampleRate > 0)
                {
                    if (audiblePlaybackStartedAtDspTime < 0d)
                    {
                        audiblePlaybackStartedAtDspTime = AudioSettings.dspTime + outputLatencySeconds;
                    }
                    var callbackSeconds = (double)data.Length / sampleRate;
                    audibleUntilDspTime = Math.Max(
                        audibleUntilDspTime,
                        AudioSettings.dspTime + callbackSeconds + outputLatencySeconds + outputTailSafetySeconds);
                }
            }
        }

        private void ReturnQueuedBuffers()
        {
            ReturnCurrentBuffer();
            while (buffers.Count > 0)
            {
                var buffer = buffers.Dequeue();
                if (buffer.Samples != null)
                {
                    ArrayPool<float>.Shared.Return(buffer.Samples);
                }
            }
        }

        private void ReturnCurrentBuffer()
        {
            if (hasCurrentBuffer && currentBuffer.Samples != null)
            {
                ArrayPool<float>.Shared.Return(currentBuffer.Samples);
            }
            currentBuffer = default;
            hasCurrentBuffer = false;
            currentOffset = 0;
        }

        private void UpdateAudibleRms()
        {
            var now = AudioSettings.dspTime;
            lock (gate)
            {
                while (scheduledRms.Count > 0 && scheduledRms.Peek().AudibleAtDspTime <= now)
                {
                    audibleRms = scheduledRms.Dequeue().Value;
                }
            }
        }

        public static double CalculateOutputLatencySeconds(
            int bufferLength,
            int bufferCount,
            int outputSampleRate)
        {
            if (bufferLength <= 0 || bufferCount <= 0 || outputSampleRate <= 0)
            {
                return 0d;
            }
            return (double)bufferLength * bufferCount / outputSampleRate;
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
            if (!ShouldStartPlayback(buffered, sampleRate, StreamCompleted, startupBufferSeconds))
            {
                return;
            }
            Volatile.Write(ref playbackStartedFlag, 1);
            Volatile.Write(ref playbackGeneration, Volatile.Read(ref streamGeneration));
            Interlocked.Exchange(ref playbackRequestedAtTicks, System.Diagnostics.Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref firstAudioCallbackAtTicks, 0L);
            playbackTelemetryReported = false;
            nextProgressReportDspTime = AudioSettings.dspTime + .25d;
            playbackStartBufferedMs = Mathf.RoundToInt(buffered * 1000f / Mathf.Max(1, sampleRate));
            audioSource.Play();
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!PlaybackStarted || data == null || data.Length == 0)
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
            var requestedAt = Interlocked.Read(ref playbackRequestedAtTicks);
            if (!PlaybackStarted || playbackTelemetryReported || requestedAt <= 0L)
            {
                return;
            }

            var callbackAt = Interlocked.Read(ref firstAudioCallbackAtTicks);
            if (callbackAt <= 0L)
            {
                return;
            }

            playbackTelemetryReported = true;
            var elapsed = (callbackAt - requestedAt) * 1000d /
                System.Diagnostics.Stopwatch.Frequency;
            PlaybackTelemetryReady?.Invoke(new PlaybackTelemetry(
                Volatile.Read(ref playbackGeneration),
                playbackStartBufferedMs,
                Mathf.Clamp((int)Math.Round(elapsed), 0, 3600000),
                UnderflowCount));
        }

        private void ReportPlaybackProgress()
        {
            if (!PlaybackStarted || audioSource == null || !audioSource.isPlaying ||
                AudioSettings.dspTime < nextProgressReportDspTime)
            {
                return;
            }

            nextProgressReportDspTime = AudioSettings.dspTime + .25d;
            int bufferedMs;
            lock (gate)
            {
                bufferedMs = sampleRate <= 0 ? 0 : Mathf.RoundToInt(queuedSamples * 1000f / sampleRate);
            }
            PlaybackTelemetryReady?.Invoke(new PlaybackTelemetry(
                Volatile.Read(ref playbackGeneration),
                bufferedMs,
                0,
                UnderflowCount,
                Mathf.RoundToInt(AudiblePlaybackSeconds * 1000f),
                true));
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
