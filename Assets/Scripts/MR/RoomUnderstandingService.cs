using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR.Features.Meta;

namespace QuestMmdPlayer
{
    [Serializable]
    public struct RoomSurfaceObservation
    {
        public string Id;
        public PlaneClassification Classification;
        public Pose Pose;
        public Vector2 Size;

        public RoomSurfaceObservation(string id, PlaneClassification classification, Pose pose, Vector2 size)
        {
            Id = id ?? string.Empty;
            Classification = classification;
            Pose = pose;
            Size = size;
        }
    }

    /// <summary>
    /// Reads Meta Quest Space Setup planes through AR Foundation. The resulting
    /// snapshot contains semantic labels and poses only; no camera pixels leave
    /// the headset.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)]
    public sealed class RoomUnderstandingService : MonoBehaviour
    {
        [SerializeField, Range(.25f, 5f)] private float refreshIntervalSeconds = 1f;
        [SerializeField, Range(.1f, 1f)] private float minimumSurfaceExtent = .35f;

        private readonly List<RoomSurfaceObservation> surfaces = new List<RoomSurfaceObservation>();
        private XROrigin xrOrigin;
        private ARPlaneManager planeManager;
        private float nextRefreshAt;

        public event Action SnapshotChanged;

        public IReadOnlyList<RoomSurfaceObservation> Surfaces => surfaces;
        public int FloorCount { get; private set; }
        public int SeatCount { get; private set; }
        public int TableCount { get; private set; }
        public int WallCount { get; private set; }
        public string Status { get; private set; } = "Room understanding is starting";
        public string ContextSummary => BuildSummary(surfaces);
        public bool HasRoomData => surfaces.Count > 0;

        private void Awake()
        {
            ResolveDependencies();
        }

        private void OnEnable()
        {
            nextRefreshAt = 0f;
        }

        private void Update()
        {
            if (Time.unscaledTime < nextRefreshAt)
            {
                return;
            }
            nextRefreshAt = Time.unscaledTime + Mathf.Max(.25f, refreshIntervalSeconds);
            RefreshNow();
        }

        public void RefreshNow()
        {
            ResolveDependencies();
            surfaces.Clear();
            FloorCount = SeatCount = TableCount = WallCount = 0;
            if (planeManager == null || !planeManager.isActiveAndEnabled)
            {
                Status = "Room planes unavailable; run Quest room setup";
                SnapshotChanged?.Invoke();
                return;
            }

            foreach (var plane in planeManager.trackables)
            {
                if (plane == null || plane.trackingState == TrackingState.None)
                {
                    continue;
                }
                var center = plane.transform.TransformPoint(new Vector3(plane.center.x, 0f, plane.center.y));
                var observation = new RoomSurfaceObservation(
                    plane.trackableId.ToString(),
                    plane.classification,
                    new Pose(center, plane.transform.rotation),
                    plane.size);
                surfaces.Add(observation);
                switch (observation.Classification)
                {
                    case PlaneClassification.Floor: FloorCount++; break;
                    case PlaneClassification.Seat: SeatCount++; break;
                    case PlaneClassification.Table: TableCount++; break;
                    case PlaneClassification.Wall: WallCount++; break;
                }
            }

            Status = surfaces.Count == 0
                ? "No room surfaces found; run Quest room setup"
                : ContextSummary;
            SnapshotChanged?.Invoke();
        }

        public bool RequestSceneCapture()
        {
            var manager = XRGeneralSettings.Instance == null ? null : XRGeneralSettings.Instance.Manager;
            var loader = manager == null ? null : manager.activeLoader;
            var subsystem = loader == null ? null : loader.GetLoadedSubsystem<XRSessionSubsystem>();
            if (!(subsystem is MetaOpenXRSessionSubsystem metaSession))
            {
                Status = "Meta scene capture is unavailable";
                return false;
            }

            var requested = metaSession.TryRequestSceneCapture();
            Status = requested
                ? "Quest room setup opened"
                : "Quest room setup could not be opened";
            return requested;
        }

        public bool TryFindNearestSurface(
            PlaneClassification classification,
            Pose viewer,
            out RoomSurfaceObservation selected)
        {
            return TrySelectNearestSurface(
                surfaces,
                classification,
                viewer,
                minimumSurfaceExtent,
                out selected);
        }

        public static bool TrySelectNearestSurface(
            IReadOnlyList<RoomSurfaceObservation> candidates,
            PlaneClassification classification,
            Pose viewer,
            float minimumExtent,
            out RoomSurfaceObservation selected)
        {
            selected = default;
            if (candidates == null)
            {
                return false;
            }

            var forward = Vector3.ProjectOnPlane(viewer.rotation * Vector3.forward, Vector3.up).normalized;
            var found = false;
            var bestScore = float.PositiveInfinity;
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                if (candidate.Classification != classification ||
                    Mathf.Min(candidate.Size.x, candidate.Size.y) < Mathf.Max(.1f, minimumExtent))
                {
                    continue;
                }

                var delta = candidate.Pose.position - viewer.position;
                var horizontal = Vector3.ProjectOnPlane(delta, Vector3.up);
                var score = horizontal.sqrMagnitude;
                if (forward.sqrMagnitude > .001f && Vector3.Dot(forward, horizontal.normalized) < 0f)
                {
                    score += 16f;
                }
                if (!found || score < bestScore)
                {
                    selected = candidate;
                    bestScore = score;
                    found = true;
                }
            }
            return found;
        }

        public static string BuildSummary(IEnumerable<RoomSurfaceObservation> observations)
        {
            var floors = 0;
            var seats = 0;
            var tables = 0;
            var walls = 0;
            var doors = 0;
            var windows = 0;
            if (observations != null)
            {
                foreach (var observation in observations)
                {
                    switch (observation.Classification)
                    {
                        case PlaneClassification.Floor: floors++; break;
                        case PlaneClassification.Seat: seats++; break;
                        case PlaneClassification.Table: tables++; break;
                        case PlaneClassification.Wall: walls++; break;
                        case PlaneClassification.Door: doors++; break;
                        case PlaneClassification.Window: windows++; break;
                    }
                }
            }
            return $"房间 地面:{floors} 座位:{seats} 桌子:{tables} 墙:{walls} 门:{doors} 窗:{windows}";
        }

        private void ResolveDependencies()
        {
            xrOrigin = xrOrigin != null ? xrOrigin : FindObjectOfType<XROrigin>();
            if (xrOrigin == null)
            {
                return;
            }
            planeManager = planeManager != null
                ? planeManager
                : xrOrigin.GetComponent<ARPlaneManager>() ?? xrOrigin.gameObject.AddComponent<ARPlaneManager>();
            planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;
        }
    }
}