using System;
using System.Collections.Generic;
using UMT;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

namespace QuestMmdPlayer
{
    public enum HumanInteractionKind { None, Handshake, HeadPat, CheekPinch, BodyTouch }

    [DisallowMultipleComponent]
    // MMD/VMD writes the base pose first (10900); touch reactions are a
    // deliberate post-process so a real contact cannot be overwritten by the
    // controller's action pose in the same frame.
    [DefaultExecutionOrder(11100)]
    public sealed class AvatarHumanInteraction : MonoBehaviour
    {
        [SerializeField] bool inputEnabled = true;
        [SerializeField] bool enableBoneReactions = true;
        [SerializeField] bool enableMorphReactions = true;
        [SerializeField] float pinchDistance = .035f;
        [SerializeField, Range(.1f, .35f)] float handshakeHoldSeconds = .18f;
        [SerializeField] float handshakeTargetSmoothing = 14f;
        [SerializeField, Range(.75f, .995f)] float maxArmStretch = .98f;
        [SerializeField, Range(.12f, .8f)] float reactionEnterSeconds = .34f;
        [SerializeField, Range(.18f, 1.2f)] float reactionExitSeconds = .52f;
        [SerializeField, Range(.01f, .09f)] float maximumPhysicalYield = .055f;
        [SerializeField] Transform trackingSpace;

        readonly List<XRHandSubsystem> subsystems = new List<XRHandSubsystem>();
        readonly HandData left = new HandData(XRNode.LeftHand);
        readonly HandData right = new HandData(XRNode.RightHand);
        readonly List<Morph> morphs = new List<Morph>();
        AvatarController avatar;
        AvatarTouchInteraction touch;
        BoneSet bones;
        HumanInteractionKind current;
        HumanInteractionKind fadeKind;
        float stateTime;
        float fade;
        float fadeVelocity;
        float simulationUntil;
        HumanInteractionKind simulationKind;
        Vector3 target;
        bool scaleChanged;
        bool localReactionsEnabled = true;
        HumanInteractionKind backendReactionKind;
        float backendReactionUntil;
        HumanInteractionKind trackedContactKind;
        AvatarContactRegion trackedContactRegion;
        float trackedContactUntil;
        Vector3 trackedContactTarget;
        Vector3 trackedContactPush;
        Vector3 smoothedContactPush;
        Vector3 contactPushVelocity;
        bool handshakeUsesRightArm;
        bool hasHandshakeArm;
        Vector3 smoothedHandshakeTarget;
        bool hasSmoothedHandshakeTarget;
        bool reactionPoseApplied;
        bool currentInteractionIsPhysical;

        public event Action<HumanInteractionKind> InteractionChanged;
        public event Action<HumanInteractionKind> PhysicalInteractionChanged;
        public AvatarController Avatar => avatar;
        public HumanInteractionKind CurrentInteraction => current;
        public bool HasSemanticContact => current != HumanInteractionKind.None || simulationUntil > Time.unscaledTime;
        public string Status { get; private set; } = "No XR hand tracking/controller";
        public bool HasHeadBone => bones != null && bones.head != null;
        public bool HasHandBones => bones != null && (bones.leftHand != null || bones.rightHand != null);
        public int MatchedMorphCount => morphs.Count;
        public bool LocalReactionsEnabled => localReactionsEnabled;
        public HumanInteractionKind PendingBackendReaction => backendReactionUntil > Time.unscaledTime ? backendReactionKind : HumanInteractionKind.None;

        public bool TryGetContactRegionSwept(
            Vector3 from,
            Vector3 to,
            float radius,
            out AvatarContactRegion region,
            out Vector3 contactPoint)
        {
            EnsureTouchSubscription();
            region = AvatarContactRegion.None;
            contactPoint = to;
            return touch != null && touch.TryGetContactRegionSwept(from, to, radius, out region, out contactPoint);
        }

        public bool TryGetPenetrationCorrection(
            Collider handCollider,
            out Vector3 correction,
            out AvatarContactRegion region)
        {
            EnsureTouchSubscription();
            correction = Vector3.zero;
            region = AvatarContactRegion.None;
            return touch != null && touch.TryGetPenetrationCorrection(handCollider, out correction, out region);
        }

        public bool TryGetContactSurface(
            Vector3 probePoint,
            float radius,
            AvatarContactRegion region,
            out Vector3 surfacePoint,
            out Vector3 surfaceNormal)
        {
            EnsureTouchSubscription();
            surfacePoint = probePoint;
            surfaceNormal = Vector3.zero;
            return touch != null && touch.TryGetContactSurface(
                probePoint,
                radius,
                region,
                out surfacePoint,
                out surfaceNormal);
        }

        sealed class HandData
        {
            public readonly XRNode node;
            public bool valid, pinch, grip;
            public Vector3 palm, index, thumb, pinchPoint;
            public Quaternion rotation = Quaternion.identity;
            public HandData(XRNode node) { this.node = node; }
            public void Reset() { valid = pinch = grip = false; palm = index = thumb = pinchPoint = Vector3.zero; rotation = Quaternion.identity; }
        }

