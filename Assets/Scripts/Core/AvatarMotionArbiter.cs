using System;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Single policy for deciding which animation source may replace another.
    /// The class is deliberately engine-free so the same rules can be tested
    /// without a running headset or Unity scene.
    /// </summary>
    public enum AvatarActionSource
    {
        Unknown = 0,
        Idle = 10,
        Touch = 80,
        Backend = 60,
        Manual = 70,
        Imported = 90,
        System = 100
    }

    public readonly struct AvatarMotionDecision
    {
        public AvatarMotionDecision(bool accepted, string reason)
        {
            Accepted = accepted;
            Reason = reason ?? string.Empty;
        }

        public bool Accepted { get; }
        public string Reason { get; }
    }

    public static class AvatarMotionArbiter
    {
        public static AvatarMotionDecision Decide(
            AvatarActionSource currentSource,
            AvatarActionSource requestedSource,
            string currentAction,
            string requestedAction,
            bool importedMotionBusy)
        {
            var current = Normalize(currentAction);
            var requested = Normalize(requestedAction);
            if (requested.Length == 0)
            {
                return new AvatarMotionDecision(false, "empty_action");
            }

            // System cleanup is always allowed to restore the baseline after
            // imported playback, but idle behavior cannot interrupt it.
            if (requested == "idle" && requestedSource == AvatarActionSource.System)
            {
                return new AvatarMotionDecision(true, "system_restore");
            }

            if (importedMotionBusy && requestedSource != AvatarActionSource.Imported &&
                requestedSource != AvatarActionSource.System)
            {
                return new AvatarMotionDecision(false, "imported_motion_busy");
            }

            if (current == requested && currentSource == requestedSource)
            {
                return new AvatarMotionDecision(true, "same_action_refresh");
            }

            if ((int)requestedSource < (int)currentSource && current != "idle")
            {
                return new AvatarMotionDecision(false, "lower_priority_than_current");
            }

            return new AvatarMotionDecision(true, "accepted");
        }

        public static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        }
    }
}
