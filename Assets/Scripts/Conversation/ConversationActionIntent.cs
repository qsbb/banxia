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

            if (ContainsAny(value, "换个舞蹈", "换一个舞蹈", "换支舞", "换一支舞", "另一个舞蹈", "下一支舞", "different dance", "another dance"))
            {
                action = "dance_next";
                return true;
            }
            if (ContainsAny(value, "sit down", "sit", "\u5750\u4e0b\u6765", "\u5750\u7740", "\u5750\u4e00\u4e0b"))
            {
                action = "sit";
                return true;
            }
            if (ContainsAny(value, "lie down", "lie", "\u8eba\u4e0b", "\u8eba\u4e0b\u6765", "\u8eba\u5230", "\u8eba\u5230\u5e8a\u4e0a", "\u8eba\u5728\u5e8a\u4e0a", "\u8eba\u7740"))
            {
                action = "lie_down";
                return true;
            }
            if (ContainsAny(value, "跳舞", "舞蹈", "跳个舞", "跳一支", "来段舞", "帮我跳", "让她跳", "让角色跳", "dance"))
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
            if (ContainsAny(value, "抬手", "举手", "抬起手", "抬起来", "抬起來", "raise hand"))
            {
                action = "raise_hand";
                return true;
            }
            if (ContainsAny(value, "抬起单腿", "抬起一条腿", "抬腿", "抬起腿", "单腿站立", "raise one leg", "lift one leg", "raise your leg", "lift your leg"))
            {
                action = "raise_leg";
                return true;
            }
            if (ContainsAny(value, "转半圈", "转身", "转个身", "转一百八十度", "turn around", "half turn"))
            {
                action = "turn_half";
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