        sealed class BoneSet
        {
            public Transform upperBody, head, leftUpper, leftLower, leftHand, rightUpper, rightLower, rightHand;
            public Transform leftUpperLeg, leftLowerLeg, leftFoot, rightUpperLeg, rightLowerLeg, rightFoot;
            public Vector3 headScale = Vector3.one;
            public Quaternion upperBodyRotation, headRotation, leftUpperRotation, leftLowerRotation, leftHandRotation;
            public Quaternion rightUpperRotation, rightLowerRotation, rightHandRotation;
            public Quaternion leftUpperLegRotation, leftLowerLegRotation, leftFootRotation;
            public Quaternion rightUpperLegRotation, rightLowerLegRotation, rightFootRotation;
            public Quaternion transitionUpperBody, transitionHead, transitionLeftUpper, transitionLeftLower, transitionLeftHand;
            public Quaternion transitionRightUpper, transitionRightLower, transitionRightHand;
            public Quaternion transitionLeftUpperLeg, transitionLeftLowerLeg, transitionLeftFoot;
            public Quaternion transitionRightUpperLeg, transitionRightLowerLeg, transitionRightFoot;
            public float transitionClock;
            public bool transitionActive;

            public void CapturePose()
            {
                if (upperBody != null) upperBodyRotation = upperBody.localRotation;
                if (head != null) headRotation = head.localRotation;
                if (leftUpper != null) leftUpperRotation = leftUpper.localRotation;
                if (leftLower != null) leftLowerRotation = leftLower.localRotation;
                if (leftHand != null) leftHandRotation = leftHand.localRotation;
                if (rightUpper != null) rightUpperRotation = rightUpper.localRotation;
                if (rightLower != null) rightLowerRotation = rightLower.localRotation;
                if (rightHand != null) rightHandRotation = rightHand.localRotation;
                if (leftUpperLeg != null) leftUpperLegRotation = leftUpperLeg.localRotation;
                if (leftLowerLeg != null) leftLowerLegRotation = leftLowerLeg.localRotation;
                if (leftFoot != null) leftFootRotation = leftFoot.localRotation;
                if (rightUpperLeg != null) rightUpperLegRotation = rightUpperLeg.localRotation;
                if (rightLowerLeg != null) rightLowerLegRotation = rightLowerLeg.localRotation;
                if (rightFoot != null) rightFootRotation = rightFoot.localRotation;
            }

            public void ResetArms()
            {
                if (leftUpper != null) leftUpper.localRotation = leftUpperRotation;
                if (leftLower != null) leftLower.localRotation = leftLowerRotation;
                if (leftHand != null) leftHand.localRotation = leftHandRotation;
                if (rightUpper != null) rightUpper.localRotation = rightUpperRotation;
                if (rightLower != null) rightLower.localRotation = rightLowerRotation;
                if (rightHand != null) rightHand.localRotation = rightHandRotation;
            }

            public void ResetLegs()
            {
                if (leftUpperLeg != null) leftUpperLeg.localRotation = leftUpperLegRotation;
                if (leftLowerLeg != null) leftLowerLeg.localRotation = leftLowerLegRotation;
                if (leftFoot != null) leftFoot.localRotation = leftFootRotation;
                if (rightUpperLeg != null) rightUpperLeg.localRotation = rightUpperLegRotation;
                if (rightLowerLeg != null) rightLowerLeg.localRotation = rightLowerLegRotation;
                if (rightFoot != null) rightFoot.localRotation = rightFootRotation;
            }

            public void ResetPose()
            {
                if (upperBody != null) upperBody.localRotation = upperBodyRotation;
                if (head != null) { head.localRotation = headRotation; head.localScale = headScale; }
                ResetArms();
                ResetLegs();
            }

            public void CaptureTransitionPose()
            {
                if (upperBody != null) transitionUpperBody = upperBody.localRotation;
                if (head != null) transitionHead = head.localRotation;
                if (leftUpper != null) transitionLeftUpper = leftUpper.localRotation;
                if (leftLower != null) transitionLeftLower = leftLower.localRotation;
                if (leftHand != null) transitionLeftHand = leftHand.localRotation;
                if (rightUpper != null) transitionRightUpper = rightUpper.localRotation;
                if (rightLower != null) transitionRightLower = rightLower.localRotation;
                if (rightHand != null) transitionRightHand = rightHand.localRotation;
                if (leftUpperLeg != null) transitionLeftUpperLeg = leftUpperLeg.localRotation;
                if (leftLowerLeg != null) transitionLeftLowerLeg = leftLowerLeg.localRotation;
                if (leftFoot != null) transitionLeftFoot = leftFoot.localRotation;
                if (rightUpperLeg != null) transitionRightUpperLeg = rightUpperLeg.localRotation;
                if (rightLowerLeg != null) transitionRightLowerLeg = rightLowerLeg.localRotation;
                if (rightFoot != null) transitionRightFoot = rightFoot.localRotation;
                transitionClock = 0f;
                transitionActive = true;
            }

            public void ApplyTransition(float deltaTime, float seconds)
            {
                if (!transitionActive) return;
                transitionClock += Mathf.Max(0f, deltaTime);
                var amount = NaturalMotionTransition.Smooth01(transitionClock / Mathf.Max(.01f, seconds));
                BlendFrom(upperBody, transitionUpperBody, amount);
                BlendFrom(head, transitionHead, amount);
                BlendFrom(leftUpper, transitionLeftUpper, amount);
                BlendFrom(leftLower, transitionLeftLower, amount);
                BlendFrom(leftHand, transitionLeftHand, amount);
                BlendFrom(rightUpper, transitionRightUpper, amount);
                BlendFrom(rightLower, transitionRightLower, amount);
                BlendFrom(rightHand, transitionRightHand, amount);
                BlendFrom(leftUpperLeg, transitionLeftUpperLeg, amount);
                BlendFrom(leftLowerLeg, transitionLeftLowerLeg, amount);
                BlendFrom(leftFoot, transitionLeftFoot, amount);
                BlendFrom(rightUpperLeg, transitionRightUpperLeg, amount);
                BlendFrom(rightLowerLeg, transitionRightLowerLeg, amount);
                BlendFrom(rightFoot, transitionRightFoot, amount);
                if (amount >= 1f) transitionActive = false;
            }

