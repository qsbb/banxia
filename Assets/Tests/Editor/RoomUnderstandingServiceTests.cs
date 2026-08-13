#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;

namespace QuestMmdPlayer.Tests
{
    public sealed class RoomUnderstandingServiceTests
    {
        private sealed class FakeMrukAnchor : MonoBehaviour
        {
            public string Label { get; set; }
            public Rect? PlaneRect { get; set; }
            public Bounds? VolumeBounds { get; set; }
        }

        [Test]
        public void ExplicitRoomTrackingStopsAfterSnapshotOrDeadline()
        {
            Assert.That(RoomUnderstandingService.ShouldStopExplicitTracking(10f, 30f, 12.9f, true), Is.False);
            Assert.That(RoomUnderstandingService.ShouldStopExplicitTracking(10f, 30f, 13f, true), Is.True);
            Assert.That(RoomUnderstandingService.ShouldStopExplicitTracking(10f, 30f, 29.9f, false), Is.False);
            Assert.That(RoomUnderstandingService.ShouldStopExplicitTracking(10f, 30f, 30f, false), Is.True);
            Assert.That(RoomUnderstandingService.ShouldStopExplicitTracking(10f, 30f, 9f, true), Is.False);
        }

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
                Is.EqualTo("房间 地面:1 座位:1 床:0 桌子:1 墙:1 门:0 窗:0"));
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
        public void StablePlacementSelectionKeepsPreferredSeatWhenCandidatesJitter()
        {
            var candidates = new[]
            {
                new RoomPlacementCandidate(
                    "previous-seat",
                    RoomPlacementSurfaceKind.Seat,
                    new Pose(new Vector3(0f, .45f, 1.05f), Quaternion.identity),
                    new Pose(new Vector3(0f, .45f, 1.05f), Quaternion.identity),
                    new Vector2(.8f, .6f),
                    true),
                new RoomPlacementCandidate(
                    "slightly-nearer",
                    RoomPlacementSurfaceKind.Seat,
                    new Pose(new Vector3(0f, .45f, 1f), Quaternion.identity),
                    new Pose(new Vector3(0f, .45f, 1f), Quaternion.identity),
                    new Vector2(.8f, .6f),
                    true)
            };

            Assert.That(RoomUnderstandingService.TrySelectStablePlacementCandidate(
                candidates,
                RoomPlacementSurfaceKind.Seat,
                new Pose(Vector3.zero, Quaternion.identity),
                "previous-seat",
                .35f,
                out var selected), Is.True);
            Assert.That(selected.SurfaceId, Is.EqualTo("previous-seat"));
        }

        [Test]
        public void StableRestingSelectionKeepsPreferredSurfaceAcrossRefresh()
        {
            var candidates = new[]
            {
                new RoomPlacementCandidate(
                    "preferred-bed",
                    RoomPlacementSurfaceKind.Bed,
                    new Pose(new Vector3(0f, .5f, 1.05f), Quaternion.identity),
                    new Pose(new Vector3(0f, .5f, 1.05f), Quaternion.identity),
                    new Vector2(1.8f, .8f),
                    true,
                    true),
                new RoomPlacementCandidate(
                    "near-seat",
                    RoomPlacementSurfaceKind.Seat,
                    new Pose(new Vector3(0f, .45f, 1f), Quaternion.identity),
                    new Pose(new Vector3(0f, .45f, 1f), Quaternion.identity),
                    new Vector2(.8f, .6f),
                    true)
            };

            Assert.That(RoomUnderstandingService.TrySelectStableRestingSurface(
                candidates,
                new Pose(Vector3.zero, Quaternion.identity),
                "preferred-bed",
                .35f,
                out var selected), Is.True);
            Assert.That(selected.SurfaceId, Is.EqualTo("preferred-bed"));
        }

        [Test]
        public void OptionalMrukProbeDoesNotRequireMrukAssembly()
        {
            Assert.That(SpatialCapabilityAdapter.HasOptionalMruk(), Is.False);
        }

        [Test]
        public void CapabilitySnapshotHasExplicitUnavailableDefaults()
        {
            var snapshot = new SpatialCapabilitySnapshot();

            Assert.That(snapshot.Mruk, Is.EqualTo(SpatialCapabilityState.Unavailable));
            Assert.That(snapshot.PlaneTracking, Is.EqualTo(SpatialCapabilityState.Unavailable));
            Assert.That(snapshot.Occlusion, Is.EqualTo(SpatialCapabilityState.Unavailable));
            Assert.That(snapshot.VirtualCollision, Is.EqualTo(SpatialCapabilityState.Unavailable));
            Assert.That(snapshot.SceneCaptureAvailable, Is.False);
        }

