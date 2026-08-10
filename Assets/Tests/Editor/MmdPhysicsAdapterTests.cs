#if UNITY_EDITOR
using NUnit.Framework;
using System.Reflection;
using UMT;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class MmdPhysicsAdapterTests
    {
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
    }
}
#endif
