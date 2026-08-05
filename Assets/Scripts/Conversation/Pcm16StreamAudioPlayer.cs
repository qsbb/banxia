using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestMmdPlayer
{
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
        [SerializeField, Range(.02f, .5f)] private float outputTailSafetySeconds = .08f;

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
            if (audioSource != null && audioSource.isPlaying && IsDrained)
            {
                audioSource.Stop();
                lock (gate) latestRms = 0f;
            }
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
            if (!audioSource.isPlaying)
            {
                audioSource.Play();
            }
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
            }
        }

        private void EnsureStream(int sourceSampleRate)
        {
            if (streamClip != null && sampleRate == sourceSampleRate)
            {
                return;
            }

            StopAndClear();
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