            private static void BlendFrom(Transform bone, Quaternion from, float amount)
            {
                if (bone != null) bone.localRotation = Quaternion.Slerp(from, bone.localRotation, amount);
            }
        }

        struct Morph
        {
            public SkinnedMeshRenderer renderer;
            public int index;
            public int kind;
            public float baseWeight;
        }

        void Awake()
        {
            EnsureTouchSubscription();
        }

        void EnsureTouchSubscription()
        {
            var nextTouch = GetComponent<AvatarTouchInteraction>();
            if (touch == nextTouch)
            {
                return;
            }
            if (touch != null)
            {
                touch.TouchStateChanged -= HandleTouchStateChanged;
            }
            touch = nextTouch;
            if (touch != null)
            {
                touch.TouchStateChanged += HandleTouchStateChanged;
            }
        }

        public void Bind(AvatarController targetAvatar)
        {
            EnsureTouchSubscription();
            UnlockTouch();
            RestorePose();
            RestoreMorphs();
            avatar = targetAvatar;
            current = fadeKind = HumanInteractionKind.None;
            fade = stateTime = 0f;
            fadeVelocity = 0f;
            scaleChanged = false;
            backendReactionKind = HumanInteractionKind.None;
            backendReactionUntil = 0f;
            trackedContactKind = HumanInteractionKind.None;
            trackedContactRegion = AvatarContactRegion.None;
            trackedContactUntil = 0f;
            trackedContactTarget = Vector3.zero;
            trackedContactPush = smoothedContactPush = contactPushVelocity = Vector3.zero;
            hasHandshakeArm = false;
            hasSmoothedHandshakeTarget = false;
            reactionPoseApplied = false;
            currentInteractionIsPhysical = false;
            target = Vector3.zero;
            morphs.Clear();
            bones = null;
            if (avatar == null) { Status = "Waiting for avatar"; return; }
            bones = FindBones(avatar);
            bones.headScale = bones.head == null ? Vector3.one : bones.head.localScale;
            bones.CapturePose();
            CacheMorphs();
            Status = "XR hand tracking/controller ready";
            Debug.Log($"[HumanInteraction] Bound avatar; head={HasHeadBone}, hands={HasHandBones}, morphs={morphs.Count}.", this);
        }

        public void SetInputEnabled(bool enabled)
        {
            inputEnabled = enabled;
            if (!enabled) { SetState(HumanInteractionKind.None, false); UnlockTouch(); Status = "Human interaction disabled"; }
        }

        public void SetLocalReactionsEnabled(bool enabled)
        {
            localReactionsEnabled = enabled;
            if (!enabled)
            {
                fade = 0f;
                reactionPoseApplied = false;
                RestorePose();
                RestoreMorphs();
            }
        }

        public void PlayReaction(HumanInteractionKind kind, float seconds = 2f)
        {
            if (avatar == null || kind == HumanInteractionKind.None) return;
            backendReactionKind = kind;
            backendReactionUntil = Time.unscaledTime + Mathf.Max(.25f, seconds);
            fadeKind = kind;
        }

        public void SimulateInteraction(HumanInteractionKind kind, float seconds = 2f)
        {
            if (avatar == null || kind == HumanInteractionKind.None) return;
            simulationKind = kind;
            simulationUntil = Time.unscaledTime + Mathf.Max(.25f, seconds);
            target = SimulatedTarget();
            SetState(kind, false);
        }

        void HandleTouchStateChanged(bool touched)
        {
            if (!touched || avatar == null || current != HumanInteractionKind.None || simulationUntil > Time.unscaledTime)
            {
                return;
            }

            if (touch != null && touch.IsQaContact)
            {
                SimulateInteraction(HumanInteractionKind.Handshake, 1.25f);
                return;
            }

            // Region classification in Detect() chooses head/face/hand; do not force every contact to handshake.
            Debug.Log("[HumanInteraction] Direct contact detected; resolving contact region.", this);
        }

        void Update()
        {
            if (avatar == null) return;
            if (!inputEnabled) { UnlockTouch(); return; }
            trackingSpace = QuestXrInputUtility.ResolveTrackingSpace(trackingSpace);
            Read(left);
            Read(right);
            var proxyContact = trackedContactUntil > Time.unscaledTime ? trackedContactKind : HumanInteractionKind.None;
            var simulated = simulationUntil > Time.unscaledTime;
            var detected = simulated ? simulationKind : proxyContact != HumanInteractionKind.None ? proxyContact : Detect();
            var detectedFromPhysicalContact = !simulated && detected != HumanInteractionKind.None;
            if (proxyContact != HumanInteractionKind.None) target = trackedContactTarget;
            var sourceChanged = detected == current && detected != HumanInteractionKind.None &&
                detectedFromPhysicalContact != currentInteractionIsPhysical;
            if (detected != current) stateTime += Time.unscaledDeltaTime;
            else stateTime = 0f;
            if (detected == HumanInteractionKind.None && current != HumanInteractionKind.None)
            {
                if (stateTime >= .24f) SetState(HumanInteractionKind.None, false);
            }
            else if (detected != HumanInteractionKind.None && detected != current)
            {
                var activationDelay = detected == HumanInteractionKind.Handshake
                    ? handshakeHoldSeconds
                    : detected == HumanInteractionKind.HeadPat
                        ? .24f
                        : .14f;
                if (stateTime >= activationDelay) SetState(detected, detectedFromPhysicalContact);
            }
            else if (sourceChanged)
            {
                SetState(detected, detectedFromPhysicalContact);
            }
            if (touch != null) touch.SetSemanticInteractionLock(detected != HumanInteractionKind.None || current != HumanInteractionKind.None);
            Status = current != HumanInteractionKind.None ? Name(current) + " active" : detected != HumanInteractionKind.None ? "Contact candidate: " + Name(detected) : left.valid || right.valid ? "Hands ready | head:" + (HasHeadBone ? "yes" : "no") + " hand:" + (HasHandBones ? "yes" : "no") + " morphs:" + morphs.Count : "No XR hand tracking/controller";
        }

