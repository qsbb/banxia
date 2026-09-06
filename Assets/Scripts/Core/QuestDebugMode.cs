using System;
using System.Runtime.ExceptionServices;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// 排错总开关（用户钦定的 debug 模式）：开启后，被 catch 吞掉的异常会以
    /// 完整堆栈打印、关键守卫的静默退出会打印原因，便于真机定位问题。
    /// 默认关闭；状态持久化在 PlayerPrefs 键 <c>banxia.phone.debug-mode</c>。
    /// 语义：捕获异常时先打印完整堆栈，再重新抛出，禁止调用方继续其兜底流程；
    /// 关闭时则完全保持现有的用户友好错误处理。
    /// </summary>
    public static class QuestDebugMode
    {
        public const string PrefKey = "banxia.phone.debug-mode";

        private static volatile bool cached;
        private static volatile bool cachedValue;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void LoadPreference()
        {
            cachedValue = PlayerPrefs.GetInt(PrefKey, 0) == 1;
            cached = true;
        }

        /// <summary>调试模式是否开启（带缓存，避免热路径反复读 PlayerPrefs）。</summary>
        public static bool Enabled
        {
            get
            {
                if (!cached)
                {
                    cachedValue = PlayerPrefs.GetInt(PrefKey, 0) == 1;
                    cached = true;
                }

                return cachedValue;
            }
        }

        /// <summary>写入开关并刷新缓存。</summary>
        public static void SetEnabled(bool value)
        {
            PlayerPrefs.SetInt(PrefKey, value ? 1 : 0);
            PlayerPrefs.Save();
            cachedValue = value;
            cached = true;
        }

        /// <summary>
        /// 调试模式下打印异常的完整堆栈（含内部异常）。任何"吞异常继续跑"
        /// 的 catch 都应先调用本方法再走原兜底；返回是否打印了日志。
        /// </summary>
        public static bool Report(Exception exception, string context)
        {
            if (!Enabled || exception == null)
            {
                return false;
            }

            // Exception.ToString() includes the full stack and inner exceptions while
            // keeping this one diagnostic record atomic for logcat filtering.
            Debug.LogError("[DebugMode][" + context + "] " + exception);
            return true;
        }

        /// <summary>
        /// 调用方记录异常后，Debug 模式下重新抛出原异常；关闭时不做任何事，
        /// 让调用方继续现有的用户友好兜底。
        /// </summary>
        public static void RethrowIfEnabled(Exception exception, string context)
        {
            if (!Enabled || exception == null)
            {
                return;
            }

            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        /// <summary>调试模式下打印一次性守卫跳过原因（不受 hasLogged 类去重限制）。</summary>
        public static void LogGuard(string context, string reason)
        {
            if (Enabled)
            {
                Debug.LogWarning("[DebugMode][" + context + "] skip: " + reason);
            }
        }

        /// <summary>调试模式下的流程日志（进入/绑定/恢复等关键节点）。</summary>
        public static void Log(string message)
        {
            if (Enabled)
            {
                Debug.Log("[DebugMode] " + message);
            }
        }
    }
}
