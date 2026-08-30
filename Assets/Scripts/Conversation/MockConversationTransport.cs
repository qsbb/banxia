using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// A deterministic frontend-only transport. It exercises the same event
    /// path as AstrBot without requiring a server, microphone, or network.
    /// </summary>
    public sealed class MockConversationTransport : MonoBehaviour, IConversationTransport
    {
        private const int SampleRate = 24000;
        private const int ChunkSamples = 960;
        private const int ChunkCount = 40;

        private Coroutine activeRoutine;
        private string activeTurnId = string.Empty;
        private int bufferedInputBytes;

        public event Action<ConversationEvent> EventReceived;

        public bool IsConnected => true;
        public string Status => "Local mock transport ready";
        public List<AvatarActionReceipt> ActionReceipts { get; } = new List<AvatarActionReceipt>();

        public void StartTurn(string turnId, string userText)
        {
            StartTurn(turnId, userText, null);
        }

        public void StartTurn(string turnId, string userText, TurnImageAttachment attachment)
        {
            // 本地演示链路不消费摄像头单帧：回执文案如实体现在回复里。
            var text = userText ?? string.Empty;
            if (attachment != null)
            {
                text += "\n" + RealityCameraTurn.ComposeFailureReceipt("本地演示链路不支持摄像头画面");
            }
            StopActiveRoutine();
            activeTurnId = turnId ?? string.Empty;
            activeRoutine = StartCoroutine(StreamReply(activeTurnId, text));
        }

        public bool BeginAudioTurn(string turnId)
        {
            if (string.IsNullOrEmpty(turnId))
            {
                return false;
            }

            StopActiveRoutine();
            activeTurnId = turnId;
            bufferedInputBytes = 0;
            return true;
        }

        public bool QueueAudioChunk(string turnId, byte[] pcm16)
        {
            if (!string.Equals(turnId, activeTurnId, StringComparison.Ordinal) ||
                pcm16 == null || pcm16.Length == 0 || pcm16.Length > 16000 ||
                (pcm16.Length & 1) != 0)
            {
                return false;
            }

            bufferedInputBytes += pcm16.Length;
            return true;
        }

        public bool EndAudioTurn(string turnId)
        {
            if (!string.Equals(turnId, activeTurnId, StringComparison.Ordinal))
            {
                return false;
            }

            activeRoutine = StartCoroutine(StreamReply(
                activeTurnId,
                bufferedInputBytes > 0 ? "mock microphone input" : ""));
            return true;
        }

        public void Interrupt(string turnId)
        {
            if (!string.Equals(turnId, activeTurnId, StringComparison.Ordinal))
            {
                return;
            }

            StopActiveRoutine();
            activeTurnId = string.Empty;
            bufferedInputBytes = 0;
        }

        public string SendInteraction(string interactionName, string phase, float strength, int durationMs = 0, string hand = "none")
        {
            if (!string.Equals(phase, "start", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var emotion = interactionName == "cheek_pinch" ? "shy" : "happy";
            Emit(string.Empty, ConversationEventType.AvatarIntent, emotion: emotion, gesture: interactionName);
            StartCoroutine(EndInteractionReaction());
            return string.Empty;
        }

        public bool SendActionResult(AvatarActionReceipt receipt)
        {
            if (receipt == null) return false;
            ActionReceipts.Add(receipt);
            return true;
        }

        private IEnumerator StreamReply(string turnId, string userText)
        {
            yield return new WaitForSecondsRealtime(.18f);
            Emit(turnId, ConversationEventType.AsrPartial, ShortPreview(userText));
            yield return new WaitForSecondsRealtime(.18f);
            Emit(turnId, ConversationEventType.AsrFinal, userText);
            Emit(turnId, ConversationEventType.Thinking);

            yield return new WaitForSecondsRealtime(.45f);
            Emit(turnId, ConversationEventType.AvatarIntent, emotion: "happy", gesture: "talk", lookAt: "user");
            Emit(turnId, ConversationEventType.ReplyTextDelta, "我听见了。 ");
            yield return new WaitForSecondsRealtime(.12f);
            Emit(turnId, ConversationEventType.ReplyTextDelta, "这是一段可以随时打断的前端模拟回复。");

            for (var chunkIndex = 0; chunkIndex < ChunkCount; chunkIndex++)
            {
                if (!string.Equals(turnId, activeTurnId, StringComparison.Ordinal))
                {
                    yield break;
                }

                EventReceived?.Invoke(new ConversationEvent
                {
                    Type = ConversationEventType.AudioChunk,
                    TurnId = turnId,
                    Pcm16 = CreateVoiceLikeChunk(chunkIndex),
                    SampleRate = SampleRate
                });
                yield return new WaitForSecondsRealtime((float)ChunkSamples / SampleRate);
            }

            Emit(turnId, ConversationEventType.AvatarIntent, emotion: "neutral", gesture: "idle", lookAt: "none");
            Emit(turnId, ConversationEventType.ReplyEnd);
            activeRoutine = null;
            activeTurnId = string.Empty;
        }

        private void Emit(string turnId, ConversationEventType type, string text = null, string emotion = null, string gesture = null, string lookAt = null)
        {
            EventReceived?.Invoke(new ConversationEvent
            {
                Type = type,
                TurnId = turnId,
                Text = text,
                Emotion = emotion,
                Gesture = gesture,
                LookAt = lookAt
            });
        }

        private static short[] CreateVoiceLikeChunk(int chunkIndex)
        {
            var result = new short[ChunkSamples];
            var firstSample = chunkIndex * ChunkSamples;
            for (var i = 0; i < result.Length; i++)
            {
                var t = (firstSample + i) / (double)SampleRate;
                var syllable = .22 + .78 * Math.Abs(Math.Sin(Math.PI * 3.1 * t));
                var attack = Math.Min(1.0, (i + 1) / 80.0);
                var release = Math.Min(1.0, (result.Length - i) / 100.0);
                var voice = Math.Sin(2.0 * Math.PI * (165.0 + 18.0 * Math.Sin(t * 4.3)) * t)
                    + .32 * Math.Sin(2.0 * Math.PI * 330.0 * t)
                    + .12 * Math.Sin(2.0 * Math.PI * 660.0 * t);
                var sample = voice * syllable * attack * release * .085;
                result[i] = (short)Mathf.Clamp((float)(sample * short.MaxValue), short.MinValue, short.MaxValue);
            }
            return result;
        }

        private static string ShortPreview(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= 6)
            {
                return text ?? string.Empty;
            }
            return text.Substring(0, 6) + "...";
        }

        private IEnumerator EndInteractionReaction()
        {
            yield return new WaitForSecondsRealtime(2f);
            Emit(string.Empty, ConversationEventType.AvatarIntent, emotion: "neutral", gesture: "idle");
        }

        private void OnDisable()
        {
            StopActiveRoutine();
        }

        private void StopActiveRoutine()
        {
            if (activeRoutine == null)
            {
                return;
            }
            StopCoroutine(activeRoutine);
            activeRoutine = null;
        }
    }
}