        void LateUpdate()
        {
            if (avatar == null || bones == null) return;
            var backendActive = backendReactionUntil > Time.unscaledTime;
            var desired = SelectDesiredInteraction(
                current,
                currentInteractionIsPhysical,
                backendReactionKind,
                backendActive,
                localReactionsEnabled);
            if (desired != HumanInteractionKind.None) fadeKind = desired;
            fade = NaturalMotionTransition.UpdateWeight(
                fade,
                desired == HumanInteractionKind.None ? 0f : 1f,
                ref fadeVelocity,
                reactionEnterSeconds,
                reactionExitSeconds,
                Time.unscaledDeltaTime);
            smoothedContactPush = Vector3.SmoothDamp(
                smoothedContactPush,
                currentInteractionIsPhysical ? trackedContactPush : Vector3.zero,
                ref contactPushVelocity,
                currentInteractionIsPhysical ? .09f : .22f,
                Mathf.Infinity,
                Time.unscaledDeltaTime);
            if (fade <= .001f)
            {
                if (reactionPoseApplied || scaleChanged)
                {
                    RestorePose();
                    RestoreMorphs();
                    reactionPoseApplied = false;
                }
                return;
            }
            var kind = desired == HumanInteractionKind.None ? fadeKind : desired;
            if (enableMorphReactions) ApplyMorphs(kind, fade);
            if (enableBoneReactions) ApplyBones(kind, fade);
            bones.ApplyTransition(Time.unscaledDeltaTime, .38f);
            reactionPoseApplied = true;
        }

        /// <summary>
        /// Physical contact is the authoritative observation. A backend
        /// reaction may remain queued, but it must not hide a hand that is
        /// currently touching the avatar.
        /// </summary>
        public static HumanInteractionKind SelectDesiredInteraction(
            HumanInteractionKind physicalKind,
            bool physicalActive,
            HumanInteractionKind backendKind,
            bool backendActive,
            bool localReactionsEnabled)
        {
            if (physicalActive && physicalKind != HumanInteractionKind.None)
            {
                return physicalKind;
            }
            if (backendActive && backendKind != HumanInteractionKind.None)
            {
                return backendKind;
            }
            return localReactionsEnabled ? physicalKind : HumanInteractionKind.None;
        }

        void Read(HandData data)
        {
            data.Reset();
            var subsystem = FindSubsystem();
            if (subsystem != null && subsystem.running)
            {
                var hand = data.node == XRNode.LeftHand ? subsystem.leftHand : subsystem.rightHand;
                if (hand.isTracked)
                {
                    var hasPalm = TryJoint(hand, XRHandJointID.Palm, out var palmPose);
                    if (!hasPalm)
                    {
                        hasPalm = TryJoint(hand, XRHandJointID.Wrist, out palmPose);
                    }

                    if (hasPalm)
                    {
                        data.valid = true;
                        data.palm = World(palmPose.position);
                        data.rotation = WorldRotation(palmPose.rotation);
                        var hasIndex = TryJoint(hand, XRHandJointID.IndexTip, out var indexPose);
                        var hasThumb = TryJoint(hand, XRHandJointID.ThumbTip, out var thumbPose);
                        if (hasIndex && hasThumb)
                        {
                            data.index = World(indexPose.position);
                            data.thumb = World(thumbPose.position);
                            data.pinchPoint = (data.index + data.thumb) * .5f;
                            data.pinch = Vector3.Distance(data.index, data.thumb) <= pinchDistance;
                            data.grip = data.pinch;
                        }
                        else
                        {
                            // Palm/Wrist tracking is enough for head/body contact. Missing
                            // fingertips only disables pinch classification, not the hand.
                            data.index = data.thumb = data.pinchPoint = data.palm;
                            data.pinch = false;
                            data.grip = false;
                        }
                        return;
                    }
                }
            }

            var device = InputDevices.GetDeviceAtXRNode(data.node);
            if (!device.isValid || !device.TryGetFeatureValue(CommonUsages.devicePosition, out var position)) return;
            device.TryGetFeatureValue(CommonUsages.deviceRotation, out var rotation);
            data.valid = true;
            data.palm = trackingSpace == null ? position : trackingSpace.TransformPoint(position);
            data.rotation = trackingSpace == null ? rotation : trackingSpace.rotation * rotation;
            data.pinchPoint = data.palm;
            data.pinch = Analog(device, CommonUsages.trigger) >= .78f;
            data.grip = Button(device, CommonUsages.gripButton, CommonUsages.grip) || Button(device, CommonUsages.triggerButton, CommonUsages.trigger);
        }

        XRHandSubsystem FindSubsystem()
        {
            for (var i = 0; i < subsystems.Count; i++) if (subsystems[i] != null && subsystems[i].running) return subsystems[i];
            subsystems.Clear();
            SubsystemManager.GetSubsystems(subsystems);
            for (var i = 0; i < subsystems.Count; i++) if (subsystems[i] != null && subsystems[i].running) return subsystems[i];
            return null;
        }

