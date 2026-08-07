using NUnit.Framework;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;

namespace QuestMmdPlayer.Tests
{
    public sealed class AvatarPlacementServiceTests
    {
        [Test]
        public void LocomotionReusesExistingXrOrigin()
        {
            var originObject = new GameObject("XR Origin");
            var cameraObject = new GameObject("Main Camera");
            try
            {
                var origin = originObject.AddComponent<XROrigin>();
                cameraObject.transform.SetParent(originObject.transform, false);
                var camera = cameraObject.AddComponent<Camera>();
                origin.Camera = camera;

                Assert.That(QuestVrLocomotion.FindExistingRig(camera), Is.SameAs(origin.transform));
            }
            finally
            {
                Object.DestroyImmediate(originObject);
            }
        }

        [Test]
        public void HorizontalPoseFilterRejectsWall()
        {
            var floor = new Pose(Vector3.zero, Quaternion.identity);
            var wall = new Pose(
                Vector3.zero,
                Quaternion.FromToRotation(Vector3.up, Vector3.forward));

            Assert.That(AvatarPlacementService.IsHorizontalUpPose(floor), Is.True);
            Assert.That(AvatarPlacementService.IsHorizontalUpPose(wall), Is.False);
        }

        [Test]
        public void HeadDirectedRayIntersectsRequestedFloorPoint()
        {
            var head = new Pose(new Vector3(0f, 1.6f, 0f), Quaternion.identity);
            var ray = AvatarPlacementService.CreateHeadDirectedFloorRay(head, 0f, 2.2f);
            var floor = new Plane(Vector3.up, Vector3.zero);

            Assert.That(floor.Raycast(ray, out var distance), Is.True);
            var hit = ray.GetPoint(distance);
            Assert.That(hit.x, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(hit.y, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(hit.z, Is.EqualTo(2.2f).Within(0.0001f));
        }

        [Test]
        public void FallbackUsesTrackingFloorAndConfiguredDistance()
        {
            var head = new Pose(new Vector3(1f, 1.7f, -2f), Quaternion.identity);
            var pose = AvatarPlacementService.CreateFallbackPose(head, 0.25f, 2.2f);

            Assert.That(pose.position.x, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(pose.position.y, Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(pose.position.z, Is.EqualTo(0.2f).Within(0.0001f));
        }

        [Test]
        public void ImportedMmdNegativeZFacesUser()
        {
            var rotation = AvatarPlacementService.ComputeFacingRotation(
                new Vector3(0f, 0f, 2.2f),
                Vector3.zero,
                true);

            Assert.That(Quaternion.Angle(rotation, Quaternion.identity), Is.LessThan(0.001f));
        }

        [Test]
        public void PositiveZImportedModelFacesUser()
        {
            var rotation = AvatarPlacementService.ComputeFacingRotation(
                new Vector3(0f, 0f, 2.2f),
                Vector3.zero,
                false);

            Assert.That(Vector3.Angle(rotation * Vector3.forward, Vector3.back), Is.LessThan(.001f));
        }

        [Test]
        public void HeightEstimateUsesFloorAndClampsUnsafeValues()
        {
            Assert.That(AvatarPlacementService.EstimateUserHeight(1.72f, .1f), Is.EqualTo(1.73f).Within(.0001f));
            Assert.That(AvatarPlacementService.EstimateUserHeight(.2f, 0f), Is.EqualTo(.9f).Within(.0001f));
            Assert.That(AvatarPlacementService.EstimateUserHeight(3f, 0f), Is.EqualTo(2.3f).Within(.0001f));
        }

        [Test]
        public void FloorCandidateMustBeMeaningfullyBelowTheEyes()
        {
            Assert.That(AvatarPlacementService.IsPlausibleFloorCandidate(1.65f, 0f), Is.True);
            Assert.That(AvatarPlacementService.IsPlausibleFloorCandidate(1.65f, 1.2f), Is.False);
            Assert.That(AvatarPlacementService.IsPlausibleFloorCandidate(1.65f, -1f), Is.False);
        }

        [Test]
        public void AvatarRemainsWorldStableWhenTrackedHeadMoves()
        {
            var originObject = new GameObject("XR Origin");
            var cameraObject = new GameObject("Main Camera");
            var avatarObject = new GameObject("Avatar");
            try
            {
                var origin = originObject.AddComponent<XROrigin>();
                cameraObject.transform.SetParent(originObject.transform, false);
                var camera = cameraObject.AddComponent<Camera>();
                origin.Camera = camera;
                var avatar = avatarObject.AddComponent<AvatarController>();
                var placement = new Pose(new Vector3(0f, 0f, 2.2f), Quaternion.identity);
                avatar.SetPlacementPose(placement);

                cameraObject.transform.localPosition = new Vector3(0.8f, 1.65f, -0.4f);
                cameraObject.transform.localRotation = Quaternion.Euler(12f, 35f, 4f);

                Assert.That(Vector3.Distance(avatar.transform.position, placement.position), Is.LessThan(0.0001f));
                Assert.That(Quaternion.Angle(avatar.transform.rotation, placement.rotation), Is.LessThan(0.001f));
                Assert.That(avatar.transform.IsChildOf(origin.transform), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(originObject);
                Object.DestroyImmediate(avatarObject);
            }
        }

        [Test]
        public void HeightResetImmediatelyUsesCurrentTrackingFloor()
        {
            var originObject = new GameObject("Height Reset XR Origin");
            var cameraObject = new GameObject("Main Camera");
            var avatarObject = new GameObject("Avatar");
            var serviceObject = new GameObject("Placement Service");
            try
            {
                originObject.transform.position = new Vector3(0f, .15f, 0f);
                var origin = originObject.AddComponent<XROrigin>();
                cameraObject.tag = "MainCamera";
                cameraObject.transform.SetParent(originObject.transform, false);
                cameraObject.transform.localPosition = new Vector3(0f, 1.55f, 0f);
                var camera = cameraObject.AddComponent<Camera>();
                origin.Camera = camera;

                var avatar = avatarObject.AddComponent<AvatarController>();
                var service = serviceObject.AddComponent<AvatarPlacementService>();
                service.Bind(avatar);
                service.ResetHeightAndPlace();

                Assert.That(service.HasHeightCalibration, Is.True);
                Assert.That(service.HasCalibratedFloor, Is.True);
                Assert.That(service.HasPlacement, Is.True);
                Assert.That(service.EstimatedUserHeight, Is.EqualTo(1.66f).Within(.001f));
                Assert.That(avatar.transform.position.y, Is.EqualTo(.15f).Within(.001f));
                Assert.That(avatar.transform.position.z, Is.GreaterThan(1.2f));
            }
            finally
            {
                Object.DestroyImmediate(serviceObject);
                Object.DestroyImmediate(avatarObject);
                Object.DestroyImmediate(originObject);
            }
        }
        [Test]
        public void ResetReturnsToLatestPlacementPose()
        {
            var avatarObject = new GameObject("Avatar");
            try
            {
                var avatar = avatarObject.AddComponent<AvatarController>();
                var placement = new Pose(new Vector3(1f, 0.25f, 2f), Quaternion.Euler(0f, 35f, 0f));
                avatar.SetPlacementPose(placement);
                avatar.Move(new Vector3(2f, 0f, -1f));
                avatar.Rotate(40f);

                avatar.ResetTransform();

                Assert.That(Vector3.Distance(avatar.transform.position, placement.position), Is.LessThan(0.0001f));
                Assert.That(Quaternion.Angle(avatar.transform.rotation, placement.rotation), Is.LessThan(0.001f));
            }
            finally
            {
                Object.DestroyImmediate(avatarObject);
            }
        }

        [Test]
        public void SavedSurfaceRestorePreservesAvatarOffsetWhenPlaneRefines()
        {
            var originalSurface = Surface(
                "stable-floor",
                PlaneClassification.Floor,
                new Vector3(0f, 0f, 1f),
                new Vector2(3f, 2f));
            var avatarPose = new Pose(new Vector3(.25f, 0f, 1.5f), Quaternion.Euler(0f, 180f, 0f));
            var origin = new Pose(Vector3.zero, Quaternion.identity);
            var bookmark = AvatarPlacementService.CreatePlacementBookmark(originalSurface, avatarPose, origin);
            var refinedSurface = Surface(
                "stable-floor",
                PlaneClassification.Floor,
                new Vector3(.1f, .02f, 1.05f),
                new Vector2(3.05f, 2f));

            Assert.That(AvatarPlacementService.TryResolvePlacementBookmark(
                bookmark,
                new[] { refinedSurface },
                origin,
                .75f,
                out var restored,
                out var matched), Is.True);
            Assert.That(matched.Id, Is.EqualTo("stable-floor"));
            Assert.That(
                Vector3.Distance(restored.position, new Vector3(.35f, .02f, 1.55f)),
                Is.LessThan(.0001f));
            Assert.That(Quaternion.Angle(restored.rotation, avatarPose.rotation), Is.LessThan(.001f));
        }

        [Test]
        public void SavedSurfaceCanReacquireNearbyRenamedPlaneByLocalSignature()
        {
            var originalSurface = Surface(
                "old-id",
                PlaneClassification.Floor,
                new Vector3(1f, 0f, 2f),
                new Vector2(3f, 2f));
            var avatarPose = new Pose(new Vector3(1.4f, 0f, 2.2f), Quaternion.identity);
            var origin = new Pose(new Vector3(.5f, 0f, .5f), Quaternion.identity);
            var bookmark = AvatarPlacementService.CreatePlacementBookmark(originalSurface, avatarPose, origin);
            var renamedSurface = Surface(
                "new-id",
                PlaneClassification.Floor,
                new Vector3(1.08f, 0f, 2.04f),
                new Vector2(3.04f, 2.02f));

            Assert.That(AvatarPlacementService.TryResolvePlacementBookmark(
                bookmark,
                new[] { renamedSurface },
                origin,
                .75f,
                out _,
                out var matched), Is.True);
            Assert.That(matched.Id, Is.EqualTo("new-id"));
        }

        [Test]
        public void SavedSurfaceDoesNotJumpToUnrelatedRoomPlane()
        {
            var originalSurface = Surface(
                "old-id",
                PlaneClassification.Floor,
                Vector3.zero,
                new Vector2(3f, 2f));
            var bookmark = AvatarPlacementService.CreatePlacementBookmark(
                originalSurface,
                new Pose(new Vector3(0f, 0f, 1f), Quaternion.identity),
                new Pose(Vector3.zero, Quaternion.identity));
            var unrelated = new List<RoomSurfaceObservation>
            {
                Surface("far-floor", PlaneClassification.Floor, new Vector3(4f, 0f, 4f), new Vector2(3f, 2f)),
                Surface("near-seat", PlaneClassification.Seat, new Vector3(0f, .45f, 0f), new Vector2(1f, .6f))
            };

            Assert.That(AvatarPlacementService.TryResolvePlacementBookmark(
                bookmark,
                unrelated,
                new Pose(Vector3.zero, Quaternion.identity),
                .75f,
                out _,
                out _), Is.False);
        }

        [Test]
        public void CorruptSavedPlacementFailsClosed()
        {
            var bookmark = new AvatarPlacementBookmark
            {
                Version = 1,
                SurfaceId = "floor",
                SurfaceClassification = (int)PlaneClassification.Floor,
                SurfacePositionInOrigin = new Vector3(float.NaN, 0f, 0f),
                SurfaceRotationInOrigin = Quaternion.identity,
                SurfaceSize = Vector2.one,
                AvatarPositionRelativeToSurface = Vector3.zero,
                AvatarRotationRelativeToSurface = Quaternion.identity
            };

            Assert.That(AvatarPlacementService.IsValidPlacementBookmark(bookmark), Is.False);
            Assert.That(AvatarPlacementService.TryResolvePlacementBookmark(
                bookmark,
                new[] { Surface("floor", PlaneClassification.Floor, Vector3.zero, Vector2.one) },
                new Pose(Vector3.zero, Quaternion.identity),
                .75f,
                out _,
                out _), Is.False);
        }

        private static RoomSurfaceObservation Surface(
            string id,
            PlaneClassification classification,
            Vector3 position,
            Vector2 size)
        {
            return new RoomSurfaceObservation(
                id,
                classification,
                new Pose(position, Quaternion.identity),
                size);
        }
    }
}