        [TestCase("FLOOR", PlaneClassification.Floor, RoomPlacementSurfaceKind.Floor)]
        [TestCase("WALL_FACE", PlaneClassification.Wall, null)]
        [TestCase("TABLE", PlaneClassification.Table, RoomPlacementSurfaceKind.Table)]
        [TestCase("COUCH", PlaneClassification.Seat, RoomPlacementSurfaceKind.Couch)]
        [TestCase("BED", PlaneClassification.Seat, RoomPlacementSurfaceKind.Bed)]
        public void MrukSemanticLabelsMapWithoutImportingMruk(
            string label,
            PlaneClassification expectedClassification,
            RoomPlacementSurfaceKind? expectedKind)
        {
            Assert.That(SpatialCapabilityAdapter.TryMapMrukLabel(
                label,
                out var classification,
                out var semanticKind), Is.True);
            Assert.That(classification, Is.EqualTo(expectedClassification));
            Assert.That(semanticKind, Is.EqualTo(expectedKind));
        }

        [Test]
        public void MrukUnsupportedLabelsAreClosedByDefault()
        {
            Assert.That(SpatialCapabilityAdapter.TryMapMrukLabel(
                "GLOBAL_MESH",
                out _,
                out _), Is.False);
        }

        [Test]
        public void MrukBedSemanticSurvivesArFoundationSeatProjection()
        {
            var bed = new RoomSurfaceObservation(
                "mruk-bed",
                PlaneClassification.Seat,
                new Pose(new Vector3(0f, .5f, 1f), Quaternion.identity),
                new Vector2(2f, 1f),
                RoomPlacementSurfaceKind.Bed);

            Assert.That(RoomUnderstandingService.TryCreatePlacementCandidate(
                bed,
                new Pose(Vector3.zero, Quaternion.identity),
                .35f,
                .45f,
                .35f,
                out var candidate), Is.True);
            Assert.That(candidate.Kind, Is.EqualTo(RoomPlacementSurfaceKind.Bed));
            Assert.That(candidate.SupportsLying, Is.True);
        }

        [Test]
        public void MrukBedDoesNotMasqueradeAsSeatButCouchDoes()
        {
            var observations = new[]
            {
                new RoomSurfaceObservation(
                    "mruk-bed-runtime",
                    PlaneClassification.Seat,
                    new Pose(Vector3.zero, Quaternion.identity),
                    new Vector2(2f, 1f),
                    RoomPlacementSurfaceKind.Bed),
                new RoomSurfaceObservation(
                    "mruk-couch-runtime",
                    PlaneClassification.Seat,
                    new Pose(Vector3.forward, Quaternion.identity),
                    new Vector2(1.8f, .7f),
                    RoomPlacementSurfaceKind.Couch)
            };

            var snapshot = RoomUnderstandingService.BuildSemanticSnapshot(observations);

            Assert.That(snapshot.SeatCount, Is.EqualTo(1));
            Assert.That(snapshot.FloorCount, Is.Zero);
            Assert.That(snapshot.TableCount, Is.Zero);
            Assert.That(RoomUnderstandingService.CountsAsSeat(observations[0]), Is.False);
            Assert.That(RoomUnderstandingService.CountsAsSeat(observations[1]), Is.True);
        }

