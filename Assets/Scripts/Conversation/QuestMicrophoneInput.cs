using System.Collections.Generic;
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

        [SerializeField, Range(40, 100)] private int chunkMilliseconds = 80;
        [SerializeField, Range(2, 20)] private int clipBufferSeconds = 10;
        [SerializeField, Range(5, 60)] private int maxRecordingSeconds = 30;
        [SerializeField] private bool enableTrackedVoiceGesture = true;
        [SerializeField, Range(.2f, .8f)] private float trackedVoiceHoldSeconds = .35f;
        [SerializeField] private bool autoStopOnSilence = true;
        [SerializeField, Range(.003f, .08f)] private float voiceSilenceRms = .008f;
        [SerializeField, Range(.4f, 2.5f)] private float voiceSilenceSeconds = 1.15f;
        [SerializeField, Range(.2f, 1f)] private float minimumVoiceSeconds = .45f;

        private readonly List<float> pendingMono = new List<float>(4096);
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
        private CompanionWorldMenu menu;
        private AvatarTouchInteraction touchInteraction;

        public bool IsRecording { get; private set; }
        public float InputLevel { get; private set; }
        public string Status { get; private set; } = "Microphone ready";
        public string ShortStatus => IsRecording ? "REC" : Status.StartsWith("Microphone ready") ? "READY" : "OFF";

        public void Bind(ConversationController owner)
        {
            conversation = owner;
        }

        private void Awake()
        {
            conversation = GetComponent<ConversationController>();
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

            if (!IsRecording)
            {
                return;
            }

            CaptureAvailableFrames();
            if (Time.unscaledTime - recordingStartedAt >= maxRecordingSeconds)
            {
                StopAndSend();
            }
            else if (autoStopOnSilence && Time.unscaledTime - recordingStartedAt >= Mathf.Max(.2f, minimumVoiceSeconds) &&
                Time.unscaledTime - lastVoiceAt >= Mathf.Max(.4f, voiceSilenceSeconds))
            {
                StopAndSend();
            }
        }

        private bool ReadTrackedVoicePinch(out bool trackedHand)
        {
            trackedHand = false;
            if (!enableTrackedVoiceGesture)
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

        public bool StartRecording()
        {
            if (IsRecording)
            {
                return true;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                Permission.RequestUserPermission(Permission.Microphone);
                Status = "Microphone permission required";
                return false;
            }
#endif
            conversation = conversation != null ? conversation : GetComponent<ConversationController>();
            if (conversation == null || !conversation.CanStartVoiceInput)
            {
                Status = "Conversation backend offline";
                return false;
            }
            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                Status = "Microphone unavailable";
                return false;
            }

            deviceName = Microphone.devices[0];
            recordingClip = Microphone.Start(deviceName, true, clipBufferSeconds, TargetSampleRate);
            if (recordingClip == null)
            {
                Status = "Microphone could not start";
                return false;
            }
            if (!conversation.BeginVoiceInput())
            {
                StopMicrophoneOnly();
                Status = "Conversation backend offline";
                return false;
            }

            sourceSampleRate = recordingClip.frequency;
            sourceChannels = Mathf.Max(1, recordingClip.channels);
            lastPosition = Mathf.Max(0, Microphone.GetPosition(deviceName));
            pendingMono.Clear();
            InputLevel = 0f;
            recordingStartedAt = Time.unscaledTime;
            lastVoiceAt = recordingStartedAt;
            IsRecording = true;
            Status = "Recording voice";
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
                return;
            }
            StopMicrophoneOnly();
            conversation?.EndVoiceInput();
            Status = "Voice sent";
        }

        public void CancelRecording()
        {
            if (!IsRecording)
            {
                return;
            }

            StopMicrophoneOnly();
            conversation?.CancelVoiceInput();
            Status = "Recording cancelled";
        }

        private void CaptureAvailableFrames()
        {
            if (!IsRecording || recordingClip == null)
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
            while (IsRecording && sourceFrames > 0 && pendingMono.Count >= sourceFrames)
            {
                if (!SendSourceFrames(sourceFrames))
                {
                    CancelRecording();
                    Status = "Voice upload queue full";
                    return;
                }
            }
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
            var pcm16 = Pcm16CaptureUtility.ResampleAndEncode(source, sourceSampleRate, TargetSampleRate);
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
            if (InputLevel >= voiceSilenceRms)
            {
                lastVoiceAt = Time.unscaledTime;
            }
            return conversation != null && conversation.PushVoiceAudio(pcm16);
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
            InputLevel = 0f;
            IsRecording = false;
        }

        private void OnDisable()
        {
            CancelRecording();
        }
    }
}
