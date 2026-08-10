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
        private bool configured;
        private int activeProbeCount;
        private string status = "等待角色物理管理器";

        public string Status => status;
        public int ActiveProbeCount => activeProbeCount;
        public bool IsConfigured => configured && physicsManager != null;

        public void Bind(AvatarController targetAvatar, QuestTrackedHandVisualizer sourceHands)
        {
            avatar = targetAvatar;
            trackedHands = sourceHands;
            physicsManager = avatar == null
                ? null
                : avatar.GetComponentInChildren<MMDTransformManager>(true)?.physicsManager;
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

            if (!configured)
            {
                var radii = new float[QuestTrackedHandVisualizer.PhysicsProbeCount];
                for (var index = 0; index < radii.Length; index++)
                {
                    radii[index] = index % 6 == 0 ? palmRadius : fingertipRadius;
                }
                physicsManager.ConfigureExternalKinematicSpheres(radii);
                configured = physicsManager.externalKinematicSphereCount == radii.Length;
                status = configured ? "外部手部刚体已接入 UMT Bullet" : "外部手部刚体初始化失败";
                Debug.Log("[MmdPhysicsAdapter] " + status + "，数量=" + radii.Length, this);
                if (!configured)
                {
                    return;
                }
            }

            activeProbeCount = 0;
            for (var index = 0; index < QuestTrackedHandVisualizer.PhysicsProbeCount; index++)
            {
                if (!trackedHands.TryGetPhysicsProbe(index, out var position, out _, out var active))
                {
                    active = false;
                }
                physicsManager.SetExternalKinematicSpherePose(index, position, active);
                if (active) activeProbeCount++;
            }
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