        [Test]
        public void MrukPlaneAnchorProjectsOnlySemanticPoseAndBounds()
        {
            var root = new GameObject("MRUK projection fixture");
            try
            {
                root.transform.SetPositionAndRotation(
                    new Vector3(1f, .45f, 2f),
                    Quaternion.Euler(0f, 25f, 0f));
                var anchor = root.AddComponent<FakeMrukAnchor>();
                anchor.Label = "COUCH";
                anchor.PlaneRect = new Rect(-.7f, -.25f, 1.8f, .7f);
                anchor.VolumeBounds = new Bounds(Vector3.zero, new Vector3(1.8f, .7f, .6f));

                Assert.That(SpatialCapabilityAdapter.TryProjectMrukAnchor(
                    anchor,
                    0,
                    out var observation), Is.True);
                Assert.That(observation.Id, Does.StartWith("mruk-couch-"));
                Assert.That(observation.Classification, Is.EqualTo(PlaneClassification.Seat));
                Assert.That(observation.SemanticKind, Is.EqualTo(RoomPlacementSurfaceKind.Couch));
                Assert.That(
                    Vector3.Distance(
                        observation.Pose.position,
                        root.transform.TransformPoint(new Vector3(.2f, .1f, 0f))),
                    Is.LessThan(.0001f));
                Assert.That(observation.Pose.rotation, Is.EqualTo(root.transform.rotation));
                Assert.That(observation.Size, Is.EqualTo(new Vector2(1.8f, .7f)));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void MrukProjectionRejectsGlobalMeshEvenWhenItHasBounds()
        {
            var root = new GameObject("MRUK global mesh fixture");
            try
            {
                var anchor = root.AddComponent<FakeMrukAnchor>();
                anchor.Label = "GLOBAL_MESH";
                anchor.VolumeBounds = new Bounds(Vector3.zero, Vector3.one);

                Assert.That(SpatialCapabilityAdapter.TryProjectMrukAnchor(
                    anchor,
                    0,
                    out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
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
            Assert.That(candidate.SupportsLying, Is.False);
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
        public void LargeSeatSurfaceAdvertisesLyingCapabilityWithoutInventingBedLabel()
        {
            var restingSurface = Surface("large-seat", PlaneClassification.Seat, 1.5f, 1.9f, .8f);

            Assert.That(RoomUnderstandingService.TryCreatePlacementCandidate(
                restingSurface,
                new Pose(Vector3.zero, Quaternion.identity),
                .35f,
                .45f,
                .35f,
                out var candidate), Is.True);
            Assert.That(candidate.Kind, Is.EqualTo(RoomPlacementSurfaceKind.Seat));
            Assert.That(candidate.SupportsSitting, Is.True);
            Assert.That(candidate.SupportsLying, Is.True);
        }

        [Test]
        public void RestingSurfaceSelectionPrefersNearbyUsableTarget()
        {
            var candidates = new List<RoomPlacementCandidate>
            {
                new RoomPlacementCandidate(
                    "far-bed",
                    RoomPlacementSurfaceKind.Bed,
                    new Pose(new Vector3(0f, .45f, 3f), Quaternion.identity),
                    new Pose(new Vector3(0f, .45f, 3f), Quaternion.identity),
                    new Vector2(2f, 1.2f),
                    true,
                    true),
                new RoomPlacementCandidate(
                    "near-seat",
                    RoomPlacementSurfaceKind.Seat,
                    new Pose(new Vector3(0f, .45f, 1.2f), Quaternion.identity),
                    new Pose(new Vector3(0f, .45f, 1.2f), Quaternion.identity),
                    new Vector2(.8f, .55f),
                    true),
                new RoomPlacementCandidate(
                    "table",
                    RoomPlacementSurfaceKind.Table,
                    new Pose(new Vector3(0f, .7f, .5f), Quaternion.identity),
                    new Pose(new Vector3(0f, .7f, .5f), Quaternion.identity),
                    Vector2.one,
                    false)
            };

            Assert.That(RoomUnderstandingService.TrySelectNearestRestingSurface(
                candidates,
                new Pose(Vector3.zero, Quaternion.identity),
                out var selected), Is.True);
            Assert.That(selected.SurfaceId, Is.EqualTo("near-seat"));
        }

        [Test]
        public void TableIsClassifiedButNeverAdvertisedAsSittingOrLying()
        {
            var table = Surface("table", PlaneClassification.Table, 1f, 1.1f, .8f);

            Assert.That(RoomUnderstandingService.TryCreatePlacementCandidate(
                table,
                new Pose(Vector3.zero, Quaternion.identity),
                .35f,
                .45f,
                .35f,
                out var candidate), Is.True);
            Assert.That(candidate.Kind, Is.EqualTo(RoomPlacementSurfaceKind.Table));
            Assert.That(candidate.SupportsSitting, Is.False);
            Assert.That(candidate.SupportsLying, Is.False);
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
        public void BedHasItsOwnBoundedSemanticCount()
        {
            var observations = new[]
            {
                new RoomSurfaceObservation(
                    "bed",
                    PlaneClassification.Seat,
                    new Pose(Vector3.zero, Quaternion.identity),
                    new Vector2(2f, 1.2f),
                    RoomPlacementSurfaceKind.Bed)
            };

            var snapshot = RoomUnderstandingService.BuildSemanticSnapshot(observations);

            Assert.That(snapshot.BedCount, Is.EqualTo(1));
            Assert.That(snapshot.SeatCount, Is.Zero);
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
