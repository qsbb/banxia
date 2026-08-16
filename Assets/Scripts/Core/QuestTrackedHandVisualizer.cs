using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

namespace QuestMmdPlayer
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10800)]
    public sealed class QuestTrackedHandVisualizer : MonoBehaviour
    {
        private static readonly XRHandJointID[] JointIds =
        {
            XRHandJointID.Wrist, XRHandJointID.Palm,
            XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip,
            XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip,
            XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip,
            XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip,
            XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip
        };

        private static readonly int[,] Segments =
        {
            { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 4 }, { 4, 5 },
            { 1, 6 }, { 6, 7 }, { 7, 8 }, { 8, 9 }, { 9, 10 },
            { 1, 11 }, { 11, 12 }, { 12, 13 }, { 13, 14 }, { 14, 15 },
            { 1, 16 }, { 16, 17 }, { 17, 18 }, { 18, 19 }, { 19, 20 },
            { 1, 21 }, { 21, 22 }, { 22, 23 }, { 23, 24 }, { 24, 25 }
        };

        [SerializeField] private bool showHands = true;
        [SerializeField, Range(.006f, .035f)] private float jointRadius = .014f;
        [SerializeField, Range(.002f, .014f)] private float lineWidth = .006f;
        [SerializeField, Range(.02f, .25f)] private float trackingLossVisualGrace = .10f;
        [SerializeField, Range(.02f, .08f)] private float pinchEnterDistance = .032f;
        [SerializeField, Range(.025f, .10f)] private float pinchReleaseDistance = .045f;
        [SerializeField, Range(.04f, .5f)] private float contactFactUpdateInterval = .10f;
        [SerializeField, Range(.25f, 5f)] private float contactDiagnosticHoverInterval = 1f;
        [SerializeField] private Material handMaterial;

        private readonly List<XRHandSubsystem> subsystems = new List<XRHandSubsystem>();
        private readonly HandVisual left = new HandVisual("Left", XRNode.LeftHand, new Color(.25f, .86f, .66f, .92f));
        private readonly HandVisual right = new HandVisual("Right", XRNode.RightHand, new Color(.36f, .7f, 1f, .92f));
        private AvatarHumanInteraction interaction;
        private Transform trackingSpace;
        private AvatarContactRegion lastPhysicalContact;
        private float lastPhysicalContactLogAt = float.NegativeInfinity;
        private readonly TrackedHandContactAggregator contactAggregator = new TrackedHandContactAggregator();
        private readonly PokeInteractionLifecycle pokeLifecycle = new PokeInteractionLifecycle();
        private RuntimeDebugLog diagnostics;
        private float lastContactDiagnosticHoverAt = float.NegativeInfinity;
        private bool contactEventsSubscribed;

        public string Status { get; private set; } = "代理手等待 XR 输入";
        public int TrackedHandCount { get; private set; }
        public int ActiveContactCount { get; private set; }
        public TrackedHandContactFact LatestContactFact { get; private set; }
        public const int PhysicsProbeCount = 12;
        public bool HandsVisible => showHands;

        public void SetHandsVisible(bool visible)
        {
            showHands = visible;
            // Do not feed the already-rendered state back into the state
            // machine. Once showHands is false, visual.visible is also false;
            // using it here made a later enable request unable to recover.
            SetVisible(left, ShouldShowTrackedHand(left.inputSource, showHands), false);
            SetVisible(right, ShouldShowTrackedHand(right.inputSource, showHands), false);
        }

        /// <summary>
        /// Provider-neutral observations only. The detector does not move the
        /// tracked hand or decide which avatar reaction should play.
        /// </summary>
        public event Action<TrackedHandContactFact> ContactFactChanged;
        public event Action<PokeInteractionEvent> PokeEvent;

        private static readonly int[] PhysicsProbeJointIndices = { 1, 5, 10, 15, 20, 25 };

        /// <summary>
        /// Returns the palm and five fingertip probes used by the UMT Bullet
        /// adapter. The method is allocation-free and exposes no scene object.
        /// </summary>
        public bool TryGetPhysicsProbe(
            int probeIndex,
            out Vector3 position,
            out float radius,
            out bool active)
        {
            position = Vector3.zero;
            radius = 0f;
            active = false;
            if (probeIndex < 0 || probeIndex >= PhysicsProbeCount)
            {
                return false;
            }

            var visual = probeIndex < 6 ? left : right;
            var jointIndex = PhysicsProbeJointIndices[probeIndex % 6];
            if (visual.joints == null || visual.contactColliders == null || visual.jointTracked == null ||
                visual.joints[jointIndex] == null || visual.contactColliders[jointIndex] == null)
            {
                return false;
            }

            var collider = visual.contactColliders[jointIndex];
            position = visual.joints[jointIndex].transform.position;
            radius = collider is SphereCollider sphere
                ? sphere.radius * MaximumScale(collider.transform.lossyScale)
                : jointRadius;
            active = visual.root != null && visual.root.activeInHierarchy &&
                     collider.enabled && visual.jointTracked[jointIndex];
            return true;
        }

        private sealed class HandVisual
        {
            internal readonly string name;
            internal readonly XRNode node;
            internal readonly Color color;
            internal GameObject root;
            internal GameObject meshRoot;
            internal GameObject[] joints;
            internal Renderer[] proxyRenderers;
            internal Collider[] contactColliders;
            internal LineRenderer[] lines;
            internal TrackedHandContactRelay[] relays;
            internal TrackedHandContactTracker[] contactTrackers;
            internal bool[] jointTracked;
            internal Vector3[] previousJointPositions;
            internal bool[] previousJointTracked;
            internal Material runtimeMaterial;
            internal bool ownsRuntimeMaterial;
            internal bool visible;
            internal bool pinching;
            internal float lastTrackedPoseAt = float.NegativeInfinity;
            internal string inputSource = "none";

            internal HandVisual(string name, XRNode node, Color color)
            {
                this.name = name;
                this.node = node;
                this.color = color;
            }

            internal void SetInteraction(AvatarHumanInteraction nextInteraction)
            {
                if (relays == null) return;
                for (var index = 0; index < relays.Length; index++) relays[index].SetInteraction(nextInteraction);
            }

            internal void UpdateRelayState()
            {
                if (relays == null) return;
                for (var index = 0; index < relays.Length; index++) relays[index].SetPinching(pinching);
            }

            internal void DisableContacts()
            {
                pinching = false;
                if (contactColliders == null) return;
                for (var index = 0; index < contactColliders.Length; index++)
                {
                    if (contactColliders[index] != null) contactColliders[index].enabled = false;
                    if (relays[index] != null) relays[index].SetTracked(false);
                    if (previousJointTracked != null) previousJointTracked[index] = false;
                }
                UpdateRelayState();
            }
        }

        public void Bind(AvatarHumanInteraction nextInteraction)
        {
            EnsureContactEventSubscriptions();
            interaction = nextInteraction;
            left.SetInteraction(interaction);
            right.SetInteraction(interaction);
        }

        private void Awake()
        {
            diagnostics = GetComponent<RuntimeDebugLog>();
            EnsureContactEventSubscriptions();
            BuildHand(left);
            BuildHand(right);
        }

        private void EnsureContactEventSubscriptions()
        {
            if (contactEventsSubscribed)
            {
                return;
            }
            contactAggregator.RawFactChanged += HandleRawContactFact;
            contactAggregator.FactChanged += HandleContactFact;
            contactEventsSubscribed = true;
        }

        private void Update()
        {
            trackingSpace = QuestXrInputUtility.ResolveTrackingSpace(trackingSpace);
            var subsystem = FindRunningSubsystem();
            var tracked = 0;
            tracked += UpdateTrackedHand(left, subsystem == null ? default(XRHand) : subsystem.leftHand, subsystem != null);
            tracked += UpdateTrackedHand(right, subsystem == null ? default(XRHand) : subsystem.rightHand, subsystem != null);
            TrackedHandCount = tracked;
            var trackingLabel = tracked == 2 ? "双手追踪" : tracked == 1 ? "单手追踪" : "控制器或无 XR 输入";
            Status = trackingLabel + " | 左：" + left.inputSource + " 右：" + right.inputSource;
        }

        private void LateUpdate()
        {
            // Hand joints are written in Update and the UMT avatar solver runs
            // before this component (see DefaultExecutionOrder). Explicitly
            // publish both sets of Transform changes before ComputePenetration.
            Physics.SyncTransforms();
            EvaluatePhysicalContacts(left);
            EvaluatePhysicalContacts(right);
            ActiveContactCount = CountActiveContacts(left) + CountActiveContacts(right);
        }

        private void EvaluatePhysicalContacts(HandVisual visual)
        {
            if (interaction == null || visual.root == null || !visual.root.activeInHierarchy ||
                visual.contactColliders == null)
            {
                EndContactFacts(visual);
                return;
            }

            for (var index = 0; index < visual.contactColliders.Length; index++)
            {
                var tracker = visual.contactTrackers == null ? null : visual.contactTrackers[index];
                if (!(visual.contactColliders[index] is SphereCollider sphere) || !sphere.enabled)
                {
                    tracker?.Clear(Time.unscaledTime);
                    continue;
                }
                var probe = ContactProbeForJoint(JointIds[index]);
                if (probe == TrackedHandContactProbe.None)
                {
                    tracker?.Clear(Time.unscaledTime);
                    continue;
                }
                var center = sphere.transform.TransformPoint(sphere.center);
                var radius = sphere.radius * MaximumScale(sphere.transform.lossyScale);
                var from = visual.previousJointTracked[index]
                    ? visual.previousJointPositions[index]
                    : center;
                if (EvaluatePhysicalProbe(
                        sphere,
                        tracker,
                        probe,
                        visual.pinching,
                        string.Equals(visual.inputSource, "hand_tracking", StringComparison.Ordinal),
                        from,
                        center,
                        radius,
                        out var region))
                {
                    if (lastPhysicalContact != region ||
                        Time.unscaledTime - lastPhysicalContactLogAt >= .75f)
                    {
                        lastPhysicalContact = region;
                        lastPhysicalContactLogAt = Time.unscaledTime;
                        Debug.Log("[HandTracking] Physical contact: " + region + " (continuous sweep).", this);
                    }
                }
                visual.previousJointPositions[index] = center;
                visual.previousJointTracked[index] = true;
            }
        }

        /// <summary>
        /// Evaluates one authoritative tracked-hand collider against the bound
        /// avatar without changing the hand pose. Optional XR adapters can use
        /// the same contact path as the built-in hand provider.
        /// </summary>
        public float LastContactEvaluationMilliseconds { get; private set; }
        private int contactTimingFrame = -1;
        private double contactEvaluationSeconds;

        public bool EvaluatePhysicalProbe(
            SphereCollider sphere,
            TrackedHandContactTracker tracker,
            TrackedHandContactProbe probe,
            bool pinching,
            bool authoritativeTrackedPose,
            Vector3 from,
            Vector3 center,
            float radius,
            out AvatarContactRegion region)
        {
            region = AvatarContactRegion.None;
            if (interaction == null || sphere == null || tracker == null ||
                probe == TrackedHandContactProbe.None)
            {
                tracker?.Clear(Time.unscaledTime);
                return false;
            }

            BeginContactTimingFrame();
            var timingStarted = Time.realtimeSinceStartupAsDouble;

            var hasPenetration = interaction.TryGetPenetrationCorrection(
                sphere,
                out var penetrationCorrection,
                out var penetrationRegion);
            var swept = interaction.TryGetContactRegionSwept(
                from,
                center,
                Mathf.Max(.005f, radius),
                out var sweptRegion,
                out var contactPoint);
            region = swept ? sweptRegion : penetrationRegion;
            if ((!swept && !hasPenetration) || !ShouldReportContact(probe, region, pinching))
            {
                tracker.Clear(Time.unscaledTime);
                region = AvatarContactRegion.None;
                CompleteContactTiming(timingStarted);
                return false;
            }

            // XR pose is authoritative: never displace the rendered hand.
            // ComputePenetration returns the vector that would move the hand
            // out of the avatar, so its inverse becomes the avatar response.
            var observationPoint = swept ? contactPoint : center;
            var normal = hasPenetration && penetrationRegion == region &&
                         penetrationCorrection.sqrMagnitude > .0000001f
                ? penetrationCorrection.normalized
                : Vector3.zero;
            if (interaction.TryGetContactSurface(
                    observationPoint,
                    Mathf.Max(.005f, radius),
                    region,
                    out var surfacePoint,
                    out var sampledNormal))
            {
                observationPoint = surfacePoint;
                if (normal.sqrMagnitude <= .0000001f)
                {
                    normal = sampledNormal;
                }
            }
            tracker.Observe(
                region,
                observationPoint,
                normal,
                hasPenetration && penetrationRegion == region
                    ? penetrationCorrection.magnitude
                    : 0f,
                pinching,
                authoritativeTrackedPose,
                Time.unscaledTime,
                contactFactUpdateInterval);
            CompleteContactTiming(timingStarted);
            return true;
        }

        private void BeginContactTimingFrame()
        {
            if (contactTimingFrame == Time.frameCount)
            {
                return;
            }
            contactTimingFrame = Time.frameCount;
            contactEvaluationSeconds = 0d;
            LastContactEvaluationMilliseconds = 0f;
        }

        private void CompleteContactTiming(double startedAt)
        {
            contactEvaluationSeconds += Math.Max(0d, Time.realtimeSinceStartupAsDouble - startedAt);
            LastContactEvaluationMilliseconds = (float)(contactEvaluationSeconds * 1000d);
        }

        /// <summary>Creates a probe lifecycle tracker wired to the aggregate contact stream.</summary>
        public TrackedHandContactTracker CreateContactTracker(
            XRNode node,
            XRHandJointID joint,
            TrackedHandContactProbe probe)
        {
            EnsureContactEventSubscriptions();
            if (probe == TrackedHandContactProbe.None)
            {
                return null;
            }
            var tracker = new TrackedHandContactTracker(node, joint, probe);
            tracker.FactChanged += contactAggregator.Accept;
            return tracker;
        }

        private void HandleContactFact(TrackedHandContactFact fact)
        {
            LatestContactFact = fact;
            // Publish Poke lifecycle from the aggregate stream, not individual
            // probes, so a palm/fingertip handoff does not produce a false Exit.
            PokeEvent?.Invoke(pokeLifecycle.Observe(fact));
            var diagnosticNow = Time.unscaledTime;
            if (diagnostics != null && ShouldRecordContactDiagnostic(
                    fact.Phase,
                    diagnosticNow,
                    lastContactDiagnosticHoverAt,
                    contactDiagnosticHoverInterval))
            {
                diagnostics.RecordStage(
                    "hand_contact",
                    fact.Phase == TrackedHandContactPhase.Ended ? "completed" : "processing",
                    ContactDiagnosticCode(fact),
                    elapsedMs: Mathf.RoundToInt(fact.DurationSeconds * 1000f));
                if (fact.Phase != TrackedHandContactPhase.Ended)
                {
                    lastContactDiagnosticHoverAt = diagnosticNow;
                }
            }
            // The aggregator emits one stable semantic fact for all active
            // palm/fingertip probes. An Ended fact is intentionally forwarded
            // only when no replacement contact remains; the interaction state
            // machine owns the bounded release timeout.
            if (fact.Phase == TrackedHandContactPhase.Ended || interaction == null)
            {
                return;
            }

            var avatarPush = fact.PenetrationDepth > .000001f
                ? -fact.SurfaceNormal * fact.PenetrationDepth
                : Vector3.zero;
            interaction.ReportTrackedHandContact(
                fact.Region,
                fact.Pinching,
                fact.Point,
                avatarPush);
        }

        public static string ContactDiagnosticCode(TrackedHandContactFact fact)
        {
            var phase = fact.Phase == TrackedHandContactPhase.Began
                ? "enter"
                : fact.Phase == TrackedHandContactPhase.Ended ? "exit" : "hover";
            var region = fact.Region.ToString().ToLowerInvariant();
            return "hand_contact_" + phase + "_" + region;
        }

        public static bool ShouldRecordContactDiagnostic(
            TrackedHandContactPhase phase,
            float now,
            float lastHoverAt,
            float hoverInterval)
        {
            return phase != TrackedHandContactPhase.Updated ||
                   now - lastHoverAt >= Mathf.Max(.25f, hoverInterval);
        }

        private void HandleRawContactFact(TrackedHandContactFact fact)
        {
            // Keep the public stream lossless for diagnostics. Semantic
            // reactions consume the aggregate event above, so another active
            // finger/palm probe cannot be cleared by this probe's Ended fact.
            ContactFactChanged?.Invoke(fact);
        }

        private static int CountActiveContacts(HandVisual visual)
        {
            if (visual.contactTrackers == null) return 0;
            var count = 0;
            for (var index = 0; index < visual.contactTrackers.Length; index++)
            {
                if (visual.contactTrackers[index] != null && visual.contactTrackers[index].IsActive) count++;
            }
            return count;
        }

        private static void EndContactFacts(HandVisual visual)
        {
            if (visual.contactTrackers == null) return;
            for (var index = 0; index < visual.contactTrackers.Length; index++)
            {
                visual.contactTrackers[index]?.Clear(Time.unscaledTime);
            }
        }

        public static float MaximumScale(Vector3 scale)
        {
            return Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
        }

        private int UpdateTrackedHand(HandVisual visual, XRHand hand, bool hasSubsystem)
        {
            if (hasSubsystem && hand.isTracked && TryGetHandPose(hand, visual))
            {
                SetInputSource(visual, "hand_tracking");
                SetVisible(visual, true, false);
                return 1;
            }

            // Quest can briefly report an untracked hand between otherwise
            // continuous joint frames. Keep the last physical probes alive for
            // the same bounded grace window as the visible hand so a 1-frame
            // dropout does not make the hand pass through the avatar.
            if (ShouldRetainTrackedHandPose(
                visual.inputSource,
                Time.unscaledTime,
                visual.lastTrackedPoseAt,
                trackingLossVisualGrace))
            {
                SetVisible(visual, true, false);
                return 0;
            }

            var device = InputDevices.GetDeviceAtXRNode(visual.node);
            if (device.isValid && device.TryGetFeatureValue(CommonUsages.devicePosition, out var position))
            {
                var rotation = Quaternion.identity;
                device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
                SetInputSource(visual, "controller");
                SetControllerPose(visual, World(position), WorldRotation(rotation));
                SetVisible(visual, true, false);
                visual.pinching = device.TryGetFeatureValue(CommonUsages.trigger, out var trigger) && trigger > .78f;
                visual.UpdateRelayState();
                return 0;
            }

            visual.DisableContacts();
            SetInputSource(visual, "none");
            if (IsTrackingGraceActive(Time.unscaledTime, visual.lastTrackedPoseAt, trackingLossVisualGrace))
            {
                SetVisible(visual, true, false);
                return 0;
            }
            SetVisible(visual, false, false);
            return 0;
        }

        private void SetInputSource(HandVisual visual, string source)
        {
            if (string.Equals(visual.inputSource, source, StringComparison.Ordinal))
            {
                return;
            }

            var previous = visual.inputSource;
            visual.inputSource = source;
            if (source == "hand_tracking" && visual.meshRoot == null)
            {
                Debug.Log("[HandTracking] " + visual.name + " using synchronized proxy mesh.", this);
            }
            Debug.Log(
                "[HandTracking] " + visual.name + " input source: " + previous + " -> " + source +
                (source == "hand_tracking" && visual.meshRoot == null ? " (proxy mesh)" : ""),
                this);
        }

        private bool TryGetHandPose(XRHand hand, HandVisual visual)
        {
            var found = 0;
            for (var index = 0; index < JointIds.Length; index++)
            {
                if (!hand.GetJoint(JointIds[index]).TryGetPose(out var pose))
                {
                    visual.jointTracked[index] = false;
                    SetJointContactState(visual, index, false);
                    continue;
                }
                visual.joints[index].transform.SetPositionAndRotation(World(pose.position), WorldRotation(pose.rotation));
                visual.jointTracked[index] = true;
                SetJointContactState(visual, index, true);
                found++;
            }
            if (found < 2)
            {
                visual.DisableContacts();
                ClearPreviousPose(visual);
                return false;
            }

            var tipsTracked = visual.jointTracked[5] && visual.jointTracked[10];
            visual.pinching = tipsTracked && AvatarTouchInteraction.UpdatePinchState(
                visual.pinching,
                Vector3.Distance(visual.joints[5].transform.position, visual.joints[10].transform.position),
                pinchEnterDistance,
                pinchReleaseDistance);
            visual.lastTrackedPoseAt = Time.unscaledTime;
            UpdateLines(visual);
            visual.UpdateRelayState();
            return true;
        }

        private void SetControllerPose(HandVisual visual, Vector3 palm, Quaternion rotation)
        {
            ClearPreviousPose(visual);
            var forward = rotation * Vector3.forward;
            var side = rotation * Vector3.right;
            visual.joints[0].transform.SetPositionAndRotation(palm - forward * .035f, rotation);
            visual.joints[1].transform.SetPositionAndRotation(palm, rotation);
            for (var index = 0; index < visual.jointTracked.Length; index++)
            {
                visual.jointTracked[index] = true;
                SetJointContactState(visual, index, index == 1);
            }
            for (var index = 2; index < visual.joints.Length; index++)
            {
                var finger = (index - 2) / 5;
                var step = (index - 2) % 5;
                var spread = (finger - 1.5f) * .022f;
                var direction = forward + side * spread;
                visual.joints[index].transform.SetPositionAndRotation(
                    palm + direction.normalized * (.045f + step * .018f), rotation);
            }
            UpdateLines(visual);
            visual.UpdateRelayState();
        }

        private void BuildHand(HandVisual visual)
        {
            visual.root = new GameObject("Tracked " + visual.name + " Proxy Hand");
            visual.root.transform.SetParent(transform, false);
            // The prefab's XRHandMeshController only updates from its own
            // event stream. This component samples XRHand joints directly, so
            // instantiate no second mesh that could freeze during a provider
            // switch; the generated proxy mesh stays synchronized.
            visual.meshRoot = null;
            var body = visual.root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.detectCollisions = true;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            visual.joints = new GameObject[JointIds.Length];
            visual.proxyRenderers = new Renderer[JointIds.Length];
            visual.contactColliders = new Collider[JointIds.Length];
            visual.relays = new TrackedHandContactRelay[JointIds.Length];
            visual.contactTrackers = new TrackedHandContactTracker[JointIds.Length];
            visual.jointTracked = new bool[JointIds.Length];
            visual.previousJointPositions = new Vector3[JointIds.Length];
            visual.previousJointTracked = new bool[JointIds.Length];
            visual.runtimeMaterial = handMaterial != null ? handMaterial : ResolveMaterial(visual.color);
            visual.ownsRuntimeMaterial = handMaterial == null;
            for (var index = 0; index < visual.joints.Length; index++)
            {
                var joint = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                joint.name = visual.name + " " + JointIds[index];
                joint.transform.SetParent(visual.root.transform, false);
                var probe = ContactProbeForJoint(JointIds[index]);
                var radius = probe == TrackedHandContactProbe.Palm ? jointRadius * 1.9f : jointRadius;
                joint.transform.localScale = Vector3.one * (radius * 2f);
                var renderer = joint.GetComponent<Renderer>();
                renderer.sharedMaterial = visual.runtimeMaterial;
                visual.proxyRenderers[index] = renderer;
                var collider = joint.GetComponent<SphereCollider>();
                collider.isTrigger = true;
                collider.enabled = false;
                visual.contactColliders[index] = collider;
                var relay = joint.AddComponent<TrackedHandContactRelay>();
                relay.Initialize(interaction, visual.name, probe);
                visual.joints[index] = joint;
                visual.relays[index] = relay;
                if (probe != TrackedHandContactProbe.None)
                {
                    visual.contactTrackers[index] = CreateContactTracker(
                        visual.node,
                        JointIds[index],
                        probe);
                }
            }

            visual.lines = new LineRenderer[Segments.GetLength(0)];
            for (var index = 0; index < visual.lines.Length; index++)
            {
                var lineObject = new GameObject(visual.name + " Hand Bone " + index);
                lineObject.transform.SetParent(visual.root.transform, false);
                var line = lineObject.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.positionCount = 2;
                line.startWidth = line.endWidth = lineWidth;
                line.sharedMaterial = visual.runtimeMaterial;
                line.enabled = false;
                visual.lines[index] = line;
            }
            SetVisible(visual, false, false);
        }

        private void UpdateLines(HandVisual visual)
        {
            for (var index = 0; index < visual.lines.Length; index++)
            {
                var startIndex = Segments[index, 0];
                var endIndex = Segments[index, 1];
                var segmentTracked = visual.jointTracked[startIndex] && visual.jointTracked[endIndex];
                visual.lines[index].enabled = segmentTracked;
                if (!segmentTracked) continue;
                var start = visual.joints[startIndex].transform.position;
                var end = visual.joints[endIndex].transform.position;
                visual.lines[index].SetPosition(0, start);
                visual.lines[index].SetPosition(1, end);
            }
        }

        private void SetVisible(HandVisual visual, bool visible, bool showOfficialMesh)
        {
            visual.visible = visible && showHands;
            visual.root.SetActive(visual.visible);
            var showProxy = visual.visible && !showOfficialMesh;
            if (visual.proxyRenderers != null)
            {
                for (var index = 0; index < visual.proxyRenderers.Length; index++)
                {
                    if (visual.proxyRenderers[index] != null)
                    {
                        visual.proxyRenderers[index].enabled = showProxy;
                    }
                }
            }
            if (visual.lines != null)
            {
                for (var index = 0; index < visual.lines.Length; index++)
                {
                    visual.lines[index].enabled = showProxy &&
                        visual.jointTracked[Segments[index, 0]] &&
                        visual.jointTracked[Segments[index, 1]];
                }
            }
        }

        private XRHandSubsystem FindRunningSubsystem()
        {
            for (var index = 0; index < subsystems.Count; index++)
            {
                if (subsystems[index] != null && subsystems[index].running) return subsystems[index];
            }
            subsystems.Clear();
            SubsystemManager.GetSubsystems(subsystems);
            for (var index = 0; index < subsystems.Count; index++)
            {
                if (subsystems[index] != null && subsystems[index].running) return subsystems[index];
            }
            return null;
        }

        private static void SetJointContactState(HandVisual visual, int index, bool tracked)
        {
            var probe = ContactProbeForJoint(JointIds[index]);
            var active = tracked && probe != TrackedHandContactProbe.None;
            if (visual.contactColliders[index] != null) visual.contactColliders[index].enabled = active;
            if (visual.relays[index] != null) visual.relays[index].SetTracked(active);
        }

        private static void ClearPreviousPose(HandVisual visual)
        {
            if (visual.previousJointTracked == null) return;
            for (var index = 0; index < visual.previousJointTracked.Length; index++)
            {
                visual.previousJointTracked[index] = false;
            }
        }

        public static bool IsTrackingGraceActive(float now, float lastTrackedAt, float graceSeconds)
        {
            return AvatarTouchInteraction.IsTrackingGraceActive(now, lastTrackedAt, graceSeconds);
        }

        public static bool ShouldRetainTrackedHandPose(
            string inputSource,
            float now,
            float lastTrackedAt,
            float graceSeconds)
        {
            return string.Equals(inputSource, "hand_tracking", StringComparison.Ordinal) &&
                   IsTrackingGraceActive(now, lastTrackedAt, graceSeconds);
        }

        public static bool ShouldShowTrackedHand(string inputSource, bool showHands)
        {
            return showHands && !string.IsNullOrEmpty(inputSource) &&
                   !string.Equals(inputSource, "none", StringComparison.Ordinal);
        }

        public static TrackedHandContactProbe ContactProbeForJoint(XRHandJointID joint)
        {
            if (joint == XRHandJointID.Palm) return TrackedHandContactProbe.Palm;
            if (joint == XRHandJointID.ThumbTip || joint == XRHandJointID.IndexTip)
            {
                return TrackedHandContactProbe.PinchTip;
            }
            return TrackedHandContactProbe.None;
        }

        public static bool ShouldReportContact(
            TrackedHandContactProbe probe,
            AvatarContactRegion region,
            bool pinching)
        {
            if (probe == TrackedHandContactProbe.Palm)
            {
                return region == AvatarContactRegion.Body ||
                       region == AvatarContactRegion.Head ||
                       region == AvatarContactRegion.Face ||
                       region == AvatarContactRegion.Hand ||
                       region == AvatarContactRegion.Hair ||
                       region == AvatarContactRegion.Limb;
            }
            if (probe != TrackedHandContactProbe.PinchTip)
            {
                return false;
            }

            // A fingertip can touch hair and body parts even without a pinch.
            // Pinching never moves the avatar; it only changes face contact to
            // the cheek-pinch semantic in AvatarHumanInteraction.
            return (region == AvatarContactRegion.Face && pinching) ||
                   region == AvatarContactRegion.Head ||
                   region == AvatarContactRegion.Hair ||
                   region == AvatarContactRegion.Body ||
                   region == AvatarContactRegion.Limb ||
                   region == AvatarContactRegion.Hand;
        }

        private Vector3 World(Vector3 value)
        {
            return trackingSpace == null ? value : trackingSpace.TransformPoint(value);
        }

        private Quaternion WorldRotation(Quaternion value)
        {
            return trackingSpace == null ? value : trackingSpace.rotation * value;
        }

        private Material ResolveMaterial(Color color)
        {
            if (handMaterial != null) return handMaterial;
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            var material = new Material(shader) { color = color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 0f);
            return material;
        }

        private void OnDestroy()
        {
            EndContactFacts(left);
            EndContactFacts(right);
            // Tracker clears above already propagate raw/aggregate Ended facts;
            // reset only removes any remaining aggregate state without
            // emitting a duplicate lifecycle event.
            contactAggregator.Reset(Time.unscaledTime);
            pokeLifecycle.Reset();
            DestroyHand(left);
            DestroyHand(right);
        }

        private static void DestroyHand(HandVisual visual)
        {
            if (visual.root != null) Destroy(visual.root);
            if (visual.meshRoot != null) Destroy(visual.meshRoot);
            if (visual.ownsRuntimeMaterial && visual.runtimeMaterial != null) Destroy(visual.runtimeMaterial);
        }
    }

    public enum TrackedHandContactProbe
    {
        None,
        Palm,
        PinchTip
    }

    public enum TrackedHandContactPhase
    {
        Began,
        Updated,
        Ended
    }

    /// <summary>
    /// Read-only XR contact evidence. Consumers may classify the fact into a
    /// semantic action; the detector itself never chooses a reaction.
    /// </summary>
    public struct TrackedHandContactFact
    {
        public readonly int SequenceId;
        public readonly TrackedHandContactPhase Phase;
        public readonly XRNode HandNode;
        public readonly XRHandJointID Joint;
        public readonly TrackedHandContactProbe Probe;
        public readonly AvatarContactRegion Region;
        public readonly Vector3 Point;
        public readonly Vector3 SurfaceNormal;
        public readonly float PenetrationDepth;
        public readonly float DurationSeconds;
        public readonly bool Pinching;
        public readonly bool UsesAuthoritativeTrackedPose;

        public TrackedHandContactFact(
            int sequenceId,
            TrackedHandContactPhase phase,
            XRNode handNode,
            XRHandJointID joint,
            TrackedHandContactProbe probe,
            AvatarContactRegion region,
            Vector3 point,
            Vector3 surfaceNormal,
            float penetrationDepth,
            float durationSeconds,
            bool pinching,
            bool usesAuthoritativeTrackedPose)
        {
            SequenceId = sequenceId;
            Phase = phase;
            HandNode = handNode;
            Joint = joint;
            Probe = probe;
            Region = region;
            Point = point;
            SurfaceNormal = surfaceNormal.sqrMagnitude > .0000001f
                ? surfaceNormal.normalized
                : Vector3.zero;
            PenetrationDepth = Mathf.Max(0f, penetrationDepth);
            DurationSeconds = Mathf.Max(0f, durationSeconds);
            Pinching = pinching;
            UsesAuthoritativeTrackedPose = usesAuthoritativeTrackedPose;
        }
    }

    /// <summary>
    /// Converts per-frame palm/fingertip observations into a stable lifecycle.
    /// </summary>
    public sealed class TrackedHandContactTracker
    {
        private readonly XRNode handNode;
        private readonly XRHandJointID joint;
        private readonly TrackedHandContactProbe probe;
        private int sequenceId;
        private float beganAt;
        private float lastPublishedAt;
        private AvatarContactRegion region;
        private Vector3 point;
        private Vector3 normal;
        private float penetrationDepth;
        private bool pinching;
        private bool authoritativeTrackedPose;

        public bool IsActive { get; private set; }
        public event Action<TrackedHandContactFact> FactChanged;

        public TrackedHandContactTracker(
            XRNode handNode,
            XRHandJointID joint,
            TrackedHandContactProbe probe)
        {
            this.handNode = handNode;
            this.joint = joint;
            this.probe = probe;
        }

        public void Observe(
            AvatarContactRegion nextRegion,
            Vector3 nextPoint,
            Vector3 nextNormal,
            float nextPenetrationDepth,
            bool nextPinching,
            bool nextAuthoritativeTrackedPose,
            float now,
            float updateInterval = .1f)
        {
            if (nextRegion == AvatarContactRegion.None)
            {
                Clear(now);
                return;
            }

            var identityChanged = IsActive &&
                (region != nextRegion || pinching != nextPinching ||
                 authoritativeTrackedPose != nextAuthoritativeTrackedPose);
            if (identityChanged)
            {
                Clear(now);
            }

            point = nextPoint;
            normal = nextNormal.sqrMagnitude > .0000001f ? nextNormal.normalized : Vector3.zero;
            penetrationDepth = Mathf.Max(0f, nextPenetrationDepth);
            pinching = nextPinching;
            authoritativeTrackedPose = nextAuthoritativeTrackedPose;
            region = nextRegion;

            if (!IsActive)
            {
                IsActive = true;
                sequenceId++;
                beganAt = lastPublishedAt = now;
                Publish(TrackedHandContactPhase.Began, now);
                return;
            }

            if (now - lastPublishedAt >= Mathf.Max(.01f, updateInterval))
            {
                lastPublishedAt = now;
                Publish(TrackedHandContactPhase.Updated, now);
            }
        }

        public void Clear(float now)
        {
            if (!IsActive) return;
            Publish(TrackedHandContactPhase.Ended, now);
            IsActive = false;
            region = AvatarContactRegion.None;
        }

        private void Publish(TrackedHandContactPhase phase, float now)
        {
            FactChanged?.Invoke(new TrackedHandContactFact(
                sequenceId,
                phase,
                handNode,
                joint,
                probe,
                region,
                point,
                normal,
                penetrationDepth,
                Mathf.Max(0f, now - beganAt),
                pinching,
                authoritativeTrackedPose));
        }
    }

    /// <summary>
    /// Aggregates the palm and fingertip lifecycle streams for a hand. Physics
    /// exposes several probes, so an Ended event from one probe must not clear
    /// a still-active contact from another probe. The selected fact is stable
    /// for equal-priority contacts and prefers the current selection to avoid
    /// semantic reaction churn.
    /// </summary>
    public sealed class TrackedHandContactAggregator
    {
        private struct ContactKey : System.IEquatable<ContactKey>
        {
            public readonly XRNode HandNode;
            public readonly XRHandJointID Joint;
            public readonly TrackedHandContactProbe Probe;
            public readonly int SequenceId;

            public ContactKey(TrackedHandContactFact fact)
            {
                HandNode = fact.HandNode;
                Joint = fact.Joint;
                Probe = fact.Probe;
                SequenceId = fact.SequenceId;
            }

            public bool Equals(ContactKey other)
            {
                return HandNode == other.HandNode && Joint == other.Joint &&
                       Probe == other.Probe && SequenceId == other.SequenceId;
            }

            public override bool Equals(object obj)
            {
                return obj is ContactKey && Equals((ContactKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (((int)HandNode * 397) ^ ((int)Joint * 17) ^ (int)Probe) * 31 ^ SequenceId;
                }
            }

            public int StableOrder()
            {
                return (((int)HandNode * 1000) + ((int)Joint * 10) + (int)Probe) * 100000 + SequenceId;
            }
        }

        private readonly Dictionary<ContactKey, TrackedHandContactFact> active =
            new Dictionary<ContactKey, TrackedHandContactFact>();
        private ContactKey selectedKey;
        private bool hasSelected;

        /// <summary>All raw probe lifecycle events, used by diagnostics.</summary>
        public event Action<TrackedHandContactFact> RawFactChanged;

        /// <summary>The currently selected aggregate semantic fact.</summary>
        public event Action<TrackedHandContactFact> FactChanged;

        public int ActiveCount => active.Count;

        public void Accept(TrackedHandContactFact fact)
        {
            RawFactChanged?.Invoke(fact);
            var key = new ContactKey(fact);
            if (fact.Phase == TrackedHandContactPhase.Ended)
            {
                active.Remove(key);
            }
            else
            {
                // A tracker increments its sequence when semantic identity
                // changes. Remove a stale sequence for the same physical
                // probe even if an Ended callback was dropped by a consumer.
                RemovePreviousSequenceForProbe(key);
                active[key] = fact;
            }

            var previousSelected = hasSelected ? selectedKey : default(ContactKey);
            var hadPrevious = hasSelected;
            SelectBest();
            if (!hasSelected)
            {
                // Forward the ending fact so observers can close a lifecycle;
                // semantic consumers intentionally ignore Ended and expire by
                // their own bounded timeout.
                if (fact.Phase == TrackedHandContactPhase.Ended)
                {
                    FactChanged?.Invoke(fact);
                }
                return;
            }

            var selectedChanged = !hadPrevious || !selectedKey.Equals(previousSelected);
            if (selectedChanged || selectedKey.Equals(key) && fact.Phase != TrackedHandContactPhase.Ended)
            {
                FactChanged?.Invoke(active[selectedKey]);
            }
        }

        public void Reset(float now)
        {
            if (hasSelected)
            {
                var selected = active[selectedKey];
                FactChanged?.Invoke(new TrackedHandContactFact(
                    selected.SequenceId,
                    TrackedHandContactPhase.Ended,
                    selected.HandNode,
                    selected.Joint,
                    selected.Probe,
                    selected.Region,
                    selected.Point,
                    selected.SurfaceNormal,
                    selected.PenetrationDepth,
                    selected.DurationSeconds,
                    selected.Pinching,
                    selected.UsesAuthoritativeTrackedPose));
            }
            active.Clear();
            hasSelected = false;
        }

        private void SelectBest()
        {
            var bestKey = default(ContactKey);
            var bestScore = int.MinValue;
            foreach (var pair in active)
            {
                var score = ContactScore(pair.Value);
                var staysSelected = hasSelected && pair.Key.Equals(selectedKey);
                var winsStableTie = bestScore == score && !staysSelected &&
                    (bestScore == int.MinValue || pair.Key.StableOrder() < bestKey.StableOrder());
                if (score > bestScore || staysSelected || winsStableTie)
                {
                    bestScore = score;
                    bestKey = pair.Key;
                }
            }
            hasSelected = bestScore != int.MinValue;
            if (hasSelected)
            {
                selectedKey = bestKey;
            }
        }

        private void RemovePreviousSequenceForProbe(ContactKey incoming)
        {
            ContactKey stale = default(ContactKey);
            var found = false;
            foreach (var pair in active)
            {
                var key = pair.Key;
                if (key.HandNode == incoming.HandNode && key.Joint == incoming.Joint &&
                    key.Probe == incoming.Probe && key.SequenceId != incoming.SequenceId)
                {
                    stale = key;
                    found = true;
                    break;
                }
            }
            if (found)
            {
                active.Remove(stale);
                if (hasSelected && selectedKey.Equals(stale))
                {
                    hasSelected = false;
                }
            }
        }

        private static int ContactScore(TrackedHandContactFact fact)
        {
            var regionScore = fact.Region == AvatarContactRegion.Face ? 50 :
                fact.Region == AvatarContactRegion.Head || fact.Region == AvatarContactRegion.Hair ? 40 :
                fact.Region == AvatarContactRegion.Hand ? 30 :
                fact.Region == AvatarContactRegion.Body || fact.Region == AvatarContactRegion.Limb ? 20 : 0;
            var probeScore = fact.Probe == TrackedHandContactProbe.Palm ? 2 : 1;
            return regionScore + probeScore;
        }
    }

    [DefaultExecutionOrder(10700)]
    internal sealed class TrackedHandContactRelay : MonoBehaviour
    {
        private AvatarHumanInteraction interaction;
        private string handName;
        private bool pinching;
        private bool tracked;
        private TrackedHandContactProbe probe;

        public void Initialize(
            AvatarHumanInteraction nextInteraction,
            string nextHandName,
            TrackedHandContactProbe nextProbe)
        {
            interaction = nextInteraction;
            handName = nextHandName;
            probe = nextProbe;
        }

        public void SetInteraction(AvatarHumanInteraction nextInteraction)
        {
            interaction = nextInteraction;
        }

        public void SetPinching(bool value)
        {
            pinching = value;
        }

        public void SetTracked(bool value)
        {
            tracked = value;
        }

        private void OnTriggerStay(Collider other)
        {
            // Trigger callbacks are intentionally not semantic inputs. They
            // can arrive before/after the ordered swept/penetration sample and
            // would otherwise duplicate or resurrect a stale reaction. The
            // owner visualizer publishes the canonical lifecycle fact.
        }
    }
}
