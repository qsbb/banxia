using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>Shared easing for actions, touch responses, and future IK layers.</summary>
    public static class NaturalMotionTransition
    {
        public static float Smooth01(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * (3f - 2f * value);
        }

        public static float UpdateWeight(
            float current,
            float target,
            ref float velocity,
            float enterSeconds,
            float exitSeconds,
            float deltaTime)
        {
            var smoothTime = target > current ? enterSeconds : exitSeconds;
            return Mathf.SmoothDamp(
                current,
                Mathf.Clamp01(target),
                ref velocity,
                Mathf.Max(.01f, smoothTime),
                Mathf.Infinity,
                Mathf.Max(0f, deltaTime));
        }
    }
}
