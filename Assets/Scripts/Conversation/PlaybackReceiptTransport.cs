using System;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Optional additive transport contract for real client playback facts.
    /// It stays outside IConversationTransport so legacy and mock transports
    /// remain source-compatible.
    /// </summary>
    public interface IPlaybackReceiptTransport
    {
        bool SendPlaybackReceipt(PlaybackReceipt receipt);
    }

    public enum PlaybackReceiptKind
    {
        Started,
        Progress,
        Ended,
        Interrupted
    }

    /// <summary>
    /// Bounded, privacy-safe device playback state. Identifiers refer only to
    /// the already active bridge session, turn and generated speech stream.
    /// </summary>
    public sealed class PlaybackReceipt
    {
        public string TurnId;
        public string SpeechId;
        public PlaybackReceiptKind Kind;
        public int PlayedMs;
        public int BufferedMs;
        public int UnderflowCount;
        public string ReasonCode;

        public PlaybackReceipt(
            string turnId,
            string speechId,
            PlaybackReceiptKind kind,
            int playedMs = 0,
            int bufferedMs = 0,
            int underflowCount = 0,
            string reasonCode = "")
        {
            TurnId = turnId ?? string.Empty;
            SpeechId = speechId ?? string.Empty;
            Kind = kind;
            PlayedMs = ClampDuration(playedMs);
            BufferedMs = ClampDuration(bufferedMs);
            // Keep the client payload within the bridge's bounded schema.
            UnderflowCount = Math.Max(0, Math.Min(100000, underflowCount));
            ReasonCode = SanitizeReason(reasonCode);
        }

        private static int ClampDuration(int value)
        {
            return Math.Max(0, Math.Min(3600000, value));
        }

        private static string SanitizeReason(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            return value.Length <= 64 ? value : value.Substring(0, 64);
        }
    }
}
