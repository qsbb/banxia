using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;

namespace QuestMmdPlayer
{
    [Serializable]
    public struct RoomSurfaceObservation
    {
        public string Id;
        public PlaneClassification Classification;
        public Pose Pose;
        public Vector2 Size;
        public RoomPlacementSurfaceKind? SemanticKind;

        public RoomSurfaceObservation(string id, PlaneClassification classification, Pose pose, Vector2 size)
            : this(id, classification, pose, size, null)
        {
        }

        public RoomSurfaceObservation(
            string id,
            PlaneClassification classification,
            Pose pose,
            Vector2 size,
            RoomPlacementSurfaceKind? semanticKind)
        {
            Id = id ?? string.Empty;
            Classification = classification;
            Pose = pose;
            Size = size;
            SemanticKind = semanticKind;
        }
    }

    public enum RoomPlacementSurfaceKind
    {
        Floor,
        Seat,
        Couch,
        Bed,
        Table
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
        public bool SupportsLying;

        public RoomPlacementCandidate(
            string surfaceId,
            RoomPlacementSurfaceKind kind,
            Pose surfacePose,
            Pose suggestedPose,
            Vector2 size,
            bool supportsSitting,
            bool supportsLying = false)
        {
            SurfaceId = surfaceId ?? string.Empty;
            Kind = kind;
            SurfacePose = surfacePose;
            SuggestedPose = suggestedPose;
            Size = size;
            SupportsSitting = supportsSitting;
            SupportsLying = supportsLying;
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
        public int BedCount;
        public int TableCount;
        public int WallCount;
        public int DoorCount;
        public int WindowCount;

        public string ToContextString()
        {
            return $"房间 地面:{FloorCount} 座位:{SeatCount} 床:{BedCount} 桌子:{TableCount} 墙:{WallCount} 门:{DoorCount} 窗:{WindowCount}";
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
        [SerializeField, Range(5f, 60f)] private float sceneCaptureTrackingSeconds = 20f;

        private readonly List<RoomSurfaceObservation> surfaces = new List<RoomSurfaceObservation>();
        private readonly List<RoomPlacementCandidate> placementCandidates = new List<RoomPlacementCandidate>();
        private XROrigin xrOrigin;
        private ARPlaneManager planeManager;
        private float nextRefreshAt;
        private float sceneCaptureTrackingStartedAt;
        private float sceneCaptureTrackingDeadline;
        private bool sceneCaptureTrackingRequested;
        private string preferredPlacementSurfaceId = string.Empty;
        private string lastCapabilitySignature = string.Empty;

        public event Action SnapshotChanged;

        public IReadOnlyList<RoomSurfaceObservation> Surfaces => surfaces;
        public IReadOnlyList<RoomPlacementCandidate> PlacementCandidates => placementCandidates;
        public int FloorCount { get; private set; }
        public int SeatCount { get; private set; }
        public int TableCount { get; private set; }
        public int WallCount { get; private set; }
        public string Status { get; private set; } = "Room understanding is starting";
        public SpatialCapabilitySnapshot Capabilities { get; private set; }
        public RoomSemanticSnapshot SemanticSnapshot => BuildSemanticSnapshot(surfaces);
        public string ContextSummary => SemanticSnapshot.ToContextString();
        public bool HasRoomData => surfaces.Count > 0;
        public bool IsSceneCaptureTrackingRequested => sceneCaptureTrackingRequested;

        private void Awake()
        {
            ResolveDependencies();
            RefreshCapabilities();
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
            if (sceneCaptureTrackingRequested && ShouldStopExplicitTracking(
                    sceneCaptureTrackingStartedAt,
                    sceneCaptureTrackingDeadline,
                    Time.unscaledTime,
                    surfaces.Count > 0))
            {
                sceneCaptureTrackingRequested = false;
                if (planeManager != null)
                {
                    planeManager.enabled = false;
                }
                Status = surfaces.Count == 0
                    ? "Room scan timed out; run Quest room setup"
                    : ContextSummary;
            }
        }

        public void RefreshNow()
        {
            ResolveDependencies();
            RefreshCapabilities();
            if (SpatialCapabilityAdapter.TryReadMrukSurfaces(surfaces, out var mrukStatus))
            {
                FinishRefresh("MRUK", mrukStatus);
                return;
            }
            if (planeManager == null || !planeManager.isActiveAndEnabled)
            {
                surfaces.Clear();
                placementCandidates.Clear();
                FloorCount = SeatCount = TableCount = WallCount = 0;
                Status = "Room tracking is idle; scan the room when needed";
                SnapshotChanged?.Invoke();
                return;
            }

            surfaces.Clear();

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
            }

            FinishRefresh("ARPlane", "plane_tracking");
        }

        public bool RequestSceneCapture()
        {
            ResolveDependencies();
            if (planeManager != null)
            {
                planeManager.enabled = true;
                planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;
            }
            var manager = XRGeneralSettings.Instance == null ? null : XRGeneralSettings.Instance.Manager;
            var loader = manager == null ? null : manager.activeLoader;
            var subsystem = loader == null ? null : loader.GetLoadedSubsystem<XRSessionSubsystem>();
            if (!SpatialCapabilityAdapter.TryRequestSceneCapture(subsystem, out var requested))
            {
                if (planeManager != null) planeManager.enabled = false;
                Status = "Meta scene capture is unavailable";
                Debug.Log("[RoomUnderstanding] scene_capture unavailable; using plane/fallback path.", this);
                return false;
            }
            sceneCaptureTrackingRequested = requested;
            sceneCaptureTrackingStartedAt = Time.unscaledTime;
            sceneCaptureTrackingDeadline = Time.unscaledTime + Mathf.Max(5f, sceneCaptureTrackingSeconds);
            if (!requested && planeManager != null)
            {
                planeManager.enabled = false;
            }
            Status = requested
                ? "Quest room setup opened"
                : "Quest room setup could not be opened";
            Debug.Log($"[RoomUnderstanding] scene_capture requested={requested}; {Capabilities}", this);
            return requested;
        }

        public void RefreshCapabilities()
        {
            var manager = XRGeneralSettings.Instance == null ? null : XRGeneralSettings.Instance.Manager;
            var loader = manager == null ? null : manager.activeLoader;
            var subsystem = loader == null ? null : loader.GetLoadedSubsystem<XRSessionSubsystem>();
            Capabilities = SpatialCapabilityAdapter.Detect(planeManager, xrOrigin, subsystem);
            var signature = Capabilities.ToString();
            if (!string.Equals(signature, lastCapabilitySignature, StringComparison.Ordinal))
            {
                lastCapabilitySignature = signature;
                Debug.Log($"[RoomUnderstanding] capability {signature}", this);
            }
        }

        public static bool ShouldStopExplicitTracking(
            float startedAt,
            float deadline,
            float now,
            bool hasSurfaces)
        {
            if (now < startedAt)
            {
                return false;
            }
            return now >= deadline || hasSurfaces && now - startedAt >= 3f;
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
            if (!TrySelectStablePlacementCandidate(
                placementCandidates,
                RoomPlacementSurfaceKind.Seat,
                viewer,
                preferredPlacementSurfaceId,
                .35f,
                out var nearest))
            {
                return false;
            }

            var surface = new RoomSurfaceObservation(
                nearest.SurfaceId,
                PlaneClassification.Seat,
                nearest.SurfacePose,
                nearest.Size);
            var created = TryCreatePlacementCandidate(
                surface,
                viewer,
                minimumSurfaceExtent,
                minimumSeatWidth,
                minimumSeatDepth,
                out selected);
            if (created) preferredPlacementSurfaceId = selected.SurfaceId;
            return created;
        }

        public bool TryFindNearestRestingSurface(Pose viewer, out RoomPlacementCandidate selected)
        {
            var found = TrySelectStableRestingSurface(
                placementCandidates,
                viewer,
                preferredPlacementSurfaceId,
                .35f,
                out selected);
            if (found) preferredPlacementSurfaceId = selected.SurfaceId;
            return found;
        }

        public static bool TrySelectNearestRestingSurface(
            IReadOnlyList<RoomPlacementCandidate> candidates,
            Pose viewer,
            out RoomPlacementCandidate selected)
        {
            return TrySelectStableRestingSurface(
                candidates, viewer, string.Empty, 0f, out selected);
        }

        public static bool TrySelectStableRestingSurface(
            IReadOnlyList<RoomPlacementCandidate> candidates,
            Pose viewer,
            string preferredSurfaceId,
            float preferredSurfaceHysteresis,
            out RoomPlacementCandidate selected)
        {
            selected = default;
            if (candidates == null)
            {
                return false;
            }
            var found = false;
            var bestScore = float.PositiveInfinity;
            var viewerForward = Vector3.ProjectOnPlane(viewer.rotation * Vector3.forward, Vector3.up).normalized;
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                if ((!candidate.SupportsSitting && !candidate.SupportsLying) ||
                    !IsFinite(candidate.SuggestedPose.position) ||
                    !IsFinite(candidate.SuggestedPose.rotation))
                {
                    continue;
                }
                var delta = Vector3.ProjectOnPlane(
                    candidate.SuggestedPose.position - viewer.position,
                    Vector3.up);
                var score = delta.sqrMagnitude;
                if (viewerForward.sqrMagnitude > .001f && delta.sqrMagnitude > .001f &&
                    Vector3.Dot(viewerForward, delta.normalized) < 0f)
                {
                    score += 16f;
                }
                // Prefer an explicitly labelled bed for lying when candidates
                // are at effectively the same distance.
                if (candidate.Kind == RoomPlacementSurfaceKind.Bed)
                {
                    score -= .05f;
                }
                if (!string.IsNullOrEmpty(preferredSurfaceId) &&
                    string.Equals(candidate.SurfaceId, preferredSurfaceId, StringComparison.Ordinal))
                {
                    var hysteresis = Mathf.Max(0f, preferredSurfaceHysteresis);
                    score -= hysteresis * hysteresis;
                }
                if (!found || score < bestScore - .0001f ||
                    Mathf.Abs(score - bestScore) <= .0001f &&
                    string.CompareOrdinal(candidate.SurfaceId, selected.SurfaceId) < 0)
                {
                    selected = candidate;
                    bestScore = score;
                    found = true;
                }
            }
            return found;
        }

