using System;
using UnityEngine;
using UnityEngine.XR;

namespace QuestMmdPlayer
{
    [DisallowMultipleComponent]
    public sealed class QuestAvatarRayInteraction : MonoBehaviour
    {
        [SerializeField] private float maximumDistance = 5f;
        [SerializeField] private float triggerThreshold = .7f;
        [SerializeField] private float reactionSeconds = 2f;

        private AvatarController avatar;
        private AvatarHumanInteraction humanInteraction;
        private CompanionWorldMenu menu;
        private Transform trackingSpace;
        private LineRenderer line;
        private Material lineMaterial;
        private Renderer[] renderers = Array.Empty<Renderer>();
        private Bounds avatarBounds;
        private bool hasBounds;
        private bool previousTrigger;
        private float nextBoundsRefresh;

        public string Status { get; private set; } = "Avatar pointer waiting";

        public void Bind(AvatarController target, AvatarHumanInteraction interaction, CompanionWorldMenu worldMenu)
        {
            avatar = target;
            humanInteraction = interaction;
            menu = worldMenu;
            renderers = avatar == null ? Array.Empty<Renderer>() : avatar.GetComponentsInChildren<Renderer>(true);
            hasBounds = false;
            nextBoundsRefresh = 0f;
            EnsureLine();
        }

        private void Update()
        {
            var device = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            trackingSpace = QuestXrInputUtility.ResolveTrackingSpace(trackingSpace);
            var usingHand = QuestXrInputUtility.TryGetTrackedHandPointer(
                XRNode.RightHand,
                trackingSpace,
                out var pose,
                out var handPinch);
            var trigger = ReadTrigger(device);
            var triggerDown = trigger && !previousTrigger;
            previousTrigger = trigger;

            if (menu != null && menu.IsOpen)
            {
                SetLineVisible(false);
                Status = "Menu has pointer focus";
                return;
            }
            if (avatar == null || humanInteraction == null)
            {
                SetLineVisible(false);
                Status = "Avatar pointer waiting";
                return;
            }

            if (!usingHand && !QuestXrInputUtility.TryGetWorldPose(XRNode.RightHand, trackingSpace, out pose))
            {
                SetLineVisible(false);
                Status = "Right hand/controller unavailable";
                return;
            }

            RefreshBoundsIfNeeded();
            var ray = new Ray(pose.position, pose.rotation * Vector3.forward);
            if (!hasBounds || !avatarBounds.IntersectRay(ray, out var distance) || distance > maximumDistance)
            {
                SetLineVisible(false);
                Status = "Point at avatar";
                return;
            }

            var hitPoint = ray.GetPoint(distance);
            var grip = ReadGrip(device);
            var kind = ClassifyInteraction(avatarBounds, hitPoint, grip);
            EnsureLine();
            line.enabled = true;
            line.SetPosition(0, ray.origin);
            line.SetPosition(1, hitPoint);
            Status = kind == HumanInteractionKind.HeadPat ? "Aim: head pat" :
                kind == HumanInteractionKind.CheekPinch ? "Aim: cheek pinch" : "Aim: handshake";

            if (!triggerDown)
            {
                return;
            }

            humanInteraction.SimulateInteraction(kind, reactionSeconds);
            if (!usingHand)
            {
                device.SendHapticImpulse(0u, .4f, .07f);
            }
            Debug.Log($"[AvatarPointer] Triggered {kind} at {hitPoint:F3}.", this);
        }

        public static HumanInteractionKind ClassifyInteraction(Bounds bounds, Vector3 hitPoint, bool gripHeld)
        {
            var normalizedHeight = Mathf.InverseLerp(bounds.min.y, bounds.max.y, hitPoint.y);
            if (normalizedHeight >= .62f)
            {
                return gripHeld ? HumanInteractionKind.CheekPinch : HumanInteractionKind.HeadPat;
            }
            return HumanInteractionKind.Handshake;
        }

        private void RefreshBoundsIfNeeded()
        {
            if (Time.unscaledTime < nextBoundsRefresh && hasBounds)
            {
                return;
            }
            nextBoundsRefresh = Time.unscaledTime + .25f;
            hasBounds = false;
            for (var i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null || !renderers[i].enabled)
                {
                    continue;
                }
                if (!hasBounds)
                {
                    avatarBounds = renderers[i].bounds;
                    hasBounds = true;
                }
                else
                {
                    avatarBounds.Encapsulate(renderers[i].bounds);
                }
            }
        }

        private void EnsureLine()
        {
            if (line != null)
            {
                return;
            }
            var lineObject = new GameObject("Avatar Interaction Pointer");
            lineObject.transform.SetParent(transform, false);
            line = lineObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.startWidth = .004f;
            line.endWidth = .002f;
            line.numCapVertices = 4;
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            lineMaterial = new Material(shader);
            var color = new Color(1f, .46f, .62f, .9f);
            lineMaterial.color = color;
            if (lineMaterial.HasProperty("_BaseColor")) lineMaterial.SetColor("_BaseColor", color);
            line.material = lineMaterial;
            line.startColor = color;
            line.endColor = new Color(1f, .46f, .62f, .35f);
            line.enabled = false;
        }

        private void SetLineVisible(bool visible)
        {
            if (line != null)
            {
                line.enabled = visible;
            }
        }

        private bool ReadTrigger(InputDevice device)
        {
            if (!device.isValid)
            {
                return false;
            }
            if (device.TryGetFeatureValue(CommonUsages.triggerButton, out var pressed) && pressed)
            {
                return true;
            }
            return device.TryGetFeatureValue(CommonUsages.trigger, out var value) && value >= triggerThreshold;
        }

        private static bool ReadGrip(InputDevice device)
        {
            if (device.TryGetFeatureValue(CommonUsages.gripButton, out var pressed) && pressed)
            {
                return true;
            }
            return device.TryGetFeatureValue(CommonUsages.grip, out var value) && value >= .65f;
        }

        private void OnDestroy()
        {
            if (lineMaterial != null)
            {
                Destroy(lineMaterial);
            }
        }
    }
}