        static bool TryJoint(XRHand hand, XRHandJointID id, out Pose pose) { return hand.GetJoint(id).TryGetPose(out pose); }
        Vector3 World(Vector3 p) { return trackingSpace == null ? p : trackingSpace.TransformPoint(p); }
        Quaternion WorldRotation(Quaternion q) { return trackingSpace == null ? q : trackingSpace.rotation * q; }

        HumanInteractionKind Detect()
        {
            // Semantic interactions are fed only by TrackedHandContactRelay.
            // Proximity, pinch state, and controller poses alone cannot create
            // a head pat, cheek pinch, or handshake.
            return HumanInteractionKind.None;
        }

        public void ReportTrackedHandContact(
            AvatarContactRegion region,
            bool pinching,
            Vector3 point,
            Vector3 avatarPush = default)
        {
            var kind = ClassifyPhysicalContact(region, pinching);
            if (kind == HumanInteractionKind.None) return;
            var changed = trackedContactKind != kind;
            trackedContactKind = kind;
            trackedContactRegion = region;
            trackedContactTarget = point;
            if (changed || avatarPush.sqrMagnitude > .0000001f || trackedContactUntil <= Time.unscaledTime)
                trackedContactPush = Vector3.ClampMagnitude(avatarPush, maximumPhysicalYield);
            trackedContactUntil = Time.unscaledTime + .14f;
            if (kind == HumanInteractionKind.Handshake) AcquireHandshakeTarget(point);
            if (changed)
            {
                Debug.Log(
                    "[HumanInteraction] Physical contact region=" + region +
                    " pinching=" + pinching + " semantic=" + Name(kind),
                    this);
            }
        }

        public static HumanInteractionKind ClassifyPhysicalContact(
            AvatarContactRegion region,
            bool pinching)
        {
            if (region == AvatarContactRegion.Face && pinching)
            {
                return HumanInteractionKind.CheekPinch;
            }
            if (region == AvatarContactRegion.Hair ||
                (region == AvatarContactRegion.Head && !pinching))
            {
                return HumanInteractionKind.HeadPat;
            }
            if (region == AvatarContactRegion.Hand)
            {
                return HumanInteractionKind.Handshake;
            }
            if (region == AvatarContactRegion.Face ||
                region == AvatarContactRegion.Body ||
                region == AvatarContactRegion.Limb)
            {
                return HumanInteractionKind.BodyTouch;
            }
            return HumanInteractionKind.None;
        }

        void AcquireHandshakeTarget(Vector3 point)
        {
            if (bones == null) return;
            var rightDistance = bones.rightHand == null ? float.MaxValue : Vector3.Distance(bones.rightHand.position, point);
            var leftDistance = bones.leftHand == null ? float.MaxValue : Vector3.Distance(bones.leftHand.position, point);
            var useRight = bones.rightHand != null && (bones.leftHand == null || rightDistance <= leftDistance);
            if (!hasHandshakeArm || handshakeUsesRightArm != useRight)
                hasSmoothedHandshakeTarget = false;
            handshakeUsesRightArm = useRight;
            hasHandshakeArm = bones.rightHand != null || bones.leftHand != null;
            target = point;
        }

        void SetState(HumanInteractionKind next, bool physicalContact)
        {
            var nextIsPhysical = next != HumanInteractionKind.None && physicalContact;
            if (current == next && currentInteractionIsPhysical == nextIsPhysical)
            {
                return;
            }
            var kindChanged = current != next;
            var previousWasPhysical = currentInteractionIsPhysical;
            if (kindChanged && current != HumanInteractionKind.None && next != HumanInteractionKind.None && bones != null)
                bones.CaptureTransitionPose();
            current = next;
            currentInteractionIsPhysical = nextIsPhysical;
            if (kindChanged && next != HumanInteractionKind.None)
            {
                fadeKind = next;
                stateTime = 0f;
                if (localReactionsEnabled && avatar != null) avatar.SetEmotion(next == HumanInteractionKind.CheekPinch ? "shy" : "happy");
            }
            else if (kindChanged && localReactionsEnabled && avatar != null) avatar.SetEmotion("neutral");
            if (kindChanged && next == HumanInteractionKind.Handshake)
            {
                smoothedHandshakeTarget = target;
                hasSmoothedHandshakeTarget = target != Vector3.zero;
            }
            else if (kindChanged && next == HumanInteractionKind.None)
            {
                hasHandshakeArm = false;
                hasSmoothedHandshakeTarget = false;
            }
            if (kindChanged) InteractionChanged?.Invoke(next);
            if (previousWasPhysical) PhysicalInteractionChanged?.Invoke(HumanInteractionKind.None);
            if (currentInteractionIsPhysical) PhysicalInteractionChanged?.Invoke(next);
            if (next != HumanInteractionKind.None) Debug.Log($"[HumanInteraction] Started {Name(next)}.", this);
        }

        Vector3 SimulatedTarget()
        {
            var hand = bones == null ? null : bones.rightHand != null ? bones.rightHand : bones.leftHand;
            if (hand == null) return avatar.transform.position + avatar.transform.forward * .35f;
            var toward = Camera.main == null ? -avatar.transform.forward : (Camera.main.transform.position - hand.position).normalized;
            return hand.position + toward * .28f;
        }

