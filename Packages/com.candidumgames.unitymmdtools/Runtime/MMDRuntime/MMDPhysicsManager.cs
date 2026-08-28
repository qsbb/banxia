using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using static UMT.PMXUtilities;

namespace UMT
{
    /// <summary>
    /// Coordinates native Bullet-backed MMD rigid-body physics: builds rigid bodies, joints, optional ground collision, and physics runtime data from the <see cref="PMXModel"/>, and drives the simulation each frame in concert with <see cref="MMDTransformManager"/>.
    /// </summary>
    [RequireComponent(typeof(MMDTransformManager))]
    public sealed class MMDPhysicsManager : MonoBehaviour
    {
        private const float k_MMDPhysicsGravity = 98.0f;
        private const byte k_MMDGroundCollisionGroup = 15;
        private const short k_MMDGroundCollisionMask = -1;
        private const int k_SolverIterations = 4;
        private const int k_DefaultSimulationFrequencyHz = 120;
        private const int k_DefaultMaxSubStepsPerFrame = 4;
        private const int k_DefaultLockedTranslationReinforceCount = 2;
        // Long chains whose translation rows are fully locked (the common
        // hair/skirt construction in PMX models) amplify solver residuals.
        // Heavy-model CPU reduction is only safe when no such chain exists.
        private const int k_StabilitySensitiveLockedComponentJointCount = 4;
        private const float k_LockedTranslationEpsilon = 0.00001f;
        private static int s_SimulationFrequencyHz = k_DefaultSimulationFrequencyHz;
        private static int s_MaxSubStepsPerFrame = k_DefaultMaxSubStepsPerFrame;
        private static int s_LockedTranslationReinforceCount = k_DefaultLockedTranslationReinforceCount;
        /// <summary>MMD's reference solver iteration count; the spring damping compensation below is expressed relative to it.</summary>
        private const int k_MMDReferenceSolverIterations = 4;
        /// <summary>Bullet's default convex collision margin, in MMD units; scaled into Unity units for the native config.</summary>
        private const float k_BulletConvexMargin = 0.04f;
        /// <summary>Bullet spring damping for Spring6DOF translation rows: solverIterations / referenceIterations keeps the spring motor's target velocity matched to the MMD reference.</summary>
        private const float k_SpringTranslationDamping = (float)k_SolverIterations / k_MMDReferenceSolverIterations;
        /// <summary>Bullet spring damping for Spring6DOF rotation rows: solverIterations / (referenceIterations * scale^2). Bullet's motor-based spring drives targetVelocity = fps * (damping / iterations) * stiffness * angle with impulse clamp stiffness * angle / fps; the clamp scales correctly with the scale^2 stiffness factor (inertia grows with scale^2 at fixed mass) but the velocity term carries no inertia, so without this compensation the meter-scale simulation leaves MMD's operating regime - historically Unity's rotation springs were much SOFTER than MMD because the tiny meter-scale inertias fell into the motor's velocity-limited regime. With it, target velocity, impulse clamp, and the regime boundary all match MMD's unit-scale reference exactly.</summary>
        private const float k_SpringRotationDamping = (float)k_SolverIterations / (k_MMDReferenceSolverIterations * MMDConstants.k_MMDUnitToUnityUnit * MMDConstants.k_MMDUnitToUnityUnit);
        /// <summary>Redundant point-to-point constraint copies per fully-locked-translation joint (tLim min == max == 0). Each copy re-solves those equality rows once more per solver iteration, so spring-less locked hair chains survive fast motion at MMD's 4 iterations without firming up rotation limits, springs, or contacts; the converged pose is unchanged. Empirically the chains need ~12-iteration-equivalent convergence, hence 2 copies at 4 iterations.</summary>
        private bool m_SimulationSuspended;

        /// <summary>Rigid-body components managed by this physics manager.</summary>
        public MMDRigidBody[] rigidBodies = Array.Empty<MMDRigidBody>();
        /// <summary>Joint components managed by this physics manager.</summary>
        public MMDJoint[] joints = Array.Empty<MMDJoint>();
        /// <summary>Whether the static ground collider is enabled.</summary>
        public bool enableGroundCollision = true;
        /// <summary>Random seed used to reset the deterministic physics simulation.</summary>
        public uint physicsSeed = 0;

        /// <summary>
        /// Mutable native physics solver state: the Bullet context, transform/index buffers, rigid-body simulation data, and whether the initial pose has been seeded.
        /// </summary>
        internal struct PhysicsSolverContext
        {
            /// <summary>Native Bullet physics context wrapper.</summary>
            internal MMDBulletPhysics bulletPhysicsContext;
            /// <summary>Scratch buffer of rigid-body world transforms.</summary>
            internal NativeArray<float4x4> worldTransforms;
            /// <summary>Scratch buffer of rigid-body indices paired with <see cref="worldTransforms"/>, prefilled with the identity mapping at initialization.</summary>
            internal NativeArray<int> rigidBodyIndices;
            /// <summary>Indices of kinetic (bone-driven) rigid bodies, precomputed at initialization.</summary>
            internal NativeArray<int> kineticRigidBodyIndices;
            /// <summary>Indices of simulated rigid bodies with related bones, sorted by (bone transform level, related bone index), precomputed at initialization.</summary>
            internal NativeArray<int> sortedSimulatedRigidBodyIndices;
            /// <summary>Per-rigid-body simulation data mirrored to the native context.</summary>
            internal NativeArray<MMDRigidBody.RigidBodySimulationData> rigidBodySimulationData;
            /// <summary>Kinetic-body world targets of the previous physics frame (indexed like <see cref="kineticRigidBodyIndices"/>), interpolated toward the current targets across the fixed substeps so kinematic bodies move continuously with Bullet-derived velocities instead of teleporting once per frame.</summary>
            internal NativeArray<float4x4> previousKineticTargets;
            /// <summary>Scratch: current-frame kinetic-body world targets.</summary>
            internal NativeArray<float4x4> currentKineticTargets;
            /// <summary>Marks kinematic bodies driven by external tracked objects rather than PMX bones.</summary>
            internal NativeArray<bool> externalKinematicFlags;
            /// <summary>World targets for externally driven kinematic bodies.</summary>
            internal NativeArray<float4x4> externalKinematicTargets;
            /// <summary>Last physics-applied bone-local positions, indexed like <see cref="sortedSimulatedRigidBodyIndices"/>. Banxia pose-hold patch: lets zero-substep frames re-assert the last simulated pose instead of decaying to the animation sample.</summary>
            internal NativeArray<float3> lastPhysicsLocalPositions;
            /// <summary>Last physics-applied bone-local rotations, indexed like <see cref="sortedSimulatedRigidBodyIndices"/>. Banxia pose-hold patch.</summary>
            internal NativeArray<quaternion> lastPhysicsLocalRotations;
            /// <summary>Validity flag for the cached physics pose per simulated body; zero until the first substep apply (banxia pose-hold patch).</summary>
            internal NativeArray<byte> lastPhysicsPoseValid;
            /// <summary>Simulated bones whose local pose deviated from the cached physics pose during the latest zero-substep pass (banxia pose-hold patch).</summary>
            internal int lastPoseSourceFlipCount;
            /// <summary>Cumulative zero-substep frames in which any simulated bone deviated from the cached physics pose (banxia pose-hold patch).</summary>
            internal int totalPoseSourceFlipFrames;
            /// <summary>Unconsumed simulation time carried between frames so variable frame times map onto whole fixed substeps without drifting.</summary>
            internal float timeAccumulator;
            /// <summary>Number of Bullet substeps executed by the latest transform pass.</summary>
            internal int lastSubstepCount;
            /// <summary>Catch-up time discarded by the latest transform pass.</summary>
            internal float lastDroppedSimulationSeconds;
            /// <summary>Total catch-up time discarded since this context was built.</summary>
            internal float totalDroppedSimulationSeconds;
            /// <summary>Number of frames where stale catch-up time was discarded.</summary>
            internal int droppedSimulationFrameCount;
            /// <summary>Fixed-step frequency captured when this native context was built.</summary>
            internal int simulationFrequencyHz;
            /// <summary>Per-frame substep cap captured when this native context was built.</summary>
            internal int maximumSubstepsPerFrame;
            /// <summary>Effective locked-translation reinforcement selected for this model.</summary>
            internal int lockedTranslationReinforceCount;
            /// <summary>Requests a velocity-clearing kinetic resync on the next physics pass.</summary>
            [MarshalAs(UnmanagedType.U1)]
            internal bool resetKineticInterpolation;
            /// <summary>Whether the initial bone-driven rigid-body pose has been applied.</summary>
            [MarshalAs(UnmanagedType.U1)]
            internal bool initialPoseApplied;
        }
        private PhysicsSolverContext m_PhysicsSolverContext;

        /// <summary>Transform access array over the rigid-body owner objects driving the flush transform job, rebuilt with the runtime data.</summary>
        private TransformAccessArray m_RigidBodyTransformAccess;
        private float[] m_ExternalKinematicSphereRadii = Array.Empty<float>();
        private bool[] m_ExternalKinematicActive = Array.Empty<bool>();
        private byte m_ExternalKinematicCollisionGroup = 15;
        private bool m_ExternalKinematicFullCoverage;
        private NativeArray<float4x4> m_ExternalPoseScratchTransforms;
        private NativeArray<int> m_ExternalPoseScratchIndices;

