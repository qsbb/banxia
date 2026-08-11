using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEngine;
using UnityEngine.XR;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace QuestMmdPlayer
{
    [DisallowMultipleComponent]
    public sealed class QuestMicrophoneInput : MonoBehaviour
    {
        private const int TargetSampleRate = 16000;
        private const string AlwaysListeningPreferenceKey = "banxia.voice.always_listening";

        [SerializeField, Range(40, 100)] private int chunkMilliseconds = 80;
        [SerializeField, Range(2, 20)] private int clipBufferSeconds = 10;
        [SerializeField, Range(5, 60)] private int maxRecordingSeconds = 30;
        [SerializeField] private bool enableTrackedVoiceGesture;
        [SerializeField, Range(.2f, 1.2f)] private float trackedVoiceHoldSeconds = .75f;
        [SerializeField] private bool autoStopOnSilence = true;
        [SerializeField, Range(.003f, .08f)] private float voiceSilenceRms = .006f;
        [SerializeField, Range(.4f, 3f)] private float voiceSilenceSeconds = 1.8f;
        [SerializeField, Range(.2f, 1f)] private float minimumVoiceSeconds = .45f;
        [SerializeField, Range(1.5f, 8f)] private float initialVoiceTimeoutSeconds = 4f;
        [SerializeField] private bool alwaysListening = true;
        [SerializeField, Range(.12f, .6f)] private float voicePreRollSeconds = .32f;
        [SerializeField, Range(.08f, .4f)] private float voiceActivationSeconds = .12f;

        private readonly List<float> pendingMono = new List<float>(4096);
        private readonly List<float> preRollMono = new List<float>(8192);
        private ConversationController conversation;
        private AudioClip recordingClip;
        private string deviceName;
        private int sourceSampleRate;
        private int sourceChannels;
        private int lastPosition;
        private bool previousTalkButton;
        private float recordingStartedAt;
        private float trackedPinchStartedAt = -1f;
        private bool previousTrackedPinch;
        private bool trackedPinchTurnCompleted;
        private float lastVoiceAt;
        private bool detectedSpeech;
        private CompanionWorldMenu menu;
        private AvatarTouchInteraction touchInteraction;
        private VoiceActivityGate activityGate;
        private float nextMonitorAttemptAt;
        private bool permissionRequested;
        private RuntimeDebugLog diagnostics;
        private int pcmEncodeCount;
        private int pcmEncodeTotalMs;
        private int pcmEncodeMaxMs;

        public bool IsRecording { get; private set; }
        public bool IsMonitoring { get; private set; }
        public bool AlwaysListening => alwaysListening;
        public float InputLevel { get; private set; }
        public int LastTurnPcmBytes { get; private set; }
        public int LastTurnChunkCount { get; private set; }
        public float ActivationThreshold => activityGate == null ? voiceSilenceRms : activityGate.Threshold;
        public float ActivationProgress => activityGate == null ? 0f : activityGate.ActivationProgress;
        public bool SpeechDetected => detectedSpeech;
        public float LastTurnCaptureSeconds { get; private set; }
        public string DiagnosticStatus => $"{Status} | level {InputLevel:F3}/{ActivationThreshold:F3} | " +
            $"chunks {LastTurnChunkCount} | pcm {LastTurnPcmBytes} B | capture {LastTurnCaptureSeconds:F2}s";
        public string Status { get; private set; } = "Microphone ready";
        public string ShortStatus => IsRecording ? "REC" : IsMonitoring && alwaysListening ? "LIVE" :
            Status.StartsWith("Microphone ready") ? "READY" : "OFF";

        public void Bind(ConversationController owner)
        {
            conversation = owner;
        }

        private void Awake()
        {
            conversation = GetComponent<ConversationController>();
            diagnostics = GetComponent<RuntimeDebugLog>();
            alwaysListening = PlayerPrefs.GetInt(
                AlwaysListeningPreferenceKey,
                alwaysListening ? 1 : 0) != 0;
            RecreateActivityGate();
        }

        private void Update()
        {
            var trackedPinch = ReadTrackedVoicePinch(out var trackedHand);
            var leftDevice = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            var talkPressed = !trackedHand && leftDevice.isValid &&
                leftDevice.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out var stickPressed) && stickPressed;
#if UNITY_EDITOR || UNITY_STANDALONE
            talkPressed |= Input.GetKey(KeyCode.V);
#endif
            if (talkPressed && !previousTalkButton)
            {
                StartRecording();
            }
            else if (!talkPressed && previousTalkButton && IsRecording)
            {
                StopAndSend();
            }
            previousTalkButton = talkPressed;

            if (trackedPinch && !previousTrackedPinch)
            {
                trackedPinchStartedAt = Time.unscaledTime;
                trackedPinchTurnCompleted = false;
            }
            if (trackedPinch && !IsRecording && !trackedPinchTurnCompleted && trackedPinchStartedAt >= 0f &&
                Time.unscaledTime - trackedPinchStartedAt >= Mathf.Max(.2f, trackedVoiceHoldSeconds))
            {
                trackedPinchTurnCompleted = StartRecording();
            }
            else if (!trackedPinch && previousTrackedPinch && IsRecording)
            {
                StopAndSend();
            }
            if (!trackedPinch)
            {
                trackedPinchStartedAt = -1f;
                trackedPinchTurnCompleted = false;
            }
            previousTrackedPinch = trackedPinch;

            if (alwaysListening && !IsMonitoring && Time.unscaledTime >= nextMonitorAttemptAt)
            {
                nextMonitorAttemptAt = Time.unscaledTime + 2f;
                StartMonitoring();
            }
            if (IsMonitoring)
            {
                CaptureAvailableFrames();
            }
            if (!IsRecording)
            {
                if (alwaysListening && IsMonitoring && conversation != null &&
                    conversation.State == ConversationState.Idle &&
                    Status.StartsWith("Waiting for reply", System.StringComparison.Ordinal))
                {
                    Status = "Listening for speech";
                }
                return;
            }

            if (Time.unscaledTime - recordingStartedAt >= maxRecordingSeconds)
            {
                StopAndSend();
            }
            else if (autoStopOnSilence && ShouldStopForSilence(
                detectedSpeech,
                Time.unscaledTime - recordingStartedAt,
                Time.unscaledTime - lastVoiceAt,
                minimumVoiceSeconds,
                voiceSilenceSeconds,
                initialVoiceTimeoutSeconds))
            {
                if (detectedSpeech)
                {
                    StopAndSend();
                }
                else
                {
                    RecordMicrophoneStage("cancelled", "no_speech_detected");
                    CancelRecording();
                    Status = alwaysListening ? "Listening for speech" : "No speech detected";
                }
            }
        }

        private bool ReadTrackedVoicePinch(out bool trackedHand)
        {
            trackedHand = false;
            if (!enableTrackedVoiceGesture)
            {
                return false;
            }
            if (!IsRecording && conversation != null && !conversation.CanStartVoiceInput)
            {
                return false;
            }
            var trackingSpace = QuestXrInputUtility.ResolveTrackingSpace();
            if (!QuestXrInputUtility.TryGetTrackedHandPointer(XRNode.LeftHand, trackingSpace, out _, out var pinch))
            {
                return false;
            }
            trackedHand = true;
            menu = menu != null ? menu : FindObjectOfType<CompanionWorldMenu>();
            if (menu != null && menu.IsOpen)
            {
                return false;
            }
            touchInteraction = touchInteraction != null ? touchInteraction : FindObjectOfType<AvatarTouchInteraction>();
            if (touchInteraction != null && touchInteraction.IsTouched)
            {
                return false;
            }
            return pinch;
        }

        public void ToggleRecording()
        {
            if (IsRecording)
            {
                StopAndSend();
            }
            else
            {
                StartRecording();
            }
        }

        public void ToggleAlwaysListening()
        {
            SetAlwaysListening(!alwaysListening);
        }

        public void RestartMonitoring()
        {
            if (IsRecording)
            {
                CancelRecording();
            }
            StopMicrophoneOnly();
            nextMonitorAttemptAt = 0f;
            var started = StartMonitoring();
            Debug.Log(started
                ? "[VoiceInput] Microphone monitor restarted."
                : "[VoiceInput] Microphone monitor restart failed.", this);
        }

        public void SetAlwaysListening(bool enabled)
        {
            if (alwaysListening == enabled)
            {
                if (enabled && !IsMonitoring)
                {
                    StartMonitoring();
                }
                return;
            }

            alwaysListening = enabled;
            PlayerPrefs.SetInt(AlwaysListeningPreferenceKey, enabled ? 1 : 0);
            PlayerPrefs.Save();
            if (enabled)
            {
                StartMonitoring();
            }
            else if (!IsRecording)
            {
                StopMicrophoneOnly();
                Status = "Microphone ready";
            }
            Debug.Log($"[VoiceInput] Always listening {(enabled ? "enabled" : "disabled")}.", this);
        }

        public bool StartMonitoring()
        {
            if (IsMonitoring)
            {
                return true;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                if (!permissionRequested)
                {
                    Permission.RequestUserPermission(Permission.Microphone);
                    permissionRequested = true;
                }
                Status = "Microphone permission required";
                RecordMicrophoneStage("blocked", "microphone_permission_missing");
                Debug.LogWarning("[VoiceInput] Microphone permission is not available.", this);
                return false;
            }
#endif
            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                Status = "Microphone unavailable";
                RecordMicrophoneStage("unavailable", "microphone_device_missing");
                Debug.LogWarning("[VoiceInput] No microphone device is available.", this);
                return false;
            }

            deviceName = Microphone.devices[0];
            recordingClip = Microphone.Start(deviceName, true, clipBufferSeconds, TargetSampleRate);
            if (recordingClip == null)
            {
                Status = "Microphone could not start";
                RecordMicrophoneStage("failed", "microphone_start_failed");
                Debug.LogWarning("[VoiceInput] Microphone start failed.", this);
                return false;
            }

            sourceSampleRate = recordingClip.frequency;
            sourceChannels = Mathf.Max(1, recordingClip.channels);
            lastPosition = Mathf.Max(0, Microphone.GetPosition(deviceName));
            pendingMono.Clear();
            preRollMono.Clear();
            RecreateActivityGate();
            InputLevel = 0f;
            IsMonitoring = true;
            Status = alwaysListening ? "Listening for speech" : "Microphone ready";
            diagnostics?.RecordStage(
                "microphone",
                "ready",
                "monitoring",
                sampleRate: sourceSampleRate,
                channels: sourceChannels);
            Debug.Log("[VoiceInput] Microphone monitor started.", this);
            return true;
        }

        public bool StartRecording()
        {
            if (IsRecording)
            {
                return true;
            }

            conversation = conversation != null ? conversation : GetComponent<ConversationController>();
            if (conversation == null || !conversation.CanStartVoiceInput)
            {
                Status = "Conversation backend offline";
                RecordMicrophoneStage("blocked", "bridge_disconnected");
                Debug.LogWarning("[VoiceInput] Voice turn rejected because the backend is offline.", this);
                return false;
            }
            if (!StartMonitoring())
            {
                return false;
            }

            pendingMono.Clear();
            preRollMono.Clear();
            activityGate?.ResetActivation();
            return BeginActiveVoiceTurn(false);
        }

        private bool BeginActiveVoiceTurn(bool includePreRoll)
        {
            if (conversation == null || !conversation.CanStartVoiceInput || !conversation.BeginVoiceInput())
            {
                Status = "Conversation backend offline";
                RecordMicrophoneStage("blocked", "voice_turn_rejected");
                Debug.LogWarning("[VoiceInput] Backend rejected voice start.", this);
                return false;
            }

            if (includePreRoll && preRollMono.Count > 0)
            {
                pendingMono.InsertRange(0, preRollMono);
            }
            preRollMono.Clear();
            activityGate?.ResetActivation();
            LastTurnPcmBytes = 0;
            LastTurnChunkCount = 0;
            recordingStartedAt = Time.unscaledTime;
            lastVoiceAt = recordingStartedAt;
            detectedSpeech = includePreRoll;
            IsRecording = true;
            LastTurnCaptureSeconds = 0f;
            pcmEncodeCount = 0;
            pcmEncodeTotalMs = 0;
            pcmEncodeMaxMs = 0;
            Status = "Recording voice";
            RecordMicrophoneStage("processing", includePreRoll ? "speech_detected" : "manual_capture");
            Debug.Log(includePreRoll
                ? "[VoiceInput] Speech detected; voice turn started."
                : "[VoiceInput] Manual voice turn started.", this);
            return true;
        }

        public void StopAndSend()
        {
            if (!IsRecording)
            {
                return;
            }

            CaptureAvailableFrames();
            if (!IsRecording)
            {
                return;
            }
            if (!FlushRemainder())
            {
                CancelRecording();
                Status = "Voice upload queue full";
                RecordMicrophoneStage("failed", "audio_upload_backpressure");
                Debug.LogWarning("[VoiceInput] PCM upload queue rejected a chunk.", this);
                return;
            }

            LastTurnCaptureSeconds = Mathf.Max(0f, Time.unscaledTime - recordingStartedAt);
            var hadDetectedSpeech = detectedSpeech;
            var trailingSilenceMs = Mathf.RoundToInt(
                Mathf.Max(0f, Time.unscaledTime - lastVoiceAt) * 1000f);
            var accepted = conversation != null && conversation.EndVoiceInput();
            ResetActiveVoiceCapture();
            if (accepted)
            {
                Status = alwaysListening ? "Waiting for reply | mic live" : "Voice sent";
                diagnostics?.RecordStage(
                    "microphone",
                    "completed",
                    "capture_sent",
                    elapsedMs: Mathf.RoundToInt(LastTurnCaptureSeconds * 1000f),
                    chunks: LastTurnChunkCount,
                    bytes: LastTurnPcmBytes,
                    sampleRate: TargetSampleRate,
                    channels: 1);
                if (hadDetectedSpeech)
                {
                    diagnostics?.RecordStage(
                        "microphone",
                        "completed",
                        "vad_trailing_silence",
                        elapsedMs: trailingSilenceMs);
                }
                diagnostics?.RecordStage(
                    "audio_encode",
                    "completed",
                    "pcm_summary",
                    elapsedMs: pcmEncodeTotalMs,
                    chunks: pcmEncodeCount,
                    bytes: LastTurnPcmBytes);
                if (pcmEncodeMaxMs > 0)
                {
                    diagnostics?.RecordStage(
                        "audio_encode",
                        "completed",
                        "pcm_max_chunk",
                        elapsedMs: pcmEncodeMaxMs);
                }
                Debug.Log("[VoiceInput] Voice end accepted; waiting for reply.", this);
            }
            else
            {
                conversation?.CancelVoiceInput();
                Status = alwaysListening ? "Listening for speech" : "Voice send failed";
                RecordMicrophoneStage("failed", "voice_end_rejected");
                Debug.LogWarning("[VoiceInput] Backend rejected voice end.", this);
            }
        }

        public void CancelRecording()
        {
            if (!IsRecording)
            {
                return;
            }

            conversation?.CancelVoiceInput();
            ResetActiveVoiceCapture();
            Status = alwaysListening ? "Listening for speech" : "Recording cancelled";
            Debug.Log("[VoiceInput] Active voice turn cancelled.", this);
        }

        private void ResetActiveVoiceCapture()
        {
            IsRecording = false;
            detectedSpeech = false;
            activityGate?.ResetActivation();
            pendingMono.Clear();
            preRollMono.Clear();
            InputLevel = 0f;
            if (!alwaysListening)
            {
                StopMicrophoneOnly();
            }
        }

        private void CaptureAvailableFrames()
        {
            if (!IsMonitoring || recordingClip == null)
            {
                return;
            }

            var current = Microphone.GetPosition(deviceName);
            if (current < 0 || current == lastPosition)
            {
                return;
            }
            if (current > lastPosition)
            {
                AppendFrames(lastPosition, current - lastPosition);
            }
            else
            {
                AppendFrames(lastPosition, recordingClip.samples - lastPosition);
                if (current > 0)
                {
                    AppendFrames(0, current);
                }
            }
            lastPosition = current;
            FlushFullChunks();
        }

        private void AppendFrames(int offset, int frameCount)
        {
            if (recordingClip == null || frameCount <= 0)
            {
                return;
            }

            var interleaved = new float[frameCount * sourceChannels];
            if (!recordingClip.GetData(interleaved, offset))
            {
                Status = "Microphone read failed";
                return;
            }
            for (var frame = 0; frame < frameCount; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < sourceChannels; channel++)
                {
                    sum += interleaved[frame * sourceChannels + channel];
                }
                pendingMono.Add(sum / sourceChannels);
            }
        }

        private void FlushFullChunks()
        {
            var sourceFrames = Pcm16CaptureUtility.FramesForDuration(sourceSampleRate, chunkMilliseconds);
            var chunkSeconds = sourceSampleRate <= 0 ? 0f : sourceFrames / (float)sourceSampleRate;
            while (IsMonitoring && sourceFrames > 0 && pendingMono.Count >= sourceFrames)
            {
                if (IsRecording)
                {
                    if (!SendSourceFrames(sourceFrames))
                    {
                        CancelRecording();
                        Status = "Voice upload queue full";
                        RecordMicrophoneStage("failed", "audio_upload_backpressure");
                        Debug.LogWarning("[VoiceInput] PCM upload queue rejected a chunk.", this);
                        return;
                    }
                    continue;
                }

                var level = CalculateRms(pendingMono, sourceFrames);
                InputLevel = level;
                var canActivate = alwaysListening && conversation != null &&
                    conversation.CanStartVoiceInput;
                if (!canActivate)
                {
                    pendingMono.RemoveRange(0, sourceFrames);
                    preRollMono.Clear();
                    activityGate?.ResetActivation();
                    Status = alwaysListening && conversation != null && conversation.State != ConversationState.Idle
                        ? "Listening for speech during reply"
                        : alwaysListening ? "Listening | backend offline" : "Microphone ready";
                    continue;
                }

                AppendPreRoll(sourceFrames);
                var activate = activityGate != null && activityGate.Observe(level, chunkSeconds, true);
                Status = "Listening for speech";
                if (activate && BeginActiveVoiceTurn(true))
                {
                    // The next loop iteration sends the pre-roll now inserted
                    // into pendingMono; no silent backend turn is created.
                    continue;
                }
            }
        }

        private void AppendPreRoll(int frameCount)
        {
            preRollMono.AddRange(pendingMono.GetRange(0, frameCount));
            pendingMono.RemoveRange(0, frameCount);
            var maximum = Mathf.Max(frameCount, Mathf.RoundToInt(sourceSampleRate * voicePreRollSeconds));
            if (preRollMono.Count > maximum)
            {
                preRollMono.RemoveRange(0, preRollMono.Count - maximum);
            }
        }

        public static float CalculateRms(IList<float> samples, int count)
        {
            var usable = Mathf.Min(samples == null ? 0 : samples.Count, Mathf.Max(0, count));
            if (usable <= 0)
            {
                return 0f;
            }
            var sumSquares = 0f;
            for (var index = 0; index < usable; index++)
            {
                sumSquares += samples[index] * samples[index];
            }
            return Mathf.Sqrt(sumSquares / usable);
        }

        private bool FlushRemainder()
        {
            var minimumFrames = Pcm16CaptureUtility.FramesForDuration(sourceSampleRate, 40);
            if (pendingMono.Count >= minimumFrames)
            {
                return SendSourceFrames(pendingMono.Count);
            }

            pendingMono.Clear();
            return true;
        }

        private bool SendSourceFrames(int frameCount)
        {
            var source = pendingMono.GetRange(0, frameCount);
            pendingMono.RemoveRange(0, frameCount);
            var encodeStartedAt = Stopwatch.GetTimestamp();
            var pcm16 = Pcm16CaptureUtility.ResampleAndEncode(source, sourceSampleRate, TargetSampleRate);
            var encodeMs = Mathf.Clamp(
                (int)System.Math.Round((Stopwatch.GetTimestamp() - encodeStartedAt) * 1000d / Stopwatch.Frequency),
                0,
                3600000);
            pcmEncodeCount++;
            pcmEncodeTotalMs += encodeMs;
            pcmEncodeMaxMs = Mathf.Max(pcmEncodeMaxMs, encodeMs);
            if (pcm16.Length == 0)
            {
                return true;
            }

            var sumSquares = 0f;
            for (var index = 0; index < source.Count; index++)
            {
                sumSquares += source[index] * source[index];
            }
            InputLevel = Mathf.Sqrt(sumSquares / source.Count);
            if (activityGate == null ? InputLevel >= voiceSilenceRms : activityGate.IsSpeech(InputLevel))
            {
                detectedSpeech = true;
                lastVoiceAt = Time.unscaledTime;
            }
            var accepted = conversation != null && conversation.PushVoiceAudio(pcm16);
            if (accepted)
            {
                LastTurnPcmBytes += pcm16.Length;
                LastTurnChunkCount++;
            }
            return accepted;
        }

        public static bool ShouldStopForSilence(
            bool speechDetected,
            float recordingSeconds,
            float silentSeconds,
            float minimumSeconds,
            float trailingSilenceSeconds,
            float initialTimeoutSeconds)
        {
            if (recordingSeconds < Mathf.Max(.2f, minimumSeconds))
            {
                return false;
            }

            return speechDetected
                ? silentSeconds >= Mathf.Max(.4f, trailingSilenceSeconds)
                : recordingSeconds >= Mathf.Max(1.5f, initialTimeoutSeconds);
        }

        private void StopMicrophoneOnly()
        {
            if (!string.IsNullOrEmpty(deviceName) && Microphone.IsRecording(deviceName))
            {
                Microphone.End(deviceName);
            }
            recordingClip = null;
            deviceName = null;
            pendingMono.Clear();
            preRollMono.Clear();
            activityGate?.Reset();
            InputLevel = 0f;
            IsRecording = false;
            IsMonitoring = false;
        }

        private void RecreateActivityGate()
        {
            activityGate = new VoiceActivityGate(
                voiceSilenceRms,
                Mathf.Max(voiceSilenceRms, Mathf.Min(.018f, voiceSilenceRms * 2.5f)),
                voiceActivationSeconds);
        }

        private void RecordMicrophoneStage(string status, string code = "")
        {
            diagnostics = diagnostics != null ? diagnostics : GetComponent<RuntimeDebugLog>();
            diagnostics?.RecordStage("microphone", status, code);
        }

        private void OnDisable()
        {
            if (IsRecording)
            {
                conversation?.CancelVoiceInput();
            }
            StopMicrophoneOnly();
        }
    }
}
