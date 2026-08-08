using System;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Small, conservative fallback for explicit user action requests.
    /// AstrBot remains the primary semantic source; this is only used when
    /// the normal chain returns no executable avatar.intent.
    /// </summary>
    public static class ConversationActionIntent
    {
        public static bool TryDetect(string text, out string action)
        {
            action = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var value = text.Trim().ToLowerInvariant();
            if (ContainsAny(value, "不要跳舞", "别跳舞", "不用跳舞", "不需要跳舞"))
            {
                return false;
            }

            if (ContainsAny(value, "跳舞", "舞蹈", "跳个舞", "跳一支", "来段舞", "dance"))
            {
                action = "dance";
                return true;
            }
            if (ContainsAny(value, "挥手", "招手", "wave"))
            {
                action = "wave";
                return true;
            }
            if (ContainsAny(value, "鞠躬", "bow"))
            {
                action = "bow";
                return true;
            }
            if (ContainsAny(value, "点头", "nod"))
            {
                action = "nod";
                return true;
            }
            return false;
        }

        private static bool ContainsAny(string value, params string[] candidates)
        {
            for (var index = 0; index < candidates.Length; index++)
            {
                if (value.IndexOf(candidates[index], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