        /// <summary>Number of runtime hand/contact spheres appended to the native Bullet world.</summary>
        public int externalKinematicSphereCount => m_ExternalKinematicSphereRadii.Length;
        /// <summary>Collision group selected for externally tracked contact spheres.</summary>
        public byte externalKinematicCollisionGroup => m_ExternalKinematicCollisionGroup;
        /// <summary>Whether every authored model body can collide with the selected external group without changing model-to-model filtering.</summary>
        public bool externalKinematicFullCoverage => m_ExternalKinematicFullCoverage;
        /// <summary>Bullet substeps executed by the latest rendered frame.</summary>
        public int lastSimulationSubstepCount => m_PhysicsSolverContext.lastSubstepCount;
        /// <summary>Catch-up time discarded by the latest rendered frame, in seconds.</summary>
        public float lastDroppedSimulationSeconds => m_PhysicsSolverContext.lastDroppedSimulationSeconds;
        /// <summary>Total stale catch-up time discarded since initialization, in seconds.</summary>
        public float totalDroppedSimulationSeconds => m_PhysicsSolverContext.totalDroppedSimulationSeconds;
        /// <summary>Number of rendered frames that discarded stale catch-up work.</summary>
        public int droppedSimulationFrameCount => m_PhysicsSolverContext.droppedSimulationFrameCount;
        /// <summary>Simulated bones whose local pose deviated from the cached physics pose during the latest zero-substep pass (banxia pose-hold patch).</summary>
        public int lastPoseSourceFlipCount => m_PhysicsSolverContext.lastPoseSourceFlipCount;
        /// <summary>Cumulative zero-substep frames in which any simulated bone deviated from the cached physics pose (banxia pose-hold patch).</summary>
        public int totalPoseSourceFlipFrames => m_PhysicsSolverContext.totalPoseSourceFlipFrames;
        /// <summary>Fixed Bullet simulation frequency used for normal playback.</summary>
        public static int simulationFrequencyHz => s_SimulationFrequencyHz;
        /// <summary>Hard limit that prevents a slow rendered frame from entering a physics catch-up spiral.</summary>
        public static int maximumSubstepsPerFrame => s_MaxSubStepsPerFrame;
        /// <summary>Additional locked-translation constraints configured for newly built contexts.</summary>
        public static int lockedTranslationReinforceCount => s_LockedTranslationReinforceCount;
        /// <summary>Effective locked-translation reinforcement used by this model's active context.</summary>
        public int activeLockedTranslationReinforceCount =>
            m_PhysicsSolverContext.lockedTranslationReinforceCount;
        /// <summary>Whether runtime stepping is paused while bone-driven bodies continue to synchronize.</summary>
        public bool simulationSuspended => m_SimulationSuspended;

        /// <summary>
        /// Configures the fixed-step policy used by all subsequently rebuilt MMD physics contexts.
        /// This is intentionally a fixed user-selected policy; it never changes in response to frame time.
        /// </summary>
        public static void ConfigureRuntimeQuality(int frequencyHz, int maximumSubsteps, int lockedTranslationReinforce)
        {
            s_SimulationFrequencyHz = math.clamp(frequencyHz, 30, 240);
            s_MaxSubStepsPerFrame = math.clamp(maximumSubsteps, 1, 8);
            s_LockedTranslationReinforceCount = math.clamp(lockedTranslationReinforce, 0, 2);
        }

        /// <summary>Rebuilds this manager so the current fixed-step policy takes effect immediately.</summary>
        public void ApplyConfiguredRuntimeQuality()
        {
            if (!isActiveAndEnabled)
            {
                return;
            }
            Initialize();
            DiscardAccumulatedSimulationTime();
        }

        /// <summary>Pauses or resumes time advancement without disabling bone synchronization.</summary>
        public void SetSimulationSuspended(bool suspended)
        {
            if (m_SimulationSuspended == suspended)
            {
                return;
            }
            m_SimulationSuspended = suspended;
            DiscardAccumulatedSimulationTime();
        }

        /// <summary>Pure helper used by diagnostics and regression tests.</summary>
        public static int ResolveRuntimeSubstepBudget(
            float accumulatedSeconds,
            out float retainedAccumulator,
            out float droppedSeconds)
        {
            return PhysicsMath.ResolveRuntimeSubstepBudget(
                accumulatedSeconds,
                s_SimulationFrequencyHz,
                s_MaxSubStepsPerFrame,
                out retainedAccumulator,
                out droppedSeconds);
        }

        /// <summary>Pure overload used by quality-policy regression tests.</summary>
        public static int ResolveRuntimeSubstepBudget(
            float accumulatedSeconds,
            int frequencyHz,
            int maximumSubsteps,
            out float retainedAccumulator,
            out float droppedSeconds)
        {
            return PhysicsMath.ResolveRuntimeSubstepBudget(
                accumulatedSeconds,
                frequencyHz,
                maximumSubsteps,
                out retainedAccumulator,
                out droppedSeconds);
        }

        /// <summary>
        /// Drops pending catch-up time after pause/focus transitions. The next
        /// physics pass also clears kinetic velocity so a stale target cannot
        /// kick hair or clothing when XR rendering resumes.
        /// </summary>
        public void DiscardAccumulatedSimulationTime()
        {
            m_PhysicsSolverContext.timeAccumulator = 0.0f;
            m_PhysicsSolverContext.resetKineticInterpolation = true;
        }

        /// <summary>
        /// Requests a clean simulation seed from the model's current bone pose.
        /// Use this after a period where bones advanced while live physics was
        /// disabled; retaining the old dynamic bodies would otherwise make the
        /// constraints violently pull hair and clothing toward stale poses.
        /// </summary>
        public void ReseedFromCurrentPose()
        {
            m_PhysicsSolverContext.timeAccumulator = 0.0f;
            m_PhysicsSolverContext.lastSubstepCount = 0;
            m_PhysicsSolverContext.lastDroppedSimulationSeconds = 0.0f;
            m_PhysicsSolverContext.resetKineticInterpolation = false;
            m_PhysicsSolverContext.initialPoseApplied = false;
            InvalidateLastPhysicsPoseCache();
        }

        /// <summary>
        /// Banxia pose-hold patch: clears the cached last-physics pose so the zero-substep hold replay cannot re-assert poses from before a reset or reseed.
        /// </summary>
        private void InvalidateLastPhysicsPoseCache()
        {
            if (m_PhysicsSolverContext.lastPhysicsPoseValid.IsCreated)
            {
                for (int i = 0; i < m_PhysicsSolverContext.lastPhysicsPoseValid.Length; ++i)
                {
                    m_PhysicsSolverContext.lastPhysicsPoseValid[i] = 0;
                }
            }
        }

        /// <summary>Diagnostic surface used by regression tests and runtime logs.</summary>
        public bool initialPoseSeedPending => !m_PhysicsSolverContext.initialPoseApplied;

        /// <summary>
        /// Writes each rigid body's world transform onto its owner object's Unity transform.
        /// </summary>
        [BurstCompile]
        private struct FlushRigidBodyTransformsJob : IJobParallelForTransform
        {
            /// <summary>Rigid-body world transforms, indexed by rigid-body array position.</summary>
            [ReadOnly] internal NativeArray<float4x4> worldTransforms;

            public void Execute(int index, TransformAccess rigidBodyTransform)
            {
                float4x4 worldTransform = worldTransforms[index];
                rigidBodyTransform.SetPositionAndRotation(worldTransform.c3.xyz, new quaternion(worldTransform));
            }
        }

        /// <summary>
        /// Reference to this manager's mutable physics solver context.
        /// </summary>
        internal ref PhysicsSolverContext Context => ref m_PhysicsSolverContext;

        private void OnEnable()
        {
            RebuildRuntimeData();
        }

        private void OnDisable()
        {
            DisposePhysics();
        }

        /// <summary>
        /// (Re)creates the native physics context with MMD gravity and solver settings, rebuilds runtime data, and builds rigid bodies, joints, and ground collision.
        /// </summary>
        internal void Initialize()
        {
            DisposePhysics();
            var reinforcement = ResolveLockedTranslationReinforcement(
                joints,
                rigidBodies == null ? 0 : rigidBodies.Length);
            m_PhysicsSolverContext.bulletPhysicsContext = CreateConfiguredBulletPhysics(
                reinforcement);
            m_PhysicsSolverContext.lockedTranslationReinforceCount = reinforcement;
            ApplyRuntimePolicy(ref m_PhysicsSolverContext);

            RebuildRuntimeData();
            BuildRigidBodies();
            BuildJoints();
            BuildGround();
        }

        /// <summary>
        /// Appends spherical kinematic colliders to the same native Bullet
        /// world as the model rigid bodies. This is used by the Quest hand
        /// adapter; changing the radii rebuilds the context, while pose updates
        /// only update one existing kinematic body.
        /// </summary>
        public void ConfigureExternalKinematicSpheres(float[] radii)
        {
            var sanitized = radii == null ? Array.Empty<float>() : new float[radii.Length];
            for (int i = 0; i < sanitized.Length; ++i)
            {
                sanitized[i] = math.clamp(math.abs(radii[i]), 0.004f, 0.08f);
            }

            if (m_ExternalKinematicSphereRadii.Length == sanitized.Length)
            {
                bool equal = true;
                for (int i = 0; i < sanitized.Length; ++i)
                {
                    if (math.abs(m_ExternalKinematicSphereRadii[i] - sanitized[i]) > 0.0001f)
                    {
                        equal = false;
                        break;
                    }
                }
                if (equal) return;
            }

            m_ExternalKinematicSphereRadii = sanitized;
            m_ExternalKinematicActive = new bool[sanitized.Length];
            if (isActiveAndEnabled && Application.isPlaying)
            {
                Initialize();
            }
        }

        /// <summary>Moves one external sphere. Inactive spheres are teleported below the scene and have their velocity cleared.</summary>
        public bool SetExternalKinematicSpherePose(int sphereIndex, Vector3 worldPosition, bool active)
        {
            if (sphereIndex < 0 || sphereIndex >= m_ExternalKinematicSphereRadii.Length ||
                !m_PhysicsSolverContext.externalKinematicTargets.IsCreated)
            {
                return false;
            }

            int bodyIndex = rigidBodies.Length + sphereIndex;
            float3 position = active && IsFinite(worldPosition)
                ? (float3)worldPosition
                : new float3(0.0f, -1000.0f - sphereIndex, 0.0f);
            float4x4 target = float4x4.Translate(position);
            m_PhysicsSolverContext.externalKinematicTargets[bodyIndex] = target;

            bool stateChanged = m_ExternalKinematicActive[sphereIndex] != active;
            m_ExternalKinematicActive[sphereIndex] = active;
            if (stateChanged)
            {
                // Activation changes must teleport instead of sweeping from the
                // offscreen parking position. Continuous tracked motion keeps
                // the previous frame target so the fixed substeps interpolate it.
                int kineticSlot = FindKineticSlot(bodyIndex, in m_PhysicsSolverContext.kineticRigidBodyIndices);
                if (kineticSlot >= 0 && m_PhysicsSolverContext.previousKineticTargets.IsCreated)
                {
                    m_PhysicsSolverContext.previousKineticTargets[kineticSlot] = target;
                    m_PhysicsSolverContext.currentKineticTargets[kineticSlot] = target;
                }

                if (m_PhysicsSolverContext.bulletPhysicsContext.isValid &&
                    m_ExternalPoseScratchTransforms.IsCreated && m_ExternalPoseScratchIndices.IsCreated)
                {
                    m_ExternalPoseScratchTransforms[0] = target;
                    m_ExternalPoseScratchIndices[0] = bodyIndex;
                    m_PhysicsSolverContext.bulletPhysicsContext.SetRigidBodyTransforms(
                        1,
                        m_ExternalPoseScratchTransforms,
                        m_ExternalPoseScratchIndices,
                        true);
                }
            }
            return true;
        }

