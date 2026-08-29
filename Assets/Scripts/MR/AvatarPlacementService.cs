using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace QuestMmdPlayer
{
    [System.Serializable]
    public struct AvatarPlacementBookmark
    {
        public int Version;
        public string SurfaceId;
        public int SurfaceClassification;
        public Vector3 SurfacePositionInOrigin;
        public Quaternion SurfaceRotationInOrigin;
        public Vector2 SurfaceSize;
        public Vector3 AvatarPositionRelativeToSurface;
        public Quaternion AvatarRotationRelativeToSurface;
    }

    /// <summary>Places the avatar on a tracked floor and keeps it in the MR world.</summary>
    [DisallowMultipleComponent]
    public sealed class AvatarPlacementService : MonoBehaviour
    {
        private const float PlacementRetrySeconds = 0.5f;
        private const int PlacementBookmarkVersion = 1;
        private const string PlacementBookmarkPreference = "banxia.avatar.room_placement.v1";
        private static readonly List<ARRaycastHit> RaycastHits = new List<ARRaycastHit>();
        private readonly List<XRInputSubsystem> inputSubsystems = new List<XRInputSubsystem>();
        private readonly List<RoomSurfaceObservation> restoreSurfaces = new List<RoomSurfaceObservation>();

        [SerializeField] private bool placeAutomatically = true;
        [SerializeField] private bool createSpatialAnchor = false;
        [SerializeField] private bool importedAvatarFacesNegativeZ = false;
        [SerializeField, Min(0.5f)] private float placementDistance = 1.35f;
        [SerializeField, Min(0f)] private float planeWaitSeconds = 4f;
        [SerializeField, Range(0f, 1f)] private float minimumUpDot = 0.85f;
        [SerializeField, Min(0.5f)] private float minimumUserHeight = 0.9f;
        [SerializeField, Min(0.5f)] private float maximumUserHeight = 2.3f;
        [SerializeField, Range(0.05f, 0.2f)] private float eyeToHeadTopMeters = 0.11f;
        [SerializeField, Range(0.3f, 1.0f)] private float minimumEyeToFloorMeters = 0.55f;
        private XROrigin xrOrigin;
        private Camera headCamera;
        private ARPlaneManager planeManager;
        private ARRaycastManager raycastManager;
        private ARAnchorManager anchorManager;
        private ARAnchor spatialAnchor;
        private RoomUnderstandingService roomUnderstanding;
        private AvatarController avatar;
        private bool placementRequested;
        private bool restoreRequested;
        private bool hasSavedPlacementBookmark;
        private AvatarPlacementBookmark savedPlacementBookmark;
        private bool usingFallback;
        private bool rightStickWasPressed;
        private float placementDeadline;
        private float nextPlaneAttemptTime;
        private float estimatedUserHeight = 1.6f;
        private float calibratedFloorHeight;
        private bool hasHeightCalibration;
        private bool hasCalibratedFloor;
        private RuntimeDebugLog diagnostics;
        private bool restingAlignmentActive;
        private bool returningToStanding;
        private float restingAlignmentClock;
        private const float RestingAlignmentSeconds = .8f;
        private Pose standingPose;
        private Pose restingAlignmentStartPose;
        private Pose restingTargetPose;
        private string restingAction = string.Empty;
        private string pendingStandingAction = string.Empty;
        private bool internalRestActionChange;
        private bool hasRestingPose;
        public string Status { get; private set; } = "Waiting for avatar";
        public bool HasPlacement { get; private set; }
        public bool IsUsingFallback => usingFallback;
        public bool HasSpatialAnchor => spatialAnchor != null;
        public float EstimatedUserHeight => estimatedUserHeight;
        public float CalibratedFloorHeight => calibratedFloorHeight;
        public bool HasHeightCalibration => hasHeightCalibration;
        public bool HasCalibratedFloor => hasCalibratedFloor;
        public bool HasSavedPlacementBookmark => hasSavedPlacementBookmark;
        public bool HasPreparedSeatTarget { get; private set; }
        public RoomPlacementCandidate PreparedSeatTarget { get; private set; }
        public bool HasPreparedRestingTarget { get; private set; }
        public RoomPlacementCandidate PreparedRestingTarget { get; private set; }
        public SpatialCapabilitySnapshot SpatialCapabilities => roomUnderstanding == null
            ? default
            : roomUnderstanding.Capabilities;
        public bool IsRestingOrAligning => hasRestingPose || restingAlignmentActive || returningToStanding ||
            string.Equals(avatar == null ? string.Empty : avatar.CurrentAction, "sit", StringComparison.Ordinal) ||
            string.Equals(avatar == null ? string.Empty : avatar.CurrentAction, "lie_down", StringComparison.Ordinal);
        private void Awake()
        {
            ResolveDependencies();
        }

        private void Update()
        {
            ResolveDependencies();
            UpdateRestingAlignment();
            ReadPlacementInput();
            if (avatar == null || !placementRequested)
            {
                return;
            }

            if (Time.unscaledTime < nextPlaneAttemptTime)
            {
                return;
            }

            nextPlaneAttemptTime = Time.unscaledTime + PlacementRetrySeconds;
            if (restoreRequested)
            {
                if (TryRestoreSavedPlacement())
                {
                    return;
                }
                if (Time.unscaledTime < placementDeadline)
                {
                    return;
                }
                restoreRequested = false;
            }
            if (TryPlaceOnTrackedFloor())
            {
                return;
            }

            if (placementRequested && Time.unscaledTime >= placementDeadline)
            {
                if (!HasPlacement)
                {
                    PlaceAtFallbackPose();
                }
                else
                {
                    placementRequested = false;
                    restoreRequested = false;
                    PausePlaneDetection();
                    Status = hasHeightCalibration
                        ? $"Placed in front | height {estimatedUserHeight:F2}m"
                        : "Placed in front";
                }
            }
        }

        public void Bind(AvatarController nextAvatar)
        {
            if (avatar == nextAvatar)
            {
                return;
            }

            if (avatar != null)
            {
                avatar.ActionChanged -= HandleAvatarActionChanged;
            }
            ReleaseSpatialAnchor();
            avatar = nextAvatar;
            diagnostics = GetComponent<RuntimeDebugLog>();
            HasPlacement = false;
            usingFallback = false;
            placementRequested = false;
            restoreRequested = false;
            HasPreparedSeatTarget = false;
            PreparedSeatTarget = default;
            HasPreparedRestingTarget = false;
            PreparedRestingTarget = default;
            hasRestingPose = false;
            restingAlignmentActive = false;
            returningToStanding = false;
            hasSavedPlacementBookmark = TryLoadPlacementBookmark(out savedPlacementBookmark);

            if (avatar == null)
            {
                Status = "Waiting for avatar";
                return;
            }

            avatar.ActionChanged += HandleAvatarActionChanged;

            if (placeAutomatically)
            {
                // Startup must be immediate and independent of optional Meta Scene
                // data. Explicit height reset/room placement commands can start a
                // bounded plane-tracking window later.
                ResolveDependencies();
                PlaceAtFallbackPose();
            }
            else
            {
                Status = "Ready to place";
            }
        }

        public void CalibrateHeightAndPlace()
        {
            ResetHeightAndPlace();
        }

        public void ResetHeightAndPlace()
        {
            ResolveDependencies();
            if (headCamera == null)
            {
                Status = "Head pose is unavailable";
                return;
            }

            RequestFloorTrackingOrigin();
            ReleaseSpatialAnchor();
            HasPlacement = false;
            placementRequested = false;
            hasHeightCalibration = false;
            hasCalibratedFloor = false;

            ResumePlaneDetection();
            var floor = ResolveTrackingFloorHeight();
            if (TryReadTrackedFloorHeight(out var trackedFloor))
            {
                floor = trackedFloor;
            }
            CaptureHeightCalibration(floor);
            RequestPlacement();
        }

        public void FaceUserAndPlace()
        {
            RequestPlacement();
        }

        public void RequestPlacement()
        {
            ForgetSavedPlacement();
            RequestPlacementInternal(false);
        }

        private void RequestPlacementInternal(bool trySavedPlacement)
        {
            if (avatar == null)
            {
                Status = "Waiting for avatar";
                return;
            }

            ResolveDependencies();
            ResumePlaneDetection();
            placementRequested = true;
            restoreRequested = trySavedPlacement;
            usingFallback = false;
            placementDeadline = Time.unscaledTime + planeWaitSeconds;
            nextPlaneAttemptTime = 0f;
            Status = hasHeightCalibration
                ? $"Searching for a tracked floor | height {estimatedUserHeight:F2}m"
                : "Searching for a tracked floor";
            Debug.Log("[AvatarPlacement] Placement requested.", this);

            if (restoreRequested && TryRestoreSavedPlacement())
            {
                return;
            }

            // Never leave a freshly loaded model at its import transform while
            // room planes warm up. Prefer a tracked floor immediately, otherwise
            // show the stable tracking-floor pose and keep refining in the background.
            if (!restoreRequested && TryPlaceOnTrackedFloor())
            {
                return;
            }

            var keepTryingSavedPlacement = restoreRequested;
            PlaceAtFallbackPose();
            placementRequested = true;
            restoreRequested = keepTryingSavedPlacement;
            usingFallback = true;
            placementDeadline = Time.unscaledTime + planeWaitSeconds;
            nextPlaneAttemptTime = Time.unscaledTime + PlacementRetrySeconds;
            ResumePlaneDetection();
            Status = hasHeightCalibration
                ? $"Placed in front | checking floor | height {estimatedUserHeight:F2}m"
                : "Placed in front | checking floor";
        }

        private void ResolveDependencies()
        {
            if (xrOrigin == null)
            {
                xrOrigin = FindObjectOfType<XROrigin>();
            }

            if (headCamera == null)
            {
                headCamera = xrOrigin != null && xrOrigin.Camera != null ? xrOrigin.Camera : Camera.main;
            }

            if (xrOrigin == null)
            {
                return;
            }

            planeManager = planeManager != null
                ? planeManager
                : xrOrigin.GetComponent<ARPlaneManager>() ?? xrOrigin.gameObject.AddComponent<ARPlaneManager>();
            planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;
            raycastManager = raycastManager != null
                ? raycastManager
                : xrOrigin.GetComponent<ARRaycastManager>() ?? xrOrigin.gameObject.AddComponent<ARRaycastManager>();
            anchorManager = anchorManager != null
                ? anchorManager
                : xrOrigin.GetComponent<ARAnchorManager>();
            roomUnderstanding = roomUnderstanding != null
                ? roomUnderstanding
                : FindObjectOfType<RoomUnderstandingService>();
        }

        private void ReadPlacementInput()
        {
            var device = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            var pressed = device.isValid &&
                device.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out var value) && value;
            if (pressed && !rightStickWasPressed)
            {
                RequestPlacement();
            }

            rightStickWasPressed = pressed;
#if UNITY_EDITOR || UNITY_STANDALONE
            if (Input.GetKeyDown(KeyCode.P))
            {
                RequestPlacement();
            }
#endif
        }

        private bool TryPlaceOnTrackedFloor()
        {
            if (raycastManager == null || headCamera == null || !raycastManager.isActiveAndEnabled)
            {
                return false;
            }

            var headPose = new Pose(headCamera.transform.position, headCamera.transform.rotation);
            var ray = CreateHeadDirectedFloorRay(headPose, ResolveFloorHeight(), placementDistance);
            const TrackableType mask = TrackableType.PlaneWithinPolygon | TrackableType.PlaneWithinBounds;
            if (!raycastManager.Raycast(ray, RaycastHits, mask))
            {
                return false;
            }

            foreach (var hit in RaycastHits)
            {
                if (!IsHorizontalUpPose(hit.pose, minimumUpDot))
                {
                    continue;
                }

                var trackedPlane = hit.trackable as ARPlane;
                if (trackedPlane != null && trackedPlane.classification != PlaneClassification.Floor &&
                    trackedPlane.classification != PlaneClassification.None)
                {
                    continue;
                }

                ApplyPlacement(hit.pose.position, true, trackedPlane);
                return true;
            }

            return false;
        }

        private void PlaceAtFallbackPose()
        {
            if (headCamera == null)
            {
                Status = "Head pose is unavailable";
                return;
            }

            var pose = CreateFallbackPose(
                new Pose(headCamera.transform.position, headCamera.transform.rotation),
                ResolveFloorHeight(),
                placementDistance,
                importedAvatarFacesNegativeZ);
            ApplyPlacement(pose.position, false, null);
        }

        private void ApplyPlacement(Vector3 position, bool trackedFloor, ARPlane plane)
        {
            var rotation = ComputeFacingRotation(
                position,
                headCamera == null ? position - Vector3.forward : headCamera.transform.position,
                importedAvatarFacesNegativeZ);
            var pose = new Pose(position, rotation);
            ApplyPlacementPose(pose, trackedFloor, plane, true);
        }

        private void ApplyPlacementPose(Pose pose, bool trackedFloor, ARPlane plane, bool persistTrackedSurface)
        {
            ReleaseSpatialAnchor();
            if (trackedFloor)
            {
                CaptureHeightCalibration(pose.position.y);
            }
            avatar.SetPlacementPose(pose);
            var anchored = TryCreateSpatialAnchor(pose);
            PausePlaneDetection();

            HasPlacement = true;
            placementRequested = false;
            restoreRequested = false;
            usingFallback = !trackedFloor;
            Status = trackedFloor
                ? anchored ? "Placed on tracked floor with spatial anchor" : "Placed on tracked floor"
                : anchored ? "Placed at tracking-floor fallback with spatial anchor" : "Placed at tracking-floor fallback";
            var planeId = plane == null ? "none" : plane.trackableId.ToString();
            if (persistTrackedSurface && plane != null)
            {
                SavePlacementBookmark(CreateSurfaceObservation(plane), pose);
            }
            Debug.Log(
                $"[AvatarPlacement] {Status}; position={pose.position:F3}; plane={planeId}; worldRoot={xrOrigin?.name ?? "none"}.",
                this);
        }

        public bool TryPrepareNearestSeatTarget()
        {
            ResolveDependencies();
            HasPreparedSeatTarget = false;
            PreparedSeatTarget = default;
            if (roomUnderstanding == null || headCamera == null)
            {
                return false;
            }

            if (!roomUnderstanding.HasRoomData)
            {
                roomUnderstanding.RefreshNow();
            }
            var viewer = new Pose(headCamera.transform.position, headCamera.transform.rotation);
            if (!roomUnderstanding.TryFindNearestSeat(viewer, out var target))
            {
                diagnostics?.RecordStage("spatial", "limited", "seat_surface_unavailable");
                return false;
            }

            PreparedSeatTarget = target;
            HasPreparedSeatTarget = true;
            diagnostics?.RecordStage("spatial", "ready", "seat_surface_selected");
            return true;
        }

        /// <summary>
        /// Resolves a semantic seat/couch/bed target without moving the avatar.
        /// An action system can inspect SupportsSitting/SupportsLying, choose a
        /// matching animation, then commit placement at SuggestedPose.
        /// </summary>
        public bool TryPrepareNearestRestingTarget()
        {
            ResolveDependencies();
            HasPreparedRestingTarget = false;
            PreparedRestingTarget = default;
            if (roomUnderstanding == null || headCamera == null)
            {
                return false;
            }

            if (!roomUnderstanding.HasRoomData)
            {
                roomUnderstanding.RefreshNow();
            }
            var viewer = new Pose(headCamera.transform.position, headCamera.transform.rotation);
            if (!roomUnderstanding.TryFindNearestRestingSurface(viewer, out var target))
            {
                diagnostics?.RecordStage("spatial", "limited", "rest_surface_unavailable");
                return false;
            }

            PreparedRestingTarget = target;
            HasPreparedRestingTarget = true;
            diagnostics?.RecordStage("spatial", "ready", "rest_surface_selected");
            return true;
        }

        /// <summary>
        /// Starts an explicit sit/lie request. Room understanding only supplies
        /// a candidate; this method is the sole owner of movement into it.
        /// Missing data or a capability mismatch fails closed and leaves the
        /// avatar in its current pose.
        /// </summary>
        public bool TryExecuteRestingAction(string requestedAction)
        {
            var action = string.IsNullOrWhiteSpace(requestedAction)
                ? string.Empty
                : requestedAction.Trim().ToLowerInvariant();
            if ((action != "sit" && action != "lie_down") ||
                avatar == null || returningToStanding || hasRestingPose)
            {
                return false;
            }

            if (restingAlignmentActive)
            {
                diagnostics?.RecordStage("avatar_action", "blocked", "rest_target_busy");
                return false;
            }

            var hasTarget = action == "sit"
                ? TryPrepareNearestSeatTarget()
                : TryPrepareNearestRestingTarget();
            var target = action == "sit" ? PreparedSeatTarget : PreparedRestingTarget;
            var capabilityMatches = hasTarget && (action == "sit" ? target.SupportsSitting : target.SupportsLying);
            if (!capabilityMatches || !TryCreateRestingPose(
                    target,
                    headCamera == null
                        ? new Pose(transform.position, transform.rotation)
                        : new Pose(headCamera.transform.position, headCamera.transform.rotation),
                    action,
                    importedAvatarFacesNegativeZ,
                    avatar.EstimateVisualHeight(),
                    avatar.EstimateHipHeight(),
                    out var pose))
            {
                diagnostics?.RecordStage(
                    "avatar_action",
                    "limited",
                    hasTarget ? "rest_target_capability_missing" : "rest_target_missing");
                return false;
            }

            standingPose = new Pose(avatar.transform.position, avatar.transform.rotation);
            restingAlignmentStartPose = standingPose;
            restingTargetPose = pose;
            restingAction = action;
            pendingStandingAction = string.Empty;
            restingAlignmentClock = 0f;
            restingAlignmentActive = true;
            returningToStanding = false;
            diagnostics?.RecordStage("avatar_action", "ready", "rest_target_found");
            diagnostics?.RecordStage("avatar_action", "processing", "rest_alignment_started");
            return true;
        }

        public bool TryReturnToStanding(string nextAction = "idle")
        {
            if (avatar == null || (!IsRestingOrAligning && !restingAlignmentActive))
            {
                return false;
            }
            pendingStandingAction = string.IsNullOrWhiteSpace(nextAction) ? "idle" : nextAction.ToLowerInvariant();
            restingAlignmentActive = false;
            returningToStanding = true;
            restingAlignmentStartPose = new Pose(avatar.transform.position, avatar.transform.rotation);
            restingAlignmentClock = 0f;
            if (!string.Equals(avatar.CurrentAction, "idle", StringComparison.Ordinal))
            {
                internalRestActionChange = true;
                avatar.PlayActionFromSource("idle", AvatarActionSource.System);
                internalRestActionChange = false;
            }
            diagnostics?.RecordStage("avatar_action", "processing", "rest_return_started");
            return true;
        }

        public bool ResetAvatarToStanding()
        {
            if (TryReturnToStanding("idle"))
            {
                return true;
            }
            avatar?.ResetTransform();
            return false;
        }

        private void HandleAvatarActionChanged(string action)
        {
            if (internalRestActionChange || avatar == null || !IsRestingOrAligning)
            {
                return;
            }
            if (action == "sit" || action == "lie_down")
            {
                return;
            }
            TryReturnToStanding(action);
        }

        private void UpdateRestingAlignment()
        {
            if (avatar == null || (!restingAlignmentActive && !returningToStanding))
            {
                return;
            }
            restingAlignmentClock += Time.unscaledDeltaTime;
            var amount = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(
                restingAlignmentClock / RestingAlignmentSeconds));
            var from = restingAlignmentStartPose;
            var to = restingAlignmentActive ? restingTargetPose : standingPose;
            avatar.transform.SetPositionAndRotation(
                Vector3.Lerp(from.position, to.position, amount),
                Quaternion.Slerp(from.rotation, to.rotation, amount));
            if (amount < 1f)
            {
                return;
            }

            if (restingAlignmentActive)
            {
                restingAlignmentActive = false;
                hasRestingPose = true;
                internalRestActionChange = true;
                avatar.PlayActionFromSource(restingAction, AvatarActionSource.Backend);
                internalRestActionChange = false;
                diagnostics?.RecordStage("avatar_action", "completed", "rest_alignment_completed");
                return;
            }

            returningToStanding = false;
            hasRestingPose = false;
            var next = pendingStandingAction;
            pendingStandingAction = string.Empty;
            diagnostics?.RecordStage("avatar_action", "completed", "rest_return_completed");
            if (!string.IsNullOrWhiteSpace(next) && next != "idle")
            {
                internalRestActionChange = true;
                avatar.PlayActionFromSource(next, AvatarActionSource.Backend);
                internalRestActionChange = false;
            }
        }

        public static bool TryCreateRestingPose(
            RoomPlacementCandidate candidate,
            Pose viewer,
            string action,
            bool modelFacesNegativeZ,
            float avatarHeight,
            float hipHeight,
            out Pose pose)
        {
            pose = default;
            var normalized = string.IsNullOrWhiteSpace(action) ? string.Empty : action.ToLowerInvariant();
            if ((normalized != "sit" && normalized != "lie_down") ||
                !IsFinite(viewer.position) ||
                !IsFinite(candidate.SurfacePose.position) || !IsFinite(candidate.SurfacePose.rotation) ||
                !IsFinite(candidate.SuggestedPose.position) || !IsFinite(candidate.SuggestedPose.rotation) ||
                candidate.Size.x <= .1f || candidate.Size.y <= .1f)
            {
                return false;
            }
            if ((normalized == "sit" && !candidate.SupportsSitting) ||
                (normalized == "lie_down" && !candidate.SupportsLying))
            {
                return false;
            }

            var surfaceUp = candidate.SurfacePose.rotation * Vector3.up;
            if (surfaceUp.sqrMagnitude < .5f || Vector3.Dot(surfaceUp.normalized, Vector3.up) < .65f)
            {
                return false;
            }
            surfaceUp.Normalize();
            if (normalized == "sit")
            {
                var rotation = ComputeFacingRotation(
                    candidate.SuggestedPose.position,
                    viewer.position,
                    modelFacesNegativeZ);
                var position = candidate.SuggestedPose.position + surfaceUp * .015f;
                // The placeholder pose bends at the hip, so keep the root near
                // the room floor while putting the seat under the pelvis region.
                position -= Vector3.up * Mathf.Clamp(hipHeight * .52f, .2f, .85f);
                pose = new Pose(position, rotation);
                return IsFinite(rotation) && IsWithinSurfaceBounds(candidate, position, .04f);
            }

            var localLongAxis = candidate.Size.x >= candidate.Size.y ? Vector3.right : Vector3.forward;
            var bodyDirection = candidate.SurfacePose.rotation * localLongAxis;
            bodyDirection = Vector3.ProjectOnPlane(bodyDirection, surfaceUp).normalized;
            if (bodyDirection.sqrMagnitude < .5f)
            {
                return false;
            }
            var faceDirection = modelFacesNegativeZ ? -surfaceUp : surfaceUp;
            var lieRotation = Quaternion.LookRotation(faceDirection, bodyDirection);
            var margin = .08f;
            var availableHalfLength = Mathf.Max(.05f, Mathf.Max(candidate.Size.x, candidate.Size.y) * .5f - margin);
            var rootShift = Mathf.Min(Mathf.Max(.1f, avatarHeight * .5f), availableHalfLength * .8f);
            var liePosition = candidate.SuggestedPose.position - bodyDirection * rootShift + surfaceUp * .035f;
            pose = new Pose(liePosition, lieRotation);
            return IsFinite(lieRotation) && IsWithinSurfaceBounds(candidate, liePosition, margin);
        }

        private static bool IsWithinSurfaceBounds(RoomPlacementCandidate candidate, Vector3 position, float margin)
        {
            var local = Quaternion.Inverse(candidate.SurfacePose.rotation) *
                (position - candidate.SurfacePose.position);
            var halfX = Mathf.Max(0f, candidate.Size.x * .5f - margin);
            var halfY = Mathf.Max(0f, candidate.Size.y * .5f - margin);
            return Mathf.Abs(local.x) <= halfX + .001f && Mathf.Abs(local.z) <= halfY + .001f;
        }

        public void ForgetSavedPlacement()
        {
            PlayerPrefs.DeleteKey(PlacementBookmarkPreference);
            PlayerPrefs.Save();
            hasSavedPlacementBookmark = false;
            savedPlacementBookmark = default;
            restoreRequested = false;
        }

        private bool TryRestoreSavedPlacement()
        {
            if (!hasSavedPlacementBookmark || planeManager == null || headCamera == null ||
                !planeManager.isActiveAndEnabled)
            {
                return false;
            }

            restoreSurfaces.Clear();
            foreach (var plane in planeManager.trackables)
            {
                if (plane == null || plane.trackingState == TrackingState.None)
                {
                    continue;
                }
                var observation = CreateSurfaceObservation(plane);
                if (RoomUnderstandingService.IsUsableObservation(observation))
                {
                    restoreSurfaces.Add(observation);
                }
            }

            var originPose = xrOrigin == null
                ? new Pose(Vector3.zero, Quaternion.identity)
                : new Pose(xrOrigin.transform.position, xrOrigin.transform.rotation);
            if (!TryResolvePlacementBookmark(
                savedPlacementBookmark,
                restoreSurfaces,
                originPose,
                .75f,
                out var restoredPose,
                out var matchedSurface))
            {
                return false;
            }

            ARPlane matchedPlane = null;
            foreach (var plane in planeManager.trackables)
            {
                if (plane != null && string.Equals(
                    plane.trackableId.ToString(),
                    matchedSurface.Id,
                    System.StringComparison.Ordinal))
                {
                    matchedPlane = plane;
                    break;
                }
            }
            var isFloor = matchedSurface.Classification == PlaneClassification.Floor;
            ApplyPlacementPose(restoredPose, isFloor, matchedPlane, matchedPlane != null);
            Status = isFloor
                ? "Restored on saved room floor"
                : "Restored on saved room surface";
            Debug.Log($"[AvatarPlacement] {Status}; semantic surface reacquired.", this);
            return true;
        }

        private void SavePlacementBookmark(RoomSurfaceObservation surface, Pose avatarPose)
        {
            var originPose = xrOrigin == null
                ? new Pose(Vector3.zero, Quaternion.identity)
                : new Pose(xrOrigin.transform.position, xrOrigin.transform.rotation);
            var bookmark = CreatePlacementBookmark(surface, avatarPose, originPose);
            if (!IsValidPlacementBookmark(bookmark))
            {
                return;
            }
            PlayerPrefs.SetString(PlacementBookmarkPreference, JsonUtility.ToJson(bookmark));
            PlayerPrefs.Save();
            savedPlacementBookmark = bookmark;
            hasSavedPlacementBookmark = true;
        }

        private static bool TryLoadPlacementBookmark(out AvatarPlacementBookmark bookmark)
        {
            bookmark = default;
            var json = PlayerPrefs.GetString(PlacementBookmarkPreference, string.Empty);
            if (string.IsNullOrEmpty(json))
            {
                return false;
            }
            try
            {
                bookmark = JsonUtility.FromJson<AvatarPlacementBookmark>(json);
                return IsValidPlacementBookmark(bookmark);
            }
            catch (System.Exception)
            {
                bookmark = default;
                return false;
            }
        }

        private static RoomSurfaceObservation CreateSurfaceObservation(ARPlane plane)
        {
            var center = plane.transform.TransformPoint(new Vector3(plane.center.x, 0f, plane.center.y));
            return new RoomSurfaceObservation(
                plane.trackableId.ToString(),
                plane.classification,
                new Pose(center, plane.transform.rotation),
                plane.size);
        }

        private bool TryCreateSpatialAnchor(Pose pose)
        {
            if (!createSpatialAnchor || anchorManager == null || !anchorManager.isActiveAndEnabled)
            {
                return false;
            }

            var anchorObject = new GameObject("Avatar Spatial Anchor");
            anchorObject.transform.SetPositionAndRotation(pose.position, pose.rotation);
            spatialAnchor = anchorObject.AddComponent<ARAnchor>();
            avatar.transform.SetParent(anchorObject.transform, true);
            return true;
        }

        private void ReleaseSpatialAnchor()
        {
            if (spatialAnchor == null)
            {
                return;
            }

            if (avatar != null && avatar.transform.parent == spatialAnchor.transform)
            {
                avatar.transform.SetParent(null, true);
            }

            Destroy(spatialAnchor.gameObject);
            spatialAnchor = null;
        }

        private void ResumePlaneDetection()
        {
            if (planeManager != null)
            {
                planeManager.enabled = true;
                planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;
            }

            if (raycastManager != null)
            {
                raycastManager.enabled = true;
            }
        }

        private void PausePlaneDetection()
        {
            if (raycastManager != null)
            {
                raycastManager.enabled = false;
            }

            // Meta Scene plane discovery can retry continuously when Space Setup
            // data is unavailable. Stop it after placement; the room scan command
            // owns an explicit bounded tracking window when semantic data is needed.
            if (planeManager != null &&
                (roomUnderstanding == null || !roomUnderstanding.IsSceneCaptureTrackingRequested))
            {
                planeManager.enabled = false;
            }
        }

        private float ResolveFloorHeight()
        {
            return hasCalibratedFloor ? calibratedFloorHeight : ResolveTrackingFloorHeight();
        }

        private float ResolveTrackingFloorHeight()
        {
            if (xrOrigin != null)
            {
                return xrOrigin.transform.position.y;
            }

            return headCamera == null
                ? 0f
                : headCamera.transform.position.y - Mathf.Max(.1f, Mathf.Clamp(estimatedUserHeight, minimumUserHeight, maximumUserHeight) - eyeToHeadTopMeters);
        }

        private bool TryReadTrackedFloorHeight(out float floorHeight)
        {
            floorHeight = 0f;
            if (raycastManager == null || headCamera == null || !raycastManager.isActiveAndEnabled)
            {
                return false;
            }

            var headPose = new Pose(headCamera.transform.position, headCamera.transform.rotation);
            var rays = new[]
            {
                new Ray(headPose.position, Vector3.down),
                CreateHeadDirectedFloorRay(headPose, ResolveTrackingFloorHeight(), placementDistance)
            };
            const TrackableType mask = TrackableType.PlaneWithinPolygon | TrackableType.PlaneWithinBounds;
            var foundFloor = false;
            var lowestFloor = float.PositiveInfinity;
            for (var rayIndex = 0; rayIndex < rays.Length; rayIndex++)
            {
                if (!raycastManager.Raycast(rays[rayIndex], RaycastHits, mask))
                {
                    continue;
                }
                for (var hitIndex = 0; hitIndex < RaycastHits.Count; hitIndex++)
                {
                    var hit = RaycastHits[hitIndex];
                    if (!IsHorizontalUpPose(hit.pose, minimumUpDot))
                    {
                        continue;
                    }
                    var trackedPlane = hit.trackable as ARPlane;
                    if (trackedPlane != null && trackedPlane.classification != PlaneClassification.Floor &&
                        trackedPlane.classification != PlaneClassification.None)
                    {
                        continue;
                    }
                    var candidate = hit.pose.position.y;
                    if (!IsPlausibleFloorCandidate(
                            headPose.position.y,
                            candidate,
                            minimumEyeToFloorMeters,
                            maximumUserHeight))
                    {
                        continue;
                    }
                    // The downward ray can hit a bed or table first. Compare all
                    // horizontal hits from both rays and prefer the lowest valid plane.
                    if (!foundFloor || candidate < lowestFloor)
                    {
                        lowestFloor = candidate;
                        foundFloor = true;
                    }
                }
            }
            if (!foundFloor)
            {
                return false;
            }
            floorHeight = lowestFloor;
            return true;
        }

        private void CaptureHeightCalibration(float floorHeight)
        {
            calibratedFloorHeight = floorHeight;
            hasCalibratedFloor = true;
            estimatedUserHeight = EstimateUserHeight(
                headCamera == null ? floorHeight + Mathf.Max(.1f, estimatedUserHeight - eyeToHeadTopMeters) : headCamera.transform.position.y,
                floorHeight,
                eyeToHeadTopMeters,
                minimumUserHeight,
                maximumUserHeight);
            hasHeightCalibration = true;
            Debug.Log($"[AvatarPlacement] Height reset: {estimatedUserHeight:F2}m; floor={floorHeight:F3}.", this);
            diagnostics?.RecordStage(
                "spatial",
                "ready",
                hasCalibratedFloor ? "height_reset_with_floor" : "height_reset_fallback");
        }

        private void RequestFloorTrackingOrigin()
        {
            if (xrOrigin != null)
            {
                xrOrigin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
                xrOrigin.CameraYOffset = 0f;
            }

            inputSubsystems.Clear();
            SubsystemManager.GetSubsystems(inputSubsystems);
            for (var index = 0; index < inputSubsystems.Count; index++)
            {
                var subsystem = inputSubsystems[index];
                if (subsystem != null && subsystem.running &&
                    (subsystem.GetSupportedTrackingOriginModes() & TrackingOriginModeFlags.Floor) != 0)
                {
                    subsystem.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                }
            }
        }

        private void OnDestroy()
        {
            if (avatar != null)
            {
                avatar.ActionChanged -= HandleAvatarActionChanged;
            }
            ReleaseSpatialAnchor();
        }

        public static AvatarPlacementBookmark CreatePlacementBookmark(
            RoomSurfaceObservation surface,
            Pose avatarPose,
            Pose originPose)
        {
            var originInverse = Quaternion.Inverse(originPose.rotation);
            var surfaceInverse = Quaternion.Inverse(surface.Pose.rotation);
            return new AvatarPlacementBookmark
            {
                Version = PlacementBookmarkVersion,
                SurfaceId = surface.Id ?? string.Empty,
                SurfaceClassification = (int)surface.Classification,
                SurfacePositionInOrigin = originInverse * (surface.Pose.position - originPose.position),
                SurfaceRotationInOrigin = NormalizeQuaternion(originInverse * surface.Pose.rotation),
                SurfaceSize = surface.Size,
                AvatarPositionRelativeToSurface = surfaceInverse * (avatarPose.position - surface.Pose.position),
                AvatarRotationRelativeToSurface = NormalizeQuaternion(surfaceInverse * avatarPose.rotation)
            };
        }

        public static bool TryResolvePlacementBookmark(
            AvatarPlacementBookmark bookmark,
            IReadOnlyList<RoomSurfaceObservation> candidates,
            Pose originPose,
            float maximumFallbackCenterDistance,
            out Pose avatarPose,
            out RoomSurfaceObservation matchedSurface)
        {
            avatarPose = default;
            matchedSurface = default;
            if (!IsValidPlacementBookmark(bookmark) || candidates == null)
            {
                return false;
            }

            var inverseOrigin = Quaternion.Inverse(originPose.rotation);
            var found = false;
            var bestScore = float.PositiveInfinity;
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                if ((int)candidate.Classification != bookmark.SurfaceClassification ||
                    !RoomUnderstandingService.IsUsableObservation(candidate))
                {
                    continue;
                }

                var exactId = string.Equals(
                    candidate.Id,
                    bookmark.SurfaceId,
                    System.StringComparison.Ordinal);
                var localCenter = inverseOrigin * (candidate.Pose.position - originPose.position);
                var centerDistance = Vector3.Distance(localCenter, bookmark.SurfacePositionInOrigin);
                var candidateLongSide = Mathf.Max(candidate.Size.x, candidate.Size.y);
                var candidateShortSide = Mathf.Min(candidate.Size.x, candidate.Size.y);
                var bookmarkLongSide = Mathf.Max(bookmark.SurfaceSize.x, bookmark.SurfaceSize.y);
                var bookmarkShortSide = Mathf.Min(bookmark.SurfaceSize.x, bookmark.SurfaceSize.y);
                var sizeDistance = Vector2.Distance(
                    new Vector2(candidateLongSide, candidateShortSide),
                    new Vector2(bookmarkLongSide, bookmarkShortSide));
                if (!exactId &&
                    (centerDistance > Mathf.Max(.1f, maximumFallbackCenterDistance) || sizeDistance > .6f))
                {
                    continue;
                }

                var score = exactId ? centerDistance * .01f : 100f + centerDistance + sizeDistance * .5f;
                if (found && score > bestScore + .0001f)
                {
                    continue;
                }
                if (found && Mathf.Abs(score - bestScore) <= .0001f &&
                    string.CompareOrdinal(candidate.Id, matchedSurface.Id) >= 0)
                {
                    continue;
                }

                found = true;
                bestScore = score;
                matchedSurface = candidate;
            }

            if (!found)
            {
                return false;
            }

            avatarPose = new Pose(
                matchedSurface.Pose.position +
                    matchedSurface.Pose.rotation * bookmark.AvatarPositionRelativeToSurface,
                NormalizeQuaternion(
                    matchedSurface.Pose.rotation * bookmark.AvatarRotationRelativeToSurface));
            return IsFinitePose(avatarPose);
        }

        public static bool IsValidPlacementBookmark(AvatarPlacementBookmark bookmark)
        {
            var classification = (PlaneClassification)bookmark.SurfaceClassification;
            var hasSupportedSurface = RoomUnderstandingService.TryResolvePlacementSurfaceKind(
                classification,
                out var surfaceKind) &&
                surfaceKind != RoomPlacementSurfaceKind.Table;
            return bookmark.Version == PlacementBookmarkVersion &&
                !string.IsNullOrEmpty(bookmark.SurfaceId) && bookmark.SurfaceId.Length <= 128 &&
                hasSupportedSurface &&
                IsFinite(bookmark.SurfacePositionInOrigin) &&
                IsFinite(bookmark.SurfaceRotationInOrigin) &&
                IsFinite(bookmark.SurfaceSize) && bookmark.SurfaceSize.x > 0f && bookmark.SurfaceSize.y > 0f &&
                IsFinite(bookmark.AvatarPositionRelativeToSurface) &&
                IsFinite(bookmark.AvatarRotationRelativeToSurface);
        }

        private static Quaternion NormalizeQuaternion(Quaternion value)
        {
            var magnitude = Mathf.Sqrt(
                value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            if (magnitude < .0001f || float.IsNaN(magnitude) || float.IsInfinity(magnitude))
            {
                return Quaternion.identity;
            }
            return new Quaternion(
                value.x / magnitude,
                value.y / magnitude,
                value.z / magnitude,
                value.w / magnitude);
        }

        private static bool IsFinitePose(Pose value)
        {
            return IsFinite(value.position) && IsFinite(value.rotation);
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

        public static float EstimateUserHeight(
            float eyeHeight,
            float floorHeight,
            float eyeToHeadTop = .11f,
            float minimum = .9f,
            float maximum = 2.3f)
        {
            var low = Mathf.Min(minimum, maximum);
            var high = Mathf.Max(minimum, maximum);
            return Mathf.Clamp(eyeHeight + Mathf.Max(0f, eyeToHeadTop) - floorHeight, low, high);
        }

        public static bool IsPlausibleFloorCandidate(
            float eyeHeight,
            float floorHeight,
            float minimumEyeToFloor = .55f,
            float maximumEyeToFloor = 2.3f)
        {
            var distance = eyeHeight - floorHeight;
            return !float.IsNaN(distance) && !float.IsInfinity(distance) &&
                distance >= Mathf.Max(.1f, minimumEyeToFloor) &&
                distance <= Mathf.Max(minimumEyeToFloor, maximumEyeToFloor);
        }

        public static bool IsHorizontalUpPose(Pose pose, float requiredUpDot = 0.85f)
        {
            var normal = pose.rotation * Vector3.up;
            return Vector3.Dot(normal.normalized, Vector3.up) >= Mathf.Clamp(requiredUpDot, 0f, 1f);
        }

        public static Ray CreateHeadDirectedFloorRay(Pose headPose, float floorHeight, float distance)
        {
            var forward = headPose.rotation * Vector3.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude < 0.0001f ? Vector3.forward : forward.normalized;
            var target = headPose.position + forward * Mathf.Max(0.1f, distance);
            target.y = floorHeight;
            return new Ray(headPose.position, (target - headPose.position).normalized);
        }

        public static Pose CreateFallbackPose(
            Pose headPose,
            float floorHeight,
            float distance,
            bool modelFacesNegativeZ = true)
        {
            var forward = headPose.rotation * Vector3.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude < 0.0001f ? Vector3.forward : forward.normalized;
            var position = headPose.position + forward * Mathf.Max(0.1f, distance);
            position.y = floorHeight;
            return new Pose(
                position,
                ComputeFacingRotation(position, headPose.position, modelFacesNegativeZ));
        }

        public static Quaternion ComputeFacingRotation(
            Vector3 avatarPosition,
            Vector3 userPosition,
            bool modelFacesNegativeZ = true)
        {
            var awayFromUser = avatarPosition - userPosition;
            awayFromUser.y = 0f;
            if (awayFromUser.sqrMagnitude < 0.0001f)
            {
                return Quaternion.identity;
            }

            var transformForward = modelFacesNegativeZ ? awayFromUser : -awayFromUser;
            return Quaternion.LookRotation(transformForward.normalized, Vector3.up);
        }
    }
}
