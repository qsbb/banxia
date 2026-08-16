using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Arbitrates whole-body gestures without owning any bones. Explicit touch,
    /// imported motion, speech presentation, and idle behavior remain separate layers.
    /// </summary>
    public sealed class AvatarBehaviorCoordinator
    {
        private const float GestureLockSeconds = 1.2f;
        private const float RepeatGestureCooldownSeconds = 6f;
        private const float MinimumIdleIntervalSeconds = 18f;
        private const float MaximumIdleIntervalSeconds = 32f;

        private string lastGesture = string.Empty;
        private float gestureLockUntil;
        private float repeatGestureBlockedUntil;
        private float nextIdleBehaviorAt;

        public string LastGesture => lastGesture;
        public float NextIdleBehaviorAt => nextIdleBehaviorAt;

        public void Reset(float now, float random01 = .5f)
        {
            lastGesture = string.Empty;
            gestureLockUntil = now;
            repeatGestureBlockedUntil = now;
            ScheduleNextIdle(now, random01);
        }

        public bool TryAcceptIntent(
            string gesture,
            bool semanticContact,
            bool importedMotionBusy,
            float now,
            out string acceptedGesture)
        {
            acceptedGesture = string.Empty;
            var normalized = string.IsNullOrWhiteSpace(gesture)
                ? "idle"
                : gesture.ToLowerInvariant();
            if (normalized == "lie")
            {
                normalized = "lie_down";
            }

            if (normalized == "handshake" || normalized == "head_pat" ||
                normalized == "cheek_pinch" || normalized == "talk")
            {
                acceptedGesture = normalized;
                return true;
            }

            if (normalized != "wave" && normalized != "bow" && normalized != "dance" &&
                normalized != "dance_next" && normalized != "nod" && normalized != "sway" &&
                normalized != "raise_hand" && normalized != "turn_half" &&
                normalized != "crouch" &&
                normalized != "refuse" && normalized != "step_back" &&
                normalized != "sit" && normalized != "lie_down" && normalized != "idle")
            {
                return false;
            }
            var canSwitchImportedDance = importedMotionBusy &&
                (normalized == "dance" || normalized == "dance_next");
            if (semanticContact || (importedMotionBusy && !canSwitchImportedDance) ||
                (now < gestureLockUntil && !canSwitchImportedDance))
            {
                return false;
            }
            if (normalized == lastGesture && now < repeatGestureBlockedUntil)
            {
                return false;
            }

            acceptedGesture = normalized;
            lastGesture = normalized;
            gestureLockUntil = now + GestureLockSeconds;
            repeatGestureBlockedUntil = now + RepeatGestureCooldownSeconds;
            nextIdleBehaviorAt = Mathf.Max(
                nextIdleBehaviorAt,
                now + MinimumIdleIntervalSeconds);
            return true;
        }

        public void DeferIdle(float now, float random01)
        {
            ScheduleNextIdle(now, random01);
        }

        public bool TryTakeIdleBehavior(
            ConversationState conversationState,
            bool semanticContact,
            bool importedMotionBusy,
            string currentAction,
            float now,
            float random01,
            out string gesture)
        {
            gesture = string.Empty;
            if (!CanRunIdleBehavior(
                    conversationState,
                    semanticContact,
                    importedMotionBusy,
                    currentAction) ||
                now < nextIdleBehaviorAt)
            {
                return false;
            }

            gesture = "sway";
            lastGesture = gesture;
            gestureLockUntil = now + GestureLockSeconds;
            repeatGestureBlockedUntil = now + RepeatGestureCooldownSeconds;
            ScheduleNextIdle(now, random01);
            return true;
        }

        public static bool CanRunIdleBehavior(
            ConversationState conversationState,
            bool semanticContact,
            bool importedMotionBusy,
            string currentAction)
        {
            return conversationState == ConversationState.Idle &&
                !semanticContact &&
                !importedMotionBusy &&
                string.Equals(currentAction, "idle", System.StringComparison.Ordinal);
        }

        private void ScheduleNextIdle(float now, float random01)
        {
            nextIdleBehaviorAt = now + Mathf.Lerp(
                MinimumIdleIntervalSeconds,
                MaximumIdleIntervalSeconds,
                Mathf.Clamp01(random01));
        }
    }
}
