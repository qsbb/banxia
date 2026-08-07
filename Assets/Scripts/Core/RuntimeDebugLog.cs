using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace QuestMmdPlayer
{
    [DisallowMultipleComponent]
    public sealed class RuntimeDebugLog : MonoBehaviour
    {
        [SerializeField, Range(12, 96)] private int capacity = 48;

        private readonly Queue<string> entries = new Queue<string>();
        private static readonly string[] AllowedPrefixes =
        {
            "[QuestMmdPlayer]",
            "[Conversation]",
            "[VoiceInput]",
            "[AstrBotBridge]",
            "[BackendPairing]",
            "[HumanInteraction]",
            "[TouchInteraction]",
            "[AvatarPlacement]",
            "[VmdActionLibrary]",
            "[FileImport]",
            "[Passthrough]",
            "[RuntimeDebug]"
        };

        public bool DisplayEnabled { get; private set; }
        public int Count => entries.Count;

        private void OnEnable()
        {
            Application.logMessageReceived += HandleLog;
            Record("RuntimeDebug", "前端诊断已就绪");
        }

        private void OnDisable()
        {
            Application.logMessageReceived -= HandleLog;
        }

        public void SetDisplayEnabled(bool enabled)
        {
            DisplayEnabled = enabled;
            Record("RuntimeDebug", enabled ? "菜单日志已开启" : "菜单日志已关闭");
        }

        public void ToggleDisplay()
        {
            SetDisplayEnabled(!DisplayEnabled);
        }

        public void Record(string category, string message)
        {
            var safeCategory = string.IsNullOrWhiteSpace(category) ? "App" : category.Trim();
            Add($"{Time.unscaledTime,6:F1}s [{safeCategory}] {Sanitize(message)}");
        }

        public string GetRecentText(int maximumLines = 5)
        {
            var snapshot = entries.ToArray();
            var first = Mathf.Max(0, snapshot.Length - Mathf.Max(1, maximumLines));
            var builder = new StringBuilder();
            for (var index = first; index < snapshot.Length; index++)
            {
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(snapshot[index]);
            }
            return builder.ToString();
        }

        private void HandleLog(string condition, string stackTrace, LogType type)
        {
            if (!ShouldCapture(condition, type))
            {
                return;
            }

            Add($"{Time.unscaledTime,6:F1}s {TypeLabel(type)} {Sanitize(condition)}");
        }

        private void Add(string entry)
        {
            entries.Enqueue(entry);
            while (entries.Count > Mathf.Max(12, capacity))
            {
                entries.Dequeue();
            }
        }

        private static bool ShouldCapture(string condition, LogType type)
        {
            if (string.IsNullOrWhiteSpace(condition))
            {
                return false;
            }
            for (var index = 0; index < AllowedPrefixes.Length; index++)
            {
                if (condition.StartsWith(AllowedPrefixes[index], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return type == LogType.Error || type == LogType.Exception;
        }

        private static string Sanitize(string value)
        {
            var result = string.IsNullOrWhiteSpace(value)
                ? "(empty)"
                : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            var lower = result.ToLowerInvariant();
            if (lower.Contains("authorization") || lower.Contains("api_key") ||
                lower.Contains("apikey") || lower.Contains("bridge_key") ||
                lower.Contains("bearer ") || lower.Contains("secret") ||
                lower.Contains("token="))
            {
                return "[敏感详情已隐藏]";
            }
            return result.Length <= 180 ? result : result.Substring(0, 177) + "...";
        }

        private static string TypeLabel(LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Exception:
                    return "[ERR]";
                case LogType.Warning:
                    return "[WARN]";
                default:
                    return "[LOG]";
            }
        }
    }
}