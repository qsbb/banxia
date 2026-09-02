using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Platform-neutral renderer bounds helper shared by Phone and Quest framing.
    /// Particle renderers are excluded because transient effects must not move the
    /// semantic avatar composition.
    /// </summary>
    public static class RenderBoundsUtility
    {
        public static Bounds Compute(GameObject root)
        {
            var bounds = default(Bounds);
            if (root == null)
            {
                return bounds;
            }

            var renderers = root.GetComponentsInChildren<Renderer>();
            var any = false;
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer is ParticleSystemRenderer)
                {
                    continue;
                }
                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
            return bounds;
        }
    }
}
