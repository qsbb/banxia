using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace QuestMmdPlayer
{
    /// <summary>Places the avatar on a tracked floor and keeps it in the MR world.</summary>
    [DisallowMultipleComponent]
    public sealed class AvatarPlacementService : MonoBehaviour
    {
        private const float PlacementRetrySeconds = 0.5f;
        private static readonly List<ARRaycastHit> RaycastHits = new List<ARRaycastHit>();
        private readonly List<XRInputSubsystem> inputSubsystems = new List<XRInputSubsystem>();

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
        private AvatarController avatar;
        private bool placementRequested;
        private bool usingFallback;
        private bool rightStickWasPressed;
        private float placementDeadline;
        private float nextPlaneAttemptTime;
        private float estimatedUserHeight = 1.6f;
        private float calibratedFloorHeight;
        private bool hasHeightCalibration;
        private bool hasCalibratedFloor;
        public string Status { get; private set; } = "Waiting for avatar";
        public bool HasPlacement { get; private set; }
        public bool IsUsingFallback => usingFallback;
        public bool HasSpatialAnchor => spatialAnchor != null;
        public float EstimatedUserHeight => estimatedUserHeight;
        public float CalibratedFloorHeight => calibratedFloorHeight;
        public bool HasHeightCalibration => hasHeightCalibration;
        public bool HasCalibratedFloor => hasCalibratedFloor;
        private void Awake()
        {
            ResolveDependencies();
        }

        private void Update()
        {
            ResolveDependencies();
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

            ReleaseSpatialAnchor();
            avatar = nextAvatar;
            HasPlacement = false;
            usingFallback = false;
            placementRequested = false;

            if (avatar == null)
            {
                Status = "Waiting for avatar";
                return;
            }

            if (placeAutomatically)
            {
                RequestPlacement();
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
            if (avatar == null)
            {
                Status = "Waiting for avatar";
                return;
            }

            ResolveDependencies();
            ResumePlaneDetection();
            placementRequested = true;
            usingFallback = false;
            placementDeadline = Time.unscaledTime + planeWaitSeconds;
            nextPlaneAttemptTime = 0f;
            Status = hasHeightCalibration
                ? $"Searching for a tracked floor | height {estimatedUserHeight:F2}m"
                : "Searching for a tracked floor";
            Debug.Log("[AvatarPlacement] Placement requested.", this);

            // Never leave a freshly loaded model at its import transform while
            // room planes warm up. Prefer a tracked floor immediately, otherwise
            // show the stable tracking-floor pose and keep refining in the background.
            if (TryPlaceOnTrackedFloor())
            {
                return;
            }

            PlaceAtFallbackPose();
            placementRequested = true;
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
            ReleaseSpatialAnchor();
            if (trackedFloor)
            {
                CaptureHeightCalibration(position.y);
            }
            var rotation = ComputeFacingRotation(
                position,
                headCamera == null ? position - Vector3.forward : headCamera.transform.position,
                importedAvatarFacesNegativeZ);
            var pose = new Pose(position, rotation);
            avatar.SetPlacementPose(pose);
            var anchored = TryCreateSpatialAnchor(pose);
            PausePlaneDetection();

            HasPlacement = true;
            placementRequested = false;
            usingFallback = !trackedFloor;
            Status = trackedFloor
                ? anchored ? "Placed on tracked floor with spatial anchor" : "Placed on tracked floor"
                : anchored ? "Placed at tracking-floor fallback with spatial anchor" : "Placed at tracking-floor fallback";
            var planeId = plane == null ? "none" : plane.trackableId.ToString();
            Debug.Log(
                $"[AvatarPlacement] {Status}; position={position:F3}; plane={planeId}; worldRoot={xrOrigin?.name ?? "none"}.",
                this);
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

            // Keep semantic room planes alive after placement. Meta OpenXR reads
            // these from Space Setup, and RoomUnderstandingService reuses them.
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
            ReleaseSpatialAnchor();
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