        public static bool TrySelectNearestPlacementCandidate(
            IReadOnlyList<RoomPlacementCandidate> candidates,
            RoomPlacementSurfaceKind kind,
            Pose viewer,
            out RoomPlacementCandidate selected)
        {
            return TrySelectStablePlacementCandidate(
                candidates, kind, viewer, string.Empty, 0f, out selected);
        }

        public static bool TrySelectStablePlacementCandidate(
            IReadOnlyList<RoomPlacementCandidate> candidates,
            RoomPlacementSurfaceKind kind,
            Pose viewer,
            string preferredSurfaceId,
            float preferredSurfaceHysteresis,
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
                if (!string.IsNullOrEmpty(preferredSurfaceId) &&
                    string.Equals(candidate.SurfaceId, preferredSurfaceId, StringComparison.Ordinal))
                {
                    var hysteresis = Mathf.Max(0f, preferredSurfaceHysteresis);
                    score -= hysteresis * hysteresis;
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

            if (!TryResolvePlacementSurfaceKind(surface, out var kind))
            {
                return false;
            }

            var supportsSitting = false;
            var supportsLying = false;
            switch (kind)
            {
                case RoomPlacementSurfaceKind.Floor:
                    if (Mathf.Min(surface.Size.x, surface.Size.y) < Mathf.Max(.1f, minimumFloorExtent))
                    {
                        return false;
                    }
                    break;
                case RoomPlacementSurfaceKind.Seat:
                case RoomPlacementSurfaceKind.Couch:
                    var largest = Mathf.Max(surface.Size.x, surface.Size.y);
                    var smallest = Mathf.Min(surface.Size.x, surface.Size.y);
                    if (largest < Mathf.Max(.1f, minimumSeatWidth) ||
                        smallest < Mathf.Max(.1f, minimumSeatDepth))
                    {
                        return false;
                    }
                    supportsSitting = true;
                    // AR Foundation 5.2 collapses Meta couch/bed labels into
                    // the public Seat classification. Advertise capability,
                    // not an invented label: only a long and sufficiently
                    // deep seat surface can become a lying target.
                    supportsLying = largest >= 1.35f && smallest >= .55f;
                    break;
                case RoomPlacementSurfaceKind.Bed:
                    if (Mathf.Max(surface.Size.x, surface.Size.y) < 1.35f ||
                        Mathf.Min(surface.Size.x, surface.Size.y) < .55f)
                    {
                        return false;
                    }
                    supportsSitting = true;
                    supportsLying = true;
                    break;
                case RoomPlacementSurfaceKind.Table:
                    if (Mathf.Min(surface.Size.x, surface.Size.y) < Mathf.Max(.25f, minimumFloorExtent))
                    {
                        return false;
                    }
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
                supportsSitting,
                supportsLying);
            return true;
        }

        public static bool TryResolvePlacementSurfaceKind(
            PlaneClassification classification,
            out RoomPlacementSurfaceKind kind)
        {
            if (classification == PlaneClassification.Floor)
            {
                kind = RoomPlacementSurfaceKind.Floor;
                return true;
            }
            if (classification == PlaneClassification.Seat)
            {
                kind = RoomPlacementSurfaceKind.Seat;
                return true;
            }
            if (classification == PlaneClassification.Table)
            {
                kind = RoomPlacementSurfaceKind.Table;
                return true;
            }

            // ARSubsystems versions differ in the extra Meta labels they
            // expose. Resolve Couch/Bed by the public enum name so this code
            // remains source-compatible with versions that omit either value.
            var name = Enum.GetName(typeof(PlaneClassification), classification) ?? string.Empty;
            if (name.IndexOf("couch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("sofa", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                kind = RoomPlacementSurfaceKind.Couch;
                return true;
            }
            if (name.IndexOf("bed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                kind = RoomPlacementSurfaceKind.Bed;
                return true;
            }
            kind = default;
            return false;
        }

        public static bool TryResolvePlacementSurfaceKind(
            RoomSurfaceObservation surface,
            out RoomPlacementSurfaceKind kind)
        {
            if (surface.SemanticKind.HasValue)
            {
                kind = surface.SemanticKind.Value;
                return true;
            }
            return TryResolvePlacementSurfaceKind(surface.Classification, out kind);
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
                    if (observation.SemanticKind == RoomPlacementSurfaceKind.Bed)
                    {
                        snapshot.BedCount++;
                        continue;
                    }
                    switch (observation.Classification)
                    {
                        case PlaneClassification.Floor: snapshot.FloorCount++; break;
                        case PlaneClassification.Seat:
                            if (CountsAsSeat(observation)) snapshot.SeatCount++;
                            break;
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

        public static bool CountsAsSeat(RoomSurfaceObservation observation)
        {
            // MRUK Bed uses PlaneClassification.Seat only because older
            // ARSubsystems versions have no public Bed enum. Preserve it as a
            // lying/sitting candidate without claiming that the room contains
            // an extra chair or couch in the bounded semantic snapshot.
            return observation.Classification == PlaneClassification.Seat &&
                   observation.SemanticKind != RoomPlacementSurfaceKind.Bed;
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

        private void FinishRefresh(string provider, string reason)
        {
            placementCandidates.Clear();
            FloorCount = SeatCount = TableCount = WallCount = 0;
            for (var index = 0; index < surfaces.Count; index++)
            {
                switch (surfaces[index].Classification)
                {
                    case PlaneClassification.Floor: FloorCount++; break;
                    case PlaneClassification.Seat:
                        if (CountsAsSeat(surfaces[index])) SeatCount++;
                        break;
                    case PlaneClassification.Table: TableCount++; break;
                    case PlaneClassification.Wall: WallCount++; break;
                }
            }
            surfaces.Sort(CompareObservations);
            RebuildPlacementCandidates();
            Status = surfaces.Count == 0
                ? "No room surfaces found; run Quest room setup"
                : ContextSummary;
            Debug.Log($"[RoomUnderstanding] provider={provider}; surfaces={surfaces.Count}; reason={reason}", this);
            SnapshotChanged?.Invoke();
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
