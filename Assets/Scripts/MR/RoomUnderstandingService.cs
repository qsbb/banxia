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

    public enum RoomPlacementSurfaceKind
    {
        Floor,
        Seat
    }

    [Serializable]
    public struct RoomPlacementCandidate
    {
        public string SurfaceId;
        public RoomPlacementSurfaceKind Kind;
        public Pose SurfacePose;
        public Pose SuggestedPose;
        public Vector2 Size;
        public bool SupportsSitting;

        public RoomPlacementCandidate(
            string surfaceId,
            RoomPlacementSurfaceKind kind,
            Pose surfacePose,
            Pose suggestedPose,
            Vector2 size,
            bool supportsSitting)
        {
            SurfaceId = surfaceId ?? string.Empty;
            Kind = kind;
            SurfacePose = surfacePose;
            SuggestedPose = suggestedPose;
            Size = size;
            SupportsSitting = supportsSitting;
        }
    }

    /// <summary>
    /// Privacy-bounded room context. It deliberately contains no plane IDs,
    /// poses, dimensions, meshes, images, or account/device identifiers.
    /// </summary>
    [Serializable]
    public struct RoomSemanticSnapshot
    {
        public int FloorCount;
        public int SeatCount;
        public int TableCount;
        public int WallCount;
        public int DoorCount;
        public int WindowCount;

        public string ToContextString()
        {
            return $"房间 地面:{FloorCount} 座位:{SeatCount} 桌子:{TableCount} 墙:{WallCount} 门:{DoorCount} 窗:{WindowCount}";
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
        [SerializeField, Range(.3f, 1.2f)] private float minimumSeatWidth = .45f;
        [SerializeField, Range(.25f, 1f)] private float minimumSeatDepth = .35f;

        private readonly List<RoomSurfaceObservation> surfaces = new List<RoomSurfaceObservation>();
        private readonly List<RoomPlacementCandidate> placementCandidates = new List<RoomPlacementCandidate>();
        private XROrigin xrOrigin;
        private ARPlaneManager planeManager;
        private float nextRefreshAt;

        public event Action SnapshotChanged;

        public IReadOnlyList<RoomSurfaceObservation> Surfaces => surfaces;
        public IReadOnlyList<RoomPlacementCandidate> PlacementCandidates => placementCandidates;
        public int FloorCount { get; private set; }
        public int SeatCount { get; private set; }
        public int TableCount { get; private set; }
        public int WallCount { get; private set; }
        public string Status { get; private set; } = "Room understanding is starting";
        public RoomSemanticSnapshot SemanticSnapshot => BuildSemanticSnapshot(surfaces);
        public string ContextSummary => SemanticSnapshot.ToContextString();
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
            placementCandidates.Clear();
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
                if (!IsUsableObservation(observation))
                {
                    continue;
                }
                surfaces.Add(observation);
                switch (observation.Classification)
                {
                    case PlaneClassification.Floor: FloorCount++; break;
                    case PlaneClassification.Seat: SeatCount++; break;
                    case PlaneClassification.Table: TableCount++; break;
                    case PlaneClassification.Wall: WallCount++; break;
                }
            }

            surfaces.Sort(CompareObservations);
            RebuildPlacementCandidates();

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

        public bool TryFindStableSurface(
            PlaneClassification classification,
            Pose viewer,
            string preferredSurfaceId,
            out RoomSurfaceObservation selected)
        {
            return TrySelectStableSurface(
                surfaces,
                classification,
                viewer,
                minimumSurfaceExtent,
                preferredSurfaceId,
                .35f,
                out selected);
        }

        public bool TryFindNearestSeat(Pose viewer, out RoomPlacementCandidate selected)
        {
            selected = default;
            if (!TrySelectNearestPlacementCandidate(
                placementCandidates,
                RoomPlacementSurfaceKind.Seat,
                viewer,
                out var nearest))
            {
                return false;
            }

            var surface = new RoomSurfaceObservation(
                nearest.SurfaceId,
                PlaneClassification.Seat,
                nearest.SurfacePose,
                nearest.Size);
            return TryCreatePlacementCandidate(
                surface,
                viewer,
                minimumSurfaceExtent,
                minimumSeatWidth,
                minimumSeatDepth,
                out selected);
        }

        public static bool TrySelectNearestPlacementCandidate(
            IReadOnlyList<RoomPlacementCandidate> candidates,
            RoomPlacementSurfaceKind kind,
            Pose viewer,
            out RoomPlacementCandidate selected)
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
                if (candidate.Kind != kind || !IsFinite(candidate.SurfacePose.position) ||
                    !IsFinite(candidate.SurfacePose.rotation) || !IsFinite(candidate.Size))
                {
                    continue;
                }
                var horizontal = Vector3.ProjectOnPlane(
                    candidate.SurfacePose.position - viewer.position,
                    Vector3.up);
                var score = horizontal.sqrMagnitude;
                if (forward.sqrMagnitude > .001f && horizontal.sqrMagnitude > .001f &&
                    Vector3.Dot(forward, horizontal.normalized) < 0f)
                {
                    score += 16f;
                }
                if (!found || score < bestScore - .0001f ||
                    (Mathf.Abs(score - bestScore) <= .0001f &&
                     string.CompareOrdinal(candidate.SurfaceId, selected.SurfaceId) < 0))
                {
                    selected = candidate;
                    bestScore = score;
                    found = true;
                }
            }
            return found;
        }

        public static bool TrySelectNearestSurface(
            IReadOnlyList<RoomSurfaceObservation> candidates,
            PlaneClassification classification,
            Pose viewer,
            float minimumExtent,
            out RoomSurfaceObservation selected)
        {
            return TrySelectStableSurface(
                candidates,
                classification,
                viewer,
                minimumExtent,
                string.Empty,
                0f,
                out selected);
        }

        public static bool TrySelectStableSurface(
            IReadOnlyList<RoomSurfaceObservation> candidates,
            PlaneClassification classification,
            Pose viewer,
            float minimumExtent,
            string preferredSurfaceId,
            float preferredSurfaceHysteresis,
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
                    !IsUsableObservation(candidate) ||
                    Mathf.Min(candidate.Size.x, candidate.Size.y) < Mathf.Max(.1f, minimumExtent))
                {
                    continue;
                }

                var delta = candidate.Pose.position - viewer.position;
                var horizontal = Vector3.ProjectOnPlane(delta, Vector3.up);
                var score = horizontal.sqrMagnitude - Mathf.Min(4f, candidate.Size.x * candidate.Size.y) * .025f;
                if (forward.sqrMagnitude > .001f && horizontal.sqrMagnitude > .001f &&
                    Vector3.Dot(forward, horizontal.normalized) < 0f)
                {
                    score += 16f;
                }
                if (!string.IsNullOrEmpty(preferredSurfaceId) &&
                    string.Equals(candidate.Id, preferredSurfaceId, StringComparison.Ordinal))
                {
                    var hysteresis = Mathf.Max(0f, preferredSurfaceHysteresis);
                    score -= hysteresis * hysteresis;
                }
                if (!found || score < bestScore - .0001f ||
                    (Mathf.Abs(score - bestScore) <= .0001f &&
                     string.CompareOrdinal(candidate.Id, selected.Id) < 0))
                {
                    selected = candidate;
                    bestScore = score;
                    found = true;
                }
            }
            return found;
        }

        public static bool TryCreatePlacementCandidate(
            RoomSurfaceObservation surface,
            Pose viewer,
            float minimumFloorExtent,
            float minimumSeatWidth,
            float minimumSeatDepth,
            out RoomPlacementCandidate candidate)
        {
            candidate = default;
            if (!IsUsableObservation(surface))
            {
                return false;
            }

            RoomPlacementSurfaceKind kind;
            var supportsSitting = false;
            switch (surface.Classification)
            {
                case PlaneClassification.Floor:
                    if (Mathf.Min(surface.Size.x, surface.Size.y) < Mathf.Max(.1f, minimumFloorExtent))
                    {
                        return false;
                    }
                    kind = RoomPlacementSurfaceKind.Floor;
                    break;
                case PlaneClassification.Seat:
                    var largest = Mathf.Max(surface.Size.x, surface.Size.y);
                    var smallest = Mathf.Min(surface.Size.x, surface.Size.y);
                    if (largest < Mathf.Max(.1f, minimumSeatWidth) ||
                        smallest < Mathf.Max(.1f, minimumSeatDepth))
                    {
                        return false;
                    }
                    kind = RoomPlacementSurfaceKind.Seat;
                    supportsSitting = true;
                    break;
                default:
                    return false;
            }

            var towardViewer = viewer.position - surface.Pose.position;
            towardViewer.y = 0f;
            var suggestedRotation = towardViewer.sqrMagnitude < .0001f
                ? Quaternion.Euler(0f, surface.Pose.rotation.eulerAngles.y, 0f)
                : Quaternion.LookRotation(towardViewer.normalized, Vector3.up);
            var suggestedPose = new Pose(surface.Pose.position, suggestedRotation);
            candidate = new RoomPlacementCandidate(
                surface.Id,
                kind,
                surface.Pose,
                suggestedPose,
                surface.Size,
                supportsSitting);
            return true;
        }

        public static string BuildSummary(IEnumerable<RoomSurfaceObservation> observations)
        {
            return BuildSemanticSnapshot(observations).ToContextString();
        }

        public static RoomSemanticSnapshot BuildSemanticSnapshot(IEnumerable<RoomSurfaceObservation> observations)
        {
            var snapshot = new RoomSemanticSnapshot();
            if (observations != null)
            {
                foreach (var observation in observations)
                {
                    switch (observation.Classification)
                    {
                        case PlaneClassification.Floor: snapshot.FloorCount++; break;
                        case PlaneClassification.Seat: snapshot.SeatCount++; break;
                        case PlaneClassification.Table: snapshot.TableCount++; break;
                        case PlaneClassification.Wall: snapshot.WallCount++; break;
                        case PlaneClassification.Door: snapshot.DoorCount++; break;
                        case PlaneClassification.Window: snapshot.WindowCount++; break;
                    }
                }
            }
            return snapshot;
        }

        public static bool IsUsableObservation(RoomSurfaceObservation observation)
        {
            return IsFinite(observation.Pose.position) &&
                IsFinite(observation.Pose.rotation) &&
                IsFinite(observation.Size) &&
                observation.Size.x > 0f && observation.Size.y > 0f;
        }

        private void RebuildPlacementCandidates()
        {
            var viewer = xrOrigin != null && xrOrigin.Camera != null
                ? new Pose(xrOrigin.Camera.transform.position, xrOrigin.Camera.transform.rotation)
                : new Pose(Vector3.zero, Quaternion.identity);
            for (var index = 0; index < surfaces.Count; index++)
            {
                if (TryCreatePlacementCandidate(
                    surfaces[index],
                    viewer,
                    minimumSurfaceExtent,
                    minimumSeatWidth,
                    minimumSeatDepth,
                    out var candidate))
                {
                    placementCandidates.Add(candidate);
                }
            }
        }

        private static int CompareObservations(RoomSurfaceObservation left, RoomSurfaceObservation right)
        {
            var classification = ((int)left.Classification).CompareTo((int)right.Classification);
            return classification != 0 ? classification : string.CompareOrdinal(left.Id, right.Id);
        }

        private static bool IsFinite(Vector2 value)
        {
            return IsFinite(value.x) && IsFinite(value.y);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w) &&
                value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w > .0001f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
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