        void ApplyBones(HumanInteractionKind kind, float amount)
        {
            var settle = Mathf.Sin(Time.unscaledTime * 2.15f);
            var physical = currentInteractionIsPhysical && backendReactionUntil <= Time.unscaledTime;
            if (bones.upperBody != null)
            {
                var bodyOffset = kind == HumanInteractionKind.HeadPat
                    ? physical ? PhysicalBodyOffset(.35f) : Quaternion.Euler(-1.2f, 0f, 1.4f + settle * .25f)
                    : kind == HumanInteractionKind.CheekPinch
                        ? Quaternion.Euler(0f, -1.8f, -1.2f)
                        : kind == HumanInteractionKind.BodyTouch
                            ? ContactBodyOffset(settle)
                        : Quaternion.identity;
                bones.upperBody.localRotation = Quaternion.Slerp(
                    bones.upperBodyRotation,
                    bones.upperBodyRotation * bodyOffset,
                    amount);
            }

            if (bones.head != null)
            {
                var offset = kind == HumanInteractionKind.HeadPat
                    ? physical ? PhysicalHeadOffset(settle) : Quaternion.Euler(-4.5f + settle * .65f, 0f, 3.5f)
                    : kind == HumanInteractionKind.CheekPinch
                        ? Quaternion.Euler(0f, -4.5f, -3.5f)
                        : kind == HumanInteractionKind.BodyTouch
                            ? ContactHeadOffset(settle)
                        : Quaternion.identity;
                bones.head.localRotation = Quaternion.Slerp(
                    bones.headRotation,
                    bones.headRotation * offset,
                    amount);
                var squeezed = Vector3.Scale(
                    bones.headScale,
                    new Vector3(1.015f, .985f, 1.01f));
                bones.head.localScale = kind == HumanInteractionKind.CheekPinch
                    ? Vector3.Lerp(bones.headScale, squeezed, amount)
                    : bones.headScale;
                scaleChanged = kind == HumanInteractionKind.CheekPinch && amount > .001f;
            }

            bones.ResetArms();
            if (physical && kind == HumanInteractionKind.BodyTouch && trackedContactRegion == AvatarContactRegion.Limb)
            {
                ApplyPhysicalLimbYield(amount);
                return;
            }
            if (kind != HumanInteractionKind.Handshake || target == Vector3.zero) return;
            var rightSide = hasHandshakeArm
                ? handshakeUsesRightArm
                : bones.rightHand != null && (bones.leftHand == null || Vector3.Distance(bones.rightHand.position, target) <= Vector3.Distance(bones.leftHand.position, target));
            var upper = rightSide ? bones.rightUpper : bones.leftUpper;
            var lower = rightSide ? bones.rightLower : bones.leftLower;
            var hand = rightSide ? bones.rightHand : bones.leftHand;
            if (upper == null || lower == null || hand == null) return;
            if (!hasSmoothedHandshakeTarget)
            {
                smoothedHandshakeTarget = target;
                hasSmoothedHandshakeTarget = true;
            }
            var follow = 1f - Mathf.Exp(-Mathf.Max(1f, handshakeTargetSmoothing) * Mathf.Max(Time.unscaledDeltaTime, 1f / 120f));
            smoothedHandshakeTarget = Vector3.Lerp(smoothedHandshakeTarget, target, follow);
            var side = rightSide ? avatar.transform.right : -avatar.transform.right;
            var pole = upper.position - avatar.transform.up + side * .2f;
            SolveTwoBoneIk(upper, lower, hand, smoothedHandshakeTarget, pole, maxArmStretch, amount);
        }

        private Quaternion PhysicalHeadOffset(float settle)
        {
            if (avatar == null || smoothedContactPush.sqrMagnitude <= .0000001f)
                return Quaternion.Euler(-1.1f + settle * .15f, 0f, 0f);
            var local = avatar.transform.InverseTransformDirection(smoothedContactPush);
            var scale = Mathf.Clamp01(local.magnitude / Mathf.Max(.005f, maximumPhysicalYield));
            var direction = local.normalized;
            return Quaternion.Euler(
                Mathf.Clamp(-direction.z * 5.5f - direction.y * 2.2f, -6f, 6f) * scale,
                Mathf.Clamp(direction.x * 5f, -5f, 5f) * scale,
                Mathf.Clamp(-direction.x * 6.5f, -6.5f, 6.5f) * scale);
        }

        private Quaternion PhysicalBodyOffset(float strength)
        {
            if (avatar == null || smoothedContactPush.sqrMagnitude <= .0000001f) return Quaternion.identity;
            var local = avatar.transform.InverseTransformDirection(smoothedContactPush);
            var scale = Mathf.Clamp01(local.magnitude / Mathf.Max(.005f, maximumPhysicalYield)) * strength;
            var direction = local.normalized;
            return Quaternion.Euler(-direction.z * 2.5f * scale, direction.x * 1.5f * scale, -direction.x * 3f * scale);
        }

        private void ApplyPhysicalLimbYield(float amount)
        {
            if (bones == null || smoothedContactPush.sqrMagnitude <= .0000001f) return;
            Transform upper = null, lower = null, endpoint = null;
            var rightSide = false;
            var leg = false;
            var nearest = float.MaxValue;
            ChooseLimb(bones.leftUpper, bones.leftLower, bones.leftHand, false, false, ref upper, ref lower, ref endpoint, ref rightSide, ref leg, ref nearest);
            ChooseLimb(bones.rightUpper, bones.rightLower, bones.rightHand, true, false, ref upper, ref lower, ref endpoint, ref rightSide, ref leg, ref nearest);
            ChooseLimb(bones.leftUpperLeg, bones.leftLowerLeg, bones.leftFoot, false, true, ref upper, ref lower, ref endpoint, ref rightSide, ref leg, ref nearest);
            ChooseLimb(bones.rightUpperLeg, bones.rightLowerLeg, bones.rightFoot, true, true, ref upper, ref lower, ref endpoint, ref rightSide, ref leg, ref nearest);
            if (upper == null || lower == null || endpoint == null) return;
            var destination = endpoint.position + Vector3.ClampMagnitude(smoothedContactPush, maximumPhysicalYield);
            var side = rightSide ? avatar.transform.right : -avatar.transform.right;
            var pole = leg
                ? upper.position + avatar.transform.forward * .28f + side * .08f
                : upper.position - avatar.transform.up + side * .2f;
            SolveTwoBoneIk(upper, lower, endpoint, destination, pole, maxArmStretch, amount * (leg ? .48f : .82f));
        }

