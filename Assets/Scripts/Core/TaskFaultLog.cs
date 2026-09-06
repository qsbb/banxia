using System;
using System.Threading.Tasks;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>Observes detached operations and exposes debug faults to Unity's context.</summary>
    public static class TaskFaultLog
    {
        public static async void Forget(this Task task, string context)
        {
            if (task == null)
            {
                return;
            }

            try
            {
                // Capture the caller's Unity context instead of throwing in an
                // unobserved continuation task on the thread pool.
                await task;
            }
            catch (OperationCanceledException) when (task.IsCanceled)
            {
                // Explicit cancellation is not an operation failure.
            }
            catch (Exception exception)
            {
                Debug.LogError("[TaskFault][" + (context ?? "task") + "] " +
                    ((Exception)task.Exception ?? exception));
                QuestDebugMode.RethrowIfEnabled(exception, context);
            }
        }
    }
}
