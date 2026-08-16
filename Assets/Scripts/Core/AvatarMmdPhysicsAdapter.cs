using System;
using UMT;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Bridges tracked hand probes into the existing UMT Bullet world by using
    /// its public PMX rigid-body build path. The adapter never creates a second
    /// physics world and degrades silently when a model has no UMT manager.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10900)]
    public sealed class AvatarMmdPhysicsAdapter : MonoBehaviour
    {
        [SerializeField] private bool enabledForRuntime = true;
        [SerializeField, Range(.006f, .08f)] private float palmRadius = .034f;
        [SerializeField, Range(.004f, .035f)] private float fingertipRadius = .014f;

        private AvatarController avatar;
        private QuestTrackedHandVisualizer trackedHands;
        private MMDPhysicsManager physicsManager;
        private Renderer[] avatarRenderers = Array.Empty<Renderer>();
        private bool configured;
        private bool highFrequencyContact;
        private int updateParity;
        private int activeProbeCount;
        private string status = "等待角色物理管理器";

        public string Status => status;
        public int ActiveProbeCount => activeProbeCount;
        public bool IsConfigured => configured && physicsManager != null;

        public void SetHighFrequencyContact(bool enabled)
        {
            highFrequencyContact = enabled;
            updateParity = 0;
        }

        public void Bind(AvatarController targetAvatar, QuestTrackedHandVisualizer sourceHands)
        {
            avatar = targetAvatar;
            trackedHands = sourceHands;
            physicsManager = avatar == null
                ? null
                : avatar.GetComponentInChildren<MMDTransformManager>(true)?.physicsManager;
            avatarRenderers = avatar == null
                ? Array.Empty<Renderer>()
                : avatar.GetComponentsInChildren<Renderer>(true);
            configured = false;
            activeProbeCount = 0;
            status = physicsManager == null ? "当前角色没有 UMT 物理管理器" : "等待初始化外部手部刚体";
        }

        private void Update()
        {
            if (!enabledForRuntime || trackedHands == null || physicsManager == null)
            {
                return;
            }

            if (!highFrequencyContact && configured && (++updateParity & 1) != 0)
            {
                return;
            }

            if (!configured)
            {
                var radii = new float[QuestTrackedHandVisualizer.PhysicsProbeCount];
                for (var index = 0; index < radii.Length; index++)
                {
                    // Match the Bullet probe to the rendered/collision proxy
                    // radius. Falling back to serialized defaults keeps the
                    // adapter usable before XR has produced its first pose.
                    var fallback = index % 6 == 0 ? palmRadius : fingertipRadius;
                    radii[index] = trackedHands.TryGetPhysicsProbe(
                            index,
                            out _,
                            out var probeRadius,
                            out _)
                        ? Mathf.Clamp(probeRadius, .004f, .08f)
                        : fallback;
                }
                physicsManager.ConfigureExternalKinematicSpheres(radii);
                configured = physicsManager.externalKinematicSphereCount == radii.Length;
                status = configured
                    ? physicsManager.externalKinematicFullCoverage
                        ? "外部手部刚体已接入 UMT Bullet"
                        : "外部手部刚体已接入，模型碰撞组覆盖受限"
                    : "外部手部刚体初始化失败";
                Debug.Log("[MmdPhysicsAdapter] " + status + "，数量=" + radii.Length, this);
                if (!configured)
                {
                    return;
                }
            }

            activeProbeCount = 0;
            var hasAvatarBounds = TryCalculateAvatarBounds(out var avatarBounds);
            for (var index = 0; index < QuestTrackedHandVisualizer.PhysicsProbeCount; index++)
            {
                if (!trackedHands.TryGetPhysicsProbe(index, out var position, out var radius, out var active))
                {
                    active = false;
                }
                active = ShouldActivatePhysicsProbe(
                    active,
                    hasAvatarBounds,
                    avatarBounds,
                    position,
                    radius);
                physicsManager.SetExternalKinematicSpherePose(index, position, active);
                if (active) activeProbeCount++;
            }
        }

        public static bool ShouldActivatePhysicsProbe(
            bool trackedActive,
            bool hasAvatarBounds,
            Bounds avatarBounds,
            Vector3 position,
            float radius,
            float margin = .18f)
        {
            if (!trackedActive)
            {
                return false;
            }
            if (!hasAvatarBounds)
            {
                return true;
            }
            var distance = Mathf.Max(.005f, radius) + Mathf.Max(0f, margin);
            return avatarBounds.SqrDistance(position) <= distance * distance;
        }

        private bool TryCalculateAvatarBounds(out Bounds bounds)
        {
            bounds = default;
            var found = false;
            for (var index = 0; index < avatarRenderers.Length; index++)
            {
                var renderer = avatarRenderers[index];
                if (renderer == null)
                {
                    continue;
                }
                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
            return found;
        }

        private void OnDisable()
        {
            if (physicsManager == null || !configured)
            {
                return;
            }

            for (var index = 0; index < physicsManager.externalKinematicSphereCount; index++)
            {
                physicsManager.SetExternalKinematicSpherePose(index, Vector3.zero, false);
            }
            activeProbeCount = 0;
        }
    }
}