        private void ChooseLimb(
            Transform candidateUpper,
            Transform candidateLower,
            Transform candidateEndpoint,
            bool candidateRight,
            bool candidateLeg,
            ref Transform upper,
            ref Transform lower,
            ref Transform endpoint,
            ref bool rightSide,
            ref bool leg,
            ref float nearest)
        {
            if (candidateUpper == null || candidateLower == null || candidateEndpoint == null) return;
            var distance = Vector3.Distance(candidateEndpoint.position, trackedContactTarget);
            if (distance >= nearest) return;
            nearest = distance;
            upper = candidateUpper;
            lower = candidateLower;
            endpoint = candidateEndpoint;
            rightSide = candidateRight;
            leg = candidateLeg;
        }

        public static bool SolveTwoBoneIk(Transform upper, Transform lower, Transform hand, Vector3 destination, Vector3 pole, float stretch = .98f, float weight = 1f)
        {
            if (upper == null || lower == null || hand == null || weight <= 0f) return false;
            var root = upper.position;
            var upperLength = Vector3.Distance(root, lower.position);
            var lowerLength = Vector3.Distance(lower.position, hand.position);
            if (upperLength <= .001f || lowerLength <= .001f) return false;

            var toDestination = destination - root;
            var distance = toDestination.magnitude;
            if (distance <= .001f) return false;
            var direction = toDestination / distance;
            var minimumReach = Mathf.Abs(upperLength - lowerLength) + .001f;
            var maximumReach = (upperLength + lowerLength) * Mathf.Clamp(stretch, .1f, .9995f);
            distance = Mathf.Clamp(distance, minimumReach, Mathf.Max(minimumReach, maximumReach));
            var reachableDestination = root + direction * distance;

            var poleDirection = Vector3.ProjectOnPlane(pole - root, direction);
            if (poleDirection.sqrMagnitude <= .000001f)
                poleDirection = Vector3.ProjectOnPlane(lower.position - root, direction);
            if (poleDirection.sqrMagnitude <= .000001f)
            {
                var fallback = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) < .95f ? Vector3.up : Vector3.right;
                poleDirection = Vector3.ProjectOnPlane(fallback, direction);
            }
            poleDirection.Normalize();