        /// <summary>
        /// Resets the native simulation with <see cref="physicsSeed"/>, restores simulated bones to their initial pose, and clears the initial-pose-applied flag.
        /// </summary>
        internal void ResetPhysics()
        {
            m_PhysicsSolverContext.bulletPhysicsContext.Reset(physicsSeed);
            ResetSimulatedBoneTransformsToInitial();
            m_PhysicsSolverContext.initialPoseApplied = false;
            InvalidateLastPhysicsPoseCache();
        }

        /// <summary>
        /// Seeds or syncs rigid bodies from bone transforms, steps the simulation, and applies dynamic rigid-body results back onto their related bones.
        /// </summary>
        /// <param name="physicsElapsedTime">Elapsed time to simulate; zero or less skips stepping.</param>
        /// <param name="transformManagerContext">Transform solver context providing bone matrices.</param>
        /// <param name="runtimeContext">Physics solver context to advance.</param>
        internal static void TransformPhysics(float physicsElapsedTime, ref MMDTransformManager.SolverContext transformManagerContext, ref PhysicsSolverContext runtimeContext)
        {
            PhysicsMath.TransformPhysicsInternal(physicsElapsedTime, ref transformManagerContext, ref runtimeContext);
        }

        /// <summary>
        /// Builds a standalone physics solver context from a model: creates the native context, allocates buffers, fills rigid-body simulation data, builds rigid bodies, joints, ground, and resets the simulation.
        /// </summary>
        /// <param name="model">PMX model providing rigid bodies and joints.</param>
        /// <param name="seed">Random seed used to reset the simulation.</param>
        /// <param name="enableGroundCollision">Whether to build the ground collider enabled.</param>
        /// <param name="runtimeContext">Physics solver context to initialize.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="model"/> is null.</exception>
        internal static void InitializePhysicsContext(PMXModel model, uint seed, bool enableGroundCollision, ref PhysicsSolverContext runtimeContext)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            DisposePhysicsContext(ref runtimeContext);
            var reinforcement = ResolveLockedTranslationReinforcement(
                model.joints,
                model.rigidBodies == null ? 0 : model.rigidBodies.Length);
            runtimeContext.bulletPhysicsContext = CreateConfiguredBulletPhysics(
                reinforcement);
            runtimeContext.lockedTranslationReinforceCount = reinforcement;
            ApplyRuntimePolicy(ref runtimeContext);
            ResizePersistent(ref runtimeContext.rigidBodySimulationData, model.rigidBodies.Length);
            ResizePersistent(ref runtimeContext.worldTransforms, model.rigidBodies.Length);
            ResizePersistent(ref runtimeContext.rigidBodyIndices, model.rigidBodies.Length);
            ResizePersistent(ref runtimeContext.externalKinematicFlags, model.rigidBodies.Length);
            ResizePersistent(ref runtimeContext.externalKinematicTargets, model.rigidBodies.Length);
            for (int i = 0; i < model.rigidBodies.Length; ++i)
            {
                runtimeContext.externalKinematicFlags[i] = false;
                runtimeContext.externalKinematicTargets[i] = float4x4.identity;
            }

            for (int i = 0; i < model.rigidBodies.Length; ++i)
            {
                PMXRigidBody source = model.rigidBodies[i];
                bool hasRelatedBone = source.relatedBoneIndex >= 0 && source.relatedBoneIndex < model.bones.Length;
                runtimeContext.rigidBodySimulationData[i] = new MMDRigidBody.RigidBodySimulationData
                    {
                        rigidBodyIndex = i,
                        relatedBoneIndex = source.relatedBoneIndex,
                        groupIndex = source.groupIndex,
                        collisionGroupMask = source.collisionGroupMask,
                        shape = source.shape,
                        size = source.size,
                        position = source.position,
                        rotation = source.rotation,
                        mass = source.mass,
                        linearDamping = source.linearDamping,
                        angularDamping = source.angularDamping,
                        restitution = source.restitution,
                        friction = source.friction,
                        mode = source.mode,
                        initialTransform = float4x4.identity,
                        hasRelatedBone = hasRelatedBone,
                        boneLocalTransform = float4x4.identity,
                        boneModelPosition = hasRelatedBone ? model.bones[source.relatedBoneIndex].position : float3.zero,
                        initialWorldTransform = float4x4.identity,
                        boneTransformLevel = hasRelatedBone ? model.bones[source.relatedBoneIndex].transformLevel : -1,
                    };
            }

            PhysicsMath.ComputeRigidBodyTransforms(ref runtimeContext.rigidBodySimulationData);
            PhysicsMath.BuildStaticRigidBodyLists(ref runtimeContext);
            runtimeContext.bulletPhysicsContext.BuildRigidBodies(runtimeContext.rigidBodySimulationData);
            runtimeContext.bulletPhysicsContext.BuildJoints(BuildPMXJoints(model, runtimeContext.rigidBodySimulationData));
            runtimeContext.bulletPhysicsContext.BuildGround(enableGroundCollision, k_MMDGroundCollisionGroup, k_MMDGroundCollisionMask);
            ResetPhysicsContext(seed, ref runtimeContext);
        }

        /// <summary>
        /// Creates the native Bullet context with MMD gravity/solver settings and applies the tunable configuration: spring damping compensation (so the motor-based 6DOF springs reproduce MMD's unit-scale reference at Unity's meter scale) and locked-translation reinforcement (so spring-less locked chains survive fast motion at MMD's 4 iterations). Ground plane, unit scale, and convex margin restate the historical defaults.
        /// </summary>
        /// <returns>The configured native physics context wrapper.</returns>
        private static MMDBulletPhysics CreateConfiguredBulletPhysics(int lockedTranslationReinforce)
        {
            float fixedTimeStep = 1.0f / s_SimulationFrequencyHz;
            MMDBulletPhysics bulletPhysics = new MMDBulletPhysics(new float3(0.0f, -k_MMDPhysicsGravity * MMDConstants.k_MMDUnitToUnityUnit, 0.0f), k_SolverIterations, s_MaxSubStepsPerFrame, fixedTimeStep);
            try
            {
                bulletPhysics.SetConfig(new MMDBulletPhysics.NativeConfig
                {
                    groundNormal = new float3(0.0f, 1.0f, 0.0f),
                    groundConstant = 0.0f,
                    mmdUnitToUnityUnit = MMDConstants.k_MMDUnitToUnityUnit,
                    convexMargin = k_BulletConvexMargin * MMDConstants.k_MMDUnitToUnityUnit,
                    springTranslationDamping = k_SpringTranslationDamping,
                    springRotationDamping = k_SpringRotationDamping,
                    lockedTranslationReinforce = lockedTranslationReinforce,
                });
            }
            catch (EntryPointNotFoundException)
            {
                Debug.LogWarning("UMTNativePlugin does not expose MMDBulletPhysicsSetConfig; using the native historical physics defaults.");
            }
            return bulletPhysics;
        }

        /// <summary>
        /// Keeps authored light-model convergence intact. The reduced reinforcement
        /// is reserved for joint-heavy models where duplicate constraints dominate
        /// the Quest CPU budget.
        /// </summary>
        public static int ResolveLockedTranslationReinforcement(int jointCount)
        {
            return jointCount > 80
                ? math.min(1, s_LockedTranslationReinforceCount)
                : k_DefaultLockedTranslationReinforceCount;
        }

        /// <summary>
        /// Selects reinforcement from the actual PMX joint topology. A joint
        /// count alone is not enough: a large model with independent joints is
        /// cheap to stabilize, while a long fully-locked hair/skirt component
        /// needs the extra convergence even when the global profile requests a
        /// reduced heavy-model setting.
        /// </summary>
        public static int ResolveLockedTranslationReinforcement(
            PMXJoint[] modelJoints,
            int rigidBodyCount)
        {
            var jointCount = modelJoints == null ? 0 : modelJoints.Length;
            if (RequiresFullLockedTranslationReinforcement(modelJoints, rigidBodyCount))
            {
                return k_DefaultLockedTranslationReinforceCount;
            }
            return ResolveLockedTranslationReinforcement(jointCount);
        }

        /// <summary>Runtime-component overload of the topology-aware policy.</summary>
        public static int ResolveLockedTranslationReinforcement(
            MMDJoint[] modelJoints,
            int rigidBodyCount)
        {
            var jointCount = modelJoints == null ? 0 : modelJoints.Length;
            if (RequiresFullLockedTranslationReinforcement(modelJoints, rigidBodyCount))
            {
                return k_DefaultLockedTranslationReinforceCount;
            }
            return ResolveLockedTranslationReinforcement(jointCount);
        }

        /// <summary>
        /// Returns true when the model contains a connected component with at
        /// least four fully locked translation joints. This is deliberately
        /// topology-based so future PMX imports receive the same stability
        /// treatment without a model-name or file-hash allowlist.
        /// </summary>
        public static bool RequiresFullLockedTranslationReinforcement(
            PMXJoint[] modelJoints,
            int rigidBodyCount)
        {
            if (modelJoints == null || rigidBodyCount <= 1)
            {
                return false;
            }

            var first = new int[modelJoints.Length];
            var second = new int[modelJoints.Length];
            var lockedCount = 0;
            for (var index = 0; index < modelJoints.Length; index++)
            {
                var joint = modelJoints[index];
                if (!IsFullyLockedTranslation(joint.translationLimitMin, joint.translationLimitMax) ||
                    joint.rigidBodyAIndex < 0 ||
                    joint.rigidBodyAIndex >= rigidBodyCount ||
                    joint.rigidBodyBIndex < 0 ||
                    joint.rigidBodyBIndex >= rigidBodyCount ||
                    joint.rigidBodyAIndex == joint.rigidBodyBIndex)
                {
                    continue;
                }
                first[lockedCount] = joint.rigidBodyAIndex;
                second[lockedCount] = joint.rigidBodyBIndex;
                lockedCount++;
            }
            return HasLockedComponentWithAtLeastFourJoints(
                first,
                second,
                lockedCount,
                rigidBodyCount);
        }

