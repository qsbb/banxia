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