            var along = (upperLength * upperLength + distance * distance - lowerLength * lowerLength) / (2f * distance);
            var height = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - along * along));
            var desiredElbow = root + direction * along + poleDirection * height;
            var upperFrom = lower.position - root;
            var upperTo = desiredElbow - root;
            if (upperFrom.sqrMagnitude <= .000001f || upperTo.sqrMagnitude <= .000001f) return false;
            var clampedWeight = Mathf.Clamp01(weight);
            upper.rotation = Quaternion.Slerp(upper.rotation, Quaternion.FromToRotation(upperFrom, upperTo) * upper.rotation, clampedWeight);

            var lowerFrom = hand.position - lower.position;
            var lowerTo = reachableDestination - lower.position;
            if (lowerFrom.sqrMagnitude <= .000001f || lowerTo.sqrMagnitude <= .000001f) return false;
            lower.rotation = Quaternion.Slerp(lower.rotation, Quaternion.FromToRotation(lowerFrom, lowerTo) * lower.rotation, clampedWeight);
            return true;
        }
        private Quaternion ContactBodyOffset(float settle)
        {
            switch (trackedContactRegion)
            {
                case AvatarContactRegion.Limb:
                    return Quaternion.Euler(0f, 0f, settle * .9f);
                case AvatarContactRegion.Face:
                    return Quaternion.Euler(0f, -1.1f, settle * .45f);
                case AvatarContactRegion.Hair:
                    return Quaternion.Euler(-.8f, 0f, settle * .55f);
                default:
                    return Quaternion.Euler(0f, 0f, settle * .7f);
            }
        }

        private Quaternion ContactHeadOffset(float settle)
        {
            switch (trackedContactRegion)
            {
                case AvatarContactRegion.Face:
                    return Quaternion.Euler(0f, -4.5f, -3.5f + settle * .45f);
                case AvatarContactRegion.Hair:
                    return Quaternion.Euler(-2.2f, 0f, settle * .4f);
                default:
                    return Quaternion.Euler(0f, 0f, settle * .35f);
            }
        }

        void ApplyMorphs(HumanInteractionKind kind, float amount)
        {
            for (var i = 0; i < morphs.Count; i++)
            {
                var morph = morphs[i];
                var add = kind == HumanInteractionKind.Handshake && morph.kind == 0 ? 28f :
                    kind == HumanInteractionKind.HeadPat ? (morph.kind == 0 ? 36f : morph.kind == 1 ? 12f : morph.kind == 3 ? 38f : 0f) :
                    kind == HumanInteractionKind.CheekPinch ? (morph.kind == 1 ? 45f : morph.kind == 2 ? 14f : morph.kind == 3 ? 16f : 0f) :
                    kind == HumanInteractionKind.BodyTouch && trackedContactRegion == AvatarContactRegion.Face && morph.kind == 1 ? 8f : 0f;
                morph.renderer.SetBlendShapeWeight(morph.index, Mathf.Clamp(morph.baseWeight + add * amount, 0f, 100f));
            }
        }

        void RestoreScale() { if (scaleChanged && bones != null && bones.head != null) { bones.head.localScale = bones.headScale; scaleChanged = false; } }
        void RestorePose() { if (bones != null) bones.ResetPose(); scaleChanged = false; }
        void RestoreMorphs() { for (var i = 0; i < morphs.Count; i++) morphs[i].renderer.SetBlendShapeWeight(morphs[i].index, morphs[i].baseWeight); }
        void UnlockTouch() { if (touch != null) touch.SetSemanticInteractionLock(false); }

        static BoneSet FindBones(AvatarController target)
        {
            var result = new BoneSet();
            var all = target.GetComponentsInChildren<MMDBoneTransform>(true);
            result.upperBody = Find(all, "upperbody", "spine", "\u4E0A\u534A\u8EAB", "\u4E0A\u534A\u8EAB2");
            result.head = Find(all, "head", "\u982D", "\u5934");
            result.leftUpper = Find(all, "leftupperarm", "upperarm_l", "\u5DE6\u8155");
            result.leftLower = Find(all, "leftlowerarm", "lowerarm_l", "\u5DE6\u3072\u3058", "\u5DE6\u8098");
            result.leftHand = Find(all, "lefthand", "hand_l", "\u5DE6\u624B\u9996");
            result.rightUpper = Find(all, "rightupperarm", "upperarm_r", "\u53F3\u8155");
            result.rightLower = Find(all, "rightlowerarm", "lowerarm_r", "\u53F3\u3072\u3058", "\u53F3\u8098");
            result.rightHand = Find(all, "righthand", "hand_r", "\u53F3\u624B\u9996");
            result.leftUpperLeg = Find(all, "leftupperleg", "upperleg_l", "\u5DE6\u8DB3");
            result.leftLowerLeg = Find(all, "leftlowerleg", "lowerleg_l", "\u5DE6\u3072\u3056", "\u5DE6\u819D");
            result.leftFoot = Find(all, "leftfoot", "foot_l", "\u5DE6\u8DB3\u9996");
            result.rightUpperLeg = Find(all, "rightupperleg", "upperleg_r", "\u53F3\u8DB3");
            result.rightLowerLeg = Find(all, "rightlowerleg", "lowerleg_r", "\u53F3\u3072\u3056", "\u53F3\u819D");
            result.rightFoot = Find(all, "rightfoot", "foot_r", "\u53F3\u8DB3\u9996");
            return result;
        }

        static Transform Find(MMDBoneTransform[] all, params string[] names)
        {
            for (var pass = 0; pass < 2; pass++)
                for (var n = 0; n < names.Length; n++)
                    for (var i = 0; i < all.Length; i++)
                    {
                        if (all[i] == null) continue;
                        var actual = Normalize(all[i].boneName);
                        var wanted = Normalize(names[n]);
                        if (pass == 0 ? actual == wanted : actual.Contains(wanted)) return all[i].transform;
                    }
            return null;
        }

        static string Normalize(string value) { return string.IsNullOrWhiteSpace(value) ? string.Empty : value.ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(".", string.Empty); }

        void CacheMorphs()
        {
            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var r = 0; r < renderers.Length; r++)
            {
                var mesh = renderers[r].sharedMesh;
                if (mesh == null) continue;
                for (var i = 0; i < mesh.blendShapeCount; i++)
                {
                    var value = Normalize(mesh.GetBlendShapeName(i));
                    var kind = value.Contains("smile") || value.Contains("happy") || value.Contains("\u7B11") || value.Contains("\u306B\u3053") ? 0 :
                        value.Contains("blush") || value.Contains("shy") || value.Contains("\u7167\u308C") || value.Contains("\u8D64\u9762") ? 1 :
                        value.Contains("surprise") || value.Contains("shock") || value.Contains("\u9A5A") || value.Contains("\u60CA") ? 2 :
                        value.Contains("blink") || value.Contains("eyeclose") || value.Contains("eyesclosed") || value.Contains("\u307E\u3070\u305F\u304D") || value.Contains("\u76EE\u9589") ? 3 : -1;
                    if (kind >= 0) morphs.Add(new Morph { renderer = renderers[r], index = i, kind = kind, baseWeight = renderers[r].GetBlendShapeWeight(i) });
                }
            }
        }

        void OnDisable() { UnlockTouch(); RestorePose(); RestoreMorphs(); }
        void OnDestroy() { if (touch != null) touch.TouchStateChanged -= HandleTouchStateChanged; }
        static bool Button(InputDevice d, InputFeatureUsage<bool> digital, InputFeatureUsage<float> analog) { return (d.TryGetFeatureValue(digital, out var value) && value) || Analog(d, analog) >= .65f; }
        static float Analog(InputDevice d, InputFeatureUsage<float> usage) { return d.TryGetFeatureValue(usage, out var value) ? value : 0f; }
        static string Name(HumanInteractionKind kind) { return kind == HumanInteractionKind.Handshake ? "Handshake" : kind == HumanInteractionKind.HeadPat ? "Head pat" : kind == HumanInteractionKind.CheekPinch ? "Cheek pinch" : kind == HumanInteractionKind.BodyTouch ? "Body touch" : "None"; }
    }
}
