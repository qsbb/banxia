using System;
using System.Collections.Generic;
using UMT;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Provider-neutral Quest interaction layer. It reads OpenXR controller or
    /// hand poses directly, so the prototype does not depend on XR Interaction
    /// Toolkit or a paid interaction SDK.
    /// </summary>
    public sealed class AvatarTouchInteraction : MonoBehaviour
    {
        [SerializeField] private bool inputEnabled = true;
        [SerializeField] private bool enableTouchFeedback = false;
        [SerializeField] private float touchDistance = 0.07f;
        [SerializeField, Range(1f, 2f)] private float touchReleaseDistanceMultiplier = 1.35f;
        [SerializeField] private float grabDistance = 0.16f;
        [SerializeField] private float grabThreshold = 0.65f;
        [SerializeField, Range(.02f, .08f)] private float pinchEnterDistance = .032f;
        [SerializeField, Range(.025f, .10f)] private float pinchReleaseDistance = .045f;
        [SerializeField, Range(.02f, .25f)] private float trackedPoseGraceSeconds = .10f;
        [SerializeField] private Transform trackingSpace;

        private readonly HandState leftHand = new HandState(XRNode.LeftHand);
        private readonly HandState rightHand = new HandState(XRNode.RightHand);
        private readonly List<XRHandSubsystem> handSubsystems = new List<XRHandSubsystem>();
        private MaterialPropertyBlock touchPropertyBlock;
        private readonly Collider[] contactHits = new Collider[24];
        private GameObject collisionProxyRoot;
        private bool collisionGeometryReady;
        private AvatarController avatar;
        private Renderer[] avatarRenderers = Array.Empty<Renderer>();
        private bool previousTouched;
        private bool feedbackActive;
        private bool feedbackGrabbing;
        private bool semanticInteractionLock;
        private Vector3 grabBasePosition;
        private Quaternion grabBaseRotation;
        private Vector3 grabBaseScale;
        private Vector3 singleGrabStartPosition;
        private Vector3 dualGrabStartLeft;
        private Vector3 dualGrabStartRight;
        private float dualGrabStartDistance;
        private Vector3 dualGrabStartMidpoint;

        public event Action<bool> TouchStateChanged;

        public AvatarController Avatar => avatar;
        public bool InputEnabled => inputEnabled;
        public bool IsTouched => leftHand.touched || rightHand.touched;
        public bool IsGrabbing => leftHand.grabbing || rightHand.grabbing;
        public string Status { get; private set; } = "No XR hand/controller";
        public string TouchedSide { get; private set; } = string.Empty;
        public bool IsQaContact { get; private set; }

        private sealed class HandState
        {
            public readonly XRNode node;
            public InputDevice device;
            public bool available;
            public bool trackedHand;
            public bool trackingGrace;
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 indexTip;
            public Vector3 thumbTip;
            public bool hasIndexTip;
            public bool hasThumbTip;
            public float lastTrackedPoseAt = float.NegativeInfinity;
            public bool primary;
            public bool secondary;
            public bool grabHeld;
            public bool previousPrimary;
            public bool previousSecondary;
            public bool touched;
            public bool nearGrab;
            public bool grabbing;

            public HandState(XRNode node)
            {
                this.node = node;
                rotation = Quaternion.identity;
            }

            public bool PrimaryDown => primary && !previousPrimary;
            public bool SecondaryDown => secondary && !previousSecondary;
        }

        private void Awake()
        {
            touchPropertyBlock = new MaterialPropertyBlock();
        }

        public void Bind(AvatarController target)
        {
            if (avatar == target)
            {
                return;
            }

            ClearFeedback();
            ClearCollisionProxies();
            avatar = target;
            avatarRenderers = Array.Empty<Renderer>();
            previousTouched = false;
            leftHand.touched = rightHand.touched = false;
            leftHand.nearGrab = rightHand.nearGrab = false;
            TouchedSide = string.Empty;
            IsQaContact = false;
            ResetGrabBaseline();
            if (avatar != null)
            {
                EnsureRenderers();
                EnsureCollisionProxies(CalculateAvatarBounds());
            }
            Status = avatar == null ? "Waiting for avatar" : "XR input ready";
        }

        public void SetSemanticInteractionLock(bool locked)
        {
            semanticInteractionLock = locked;
            if (locked)
            {
                leftHand.grabbing = false;
                rightHand.grabbing = false;
                ResetGrabBaseline();
            }
        }

        public void SetInputEnabled(bool enabled)
        {
            inputEnabled = enabled;
            if (!enabled)
            {
                leftHand.grabbing = false;
                rightHand.grabbing = false;
                ResetTouchState(true);
                ClearFeedback();
                Status = "Touch interaction disabled";
            }
        }

        private void ResetTouchState(bool notify)
        {
            var wasTouched = previousTouched || leftHand.touched || rightHand.touched;
            leftHand.touched = false;
            rightHand.touched = false;
            leftHand.nearGrab = false;
            rightHand.nearGrab = false;
            previousTouched = false;
            TouchedSide = string.Empty;
            IsQaContact = false;
            if (notify && wasTouched)
            {
                TouchStateChanged?.Invoke(false);
            }
        }

        private void Update()
        {
            if (!inputEnabled || avatar == null)
            {
                return;
            }

            EnsureRenderers();
            trackingSpace = QuestXrInputUtility.ResolveTrackingSpace(trackingSpace);
            ReadHand(leftHand);
            ReadHand(rightHand);

            var bounds = CalculateAvatarBounds();
            EnsureCollisionProxies(bounds);
            UpdateTouchState(bounds);
            if (!semanticInteractionLock)
            {
                HandleActionButtons();
                UpdateGrabState();
            }
            else
            {
                leftHand.grabbing = false;
                rightHand.grabbing = false;
            }
            UpdateStatus();
            UpdateFeedback();
        }

        private void ReadHand(HandState hand)
        {
            var hadTrackedPose = hand.trackedHand && hand.available;
            hand.previousPrimary = hand.primary;
            hand.previousSecondary = hand.secondary;
            hand.primary = false;
            hand.secondary = false;
            hand.grabHeld = false;
            hand.trackedHand = false;
            hand.trackingGrace = false;
            hand.available = false;
            if (TryReadTrackedHand(hand))
            {
                return;
            }

            hand.device = InputDevices.GetDeviceAtXRNode(hand.node);

            if (!hand.device.isValid)
            {
                PreserveTrackedPoseDuringGrace(hand, hadTrackedPose);
                return;
            }

            Vector3 localPosition;
            Quaternion localRotation;
            if (!hand.device.TryGetFeatureValue(CommonUsages.devicePosition, out localPosition) ||
                !hand.device.TryGetFeatureValue(CommonUsages.deviceRotation, out localRotation))
            {
                PreserveTrackedPoseDuringGrace(hand, hadTrackedPose);
                return;
            }

            hand.position = trackingSpace == null
                ? localPosition
                : trackingSpace.TransformPoint(localPosition);
            hand.rotation = trackingSpace == null
                ? localRotation
                : trackingSpace.rotation * localRotation;
            hand.primary = ReadButton(hand.device, CommonUsages.primaryButton);
            hand.secondary = ReadButton(hand.device, CommonUsages.secondaryButton);
            hand.grabHeld = ReadButton(hand.device, CommonUsages.gripButton, CommonUsages.grip) ||
                             ReadButton(hand.device, CommonUsages.triggerButton, CommonUsages.trigger, grabThreshold);
            hand.hasIndexTip = false;
            hand.hasThumbTip = false;
            hand.available = true;
        }

        private void PreserveTrackedPoseDuringGrace(HandState hand, bool hadTrackedPose)
        {
            if (!hadTrackedPose || !IsTrackingGraceActive(Time.unscaledTime, hand.lastTrackedPoseAt, trackedPoseGraceSeconds))
            {
                hand.hasIndexTip = false;
                hand.hasThumbTip = false;
                return;
            }

            hand.trackedHand = true;
            hand.trackingGrace = true;
            hand.available = true;
        }

        private bool TryReadTrackedHand(HandState state)
        {
            var subsystem = FindHandSubsystem();
            if (subsystem == null || !subsystem.running)
            {
                return false;
            }

            var trackedHand = state.node == XRNode.LeftHand ? subsystem.leftHand : subsystem.rightHand;
            if (!trackedHand.isTracked || !trackedHand.GetJoint(XRHandJointID.Palm).TryGetPose(out var palmPose))
            {
                return false;
            }

            state.trackedHand = true;
            state.trackingGrace = false;
            state.position = trackingSpace == null
                ? palmPose.position
                : trackingSpace.TransformPoint(palmPose.position);
            state.rotation = trackingSpace == null
                ? palmPose.rotation
                : trackingSpace.rotation * palmPose.rotation;
            state.hasIndexTip = trackedHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var indexTip);
            state.hasThumbTip = trackedHand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumbTip);
            if (state.hasIndexTip)
            {
                state.indexTip = trackingSpace == null
                    ? indexTip.position
                    : trackingSpace.TransformPoint(indexTip.position);
            }
            if (state.hasThumbTip)
            {
                state.thumbTip = trackingSpace == null
                    ? thumbTip.position
                    : trackingSpace.TransformPoint(thumbTip.position);
            }
            if (state.hasIndexTip && state.hasThumbTip)
            {
                var pinch = UpdatePinchState(
                    state.previousPrimary,
                    Vector3.Distance(indexTip.position, thumbTip.position),
                    pinchEnterDistance,
                    pinchReleaseDistance);
                state.primary = pinch;
                state.grabHeld = pinch;
            }
            state.lastTrackedPoseAt = Time.unscaledTime;
            state.available = true;
            return true;
        }

        public static bool UpdatePinchState(
            bool wasPinching,
            float tipDistance,
            float enterDistance = .032f,
            float releaseDistance = .045f)
        {
            var enter = Mathf.Max(.001f, enterDistance);
            var release = Mathf.Max(enter, releaseDistance);
            return wasPinching ? tipDistance <= release : tipDistance <= enter;
        }

        public static bool IsTrackingGraceActive(float now, float lastTrackedAt, float graceSeconds)
        {
            return graceSeconds > 0f && now >= lastTrackedAt && now - lastTrackedAt <= graceSeconds;
        }

        private XRHandSubsystem FindHandSubsystem()
        {
            for (var index = 0; index < handSubsystems.Count; index++)
            {
                if (handSubsystems[index] != null && handSubsystems[index].running) return handSubsystems[index];
            }

            handSubsystems.Clear();
            SubsystemManager.GetSubsystems(handSubsystems);
            for (var index = 0; index < handSubsystems.Count; index++)
            {
                if (handSubsystems[index] != null && handSubsystems[index].running) return handSubsystems[index];
            }
            return null;
        }

        private static bool ReadButton(InputDevice device, InputFeatureUsage<bool> usage)
        {
            bool value;
            return device.TryGetFeatureValue(usage, out value) && value;
        }

        private static bool ReadButton(
            InputDevice device,
            InputFeatureUsage<bool> digitalUsage,
            InputFeatureUsage<float> analogUsage,
            float threshold = 0.5f)
        {
            if (ReadButton(device, digitalUsage))
            {
                return true;
            }

            float value;
            return device.TryGetFeatureValue(analogUsage, out value) && value >= threshold;
        }

        public void SimulateContactForQa(string source)
        {
            if (avatar == null)
            {
                return;
            }

            TouchedSide = string.IsNullOrWhiteSpace(source) ? nameof(rightHand) : source;
            IsQaContact = true;
            previousTouched = true;
            TouchStateChanged?.Invoke(true);
        }

        private void UpdateTouchState(Bounds bounds)
        {
            IsQaContact = false;
            leftHand.touched = leftHand.available && IsHandContact(
                bounds,
                leftHand,
                ContactThreshold(leftHand.touched, touchDistance, touchReleaseDistanceMultiplier));
            rightHand.touched = rightHand.available && IsHandContact(
                bounds,
                rightHand,
                ContactThreshold(rightHand.touched, touchDistance, touchReleaseDistanceMultiplier));
            leftHand.nearGrab = leftHand.available && IsContact(bounds, leftHand.position, grabDistance);
            rightHand.nearGrab = rightHand.available && IsContact(bounds, rightHand.position, grabDistance);

            var touched = leftHand.touched || rightHand.touched;
            if (leftHand.touched && rightHand.touched)
            {
                TouchedSide = "left + right";
            }
            else if (leftHand.touched)
            {
                TouchedSide = "left";
            }
            else if (rightHand.touched)
            {
                TouchedSide = "right";
            }
            else
            {
                TouchedSide = string.Empty;
            }
            if (touched != previousTouched)
            {
                previousTouched = touched;
                TouchStateChanged?.Invoke(touched);
                Debug.Log(touched
                    ? $"[AvatarTouch] Contact began: {TouchedSide}."
                    : "[AvatarTouch] Contact ended.", this);
            }
        }

        public static float ContactThreshold(bool wasTouching, float enterDistance, float releaseMultiplier = 1.35f)
        {
            var enter = Mathf.Max(0f, enterDistance);
            return wasTouching ? enter * Mathf.Max(1f, releaseMultiplier) : enter;
        }

        private void HandleActionButtons()
        {
            // Pinch is a hand-tracking selection/grab gesture, never a preset action button.
            if (rightHand.trackedHand || leftHand.trackedHand)
            {
                return;
            }

            if (rightHand.PrimaryDown)
            {
                avatar.PlayAction("wave");
            }

            if (rightHand.SecondaryDown)
            {
                avatar.TogglePlayback();
            }

            if (leftHand.PrimaryDown)
            {
                avatar.PlayAction("bow");
            }

            if (leftHand.SecondaryDown)
            {
                avatar.ResetTransform();
            }
        }

        private void UpdateGrabState()
        {
            var wasLeftGrabbing = leftHand.grabbing;
            var wasRightGrabbing = rightHand.grabbing;

            leftHand.grabbing = leftHand.available && leftHand.grabHeld && leftHand.nearGrab;
            rightHand.grabbing = rightHand.available && rightHand.grabHeld && rightHand.nearGrab;

            if (wasLeftGrabbing != leftHand.grabbing || wasRightGrabbing != rightHand.grabbing)
            {
                CaptureGrabBaseline();
            }

            if (leftHand.grabbing && rightHand.grabbing)
            {
                ApplyDualGrab();
            }
            else if (leftHand.grabbing)
            {
                ApplySingleGrab(leftHand);
            }
            else if (rightHand.grabbing)
            {
                ApplySingleGrab(rightHand);
            }
        }

        private void CaptureGrabBaseline()
        {
            if (avatar == null)
            {
                return;
            }

            grabBasePosition = avatar.transform.position;
            grabBaseRotation = avatar.transform.rotation;
            grabBaseScale = avatar.transform.localScale;

            if (leftHand.grabbing && rightHand.grabbing)
            {
                dualGrabStartLeft = leftHand.position;
                dualGrabStartRight = rightHand.position;
                dualGrabStartMidpoint = (dualGrabStartLeft + dualGrabStartRight) * 0.5f;
                dualGrabStartDistance = Vector3.Distance(dualGrabStartLeft, dualGrabStartRight);
            }
            else if (leftHand.grabbing)
            {
                singleGrabStartPosition = leftHand.position;
                dualGrabStartDistance = 0f;
            }
            else if (rightHand.grabbing)
            {
                singleGrabStartPosition = rightHand.position;
                dualGrabStartDistance = 0f;
            }
            else
            {
                dualGrabStartDistance = 0f;
            }
        }

        private void ResetGrabBaseline()
        {
            leftHand.grabbing = false;
            rightHand.grabbing = false;
            dualGrabStartDistance = 0f;
        }

        private void ApplySingleGrab(HandState hand)
        {
            avatar.transform.position = grabBasePosition + (hand.position - singleGrabStartPosition);
        }

        private void ApplyDualGrab()
        {
            var currentVector = rightHand.position - leftHand.position;
            var currentDistance = currentVector.magnitude;
            var currentMidpoint = (leftHand.position + rightHand.position) * 0.5f;
            var translation = currentMidpoint - dualGrabStartMidpoint;
            var yaw = SignedYaw(dualGrabStartRight - dualGrabStartLeft, currentVector);

            avatar.transform.position = grabBasePosition + translation;
            avatar.transform.rotation = grabBaseRotation * Quaternion.AngleAxis(yaw, Vector3.up);

            if (dualGrabStartDistance > 0.01f && currentDistance > 0.01f)
            {
                var multiplier = Mathf.Clamp(currentDistance / dualGrabStartDistance, 0.25f, 3f);
                avatar.transform.localScale = grabBaseScale * multiplier;
            }
        }

        private static float SignedYaw(Vector3 from, Vector3 to)
        {
            from.y = 0f;
            to.y = 0f;
            if (from.sqrMagnitude < 0.0001f || to.sqrMagnitude < 0.0001f)
            {
                return 0f;
            }

            return Vector3.SignedAngle(from, to, Vector3.up);
        }

        private void UpdateStatus()
        {
            if (semanticInteractionLock)
            {
                Status = "Semantic hand interaction active";
                return;
            }
            if (!leftHand.available && !rightHand.available)
            {
                Status = "No XR hand/controller";
            }
            else if (leftHand.grabbing && rightHand.grabbing)
            {
                Status = "Two-hand grab: move / rotate / scale";
            }
            else if (leftHand.grabbing || rightHand.grabbing)
            {
                Status = "Grab: drag avatar";
            }
            else if (IsTouched)
            {
                Status = "Touching: " + TouchedSide;
            }
            else
            {
                Status = "XR input ready";
            }
        }

        private void EnsureRenderers()
        {
            if (avatarRenderers.Length > 0 || avatar == null)
            {
                return;
            }

            avatarRenderers = avatar.GetComponentsInChildren<Renderer>(true);
        }

        private Bounds CalculateAvatarBounds()
        {
            var bounds = new Bounds(avatar.transform.position, Vector3.one * 0.5f);
            var hasBounds = false;
            for (var i = 0; i < avatarRenderers.Length; i++)
            {
                var renderer = avatarRenderers[i];
                if (renderer == null)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return bounds;
        }

        public static bool IsWithinDistance(Bounds bounds, Vector3 point, float distance)
        {
            var safeDistance = Mathf.Max(0f, distance);
            return bounds.SqrDistance(point) <= safeDistance * safeDistance;
        }

        private bool IsContact(Bounds bounds, Vector3 point, float distance)
        {
            return TryGetContactRegion(point, distance, out _) || IsWithinDistance(bounds, point, distance);
        }

        private bool IsHandContact(Bounds bounds, HandState hand, float distance)
        {
            if (IsContact(bounds, hand.position, distance))
            {
                return true;
            }

            var fingertipDistance = Mathf.Clamp(distance * .65f, .018f, .05f);
            return hand.hasIndexTip && IsContact(bounds, hand.indexTip, fingertipDistance) ||
                   hand.hasThumbTip && IsContact(bounds, hand.thumbTip, fingertipDistance);
        }

        public bool TryGetContactRegion(Vector3 point, float radius, out AvatarContactRegion region)
        {
            region = AvatarContactRegion.None;
            if (avatar == null)
            {
                return false;
            }

            var count = Physics.OverlapSphereNonAlloc(point, Mathf.Max(.01f, radius), contactHits, ~0, QueryTriggerInteraction.Collide);
            var bestPriority = 0;
            for (var i = 0; i < count; i++)
            {
                var collider = contactHits[i];
                if (collider == null || !collider.transform.IsChildOf(avatar.transform))
                {
                    continue;
                }

                var proxy = collider.GetComponent<AvatarContactProxy>();
                var candidate = proxy == null ? AvatarContactRegion.Body : proxy.Region;
                var priority = candidate == AvatarContactRegion.Face ? 4 : candidate == AvatarContactRegion.Head ? 3 : candidate == AvatarContactRegion.Hand ? 2 : 1;
                if (priority > bestPriority)
                {
                    bestPriority = priority;
                    region = candidate;
                }
            }

            return bestPriority > 0;
        }

        private void EnsureCollisionProxies(Bounds bounds)
        {
            if (collisionGeometryReady || avatar == null)
            {
                return;
            }

            collisionProxyRoot = new GameObject("Avatar Contact Proxies");
            collisionProxyRoot.transform.SetParent(avatar.transform, false);
            var bodyRadius = Mathf.Clamp(Mathf.Min(bounds.size.x, bounds.size.z) * .28f, .08f, .24f);
            CreateCapsuleProxy("Body", AvatarContactRegion.Body, bounds.center + Vector3.up * bounds.size.y * -.03f, bodyRadius, Mathf.Max(bodyRadius * 2f, bounds.size.y * .72f));
            var headRadius = Mathf.Clamp(bounds.size.y * .12f, .08f, .18f);
            var headBone = FindAvatarBone("head", "\u982D", "\u5934");
            var headCenter = headBone == null
                ? new Vector3(bounds.center.x, bounds.min.y + bounds.size.y * .79f, bounds.center.z)
                : headBone.position + avatar.transform.up * headRadius * .25f;
            CreateSphereProxy("Head", AvatarContactRegion.Head, headCenter, headRadius, headBone);
            var faceCenter = headCenter + avatar.transform.forward * headRadius * .72f;
            CreateSphereProxy("Face", AvatarContactRegion.Face, faceCenter, headRadius * .72f, headBone);
            var handRadius = Mathf.Clamp(bounds.size.y * .035f, .035f, .075f);
            var leftBone = FindAvatarBone("lefthand", "hand_l", "\u5DE6\u624B\u9996");
            var rightBone = FindAvatarBone("righthand", "hand_r", "\u53F3\u624B\u9996");
            if (leftBone != null)
            {
                CreateSphereProxy("Left Hand", AvatarContactRegion.Hand, leftBone.position, handRadius, leftBone);
            }
            if (rightBone != null)
            {
                CreateSphereProxy("Right Hand", AvatarContactRegion.Hand, rightBone.position, handRadius, rightBone);
            }
            collisionGeometryReady = true;
        }

        private Transform FindAvatarBone(params string[] names)
        {
            var all = avatar.GetComponentsInChildren<MMDBoneTransform>(true);
            for (var pass = 0; pass < 2; pass++)
            {
                for (var nameIndex = 0; nameIndex < names.Length; nameIndex++)
                {
                    var wanted = NormalizeBoneName(names[nameIndex]);
                    for (var boneIndex = 0; boneIndex < all.Length; boneIndex++)
                    {
                        if (all[boneIndex] == null) continue;
                        var actual = NormalizeBoneName(all[boneIndex].boneName);
                        if (pass == 0 ? actual == wanted : actual.Contains(wanted))
                        {
                            return all[boneIndex].transform;
                        }
                    }
                }
            }
            return null;
        }

        private static string NormalizeBoneName(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(".", string.Empty);
        }

        private void CreateCapsuleProxy(string objectName, AvatarContactRegion region, Vector3 worldCenter, float radius, float height)
        {
            var proxyObject = new GameObject("Avatar Contact " + objectName);
            proxyObject.transform.SetParent(collisionProxyRoot.transform, true);
            proxyObject.transform.position = worldCenter;
            var collider = proxyObject.AddComponent<CapsuleCollider>();
            collider.direction = 1;
            collider.radius = radius;
            collider.height = height;
            collider.isTrigger = true;
            var proxy = proxyObject.AddComponent<AvatarContactProxy>();
            proxy.Region = region;
        }

        private void CreateSphereProxy(
            string objectName,
            AvatarContactRegion region,
            Vector3 worldCenter,
            float radius,
            Transform followTarget = null)
        {
            var proxyObject = new GameObject("Avatar Contact " + objectName);
            proxyObject.transform.SetParent(collisionProxyRoot.transform, true);
            proxyObject.transform.position = worldCenter;
            var collider = proxyObject.AddComponent<SphereCollider>();
            collider.radius = radius;
            collider.isTrigger = true;
            var proxy = proxyObject.AddComponent<AvatarContactProxy>();
            proxy.Region = region;
            proxy.SetFollowTarget(followTarget);
        }

        private void ClearCollisionProxies()
        {
            if (collisionProxyRoot != null)
            {
                if (Application.isPlaying) Destroy(collisionProxyRoot);
                else DestroyImmediate(collisionProxyRoot);
            }
            collisionProxyRoot = null;
            collisionGeometryReady = false;
        }

        private void UpdateFeedback()
        {
            if (!enableTouchFeedback)
            {
                if (feedbackActive || feedbackGrabbing) ClearFeedback();
                return;
            }

            var active = IsTouched;
            var grabbing = IsGrabbing;
            if (active == feedbackActive && grabbing == feedbackGrabbing)
            {
                return;
            }

            feedbackActive = active;
            feedbackGrabbing = grabbing;
            if (!active)
            {
                ClearFeedback();
                return;
            }

            var color = grabbing
                ? new Color(1f, 0.65f, 0.2f, 1f)
                : new Color(0.45f, 0.85f, 1f, 1f);
            touchPropertyBlock.Clear();
            touchPropertyBlock.SetColor("_BaseColor", color);
            touchPropertyBlock.SetColor("_Color", color);
            for (var i = 0; i < avatarRenderers.Length; i++)
            {
                var renderer = avatarRenderers[i];
                if (renderer != null)
                {
                    renderer.SetPropertyBlock(touchPropertyBlock);
                }
            }
        }

        private void ClearFeedback()
        {
            feedbackActive = false;
            feedbackGrabbing = false;
            for (var i = 0; i < avatarRenderers.Length; i++)
            {
                var renderer = avatarRenderers[i];
                if (renderer != null)
                {
                    renderer.SetPropertyBlock(null);
                }
            }
        }

        private void OnDisable()
        {
            ResetTouchState(true);
            ClearFeedback();
        }

        private void OnDestroy()
        {
            ClearCollisionProxies();
        }
    }

    public enum AvatarContactRegion
    {
        None,
        Body,
        Head,
        Face,
        Hand
    }

    internal sealed class AvatarContactProxy : MonoBehaviour
    {
        public AvatarContactRegion Region;
        private Transform followTarget;
        private Vector3 followOffset;

        public void SetFollowTarget(Transform target)
        {
            followTarget = target;
            followOffset = target == null ? Vector3.zero : target.InverseTransformPoint(transform.position);
        }

        private void LateUpdate()
        {
            if (followTarget != null)
            {
                transform.position = followTarget.TransformPoint(followOffset);
            }
        }
    }
}
