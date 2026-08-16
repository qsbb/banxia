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
        private bool runtimeContactEnabled = true;
        private bool highFrequencyContact;
        private int updateParity;
        private int activeProbeCount;
        private readonly Vector3[] probePositions =
            new Vector3[QuestTrackedHandVisualizer.PhysicsProbeCount];
        private readonly float[] probeRadii =
            new float[QuestTrackedHandVisualizer.PhysicsProbeCount];
        private readonly bool[] probeTracked =
            new bool[QuestTrackedHandVisualizer.PhysicsProbeCount];
        private Bounds cachedAvatarBounds;
        private bool cachedAvatarBoundsAvailable;
        private float nextAvatarBoundsRefreshAt;
        private Vector3 cachedAvatarPosition;
        private Quaternion cachedAvatarRotation;
        private Vector3 cachedAvatarScale;
        private const float AvatarBoundsRefreshInterval = .1f;
        private string status = "等待角色物理管理器";

        public string Status => status;
        public int ActiveProbeCount => activeProbeCount;
        public bool IsConfigured => configured && physicsManager != null;
        internal bool RuntimeContactEnabled => runtimeContactEnabled;
        internal bool HighFrequencyContact => highFrequencyContact;

        internal void SetRuntimeContactEnabledForQa(bool enabled)
        {
            runtimeContactEnabled = enabled;
            updateParity = 0;
            if (!enabled)
            {
                DeactivateExternalProbes();
            }
        }

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
            cachedAvatarBoundsAvailable = false;
            nextAvatarBoundsRefreshAt = 0f;
            CaptureAvatarRootPose();
            status = physicsManager == null ? "当前角色没有 UMT 物理管理器" : "等待初始化外部手部刚体";
        }

        private void Update()
        {
            if (!enabledForRuntime || !runtimeContactEnabled ||
                trackedHands == null || physicsManager == null)
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

            var hasTrackedProbe = false;
            for (var index = 0; index < QuestTrackedHandVisualizer.PhysicsProbeCount; index++)
            {
                if (!trackedHands.TryGetPhysicsProbe(index, out var position, out var radius, out var active))
                {
                    active = false;
                }
                probePositions[index] = position;
                probeRadii[index] = radius;
                probeTracked[index] = active;
                hasTrackedProbe |= active;
            }
            if (!hasTrackedProbe)
            {
                DeactivateExternalProbes();
                return;
            }

            activeProbeCount = 0;
            var hasAvatarBounds = TryGetCachedAvatarBounds(out var avatarBounds);
            for (var index = 0; index < QuestTrackedHandVisualizer.PhysicsProbeCount; index++)
            {
                var active = probeTracked[index];
                active = ShouldActivatePhysicsProbe(
                    active,
                    hasAvatarBounds,
                    avatarBounds,
                    probePositions[index],
                    probeRadii[index]);
                physicsManager.SetExternalKinematicSpherePose(index, probePositions[index], active);
                if (active) activeProbeCount++;
            }
        }

        private void DeactivateExternalProbes()
        {
            if (activeProbeCount <= 0)
            {
                return;
            }
            for (var index = 0; index < physicsManager.externalKinematicSphereCount; index++)
            {
                physicsManager.SetExternalKinematicSpherePose(index, Vector3.zero, false);
            }
            activeProbeCount = 0;
        }

        private bool TryGetCachedAvatarBounds(out Bounds bounds)
        {
            var rootChanged = HasAvatarRootPoseChanged();
            if (!cachedAvatarBoundsAvailable || rootChanged ||
                Time.unscaledTime >= nextAvatarBoundsRefreshAt)
            {
                cachedAvatarBoundsAvailable = TryCalculateAvatarBounds(out cachedAvatarBounds);
                nextAvatarBoundsRefreshAt = Time.unscaledTime + AvatarBoundsRefreshInterval;
                CaptureAvatarRootPose();
            }
            bounds = cachedAvatarBounds;
            return cachedAvatarBoundsAvailable;
        }

        private bool HasAvatarRootPoseChanged()
        {
            return avatar != null &&
                (avatar.transform.position != cachedAvatarPosition ||
                 avatar.transform.rotation != cachedAvatarRotation ||
                 avatar.transform.lossyScale != cachedAvatarScale);
        }

        private void CaptureAvatarRootPose()
        {
            if (avatar == null)
            {
                cachedAvatarPosition = Vector3.zero;
                cachedAvatarRotation = Quaternion.identity;
                cachedAvatarScale = Vector3.one;
                return;
            }
            cachedAvatarPosition = avatar.transform.position;
            cachedAvatarRotation = avatar.transform.rotation;
            cachedAvatarScale = avatar.transform.lossyScale;
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

            DeactivateExternalProbes();
        }
    }
}
