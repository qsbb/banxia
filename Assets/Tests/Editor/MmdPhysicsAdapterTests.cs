#if UNITY_EDITOR
using NUnit.Framework;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UMT;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class MmdPhysicsAdapterTests
    {
        [SetUp]
        public void SetUp()
        {
            MMDPhysicsManager.ConfigureRuntimeQuality(120, 4, 2);
        }

        [TearDown]
        public void TearDown()
        {
            MMDPhysicsManager.ConfigureRuntimeQuality(120, 4, 2);
        }

        [Test]
        public void ExternalSphereDataIsKinematicAndCollidesWithModelGroups()
        {
            var data = MMDPhysicsManager.CreateExternalKinematicSphereData(
                12,
                .03f,
                new Vector3(1f, 2f, 3f));

            Assert.AreEqual(12, data.rigidBodyIndex);
            Assert.AreEqual(-1, data.relatedBoneIndex);
            Assert.AreEqual(PMXRigidBody.Shape.Sphere, data.shape);
            Assert.AreEqual(PMXRigidBody.Mode.Kinetic, data.mode);
            Assert.That(data.size.x, Is.EqualTo(.03f).Within(.0001f));
            Assert.AreEqual(15, data.groupIndex);
            Assert.AreEqual(-1, data.collisionGroupMask);
            Assert.IsFalse(data.hasRelatedBone);
        }

        [Test]
        public void ExternalSphereUsesRequestedCollisionGroup()
        {
            var data = MMDPhysicsManager.CreateExternalKinematicSphereData(
                3,
                .02f,
                Vector3.zero,
                6);

            Assert.AreEqual(6, data.groupIndex);
            Assert.AreEqual(-1, data.collisionGroupMask);
        }

        [Test]
        public void RuntimeSubstepBudgetDropsStaleCatchUpInsteadOfSpiraling()
        {
            var steps = MMDPhysicsManager.ResolveRuntimeSubstepBudget(
                .5f,
                out var retained,
                out var dropped);

            Assert.That(steps, Is.EqualTo(MMDPhysicsManager.maximumSubstepsPerFrame));
            Assert.That(steps, Is.EqualTo(4));
            Assert.That(retained, Is.LessThanOrEqualTo(5f / MMDPhysicsManager.simulationFrequencyHz));
            Assert.That(dropped, Is.GreaterThan(.45f));
        }

        [Test]
        public void RuntimeSubstepBudgetKeepsNormalQuestFrameRemainder()
        {
            var elapsed = 1f / 72f;
            var steps = MMDPhysicsManager.ResolveRuntimeSubstepBudget(
                elapsed,
                out var retained,
                out var dropped);

            Assert.That(steps, Is.EqualTo(1));
            Assert.That(retained, Is.EqualTo(elapsed).Within(.00001f));
            Assert.That(dropped, Is.Zero.Within(.00001f));
        }

        [Test]
        public void BalancedPolicyUsesBoundedSixtyHertzBudget()
        {
            var steps = MMDPhysicsManager.ResolveRuntimeSubstepBudget(
                .06f,
                60,
                2,
                out var retained,
                out var dropped);

            Assert.That(steps, Is.EqualTo(2));
            Assert.That(retained, Is.LessThanOrEqualTo(3f / 60f));
            Assert.That(dropped, Is.GreaterThan(.015f));
        }

        [Test]
        public void RuntimeQualitySanitizesFrequencySubstepsAndReinforcement()
        {
            MMDPhysicsManager.ConfigureRuntimeQuality(1, 99, 9);

            Assert.That(MMDPhysicsManager.simulationFrequencyHz, Is.EqualTo(30));
            Assert.That(MMDPhysicsManager.maximumSubstepsPerFrame, Is.EqualTo(8));
            Assert.That(MMDPhysicsManager.lockedTranslationReinforceCount, Is.EqualTo(2));
        }

        [Test]
        public void ReducedReinforcementOnlyAppliesToJointHeavyModels()
        {
            MMDPhysicsManager.ConfigureRuntimeQuality(60, 2, 1);

            Assert.That(MMDPhysicsManager.ResolveLockedTranslationReinforcement(43), Is.EqualTo(2));
            Assert.That(MMDPhysicsManager.ResolveLockedTranslationReinforcement(138), Is.EqualTo(1));
        }

        [Test]
        public void ConnectedLockedHairOrSkirtChainRestoresStableReinforcement()
        {
            MMDPhysicsManager.ConfigureRuntimeQuality(60, 2, 1);
            var joints = new PMXJoint[81];
            for (var index = 0; index < 4; index++)
            {
                joints[index].rigidBodyAIndex = index;
                joints[index].rigidBodyBIndex = index + 1;
            }

            Assert.That(
                MMDPhysicsManager.RequiresFullLockedTranslationReinforcement(
                    joints,
                    5),
                Is.True);
            Assert.That(
                MMDPhysicsManager.ResolveLockedTranslationReinforcement(
                    joints,
                    5),
                Is.EqualTo(2));
        }

        [Test]
        public void IndependentLockedJointsKeepHeavyModelReduction()
        {
            MMDPhysicsManager.ConfigureRuntimeQuality(60, 2, 1);
            var joints = new PMXJoint[81];
            for (var index = 0; index < joints.Length; index++)
            {
                joints[index].rigidBodyAIndex = index * 2;
                joints[index].rigidBodyBIndex = index * 2 + 1;
            }

            Assert.That(
                MMDPhysicsManager.RequiresFullLockedTranslationReinforcement(
                    joints,
                    joints.Length * 2),
                Is.False);
            Assert.That(
                MMDPhysicsManager.ResolveLockedTranslationReinforcement(
                    joints,
                    joints.Length * 2),
                Is.EqualTo(1));
        }

        [Test]
        public void OptionalRealForestBerryUsesTopologyBasedStabilityProtection()
        {
            var pmxPath = System.Environment.GetEnvironmentVariable(
                "BANXIA_TEST_FOREST_BERRY_PMX");
            if (string.IsNullOrWhiteSpace(pmxPath) || !File.Exists(pmxPath))
            {
                Assert.Ignore(
                    "BANXIA_TEST_FOREST_BERRY_PMX is not configured for this run.");
            }

            PMXModel model = null;
            try
            {
                using (var stream = File.OpenRead(pmxPath))
                {
                    model = PMXReader.Read(stream, true);
                }
                Assert.That(
                    MMDPhysicsManager.RequiresFullLockedTranslationReinforcement(
                        model.joints,
                        model.rigidBodies.Length),
                    Is.True);
                Assert.That(
                    MMDPhysicsManager.ResolveLockedTranslationReinforcement(
                        model.joints,
                        model.rigidBodies.Length),
                    Is.EqualTo(2));
            }
            finally
            {
                if (model != null)
                {
                    UnityEngine.Object.DestroyImmediate(model);
                }
            }
        }

        [Test]
        public void PerformancePolicyCanRemoveHeavyModelReinforcementOnly()
        {
            try
            {
                MMDPhysicsManager.ConfigureRuntimeQuality(60, 2, 0);

                Assert.That(
                    MMDPhysicsManager.ResolveLockedTranslationReinforcement(43),
                    Is.EqualTo(2));
                Assert.That(
                    MMDPhysicsManager.ResolveLockedTranslationReinforcement(138),
                    Is.Zero);
            }
            finally
            {
                MMDPhysicsManager.ConfigureRuntimeQuality(120, 4, 2);
            }
        }

        [Test]
        public void ExternalHandProbeIsInactiveOutsideAvatarBroadphase()
        {
            var bounds = new Bounds(Vector3.zero, Vector3.one);

            Assert.IsFalse(AvatarMmdPhysicsAdapter.ShouldActivatePhysicsProbe(
                true,
                true,
                bounds,
                new Vector3(1.7f, 0f, 0f),
                .02f));
            Assert.IsTrue(AvatarMmdPhysicsAdapter.ShouldActivatePhysicsProbe(
                true,
                true,
                bounds,
                new Vector3(.65f, 0f, 0f),
                .02f));
            Assert.IsFalse(AvatarMmdPhysicsAdapter.ShouldActivatePhysicsProbe(
                false,
                false,
                default,
                Vector3.zero,
                .02f));
        }

        [TestCase(false, false, false, false)]
        [TestCase(true, false, false, false)]
        [TestCase(true, true, false, true)]
        [TestCase(true, false, true, true)]
        public void PhysicalContactEvaluationRunsOnlyWithInteractionAndAnActiveProbe(
            bool interactionAvailable,
            bool leftProbeActive,
            bool rightProbeActive,
            bool expected)
        {
            Assert.That(
                QuestTrackedHandVisualizer.ShouldEvaluatePhysicalContacts(
                    interactionAvailable,
                    leftProbeActive,
                    rightProbeActive),
                Is.EqualTo(expected));
        }

        [Test]
        public void JointHeavyModelsRemainCappedAtOneInFinePolicy()
        {
            MMDPhysicsManager.ConfigureRuntimeQuality(120, 4, 2);

            Assert.That(MMDPhysicsManager.ResolveLockedTranslationReinforcement(43), Is.EqualTo(2));
            Assert.That(MMDPhysicsManager.ResolveLockedTranslationReinforcement(138), Is.EqualTo(1));
        }

        [Test]
        public void ExternalCollisionGroupPrefersUnusedGroupWithoutChangingInternalPairs()
        {
            var root = new GameObject("ExternalCollisionGroupTest");
            var firstObject = new GameObject("First");
            var secondObject = new GameObject("Second");
            try
            {
                firstObject.transform.SetParent(root.transform, false);
                secondObject.transform.SetParent(root.transform, false);
                var first = firstObject.AddComponent<MMDRigidBody>();
                var second = secondObject.AddComponent<MMDRigidBody>();
                first.groupIndex = 15;
                first.collisionGroupMask = unchecked((short)(1 << 15));
                second.groupIndex = 3;
                second.collisionGroupMask = 1 << 3;

                var group = MMDPhysicsManager.ResolveExternalKinematicCollisionGroup(
                    new[] { first, second },
                    out var mayExpandMasks);

                Assert.AreEqual(14, group);
                Assert.IsTrue(mayExpandMasks);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void LimitedModelMaskCollidesAfterSafeUnusedGroupExpansion()
        {
            var bodies = new NativeArray<MMDRigidBody.RigidBodySimulationData>(2, Allocator.Temp);
            var transforms = new NativeArray<float4x4>(1, Allocator.Temp);
            var indices = new NativeArray<int>(1, Allocator.Temp);
            var result = new NativeArray<float4x4>(1, Allocator.Temp);
            var physics = new MMDBulletPhysics(float3.zero, 8, 120, 1f / 120f);
            try
            {
                var dynamicBody = CreateSphereBody(0, PMXRigidBody.Mode.Dynamic, 0, Vector3.zero);
                dynamicBody.collisionGroupMask = 1 << 14;
                bodies[0] = dynamicBody;
                bodies[1] = MMDPhysicsManager.CreateExternalKinematicSphereData(
                    1,
                    .05f,
                    new Vector3(-.15f, 0f, 0f),
                    14);
                physics.BuildRigidBodies(bodies);

                indices[0] = 1;
                for (var step = 0; step <= 60; step++)
                {
                    transforms[0] = float4x4.Translate(new float3(-.15f + step * .005f, 0f, 0f));
                    physics.SetRigidBodyTransforms(1, transforms, indices, false);
                    physics.StepSimulation(1f / 120f);
                }

                indices[0] = 0;
                physics.GetRigidBodyMotionTransforms(1, indices, ref result);
                Assert.Greater(result[0].c3.x, .02f);
            }
            finally
            {
                physics.Dispose();
                if (result.IsCreated) result.Dispose();
                if (indices.IsCreated) indices.Dispose();
                if (transforms.IsCreated) transforms.Dispose();
                if (bodies.IsCreated) bodies.Dispose();
            }
        }

        [Test]
        public void ExistingNativePluginResolvesExternalSphereAgainstDynamicBody()
        {
            var bodies = new NativeArray<MMDRigidBody.RigidBodySimulationData>(2, Allocator.Temp);
            var transforms = new NativeArray<float4x4>(1, Allocator.Temp);
            var indices = new NativeArray<int>(1, Allocator.Temp);
            var result = new NativeArray<float4x4>(1, Allocator.Temp);
            var physics = new MMDBulletPhysics(float3.zero, 8, 120, 1f / 120f);
            try
            {
                bodies[0] = CreateSphereBody(0, PMXRigidBody.Mode.Dynamic, 0, Vector3.zero);
                bodies[1] = MMDPhysicsManager.CreateExternalKinematicSphereData(
                    1,
                    .05f,
                    new Vector3(-.15f, 0f, 0f));
                physics.BuildRigidBodies(bodies);

                indices[0] = 1;
                for (var step = 0; step <= 60; step++)
                {
                    transforms[0] = float4x4.Translate(new float3(-.15f + step * .005f, 0f, 0f));
                    physics.SetRigidBodyTransforms(1, transforms, indices, false);
                    physics.StepSimulation(1f / 120f);
                }

                indices[0] = 0;
                physics.GetRigidBodyMotionTransforms(1, indices, ref result);
                Assert.Greater(result[0].c3.x, .02f, "The tracked-hand sphere did not push the dynamic PMX body.");
            }
            finally
            {
                physics.Dispose();
                if (result.IsCreated) result.Dispose();
                if (indices.IsCreated) indices.Dispose();
                if (transforms.IsCreated) transforms.Dispose();
                if (bodies.IsCreated) bodies.Dispose();
            }
        }

        [Test]
        public void ExternalSphereConfigurationIsSanitizedWithoutNativeRuntime()
        {
            var root = new GameObject("PhysicsManagerTest");
            try
            {
                var manager = root.AddComponent<MMDPhysicsManager>();
                manager.ConfigureExternalKinematicSpheres(new[] { 0f, .02f, 1f });

                Assert.AreEqual(3, manager.externalKinematicSphereCount);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ExistingNativePluginAcceptsAppendedExternalSpheres()
        {
            var root = new GameObject("NativeExternalSphereTest");
            try
            {
                var manager = root.AddComponent<MMDPhysicsManager>();
                manager.ConfigureExternalKinematicSpheres(new[] { .03f, .014f });
                var initialize = typeof(MMDPhysicsManager).GetMethod(
                    "Initialize",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.IsNotNull(initialize);
                Assert.DoesNotThrow(() => initialize.Invoke(manager, null));
                Assert.IsTrue(manager.SetExternalKinematicSpherePose(
                    0,
                    new Vector3(0f, 1f, 0f),
                    true));
                Assert.IsTrue(manager.SetExternalKinematicSpherePose(
                    1,
                    new Vector3(.1f, 1f, 0f),
                    true));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void OptionalRealPmxCanExposeEveryDynamicBodyToTrackedHands()
        {
            var pmxPath = System.Environment.GetEnvironmentVariable("BANXIA_TEST_PMX");
            if (string.IsNullOrWhiteSpace(pmxPath) || !File.Exists(pmxPath))
            {
                Assert.Ignore("BANXIA_TEST_PMX is not configured for this run.");
            }

            PMXModel model = null;
            var root = new GameObject("RealPmxHandCollisionGroups");
            try
            {
                using (var stream = File.OpenRead(pmxPath))
                {
                    model = PMXReader.Read(stream, true);
                }
                Assert.That(model.rigidBodies.Length, Is.GreaterThan(0));

                var bodies = new List<MMDRigidBody>(model.rigidBodies.Length);
                var dynamicCount = 0;
                for (var index = 0; index < model.rigidBodies.Length; index++)
                {
                    var source = model.rigidBodies[index];
                    var bodyObject = new GameObject("Body_" + index);
                    bodyObject.transform.SetParent(root.transform, false);
                    var body = bodyObject.AddComponent<MMDRigidBody>();
                    body.groupIndex = source.groupIndex;
                    body.collisionGroupMask = source.collisionGroupMask;
                    body.mode = source.mode;
                    bodies.Add(body);
                    if (source.mode != PMXRigidBody.Mode.Kinetic)
                    {
                        dynamicCount++;
                    }
                }
                Assert.That(dynamicCount, Is.GreaterThan(0));

                var group = MMDPhysicsManager.ResolveExternalKinematicCollisionGroup(
                    bodies,
                    out var mayExpandMasks);
                var bit = 1 << group;
                if (mayExpandMasks)
                {
                    Assert.That(
                        bodies,
                        Has.None.Matches<MMDRigidBody>(body => body.groupIndex == group),
                        "Only an unused PMX group may be opened for tracked hands.");
                }
                else
                {
                    Assert.That(
                        bodies,
                        Has.All.Matches<MMDRigidBody>(body =>
                            (((ushort)body.collisionGroupMask) & bit) != 0),
                        "A reused group must already be accepted by every real PMX body.");
                }

                foreach (var body in bodies)
                {
                    var effectiveMask = mayExpandMasks
                        ? ((ushort)body.collisionGroupMask) | bit
                        : (ushort)body.collisionGroupMask;
                    Assert.That(
                        effectiveMask & bit,
                        Is.Not.Zero,
                        "Every real PMX body must accept the external hand group after safe expansion.");
                }
            }
            finally
            {
                if (model != null) Object.DestroyImmediate(model);
                Object.DestroyImmediate(root);
            }
        }

        private static MMDRigidBody.RigidBodySimulationData CreateSphereBody(
            int index,
            PMXRigidBody.Mode mode,
            byte group,
            Vector3 position)
        {
            return new MMDRigidBody.RigidBodySimulationData
            {
                rigidBodyIndex = index,
                relatedBoneIndex = -1,
                groupIndex = group,
                collisionGroupMask = -1,
                shape = PMXRigidBody.Shape.Sphere,
                size = new float3(.05f, 0f, 0f),
                position = position,
                rotation = float3.zero,
                mass = mode == PMXRigidBody.Mode.Kinetic ? 0f : 1f,
                linearDamping = 0f,
                angularDamping = 0f,
                restitution = 0f,
                friction = .5f,
                mode = mode,
                initialTransform = float4x4.identity,
                hasRelatedBone = false,
                boneLocalTransform = float4x4.identity,
                boneModelPosition = float3.zero,
                initialWorldTransform = float4x4.Translate(position),
                boneTransformLevel = -1,
            };
        }

        [Test]
        public void HandPhysicsProbeRejectsOutOfRangeIndex()
        {
            var root = new GameObject("HandProbeTest");
            try
            {
                var hands = root.AddComponent<QuestTrackedHandVisualizer>();
                Assert.IsFalse(hands.TryGetPhysicsProbe(-1, out _, out _, out _));
                Assert.IsFalse(hands.TryGetPhysicsProbe(
                    QuestTrackedHandVisualizer.PhysicsProbeCount,
                    out _,
                    out _,
                    out _));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PerformanceQaCanDisableContactWithoutHidingTrackedHands()
        {
            var root = new GameObject("HandContactQaToggleTest");
            try
            {
                var hands = root.AddComponent<QuestTrackedHandVisualizer>();
                var adapter = root.AddComponent<AvatarMmdPhysicsAdapter>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var setEvaluation = typeof(QuestTrackedHandVisualizer).GetMethod(
                    "SetContactEvaluationEnabledForQa",
                    flags);
                var evaluationEnabled = typeof(QuestTrackedHandVisualizer).GetProperty(
                    "ContactEvaluationEnabled",
                    flags);
                var setRuntimeContact = typeof(AvatarMmdPhysicsAdapter).GetMethod(
                    "SetRuntimeContactEnabledForQa",
                    flags);
                var runtimeContactEnabled = typeof(AvatarMmdPhysicsAdapter).GetProperty(
                    "RuntimeContactEnabled",
                    flags);
                Assert.That(setEvaluation, Is.Not.Null);
                Assert.That(evaluationEnabled, Is.Not.Null);
                Assert.That(setRuntimeContact, Is.Not.Null);
                Assert.That(runtimeContactEnabled, Is.Not.Null);

                setEvaluation.Invoke(hands, new object[] { false });
                setRuntimeContact.Invoke(adapter, new object[] { false });

                Assert.That(hands.HandsVisible, Is.True);
                Assert.That(evaluationEnabled.GetValue(hands), Is.False);
                Assert.That(runtimeContactEnabled.GetValue(adapter), Is.False);
                Assert.That(adapter.ActiveProbeCount, Is.Zero);

                setEvaluation.Invoke(hands, new object[] { true });
                setRuntimeContact.Invoke(adapter, new object[] { true });
                Assert.That(evaluationEnabled.GetValue(hands), Is.True);
                Assert.That(runtimeContactEnabled.GetValue(adapter), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
#endif
