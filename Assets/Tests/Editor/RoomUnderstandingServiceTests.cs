#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;

namespace QuestMmdPlayer.Tests
{
    public sealed class RoomUnderstandingServiceTests
    {
        [Test]
        public void SummaryContainsOnlySemanticCounts()
        {
            var observations = new[]
            {
                Surface("floor", PlaneClassification.Floor, 0f, 2f, 2f),
                Surface("seat", PlaneClassification.Seat, 1f, 1f, .6f),
                Surface("table", PlaneClassification.Table, 2f, 1f, 1f),
                Surface("wall", PlaneClassification.Wall, 3f, 2f, 2f),
            };

            Assert.That(
                RoomUnderstandingService.BuildSummary(observations),
                Is.EqualTo("房间 地面:1 座位:1 桌子:1 墙:1 门:0 窗:0"));
        }

        [Test]
        public void NearestSurfacePrefersVisibleUsableSeat()
        {
            var observations = new List<RoomSurfaceObservation>
            {
                Surface("behind", PlaneClassification.Seat, -1f, 1f, 1f),
                Surface("tiny", PlaneClassification.Seat, .5f, .1f, .1f),
                Surface("front", PlaneClassification.Seat, 2f, 1f, .6f),
            };
            var viewer = new Pose(Vector3.zero, Quaternion.identity);

            Assert.That(RoomUnderstandingService.TrySelectNearestSurface(
                observations,
                PlaneClassification.Seat,
                viewer,
                .35f,
                out var selected), Is.True);
            Assert.That(selected.Id, Is.EqualTo("front"));
        }

        [Test]
        public void StableSelectionKeepsPreferredSurfaceWithinHysteresis()
        {
            var observations = new List<RoomSurfaceObservation>
            {
                Surface("previous", PlaneClassification.Seat, 1.05f, .8f, .6f),
                Surface("slightly-nearer", PlaneClassification.Seat, 1f, .8f, .6f),
            };

            Assert.That(RoomUnderstandingService.TrySelectStableSurface(
                observations,
                PlaneClassification.Seat,
                new Pose(Vector3.zero, Quaternion.identity),
                .35f,
                "previous",
                .35f,
                out var selected), Is.True);
            Assert.That(selected.Id, Is.EqualTo("previous"));
        }

        [Test]
        public void SeatCandidateFacesViewerAndExposesSittingCapability()
        {
            var seat = new RoomSurfaceObservation(
                "seat-label-only-local-id",
                PlaneClassification.Seat,
                new Pose(new Vector3(0f, .45f, 1.5f), Quaternion.identity),
                new Vector2(.9f, .55f));

            Assert.That(RoomUnderstandingService.TryCreatePlacementCandidate(
                seat,
                new Pose(Vector3.zero, Quaternion.identity),
                .35f,
                .45f,
                .35f,
                out var candidate), Is.True);
            Assert.That(candidate.Kind, Is.EqualTo(RoomPlacementSurfaceKind.Seat));
            Assert.That(candidate.SupportsSitting, Is.True);
            Assert.That(candidate.SuggestedPose.position.y, Is.EqualTo(.45f).Within(.0001f));
            Assert.That(
                Vector3.Angle(candidate.SuggestedPose.rotation * Vector3.forward, Vector3.back),
                Is.LessThan(.001f));
        }

        [Test]
        public void UndersizedSeatIsNotExposedAsPlacementCandidate()
        {
            var seat = Surface("small", PlaneClassification.Seat, 1f, .3f, .3f);

            Assert.That(RoomUnderstandingService.TryCreatePlacementCandidate(
                seat,
                new Pose(Vector3.zero, Quaternion.identity),
                .35f,
                .45f,
                .35f,
                out _), Is.False);
        }

        [Test]
        public void SeatCandidateValidationDoesNotStopAtFirstUndersizedSurface()
        {
            var viewer = new Pose(Vector3.zero, Quaternion.identity);
            var validFarther = Surface("usable", PlaneClassification.Seat, 1.2f, .8f, .55f);
            Assert.That(RoomUnderstandingService.TryCreatePlacementCandidate(
                validFarther, viewer, .35f, .45f, .35f, out var validCandidate), Is.True);
            var candidates = new List<RoomPlacementCandidate> { validCandidate };

            Assert.That(RoomUnderstandingService.TrySelectNearestPlacementCandidate(
                candidates,
                RoomPlacementSurfaceKind.Seat,
                viewer,
                out var selected), Is.True);
            Assert.That(selected.SurfaceId, Is.EqualTo("usable"));
        }

        [Test]
        public void SemanticSnapshotNeverSerializesSurfaceIdentifiersOrGeometry()
        {
            var observations = new[]
            {
                new RoomSurfaceObservation(
                    "private-plane-id",
                    PlaneClassification.Floor,
                    new Pose(new Vector3(12f, 34f, 56f), Quaternion.Euler(1f, 2f, 3f)),
                    new Vector2(7f, 8f))
            };

            var json = JsonUtility.ToJson(RoomUnderstandingService.BuildSemanticSnapshot(observations));

            Assert.That(json, Does.Contain("FloorCount"));
            Assert.That(json, Does.Not.Contain("private-plane-id"));
            Assert.That(json, Does.Not.Contain("Surface"));
            Assert.That(json, Does.Not.Contain("Position"));
            Assert.That(json, Does.Not.Contain("Size"));
        }

        [Test]
        public void InvalidObservationIsRejectedBeforeCandidateSelection()
        {
            var invalid = new RoomSurfaceObservation(
                "bad",
                PlaneClassification.Floor,
                new Pose(new Vector3(float.NaN, 0f, 0f), Quaternion.identity),
                Vector2.one);

            Assert.That(RoomUnderstandingService.IsUsableObservation(invalid), Is.False);
            Assert.That(RoomUnderstandingService.TrySelectNearestSurface(
                new[] { invalid },
                PlaneClassification.Floor,
                new Pose(Vector3.zero, Quaternion.identity),
                .35f,
                out _), Is.False);
        }

        private static RoomSurfaceObservation Surface(
            string id,
            PlaneClassification classification,
            float z,
            float width,
            float height)
        {
            return new RoomSurfaceObservation(
                id,
                classification,
                new Pose(new Vector3(0f, 0f, z), Quaternion.identity),
                new Vector2(width, height));
        }
    }
}
#endif
