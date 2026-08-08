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
        [SerializeField] private Material handMaterial;

        private readonly List<XRHandSubsystem> subsystems = new List<XRHandSubsystem>();
        private readonly HandVisual left = new HandVisual("Left", XRNode.LeftHand, new Color(.25f, .86f, .66f, .92f));
        private readonly HandVisual right = new HandVisual("Right", XRNode.RightHand, new Color(.36f, .7f, 1f, .92f));
        private AvatarHumanInteraction interaction;
        private Transform trackingSpace;
        private AvatarContactRegion lastPhysicalContact;
        private float lastPhysicalContactLogAt = float.NegativeInfinity;

        public string Status { get; private set; } = "代理手等待 XR 输入";
        public int TrackedHandCount { get; private set; }

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
            internal bool[] jointTracked;
            internal Vector3[] previousJointPositions;
            internal bool[] previousJointTracked;
            internal Material runtimeMaterial;
            internal bool ownsRuntimeMaterial;
            internal bool visible;
            internal bool pinching;
            internal Vector3 collisionCorrection;
            internal float lastTrackedPoseAt = float.NegativeInfinity;

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
            interaction = nextInteraction;
            left.SetInteraction(interaction);
            right.SetInteraction(interaction);
        }

        private void Awake()
        {
            BuildHand(left);
            BuildHand(right);
        }

        private void Update()
        {
            trackingSpace = QuestXrInputUtility.ResolveTrackingSpace(trackingSpace);
            var subsystem = FindRunningSubsystem();
            var tracked = 0;
            tracked += UpdateTrackedHand(left, subsystem == null ? default(XRHand) : subsystem.leftHand, subsystem != null);
            tracked += UpdateTrackedHand(right, subsystem == null ? default(XRHand) : subsystem.rightHand, subsystem != null);
            TrackedHandCount = tracked;
            Status = tracked == 2 ? "双手追踪" : tracked == 1 ? "单手追踪" : "控制器或无 XR 输入";
        }

        private void FixedUpdate()
        {
            EvaluatePhysicalContacts(left);
            EvaluatePhysicalContacts(right);
        }

        private void EvaluatePhysicalContacts(HandVisual visual)
        {
            if (interaction == null || visual.root == null || !visual.root.activeInHierarchy ||
                visual.contactColliders == null)
            {
                return;
            }

            var correctionTarget = Vector3.zero;
            var hasPalmCorrection = false;
            for (var index = 0; index < visual.contactColliders.Length; index++)
            {
                if (!(visual.contactColliders[index] is SphereCollider sphere) || !sphere.enabled)
                {
                    continue;
                }
                var probe = ContactProbeForJoint(JointIds[index]);
                if (probe == TrackedHandContactProbe.None)
                {
                    continue;
                }
                var center = sphere.transform.TransformPoint(sphere.center);
                var radius = sphere.radius * MaximumScale(sphere.transform.lossyScale);
                var from = visual.previousJointTracked[index]
                    ? visual.previousJointPositions[index]
                    : center;
                var hasPenetration = interaction.TryGetPenetrationCorrection(
                    sphere,
                    out var penetrationCorrection,
                    out var penetrationRegion);
                if (hasPenetration)
                {
                    correctionTarget = penetrationCorrection;
                    hasPalmCorrection = true;
                }

                if (interaction.TryGetContactRegionSwept(
                    from,
                    center,
                    Mathf.Max(.005f, radius),
                    out var region,
                    out var contactPoint) &&
                    ShouldReportContact(probe, region, visual.pinching))
                {
                    interaction.ReportTrackedHandContact(region, visual.pinching, contactPoint);
                    if (lastPhysicalContact != region ||
                        Time.unscaledTime - lastPhysicalContactLogAt >= .75f)
                    {
                        lastPhysicalContact = region;
                        lastPhysicalContactLogAt = Time.unscaledTime;
                        Debug.Log("[HandTracking] Physical contact: " + region + " (continuous sweep).", this);
                    }
                    if (!hasPenetration)
                    {
                        correctionTarget = contactPoint - center;
                        hasPalmCorrection = correctionTarget.sqrMagnitude > .000001f;
                    }
                }
                visual.previousJointPositions[index] = center;
                visual.previousJointTracked[index] = true;
            }
            ApplyVisualCollisionCorrection(visual, hasPalmCorrection ? correctionTarget : Vector3.zero);
        }

        private static void ApplyVisualCollisionCorrection(HandVisual visual, Vector3 target)
        {
            var previous = visual.collisionCorrection;
            visual.collisionCorrection = target.sqrMagnitude > .000001f
                ? target
                : Vector3.MoveTowards(previous, Vector3.zero, Time.fixedUnscaledDeltaTime * .35f);
            var delta = visual.collisionCorrection - previous;
            if (delta.sqrMagnitude <= .00000001f) return;
            // Joint poses are written in world space every frame, so moving the
            // root alone would be overwritten by the next XR pose update. Move
            // the visible probes themselves and keep the root as a stable parent.
            if (visual.joints != null)
            {
                for (var index = 0; index < visual.joints.Length; index++)
                {
                    if (visual.joints[index] != null)
                    {
                        visual.joints[index].transform.position += delta;
                    }
                }
            }
            // The official mesh is parented to tracking space and is driven by
            // the XR hand provider. Its own pose must be corrected alongside the
            // proxy joints, otherwise the visible hand can remain inside the
            // avatar while only hidden probes are pushed out.
            if (visual.meshRoot != null)
            {
                visual.meshRoot.transform.position += delta;
            }
            if (visual.lines != null)
            {
                for (var index = 0; index < visual.lines.Length; index++)
                {
                    if (visual.lines[index].enabled)
                    {
                        visual.lines[index].SetPosition(0, visual.lines[index].GetPosition(0) + delta);
                        visual.lines[index].SetPosition(1, visual.lines[index].GetPosition(1) + delta);
                    }
                }
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
                SetVisible(visual, true, visual.meshRoot != null);
                return 1;
            }

            var device = InputDevices.GetDeviceAtXRNode(visual.node);
            if (device.isValid && device.TryGetFeatureValue(CommonUsages.devicePosition, out var position))
            {
                var rotation = Quaternion.identity;
                device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
                SetControllerPose(visual, World(position), WorldRotation(rotation));
                SetVisible(visual, true, false);
                visual.pinching = device.TryGetFeatureValue(CommonUsages.trigger, out var trigger) && trigger > .78f;
                visual.UpdateRelayState();
                return 0;
            }

            visual.DisableContacts();
            if (IsTrackingGraceActive(Time.unscaledTime, visual.lastTrackedPoseAt, trackingLossVisualGrace))
            {
                SetVisible(visual, true, visual.meshRoot != null);
                return 0;
            }
            SetVisible(visual, false, false);
            return 0;
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
            visual.collisionCorrection = Vector3.zero;
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
            visual.meshRoot = LoadOfficialMesh(visual);
            var body = visual.root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.detectCollisions = true;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            visual.joints = new GameObject[JointIds.Length];
            visual.proxyRenderers = new Renderer[JointIds.Length];
            visual.contactColliders = new Collider[JointIds.Length];
            visual.relays = new TrackedHandContactRelay[JointIds.Length];
            visual.jointTracked = new bool[JointIds.Length];
            visual.previousJointPositions = new Vector3[JointIds.Length];
            visual.previousJointTracked = new bool[JointIds.Length];
            visual.runtimeMaterial = handMaterial != null ? handMaterial : ResolveMaterial(visual.color);
            visual.ownsRuntimeMaterial = handMaterial == null;
            ApplyOfficialMeshMaterial(visual);
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

        private GameObject LoadOfficialMesh(HandVisual visual)
        {
            var resourceName = visual.node == XRNode.LeftHand
                ? "BanxiaHands/Prefabs/Left Hand Tracking"
                : "BanxiaHands/Prefabs/Right Hand Tracking";
            var prefab = Resources.Load<GameObject>(resourceName);
            if (prefab == null)
            {
                Debug.LogWarning("[HandTracking] Official XR Hands mesh resource is unavailable; using proxy hand.", this);
                return null;
            }

            var instance = Instantiate(prefab, transform);
            instance.name = visual.name + " Official Hand Mesh";
            instance.SetActive(false);
            return instance;
        }

        private static void ApplyOfficialMeshMaterial(HandVisual visual)
        {
            if (visual.meshRoot == null || visual.runtimeMaterial == null)
            {
                return;
            }

            var renderers = visual.meshRoot.GetComponentsInChildren<Renderer>(true);
            for (var index = 0; index < renderers.Length; index++)
            {
                renderers[index].sharedMaterial = visual.runtimeMaterial;
                renderers[index].enabled = true;
            }
        }

        private void UpdateOfficialMeshParent(HandVisual visual)
        {
            if (visual.meshRoot == null || trackingSpace == null || visual.meshRoot.transform.parent == trackingSpace)
            {
                return;
            }
            visual.meshRoot.transform.SetParent(trackingSpace, false);
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
            UpdateOfficialMeshParent(visual);
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
            if (visual.meshRoot != null)
            {
                visual.meshRoot.SetActive(visual.visible && showOfficialMesh);
            }
            if (!visible) visual.collisionCorrection = Vector3.zero;
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
                       region == AvatarContactRegion.Hand;
            }
            return probe == TrackedHandContactProbe.PinchTip &&
                   region == AvatarContactRegion.Face &&
                   pinching;
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
            var proxy = other.GetComponent<AvatarContactProxy>();
            if (proxy == null || interaction == null || !tracked ||
                !QuestTrackedHandVisualizer.ShouldReportContact(probe, proxy.Region, pinching)) return;
            interaction.ReportTrackedHandContact(proxy.Region, pinching, transform.position);
        }
    }
}