        /// <summary>Runtime-component counterpart used after Unity objects are built.</summary>
        public static bool RequiresFullLockedTranslationReinforcement(
            MMDJoint[] modelJoints,
            int rigidBodyCount)
        {
            if (modelJoints == null || rigidBodyCount <= 1)
            {
                return false;
            }

            var first = new int[modelJoints.Length];
            var second = new int[modelJoints.Length];
            var lockedCount = 0;
            for (var index = 0; index < modelJoints.Length; index++)
            {
                var joint = modelJoints[index];
                if (!IsFullyLockedTranslation(joint.translationLimitMin, joint.translationLimitMax) ||
                    joint.rigidBodyAIndex < 0 ||
                    joint.rigidBodyAIndex >= rigidBodyCount ||
                    joint.rigidBodyBIndex < 0 ||
                    joint.rigidBodyBIndex >= rigidBodyCount ||
                    joint.rigidBodyAIndex == joint.rigidBodyBIndex)
                {
                    continue;
                }
                first[lockedCount] = joint.rigidBodyAIndex;
                second[lockedCount] = joint.rigidBodyBIndex;
                lockedCount++;
            }
            return HasLockedComponentWithAtLeastFourJoints(
                first,
                second,
                lockedCount,
                rigidBodyCount);
        }

        private static bool IsFullyLockedTranslation(float3 minimum, float3 maximum)
        {
            return math.all(math.abs(minimum) <= k_LockedTranslationEpsilon) &&
                math.all(math.abs(maximum) <= k_LockedTranslationEpsilon);
        }

        private static bool HasLockedComponentWithAtLeastFourJoints(
            int[] first,
            int[] second,
            int edgeCount,
            int rigidBodyCount)
        {
            if (edgeCount < k_StabilitySensitiveLockedComponentJointCount)
            {
                return false;
            }

            var parent = new int[rigidBodyCount];
            var componentEdges = new int[rigidBodyCount];
            for (var index = 0; index < parent.Length; index++)
            {
                parent[index] = index;
            }
            for (var index = 0; index < edgeCount; index++)
            {
                Union(parent, first[index], second[index]);
            }
            for (var index = 0; index < edgeCount; index++)
            {
                var root = Find(parent, first[index]);
                componentEdges[root]++;
                if (componentEdges[root] >= k_StabilitySensitiveLockedComponentJointCount)
                {
                    return true;
                }
            }
            return false;
        }

        private static int Find(int[] parent, int value)
        {
            var root = value;
            while (parent[root] != root)
            {
                root = parent[root];
            }
            while (parent[value] != value)
            {
                var next = parent[value];
                parent[value] = root;
                value = next;
            }
            return root;
        }

        private static void Union(int[] parent, int first, int second)
        {
            var firstRoot = Find(parent, first);
            var secondRoot = Find(parent, second);
            if (firstRoot != secondRoot)
            {
                parent[secondRoot] = firstRoot;
            }
        }

        private static void ApplyRuntimePolicy(ref PhysicsSolverContext runtimeContext)
        {
            runtimeContext.simulationFrequencyHz = s_SimulationFrequencyHz;
            runtimeContext.maximumSubstepsPerFrame = s_MaxSubStepsPerFrame;
        }

        /// <summary>
        /// Resets a physics solver context's native simulation with the given seed and clears its initial-pose-applied flag, if the context is valid.
        /// </summary>
        /// <param name="seed">Random seed used to reset the simulation.</param>
        /// <param name="runtimeContext">Physics solver context to reset.</param>
        internal static void ResetPhysicsContext(uint seed, ref PhysicsSolverContext runtimeContext)
        {
            if (!runtimeContext.bulletPhysicsContext.isValid)
            {
                return;
            }

            runtimeContext.bulletPhysicsContext.Reset(seed);
            runtimeContext.initialPoseApplied = false;
        }

