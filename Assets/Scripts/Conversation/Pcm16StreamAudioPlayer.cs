using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
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
        private float peakStreamRms;
        private int peakEnqueueSample;
        private int nonzeroChunkCount;
        private long consumedSampleCount;
        private const int DefaultSampleRate = 24000;
        private const int NonzeroChunkThreshold = 500;
        private int dspBufferLength = 1024;
        private int dspBufferCount = 4;
        // AudioSettings.outputSampleRate 是主线程专属 API——ReadAudio 在音频线程执行，
        // 直接调用会抛 UnityException 导致回调缓冲被丢弃（静音）。在主线程缓存一次。
        private int cachedOutputSampleRate = 48000;
        // 输出采样时钟：PCM 回调每执行一次累加 data.Length。
        // 与 AudioSettings.dspTime 同义但完全线程安全（dspTime 同样是主线程专属 API，
        // 只是之前被 outputSampleRate 的异常挡住从未执行到）。
        private long callbackOutputSamples;
        private double OutputClockSeconds
        {
            get
            {
                lock (gate)
                {
                    return sampleRate <= 0 ? 0d : (double)callbackOutputSamples / sampleRate;
                }
            }
        }
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
        // QA 测试音：定位"数字信号有声但听不到"的最后一环（Unity→扬声器物理通路）。
        // 仅 Development/Editor 构建编译；不再开机自动播（用户要求去掉蜂鸣音），
        // 仅可通过在 persistentDataPath 写 qa_tone.trigger 文件（adb run-as）手动触发。
        private float qaToneAt = -1f;
        private float nextQaTriggerCheckAt;
        private string qaTriggerPath;
        [SerializeField, Range(.02f, .5f)] private float outputTailSafetySeconds = .22f;

        public bool IsDrained
        {
            get
            {
                lock (gate)
                {
                    var now = sampleRate <= 0 ? 0d : (double)callbackOutputSamples / sampleRate;
                    return queuedSamples <= 0 && now >= audibleUntilDspTime;
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
                    var now = sampleRate <= 0 ? 0d : (double)callbackOutputSamples / sampleRate;
                    return Mathf.Max(0f, (float)(now - audiblePlaybackStartedAtDspTime));
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
            cachedOutputSampleRate = AudioSettings.outputSampleRate;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            qaTriggerPath = Path.Combine(Application.persistentDataPath, "qa_tone.trigger");
#endif
            AudioSettings.OnAudioConfigurationChanged += HandleAudioConfigurationChanged;
            // 常驻播放架构：clip 创建一次、Play 一次、永不 Stop。
            // PCM 回调持续拉取队列，空闲时零填充。彻底消除 Android/OpenSL 上
            // streaming clip 经 Stop→Play 复用后输出静默的已知脆弱路径。
            EnsurePersistentStream(DefaultSampleRate);
        }

        private void HandleAudioConfigurationChanged(bool deviceWasChanged)
        {
            var config = AudioSettings.GetConfiguration();
            cachedOutputSampleRate = config.sampleRate;
            Debug.LogWarning(
                $"[PcmStream] 音频配置变更: deviceChanged={deviceWasChanged} " +
                $"rate={config.sampleRate}Hz speaker={config.speakerMode} " +
                $"voices={config.numRealVoices}/{config.numVirtualVoices}",
                this);
            if (deviceWasChanged && audioSource != null && streamClip != null)
            {
                // 输出设备切换后强制重建底层 voice，防止 OpenSL 路由残留。
                audioSource.Stop();
                audioSource.Play();
            }
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private void PlayQaTone(string reason)
        {
            if (PlaybackStarted)
            {
                Debug.LogWarning("[PcmStream] QA 测试音跳过：正在播放回复", this);
                return;
            }
            const int rate = 24000;
            var count = rate * 3 / 2;
            var tone = new short[count];
            for (var i = 0; i < count; i++)
            {
                tone[i] = (short)(Mathf.Sin(2f * Mathf.PI * 880f * i / rate) * 0.35f * 32767f);
            }
            Debug.LogWarning(
                $"[PcmStream] QA 测试音({reason})：880Hz×1.5s——戴头显应听到蜂鸣；若听不到且 peak_rms>0 则是系统输出层问题",
                this);
            BeginStream();
            Enqueue(tone, rate, count);
            MarkStreamCompleted();
        }
#endif

        private void Update()
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (qaToneAt > 0f && Time.unscaledTime >= qaToneAt)
            {
                qaToneAt = -1f;
                PlayQaTone("startup");
            }
            if (Time.unscaledTime >= nextQaTriggerCheckAt)
            {
                nextQaTriggerCheckAt = Time.unscaledTime + 1f;
                var path = qaTriggerPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception)
                    {
                        // 忽略删除失败，避免每帧重试刷屏。
                    }
                    PlayQaTone("trigger");
                }
            }
#endif
            // 常驻播放看门狗：任何原因导致 voice 停止（挂起恢复、设备切换等）立即复活。
            if (audioSource != null && streamClip != null && !audioSource.isPlaying)
            {
                audioSource.Play();
            }
            TryStartPlayback();
            UpdateAudibleRms();
            ReportPlaybackTelemetry();
            ReportPlaybackProgress();
            if (PlaybackStarted && StreamCompleted && IsDrained)
            {
                Volatile.Write(ref playbackStartedFlag, 0);
                // 播完总结：peak_rms 此时覆盖整条播放（区别于流结束快照只覆盖到网络末尾）。
                // nonzero_chunks 反映入队侧（线路上）真实有声块数——两侧对照可一锤定音
                // 区分"线路发来零数据"与"播放器内部丢数据"。
                Debug.Log(
                    $"[PcmStream] Playback summary gen={Volatile.Read(ref playbackGeneration)} " +
                    $"peak_rms={peakStreamRms:F4} peak_in={peakEnqueueSample} " +
                    $"nonzero_chunks={nonzeroChunkCount}/{enqueuedChunkCount} " +
                    $"consumed_ms={(sampleRate > 0 ? consumedSampleCount * 1000 / sampleRate : 0)} " +
                    $"underflows={UnderflowCount}",
                    this);
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
            peakStreamRms = 0f;
            peakEnqueueSample = 0;
            nonzeroChunkCount = 0;
            consumedSampleCount = 0L;
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
                $"underflows={UnderflowCount} peak_rms={peakStreamRms:F4} peak_in={peakEnqueueSample} rate={sampleRate} " +
                $"vol={audioSource.volume:F2} mute={audioSource.mute} " +
                $"listeners={FindObjectsOfType<AudioListener>(false).Length} " +
                $"dspRate={cachedOutputSampleRate}Hz",
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

            EnsurePersistentStream(sourceSampleRate);
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var converted = ArrayPool<float>.Shared.Rent(length);
            // 转换在锁内完成：与音频线程的消费建立确定的先后/可见性关系，
            // 同时统计本块是否真实有声（鉴别线路零数据 vs 播放器丢数据）。
            lock (gate)
            {
                var chunkPeak = 0;
                for (var i = 0; i < length; i++)
                {
                    var raw = pcm16[i];
                    converted[i] = raw / 32768f;
                    var magnitude = raw < 0 ? -(int)raw : (int)raw;
                    if (magnitude > chunkPeak)
                    {
                        chunkPeak = magnitude;
                    }
                }
                if (chunkPeak > peakEnqueueSample)
                {
                    peakEnqueueSample = chunkPeak;
                }
                if (chunkPeak > NonzeroChunkThreshold)
                {
                    nonzeroChunkCount++;
                }
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
            // 常驻播放：这里绝不 Stop AudioSource——voice 一生只 Play 一次。
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
                callbackOutputSamples = 0L;
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

        private void EnsurePersistentStream(int sourceSampleRate)
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
            // 立即永久播放：PCM 回调从此持续运转，空闲零填充、有数据即出声。
            audioSource.Play();
        }

        private void ReadAudio(float[] data)
        {
            var sumSquares = 0f;
            lock (gate)
            {
                var outputNow = sampleRate <= 0 ? 0d : (double)callbackOutputSamples / sampleRate;
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
                    consumedSampleCount += count;
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
                if (latestRms > peakStreamRms)
                {
                    peakStreamRms = latestRms;
                }
                var outputLatencySeconds = CalculateOutputLatencySeconds(
                    dspBufferLength,
                    dspBufferCount,
                    cachedOutputSampleRate);
                scheduledRms.Enqueue(new ScheduledRms(
                    outputNow + outputLatencySeconds,
                    latestRms));
                if (write > 0 && sampleRate > 0)
                {
                    if (audiblePlaybackStartedAtDspTime < 0d)
                    {
                        audiblePlaybackStartedAtDspTime = outputNow + outputLatencySeconds;
                    }
                    var callbackSeconds = (double)data.Length / sampleRate;
                    audibleUntilDspTime = Math.Max(
                        audibleUntilDspTime,
                        outputNow + callbackSeconds + outputLatencySeconds + outputTailSafetySeconds);
                }
                callbackOutputSamples += data.Length;
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
            var now = OutputClockSeconds;
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
            if (audioSource == null || PlaybackStarted)
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
            // 常驻播放：音源始终 isPlaying，这里无需也无法再 Play。
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
            if (!PlaybackStarted || audioSource == null ||
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
            AudioSettings.OnAudioConfigurationChanged -= HandleAudioConfigurationChanged;
            if (audioSource != null)
            {
                audioSource.Stop();
            }
            if (streamClip != null)
            {
                Destroy(streamClip);
            }
        }
    }
}