        /// <summary>
        /// Disposes a physics solver context's native context and native arrays, resetting it to default.
        /// </summary>
        /// <param name="runtimeContext">Physics solver context to dispose.</param>
        internal static void DisposePhysicsContext(ref PhysicsSolverContext runtimeContext)
        {
            if (runtimeContext.bulletPhysicsContext.isValid)
            {
                runtimeContext.bulletPhysicsContext.Dispose();
            }

            DisposeNativeArray(ref runtimeContext.worldTransforms);
            DisposeNativeArray(ref runtimeContext.rigidBodyIndices);
            DisposeNativeArray(ref runtimeContext.kineticRigidBodyIndices);
            DisposeNativeArray(ref runtimeContext.sortedSimulatedRigidBodyIndices);
            DisposeNativeArray(ref runtimeContext.rigidBodySimulationData);
            DisposeNativeArray(ref runtimeContext.previousKineticTargets);
            DisposeNativeArray(ref runtimeContext.currentKineticTargets);
            DisposeNativeArray(ref runtimeContext.externalKinematicFlags);
            DisposeNativeArray(ref runtimeContext.externalKinematicTargets);
            runtimeContext = default;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                   !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private static int FindKineticSlot(int bodyIndex, in NativeArray<int> kineticIndices)
        {
            for (int i = 0; i < kineticIndices.Length; ++i)
            {
                if (kineticIndices[i] == bodyIndex) return i;
            }
            return -1;
        }

        /// <summary>Creates validated native data for one externally tracked sphere.</summary>
        public static MMDRigidBody.RigidBodySimulationData CreateExternalKinematicSphereData(
            int rigidBodyIndex,
            float radius,
            Vector3 worldPosition,
            byte collisionGroup = 15)
        {
            float safeRadius = math.clamp(math.abs(radius), 0.004f, 0.08f);
            float3 safePosition = IsFinite(worldPosition) ? (float3)worldPosition : new float3(0.0f, -1000.0f, 0.0f);
            return new MMDRigidBody.RigidBodySimulationData
            {
                rigidBodyIndex = math.max(0, rigidBodyIndex),
                relatedBoneIndex = -1,
                groupIndex = (byte)math.clamp(collisionGroup, 0, 15),
                // UMT forwards this value directly to Bullet's collision mask.
                // All bits must be enabled so tracked hands can contact PMX
                // dynamic bodies regardless of their authored group.
                collisionGroupMask = -1,
                shape = PMXRigidBody.Shape.Sphere,
                size = new float3(safeRadius, 0.0f, 0.0f),
                position = safePosition,
                rotation = float3.zero,
                mass = 0.0f,
                linearDamping = 1.0f,
                angularDamping = 1.0f,
                restitution = 0.0f,
                friction = 0.65f,
                mode = PMXRigidBody.Mode.Kinetic,
                initialTransform = float4x4.identity,
                hasRelatedBone = false,
                boneLocalTransform = float4x4.identity,
                boneModelPosition = float3.zero,
                initialWorldTransform = float4x4.identity,
                boneTransformLevel = -1,
            };
        }

        /// <summary>
        /// Fills a per-bone boolean mask marking bones driven by non-kinetic (physics-controlled) rigid bodies, used to select bones for physics baking.
        /// </summary>
        /// <param name="runtimeContext">Physics solver context with rigid-body simulation data.</param>
        /// <param name="result">Per-bone mask, indexed by bone index, set true for physics-controlled bones.</param>
        internal static void BuildPhysicsControlledBoneSelection(in PhysicsSolverContext runtimeContext, ref NativeArray<bool> result)
        {
            for (int i = 0; i < result.Length; ++i)
            {
                result[i] = false;
            }

            for (int i = 0; i < runtimeContext.rigidBodySimulationData.Length; ++i)
            {
                MMDRigidBody.RigidBodySimulationData rigidBody = runtimeContext.rigidBodySimulationData[i];
                if (rigidBody.hasRelatedBone && rigidBody.relatedBoneIndex >= 0 && rigidBody.relatedBoneIndex < result.Length && rigidBody.mode != PMXRigidBody.Mode.Kinetic)
                {
                    result[rigidBody.relatedBoneIndex] = true;
                }
            }
        }

        /// <summary>
        /// Disposes this manager's native physics context and native arrays and clears its solver state.
        /// </summary>
        public void DisposePhysics()
        {
            if (m_PhysicsSolverContext.bulletPhysicsContext.isValid)
            {
                m_PhysicsSolverContext.bulletPhysicsContext.Dispose();
                m_PhysicsSolverContext.bulletPhysicsContext = default;
            }

            m_PhysicsSolverContext.initialPoseApplied = false;
            if (m_RigidBodyTransformAccess.isCreated)
            {
                m_RigidBodyTransformAccess.Dispose();
            }
            DisposeNativeArray(ref m_PhysicsSolverContext.worldTransforms);
            DisposeNativeArray(ref m_PhysicsSolverContext.rigidBodyIndices);
            DisposeNativeArray(ref m_PhysicsSolverContext.kineticRigidBodyIndices);
            DisposeNativeArray(ref m_PhysicsSolverContext.sortedSimulatedRigidBodyIndices);
            DisposeNativeArray(ref m_PhysicsSolverContext.rigidBodySimulationData);
            DisposeNativeArray(ref m_PhysicsSolverContext.previousKineticTargets);
            DisposeNativeArray(ref m_PhysicsSolverContext.currentKineticTargets);
            DisposeNativeArray(ref m_PhysicsSolverContext.externalKinematicFlags);
            DisposeNativeArray(ref m_PhysicsSolverContext.externalKinematicTargets);
            DisposeNativeArray(ref m_PhysicsSolverContext.lastPhysicsLocalPositions);
            DisposeNativeArray(ref m_PhysicsSolverContext.lastPhysicsLocalRotations);
            DisposeNativeArray(ref m_PhysicsSolverContext.lastPhysicsPoseValid);
            DisposeNativeArray(ref m_ExternalPoseScratchTransforms);
            DisposeNativeArray(ref m_ExternalPoseScratchIndices);
        }

        private void OnDestroy()
        {
            DisposePhysics();
        }

        /// <summary>
        /// Reinitializes runtime data for all rigid bodies and joints, recomputes rigid-body transforms, and reallocates scratch buffers as needed.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when a rigid-body or joint array element is null.</exception>
        internal void RebuildRuntimeData()
        {
            int modelBodyCount = rigidBodies.Length;
            int totalBodyCount = modelBodyCount + m_ExternalKinematicSphereRadii.Length;
            m_ExternalKinematicCollisionGroup = ResolveExternalKinematicCollisionGroup(
                rigidBodies,
                out bool mayExpandModelMasks);
            m_ExternalKinematicFullCoverage = m_ExternalKinematicSphereRadii.Length == 0 ||
                mayExpandModelMasks ||
                ModelMasksAcceptGroup(rigidBodies, m_ExternalKinematicCollisionGroup);
            short externalGroupBit = unchecked((short)(1 << m_ExternalKinematicCollisionGroup));
            ResizePersistent(ref m_PhysicsSolverContext.rigidBodySimulationData, totalBodyCount);
            ResizePersistent(ref m_PhysicsSolverContext.externalKinematicFlags, totalBodyCount);
            ResizePersistent(ref m_PhysicsSolverContext.externalKinematicTargets, totalBodyCount);
            // Banxia pose-hold patch: per-body cache of the last physics-applied bone pose (fresh arrays are zero-filled, so the validity flags start clear).
            ResizePersistent(ref m_PhysicsSolverContext.lastPhysicsLocalPositions, totalBodyCount);
            ResizePersistent(ref m_PhysicsSolverContext.lastPhysicsLocalRotations, totalBodyCount);
            ResizePersistent(ref m_PhysicsSolverContext.lastPhysicsPoseValid, totalBodyCount);
            m_PhysicsSolverContext.lastPoseSourceFlipCount = 0;
            m_PhysicsSolverContext.totalPoseSourceFlipFrames = 0;

            for (int i = 0; i < modelBodyCount; ++i)
            {
                MMDRigidBody rigidBody = rigidBodies[i];
                if (rigidBody == null)
                {
                    throw new InvalidOperationException($"MMD rigid body array element {i} is null.");
                }

                rigidBody.InitializeRuntimeData();
                MMDRigidBody.RigidBodySimulationData runtimeData = rigidBody.runtimeData;
                if (m_ExternalKinematicSphereRadii.Length > 0 && mayExpandModelMasks)
                {
                    runtimeData.collisionGroupMask = unchecked((short)(runtimeData.collisionGroupMask | externalGroupBit));
                }
                m_PhysicsSolverContext.rigidBodySimulationData[i] = runtimeData;
                m_PhysicsSolverContext.externalKinematicFlags[i] = false;
                m_PhysicsSolverContext.externalKinematicTargets[i] = float4x4.identity;
            }

            for (int i = 0; i < m_ExternalKinematicSphereRadii.Length; ++i)
            {
                int bodyIndex = modelBodyCount + i;
                float3 offscreenPosition = new float3(0.0f, -1000.0f - i, 0.0f);
                m_PhysicsSolverContext.rigidBodySimulationData[bodyIndex] =
                    CreateExternalKinematicSphereData(
                        bodyIndex,
                        m_ExternalKinematicSphereRadii[i],
                        offscreenPosition,
                        m_ExternalKinematicCollisionGroup);
                m_PhysicsSolverContext.externalKinematicFlags[bodyIndex] = true;
                m_PhysicsSolverContext.externalKinematicTargets[bodyIndex] = float4x4.Translate(offscreenPosition);
            }

            PhysicsMath.ComputeRigidBodyTransforms(ref m_PhysicsSolverContext.rigidBodySimulationData);
            for (int i = 0; i < modelBodyCount; ++i)
            {
                rigidBodies[i].runtimeData = m_PhysicsSolverContext.rigidBodySimulationData[i];
            }

            for (int i = 0; i < joints.Length; ++i)
            {
                MMDJoint joint = joints[i];
                if (joint == null)
                {
                    throw new InvalidOperationException($"MMD joint array element {i} is null.");
                }

                joint.InitializeRuntimeData();
            }

            ReallocateArraysIfNeeded(totalBodyCount);
            PhysicsMath.BuildStaticRigidBodyLists(ref m_PhysicsSolverContext);
            ResizePersistent(ref m_ExternalPoseScratchTransforms, 1);
            ResizePersistent(ref m_ExternalPoseScratchIndices, 1);
            Array.Clear(m_ExternalKinematicActive, 0, m_ExternalKinematicActive.Length);

            if (m_RigidBodyTransformAccess.isCreated)
            {
                m_RigidBodyTransformAccess.Dispose();
            }
            m_RigidBodyTransformAccess = new TransformAccessArray(rigidBodies.Length);
            for (int i = 0; i < rigidBodies.Length; ++i)
            {
                m_RigidBodyTransformAccess.Add(rigidBodies[i].transform);
            }
        }

        /// <summary>
        /// Selects a hand-only collision group whenever the model leaves one
        /// unused. Opening that bit on model masks then cannot create new
        /// model-to-model contacts. Fully occupied models retain their authored
        /// filters unless one group is already accepted by every body.
        /// </summary>
        public static byte ResolveExternalKinematicCollisionGroup(
            IReadOnlyList<MMDRigidBody> modelBodies,
            out bool mayExpandModelMasks)
        {
            ushort usedGroups = 0;
            if (modelBodies != null)
            {
                for (int i = 0; i < modelBodies.Count; ++i)
                {
                    MMDRigidBody body = modelBodies[i];
                    if (body == null) continue;
                    usedGroups |= (ushort)(1 << math.clamp(body.groupIndex, 0, 15));
                }
            }

            for (int group = 15; group >= 0; --group)
            {
                if ((usedGroups & (1 << group)) != 0) continue;
                mayExpandModelMasks = true;
                return (byte)group;
            }

            for (int group = 15; group >= 0; --group)
            {
                if (ModelMasksAcceptGroup(modelBodies, (byte)group))
                {
                    mayExpandModelMasks = false;
                    return (byte)group;
                }
            }

            mayExpandModelMasks = false;
            return 15;
        }

        private static bool ModelMasksAcceptGroup(
            IReadOnlyList<MMDRigidBody> modelBodies,
            byte group)
        {
            if (modelBodies == null || modelBodies.Count == 0) return true;
            int bit = 1 << math.clamp(group, 0, 15);
            for (int i = 0; i < modelBodies.Count; ++i)
            {
                MMDRigidBody body = modelBodies[i];
                if (body == null) continue;
                if ((((ushort)body.collisionGroupMask) & bit) == 0) return false;
            }
            return true;
        }

        private void BuildRigidBodies()
        {
            m_PhysicsSolverContext.bulletPhysicsContext.BuildRigidBodies(m_PhysicsSolverContext.rigidBodySimulationData);
        }

        private void BuildGround()
        {
            m_PhysicsSolverContext.bulletPhysicsContext.BuildGround(enableGroundCollision, k_MMDGroundCollisionGroup, k_MMDGroundCollisionMask);
        }

        /// <summary>
        /// Toggles ground collision both on this manager and in the native context.
        /// </summary>
        /// <param name="enabled">Whether ground collision is active.</param>
        public void SetGroundCollisionEnabled(bool enabled)
        {
            enableGroundCollision = enabled;
            // Banxia patch: tolerate calls before the native context is built; the flag is consumed by BuildGround on (re)initialization.
            if (m_PhysicsSolverContext.bulletPhysicsContext.isValid)
            {
                m_PhysicsSolverContext.bulletPhysicsContext.SetGroundCollisionEnabled(enabled);
            }
        }

        private void BuildJoints()
        {
            MMDBulletPhysics.NativeJointData[] nativeJoints = new MMDBulletPhysics.NativeJointData[joints.Length];
            for (int i = 0; i < joints.Length; ++i)
            {
                nativeJoints[i] = joints[i].runtimeData;
            }

            m_PhysicsSolverContext.bulletPhysicsContext.BuildJoints(nativeJoints);
        }

        /// <summary>
        /// Pushes solved rigid-body and joint transforms onto their Unity transforms, reading them from the native simulation when <paramref name="usePhysicsTransforms"/> is set, otherwise computing them from bone matrices.
        /// </summary>
        /// <param name="transformManagerContext">Transform solver context providing bone matrices.</param>
        /// <param name="usePhysicsTransforms">Whether to read rigid-body transforms from the native simulation.</param>
        internal void UpdateTransforms(ref MMDTransformManager.SolverContext transformManagerContext, bool usePhysicsTransforms)
        {
            int rigidBodyCount = m_PhysicsSolverContext.rigidBodySimulationData.Length;

            if (usePhysicsTransforms && m_PhysicsSolverContext.bulletPhysicsContext.isValid)
            {
                m_PhysicsSolverContext.bulletPhysicsContext.GetRigidBodyMotionTransforms(rigidBodyCount, m_PhysicsSolverContext.rigidBodyIndices, ref m_PhysicsSolverContext.worldTransforms);
            }
            else
            {
                for (int i = 0; i < rigidBodyCount; ++i)
                {
                    m_PhysicsSolverContext.worldTransforms[i] = PhysicsMath.ComputeRigidBodyWorldTransform(m_PhysicsSolverContext.rigidBodySimulationData[i], in transformManagerContext.boneStateData);
                }
            }

            new FlushRigidBodyTransformsJob { worldTransforms = m_PhysicsSolverContext.worldTransforms, }.Schedule(m_RigidBodyTransformAccess).Complete();

            // Joints are rigidly parented to rigid body A, so their local transform is fixed at build time and tracks rigid body A through the hierarchy. Do not overwrite it here: the native frameInA uses rigid body A's bone-offset rest frame, a different basis than the build-time local placement, so writing it displaces the joint object and that displacement persists after exiting play mode.
        }

        private void ReallocateArraysIfNeeded(int count)
        {
            if (!m_PhysicsSolverContext.worldTransforms.IsCreated || m_PhysicsSolverContext.worldTransforms.Length < count)
            {
                DisposeNativeArray(ref m_PhysicsSolverContext.worldTransforms);
                DisposeNativeArray(ref m_PhysicsSolverContext.rigidBodyIndices);
                m_PhysicsSolverContext.worldTransforms = new NativeArray<float4x4>(count, Allocator.Persistent);
                m_PhysicsSolverContext.rigidBodyIndices = new NativeArray<int>(count, Allocator.Persistent);
            }
        }

        private static void DisposeNativeArray<T>(ref NativeArray<T> array) where T : struct
        {
            if (array.IsCreated)
            {
                array.Dispose();
            }
        }

        private static MMDBulletPhysics.NativeJointData[] BuildPMXJoints(PMXModel model, NativeArray<MMDRigidBody.RigidBodySimulationData> rigidBodies)
        {
            MMDBulletPhysics.NativeJointData[] result = new MMDBulletPhysics.NativeJointData[model.joints.Length];
            for (int i = 0; i < model.joints.Length; ++i)
            {
                PMXJoint joint = model.joints[i];
                if (joint.type != PMXJoint.Type.Spring6DOF && joint.type != PMXJoint.Type.Generic6DOF)
                {
                    throw new NotSupportedException($"PMX joint type {joint.type} is not supported by MMD Bullet physics.");
                }
                if (joint.rigidBodyAIndex < 0 || joint.rigidBodyAIndex >= rigidBodies.Length || joint.rigidBodyBIndex < 0 || joint.rigidBodyBIndex >= rigidBodies.Length)
                {
                    throw new InvalidOperationException($"PMX joint {i} has invalid rigid body indices.");
                }

                float4x4 jointWorld = float4x4.TRS(joint.position, quaternion.EulerZXY(joint.rotation), new float3(1.0f, 1.0f, 1.0f));
                result[i] = new MMDBulletPhysics.NativeJointData
                {
                    type = joint.type,
                    rigidBodyAIndex = joint.rigidBodyAIndex,
                    rigidBodyBIndex = joint.rigidBodyBIndex,
                    frameInA = math.mul(math.inverse(rigidBodies[joint.rigidBodyAIndex].initialWorldTransform), jointWorld),
                    frameInB = math.mul(math.inverse(rigidBodies[joint.rigidBodyBIndex].initialWorldTransform), jointWorld),
                    translationLimitMin = joint.translationLimitMin,
                    translationLimitMax = joint.translationLimitMax,
                    rotationLimitMin = joint.rotationLimitMin,
                    rotationLimitMax = joint.rotationLimitMax,
                    springTranslation = joint.springTranslation,
                    springRotation = joint.springRotation * MMDConstants.k_MMDUnitToUnityUnit * MMDConstants.k_MMDUnitToUnityUnit,
                };
            }

            return result;
        }

        private void ResetSimulatedBoneTransformsToInitial()
        {
            for (int i = 0; i < rigidBodies.Length; ++i)
            {
                MMDRigidBody rigidBody = rigidBodies[i];
                if (!IsSimulated(rigidBody.runtimeData) || rigidBody.relatedBone == null)
                {
                    continue;
                }

                ResetBoneTransformToInitial(rigidBody.relatedBone);
            }
        }

        private static bool IsSimulated(MMDRigidBody.RigidBodySimulationData rigidBodySimulationData)
        {
            return rigidBodySimulationData.mode == PMXRigidBody.Mode.Dynamic || rigidBodySimulationData.mode == PMXRigidBody.Mode.DynamicBoneAligned;
        }

        private static void ResetBoneTransformToInitial(MMDBoneTransform bone)
        {
            bone.transform.localPosition = bone.initialLocalPosition;
            bone.transform.localRotation = bone.initialLocalRotation;
            bone.solverResetPending = true;
        }

        [BurstCompile]
        private static class PhysicsMath
        {
            /// <summary>
            /// Burst implementation that precomputes each rigid body's initial, bone-local, and rest world transforms from its PMX position/rotation and owning bone model position.
            /// </summary>
            /// <param name="runtimeDataArray">Rigid-body simulation data to update in place.</param>
            [BurstCompile]
            internal static void ComputeRigidBodyTransforms(ref NativeArray<MMDRigidBody.RigidBodySimulationData> runtimeDataArray)
            {
                for (int i = 0; i < runtimeDataArray.Length; ++i)
                {
                    MMDRigidBody.RigidBodySimulationData rigidBody = runtimeDataArray[i];
                    rigidBody.initialTransform = ComputeRigidBodyInitialTransform(rigidBody.position, rigidBody.rotation, rigidBody.boneModelPosition);
                    rigidBody.boneLocalTransform = math.inverse(rigidBody.initialTransform);
                    rigidBody.initialWorldTransform = ComputeRigidBodyRestWorldTransform(rigidBody);
                    runtimeDataArray[i] = rigidBody;
                }
            }

            /// <summary>
            /// Precomputes the static rigid-body index lists consumed each physics step: the identity index mapping, the kinetic (bone-driven) body list, and the simulated body list sorted by (bone transform level, related bone index). The filters and sort keys are immutable per model, so this runs once at initialization instead of re-filtering and re-sorting every step.
            /// </summary>
            /// <param name="runtimeContext">Physics solver context with populated rigid-body simulation data and an allocated index scratch buffer.</param>
            internal static void BuildStaticRigidBodyLists(ref PhysicsSolverContext runtimeContext)
            {
                int kineticCount = 0;
                int simulatedCount = 0;
                for (int i = 0; i < runtimeContext.rigidBodySimulationData.Length; ++i)
                {
                    MMDRigidBody.RigidBodySimulationData rigidBody = runtimeContext.rigidBodySimulationData[i];
                    runtimeContext.rigidBodyIndices[i] = rigidBody.rigidBodyIndex;
                    if (rigidBody.mode == PMXRigidBody.Mode.Kinetic)
                    {
                        ++kineticCount;
                    }
                    if (IsSimulated(rigidBody) && rigidBody.hasRelatedBone)
                    {
                        ++simulatedCount;
                    }
                }

                ResizePersistent(ref runtimeContext.kineticRigidBodyIndices, kineticCount);
                ResizePersistent(ref runtimeContext.sortedSimulatedRigidBodyIndices, simulatedCount);
                ResizePersistent(ref runtimeContext.previousKineticTargets, kineticCount);
                ResizePersistent(ref runtimeContext.currentKineticTargets, kineticCount);
                kineticCount = 0;
                simulatedCount = 0;
                for (int i = 0; i < runtimeContext.rigidBodySimulationData.Length; ++i)
                {
                    MMDRigidBody.RigidBodySimulationData rigidBody = runtimeContext.rigidBodySimulationData[i];
                    if (rigidBody.mode == PMXRigidBody.Mode.Kinetic)
                    {
                        runtimeContext.kineticRigidBodyIndices[kineticCount] = i;
                        ++kineticCount;
                    }
                    if (IsSimulated(rigidBody) && rigidBody.hasRelatedBone)
                    {
                        runtimeContext.sortedSimulatedRigidBodyIndices[simulatedCount] = i;
                        ++simulatedCount;
                    }
                }

                runtimeContext.sortedSimulatedRigidBodyIndices.Sort(new RigidBodyTransformLevelComparer { rigidBodies = runtimeContext.rigidBodySimulationData, });
            }

            /// <summary>
            /// Burst implementation that advances one MMD physics step: syncs or seeds bone-driven rigid bodies, steps the native Bullet simulation when time elapses, and writes simulated transforms back to bones.
            /// </summary>
            /// <param name="elapsedTime">Elapsed time for the step; values at or below zero skip the simulation advance.</param>
            /// <param name="transformManagerContext">Bone solver context supplying and receiving bone transforms.</param>
            /// <param name="runtimeContext">Physics solver context holding rigid-body data and the native Bullet context.</param>
            [BurstCompile]
            internal static void TransformPhysicsInternal(float elapsedTime, ref MMDTransformManager.SolverContext transformManagerContext, ref PhysicsSolverContext runtimeContext)
            {
                // The public diagnostic is per rendered frame. Clear it before
                // zero-time synchronization so a drop from an earlier frame is
                // never reported again while physics is suspended.
                runtimeContext.lastSubstepCount = 0;
                runtimeContext.lastDroppedSimulationSeconds = 0.0f;
                runtimeContext.lastPoseSourceFlipCount = 0;
                if (!runtimeContext.bulletPhysicsContext.isValid)
                {
                    return;
                }

                // The prepare pass below memoizes observed bone world matrices per physics step; clear the per-step flags so no matrix computed in an earlier step (or rewritten by IK since) is trusted.
                for (int i = 0; i < transformManagerContext.boneStateData.Length; ++i)
                {
                    PMXUtilities.ElementAt(transformManagerContext.boneStateData, i).worldMatrixValid = false;
                }

                PrepareRigidBodyRelatedBoneMatrices(ref transformManagerContext, ref runtimeContext);
                if (!runtimeContext.initialPoseApplied)
                {
                    SeedRigidBodiesFromBones(in transformManagerContext, ref runtimeContext);
                    if (elapsedTime <= 0.0f)
                    {
                        return;
                    }
                }
                else if (elapsedTime <= 0.0f)
                {
                    SyncBoneDrivenRigidBodies(in transformManagerContext, ref runtimeContext);
                    // Banxia pose-hold patch: zero-substep frames previously left the simulated bones on the
                    // animation-layer sample, hard-alternating with the physics pose rendered on stepping
                    // frames. Re-assert the last physics pose so the rendered pose stays on the physics
                    // solution while the fixed-step schedule catches up.
                    ReplayLastPhysicsPoseToBones(ref transformManagerContext, ref runtimeContext);
                    return;
                }

                if (StepSimulationWithKineticInterpolation(elapsedTime, in transformManagerContext, ref runtimeContext))
                {
                    ApplyDynamicRigidBodiesToBones(ref transformManagerContext, ref runtimeContext);
                }
            }

            /// <summary>
            /// Advances the simulation in fixed substeps, moving the kinetic bodies along an interpolated path from their previous-frame targets to the current ones and pushing each waypoint WITHOUT clearing velocity, so Bullet derives proper kinematic velocities from the motion. This gives the constraint solver continuously-moving kinematic anchors it can track at MMD's reference iteration count, instead of the old once-per-frame teleport with zeroed velocity whose full-frame position error made translation-locked chains (spring-less hair) blow apart during fast motion. Variable frame times are carried in a time accumulator so they map onto whole substeps without drifting.
            /// </summary>
            /// <param name="elapsedTime">Elapsed time to simulate.</param>
            /// <param name="transformManagerContext">Bone solver context supplying bone matrices for the kinetic targets.</param>
            /// <param name="runtimeContext">Physics solver context to advance.</param>
            /// <returns>Whether at least one substep was simulated.</returns>
            private static bool StepSimulationWithKineticInterpolation(float elapsedTime, in MMDTransformManager.SolverContext transformManagerContext, ref PhysicsSolverContext runtimeContext)
            {
                ComputeKineticTargets(in transformManagerContext, ref runtimeContext, ref runtimeContext.currentKineticTargets);
                runtimeContext.lastSubstepCount = 0;
                runtimeContext.lastDroppedSimulationSeconds = 0.0f;
                if (runtimeContext.resetKineticInterpolation)
                {
                    HardSyncCurrentKineticTargets(ref runtimeContext);
                    runtimeContext.resetKineticInterpolation = false;
                }

                runtimeContext.timeAccumulator += math.max(0.0f, elapsedTime);
                int substepCount = ResolveRuntimeSubstepBudget(
                    runtimeContext.timeAccumulator,
                    runtimeContext.simulationFrequencyHz,
                    runtimeContext.maximumSubstepsPerFrame,
                    out float retainedAccumulator,
                    out float droppedSeconds);
                runtimeContext.timeAccumulator = retainedAccumulator;
                if (droppedSeconds > 0.0f)
                {
                    runtimeContext.lastDroppedSimulationSeconds = droppedSeconds;
                    runtimeContext.totalDroppedSimulationSeconds += droppedSeconds;
                    runtimeContext.droppedSimulationFrameCount++;
                    HardSyncCurrentKineticTargets(ref runtimeContext);
                }
                if (substepCount <= 0)
                {
                    return false;
                }

                runtimeContext.lastSubstepCount = substepCount;
                float fixedTimeStep = 1.0f / math.clamp(runtimeContext.simulationFrequencyHz, 30, 240);
                runtimeContext.timeAccumulator = math.min(runtimeContext.timeAccumulator - substepCount * fixedTimeStep, fixedTimeStep);

                int kineticCount = runtimeContext.kineticRigidBodyIndices.Length;
                for (int step = 0; step < substepCount; ++step)
                {
                    float alpha = (float)(step + 1) / substepCount;
                    for (int i = 0; i < kineticCount; ++i)
                    {
                        float4x4 previous = runtimeContext.previousKineticTargets[i];
                        float4x4 current = runtimeContext.currentKineticTargets[i];
                        float3 position = math.lerp(previous.c3.xyz, current.c3.xyz, alpha);
                        quaternion rotation = math.slerp(new quaternion(previous), new quaternion(current), alpha);
                        runtimeContext.worldTransforms[i] = new float4x4(rotation, position);
                    }
                    runtimeContext.bulletPhysicsContext.SetRigidBodyTransforms(kineticCount, runtimeContext.worldTransforms, runtimeContext.kineticRigidBodyIndices, false);
                    runtimeContext.bulletPhysicsContext.StepSimulation(fixedTimeStep);
                }

                NativeArray<float4x4> swap = runtimeContext.previousKineticTargets;
                runtimeContext.previousKineticTargets = runtimeContext.currentKineticTargets;
                runtimeContext.currentKineticTargets = swap;
                return true;
            }

            private static void HardSyncCurrentKineticTargets(ref PhysicsSolverContext runtimeContext)
            {
                int kineticCount = runtimeContext.kineticRigidBodyIndices.Length;
                for (int i = 0; i < kineticCount; ++i)
                {
                    float4x4 current = runtimeContext.currentKineticTargets[i];
                    runtimeContext.previousKineticTargets[i] = current;
                    runtimeContext.worldTransforms[i] = current;
                }
                runtimeContext.bulletPhysicsContext.SetRigidBodyTransforms(
                    kineticCount,
                    runtimeContext.worldTransforms,
                    runtimeContext.kineticRigidBodyIndices,
                    true);
            }

            /// <summary>
            /// Resolves a bounded fixed-step budget. Whole steps beyond the
            /// per-frame cap are discarded instead of being carried into later
            /// frames, which prevents a self-sustaining catch-up spiral.
            /// </summary>
            internal static int ResolveRuntimeSubstepBudget(
                float accumulatedSeconds,
                int frequencyHz,
                int maximumSubsteps,
                out float retainedAccumulator,
                out float droppedSeconds)
            {
                float normalized = math.max(0.0f, accumulatedSeconds);
                float fixedTimeStep = 1.0f / math.clamp(frequencyHz, 30, 240);
                int requestedSteps = (int)(normalized / fixedTimeStep);
                int allowedSteps = math.min(requestedSteps, math.clamp(maximumSubsteps, 1, 8));
                float fractionalRemainder = normalized - requestedSteps * fixedTimeStep;
                retainedAccumulator = allowedSteps * fixedTimeStep +
                    math.clamp(fractionalRemainder, 0.0f, fixedTimeStep);
                droppedSeconds = math.max(0.0f, normalized - retainedAccumulator);
                return allowedSteps;
            }

            /// <summary>
            /// Computes the world target transform of every kinetic (bone-driven) rigid body from the current bone matrices.
            /// </summary>
            /// <param name="transformManagerContext">Bone solver context supplying bone matrices.</param>
            /// <param name="runtimeContext">Physics solver context providing the kinetic index list and simulation data.</param>
            /// <param name="targets">Output array, indexed like the kinetic index list.</param>
            private static void ComputeKineticTargets(in MMDTransformManager.SolverContext transformManagerContext, ref PhysicsSolverContext runtimeContext, ref NativeArray<float4x4> targets)
            {
                for (int i = 0; i < runtimeContext.kineticRigidBodyIndices.Length; ++i)
                {
                    int bodyIndex = runtimeContext.kineticRigidBodyIndices[i];
                    MMDRigidBody.RigidBodySimulationData rigidBody = runtimeContext.rigidBodySimulationData[bodyIndex];
                    targets[i] = runtimeContext.externalKinematicFlags.IsCreated &&
                        runtimeContext.externalKinematicFlags[bodyIndex]
                        ? runtimeContext.externalKinematicTargets[bodyIndex]
                        : ComputeRigidBodyWorldTransform(rigidBody, in transformManagerContext.boneStateData);
                }
            }

            private static void SeedRigidBodiesFromBones(in MMDTransformManager.SolverContext transformManagerContext, ref PhysicsSolverContext runtimeContext)
            {
                for (int i = 0; i < runtimeContext.rigidBodySimulationData.Length; ++i)
                {
                    MMDRigidBody.RigidBodySimulationData rigidBody = runtimeContext.rigidBodySimulationData[i];
                    runtimeContext.worldTransforms[i] = runtimeContext.externalKinematicFlags.IsCreated &&
                        runtimeContext.externalKinematicFlags[i]
                        ? runtimeContext.externalKinematicTargets[i]
                        : ComputeRigidBodyWorldTransform(rigidBody, in transformManagerContext.boneStateData);
                }

                runtimeContext.bulletPhysicsContext.SetRigidBodyTransforms(runtimeContext.rigidBodySimulationData.Length, runtimeContext.worldTransforms, runtimeContext.rigidBodyIndices, true);

                ComputeKineticTargets(in transformManagerContext, ref runtimeContext, ref runtimeContext.previousKineticTargets);
                runtimeContext.timeAccumulator = 0.0f;
                runtimeContext.initialPoseApplied = true;
            }

            // Hard-syncs kinetic bodies to the current pose (teleport, velocities cleared) for zero-elapsed updates, keeping the interpolation baseline in step.
            private static void SyncBoneDrivenRigidBodies(in MMDTransformManager.SolverContext transformManagerContext, ref PhysicsSolverContext runtimeContext)
            {
                ComputeKineticTargets(in transformManagerContext, ref runtimeContext, ref runtimeContext.previousKineticTargets);
                for (int i = 0; i < runtimeContext.previousKineticTargets.Length; ++i)
                {
                    runtimeContext.worldTransforms[i] = runtimeContext.previousKineticTargets[i];
                }
                runtimeContext.bulletPhysicsContext.SetRigidBodyTransforms(runtimeContext.kineticRigidBodyIndices.Length, runtimeContext.worldTransforms, runtimeContext.kineticRigidBodyIndices, true);
            }

            private static void PrepareRigidBodyRelatedBoneMatrices(ref MMDTransformManager.SolverContext transformManagerContext, ref PhysicsSolverContext runtimeContext)
            {
                for (int i = 0; i < runtimeContext.rigidBodySimulationData.Length; ++i)
                {
                    MMDRigidBody.RigidBodySimulationData rigidBody = runtimeContext.rigidBodySimulationData[i];
                    if (rigidBody.hasRelatedBone)
                    {
                        PrepareBoneWorldMatrix(ref transformManagerContext, rigidBody.relatedBoneIndex);
                    }
                }
            }

            private static void PrepareBoneWorldMatrix(ref MMDTransformManager.SolverContext transformManagerContext, int boneIndex)
            {
                ref MMDBoneTransform.BoneSolverState state = ref PMXUtilities.ElementAt(transformManagerContext.boneStateData, boneIndex);
                if (state.hasSolvedTransform || state.worldMatrixValid)
                {
                    return;
                }

                ref MMDBoneTransform.BoneSolverConfig config = ref PMXUtilities.ElementAt(transformManagerContext.boneConfigData, boneIndex);
                if (config.parentBoneIndex >= 0)
                {
                    PrepareBoneWorldMatrix(ref transformManagerContext, config.parentBoneIndex);
                }

                if ((config.rotationConstraint || config.translationConstraint) && transformManagerContext.solveConstraints)
                {
                    MMDBoneTransform.UpdateLocalMatrix(ref transformManagerContext, boneIndex);
                }
                else
                {
                    float4x4 parentWorldMatrix = MMDBoneTransform.GetParentWorldMatrix(ref transformManagerContext, boneIndex);
                    MMDBoneTransform.ObserveLocalMatrix(in config, ref state, parentWorldMatrix);
                }

                state.worldMatrixValid = true;
            }

            private struct RigidBodyTransformLevelComparer : IComparer<int>
            {
                public NativeArray<MMDRigidBody.RigidBodySimulationData> rigidBodies;

                public int Compare(int x, int y)
                {
                    int boneIndexX = rigidBodies[x].relatedBoneIndex;
                    int boneIndexY = rigidBodies[y].relatedBoneIndex;
                    int transformLevelComparison = rigidBodies[x].boneTransformLevel.CompareTo(rigidBodies[y].boneTransformLevel);
                    return transformLevelComparison != 0 ? transformLevelComparison : boneIndexX.CompareTo(boneIndexY);
                }
            }

            private static void ApplyDynamicRigidBodiesToBones(ref MMDTransformManager.SolverContext transformManagerContext, ref PhysicsSolverContext runtimeContext)
            {
                int count = runtimeContext.sortedSimulatedRigidBodyIndices.Length;
                runtimeContext.bulletPhysicsContext.GetRigidBodyMotionTransforms(count, runtimeContext.sortedSimulatedRigidBodyIndices, ref runtimeContext.worldTransforms);

                for (int i = 0; i < count; ++i)
                {
                    MMDRigidBody.RigidBodySimulationData rigidBody = runtimeContext.rigidBodySimulationData[runtimeContext.sortedSimulatedRigidBodyIndices[i]];
                    float4x4 boneWorldMatrix = math.mul(runtimeContext.worldTransforms[i], rigidBody.boneLocalTransform);
                    bool cacheValid = i < runtimeContext.lastPhysicsPoseValid.Length;
                    if (rigidBody.mode == PMXRigidBody.Mode.DynamicBoneAligned)
                    {
                        float3 modelTranslationDelta = ApplyKineticBoneAlignedWorldMatrixToBone(
                            ref transformManagerContext,
                            rigidBody.relatedBoneIndex,
                            boneWorldMatrix,
                            out float3 alignedLocalPosition,
                            out quaternion alignedLocalRotation);
                        ShiftKineticBoneAlignedBodyPosition(ref runtimeContext.bulletPhysicsContext, rigidBody, modelTranslationDelta);
                        // Cache rotation only: bone-aligned bodies keep their bone-driven translation, so the hold replay must never freeze a legitimately animated position.
                        if (cacheValid)
                        {
                            runtimeContext.lastPhysicsLocalPositions[i] = alignedLocalPosition;
                            runtimeContext.lastPhysicsLocalRotations[i] = alignedLocalRotation;
                            runtimeContext.lastPhysicsPoseValid[i] = 1;
                        }
                        continue;
                    }

                    ApplyKineticWorldMatrixToBone(
                        ref transformManagerContext,
                        rigidBody.relatedBoneIndex,
                        boneWorldMatrix,
                        out float3 localPosition,
                        out quaternion localRotation);
                    if (cacheValid)
                    {
                        runtimeContext.lastPhysicsLocalPositions[i] = localPosition;
                        runtimeContext.lastPhysicsLocalRotations[i] = localRotation;
                        runtimeContext.lastPhysicsPoseValid[i] = 1;
                    }
                }
            }

            /// <summary>
            /// Banxia pose-hold patch: re-asserts the last physics-applied bone pose on zero-substep frames so the rendered pose stays on the physics solution, and maintains the pose-source-flip diagnostic counting simulated bones whose final local pose deviates from the cached physics pose.
            /// </summary>
            [BurstCompile]
            private static void ReplayLastPhysicsPoseToBones(ref MMDTransformManager.SolverContext transformManagerContext, ref PhysicsSolverContext runtimeContext)
            {
                int count = runtimeContext.sortedSimulatedRigidBodyIndices.Length;
                if (count == 0 || !runtimeContext.lastPhysicsPoseValid.IsCreated)
                {
                    return;
                }

                int flipBones = 0;
                for (int i = 0; i < count && i < runtimeContext.lastPhysicsPoseValid.Length; ++i)
                {
                    MMDRigidBody.RigidBodySimulationData rigidBody = runtimeContext.rigidBodySimulationData[runtimeContext.sortedSimulatedRigidBodyIndices[i]];
                    int boneIndex = rigidBody.relatedBoneIndex;
                    if (boneIndex < 0 || boneIndex >= transformManagerContext.boneStateData.Length)
                    {
                        continue;
                    }

                    ref MMDBoneTransform.BoneSolverState state = ref PMXUtilities.ElementAt(transformManagerContext.boneStateData, boneIndex);
                    if (runtimeContext.lastPhysicsPoseValid[i] == 0)
                    {
                        // No cached physics pose yet (before the first substep apply): this frame still renders the animation sample.
                        ++flipBones;
                        continue;
                    }

                    bool boneAligned = rigidBody.mode == PMXRigidBody.Mode.DynamicBoneAligned;
                    float3 holdPosition = boneAligned ? state.localPosition : runtimeContext.lastPhysicsLocalPositions[i];
                    quaternion holdRotation = runtimeContext.lastPhysicsLocalRotations[i];
                    MMDBoneTransform.ApplyLocalTransformToBone(ref transformManagerContext, boneIndex, holdPosition, holdRotation);
                    if (HasPhysicsPoseDeviation(ref state, boneAligned, ref runtimeContext, i))
                    {
                        ++flipBones;
                    }
                }

                runtimeContext.lastPoseSourceFlipCount = flipBones;
                if (flipBones > 0)
                {
                    ++runtimeContext.totalPoseSourceFlipFrames;
                }
            }

            /// <summary>
            /// Banxia pose-hold patch: reports whether a bone's current local pose deviates from the cached physics pose beyond the flip-detection threshold (position >1mm or rotation >~0.8 degrees).
            /// </summary>
            private static bool HasPhysicsPoseDeviation(ref MMDBoneTransform.BoneSolverState state, bool rotationOnly, ref PhysicsSolverContext runtimeContext, int cacheIndex)
            {
                if (!rotationOnly)
                {
                    float3 positionDelta = state.localPosition - runtimeContext.lastPhysicsLocalPositions[cacheIndex];
                    if (math.lengthsq(positionDelta) > 1e-6f)
                    {
                        return true;
                    }
                }

                float rotationDot = math.dot(state.localRotation, runtimeContext.lastPhysicsLocalRotations[cacheIndex]);
                return math.abs(rotationDot - 1.0f) > 1e-4f;
            }

            /// <summary>
            /// Computes a rigid body's world transform, composing its related bone's world matrix with its initial transform when bone-related, or returning the initial transform otherwise.
            /// </summary>
            /// <param name="rigidBody">Rigid-body simulation data to transform.</param>
            /// <param name="boneStateData">Bone solver state used to resolve the related bone's world matrix.</param>
            /// <returns>The rigid body's world transform matrix.</returns>
            internal static float4x4 ComputeRigidBodyWorldTransform(MMDRigidBody.RigidBodySimulationData rigidBody, in NativeArray<MMDBoneTransform.BoneSolverState> boneStateData)
            {
                return rigidBody.hasRelatedBone ? math.mul(PMXUtilities.ElementAt(boneStateData, rigidBody.relatedBoneIndex).worldMatrix, rigidBody.initialTransform) : rigidBody.initialTransform;
            }

            private static void ShiftKineticBoneAlignedBodyPosition(ref MMDBulletPhysics context, MMDRigidBody.RigidBodySimulationData rigidBody, float3 modelTranslationDelta)
            {
                context.ShiftRigidBodyPosition(rigidBody.rigidBodyIndex, modelTranslationDelta);
            }

            private static bool IsSimulated(MMDRigidBody.RigidBodySimulationData rigidBodySimulationData)
            {
                return rigidBodySimulationData.mode == PMXRigidBody.Mode.Dynamic || rigidBodySimulationData.mode == PMXRigidBody.Mode.DynamicBoneAligned;
            }

            private static void ApplyKineticWorldMatrixToBone(ref MMDTransformManager.SolverContext transformManagerContext, int boneIndex, float4x4 worldMatrix, out float3 appliedLocalPosition, out quaternion appliedLocalRotation)
            {
                float4x4 parentWorldMatrix = MMDBoneTransform.GetParentWorldMatrix(ref transformManagerContext, boneIndex);
                float4x4 localMatrix = math.mul(math.inverse(parentWorldMatrix), worldMatrix);
                appliedLocalPosition = localMatrix.c3.xyz;
                appliedLocalRotation = new quaternion(localMatrix);
                MMDBoneTransform.ApplyLocalTransformToBone(ref transformManagerContext, boneIndex, appliedLocalPosition, appliedLocalRotation);
            }

            private static float3 ApplyKineticBoneAlignedWorldMatrixToBone(ref MMDTransformManager.SolverContext transformManagerData, int boneIndex, float4x4 worldMatrix, out float3 appliedLocalPosition, out quaternion appliedLocalRotation)
            {
                float3 boneLocalPosition = PMXUtilities.ElementAt(transformManagerData.boneStateData, boneIndex).localPosition;
                float4x4 parentWorldMatrix = MMDBoneTransform.GetParentWorldMatrix(ref transformManagerData, boneIndex);
                float4x4 localMatrix = math.mul(math.inverse(parentWorldMatrix), worldMatrix);
                float3 localPosition = localMatrix.c3.xyz;
                float3 localTranslationDelta = localPosition - boneLocalPosition;
                appliedLocalPosition = boneLocalPosition;
                appliedLocalRotation = new quaternion(localMatrix);
                MMDBoneTransform.ApplyLocalTransformToBone(ref transformManagerData, boneIndex, boneLocalPosition, appliedLocalRotation);
                return math.mul(parentWorldMatrix, new float4(localTranslationDelta, 0)).xyz;
            }

            private static float4x4 ComputeRigidBodyRestWorldTransform(MMDRigidBody.RigidBodySimulationData rigidBody)
            {
                if (!rigidBody.hasRelatedBone)
                {
                    return rigidBody.initialTransform;
                }

                return math.mul(float4x4.Translate(rigidBody.boneModelPosition), rigidBody.initialTransform);
            }

            private static float4x4 ComputeRigidBodyInitialTransform(float3 position, float3 rotation, float3 bonePosition)
            {
                float3 relatedPosition = position - bonePosition;
                float4x4 initialTransform = math.mul(float4x4.Translate(relatedPosition), float4x4.EulerZXY(rotation));
                return initialTransform;
            }
        }
    }
}
