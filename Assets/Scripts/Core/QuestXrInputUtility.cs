using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

namespace QuestMmdPlayer
{
    public static class QuestXrInputUtility
    {
        private static readonly List<XRHandSubsystem> HandSubsystems = new List<XRHandSubsystem>();

        public static float RemapAxisOutsideDeadZone(float value, float deadZone)
        {
            var threshold = Mathf.Clamp(deadZone, 0f, .99f);
            var magnitude = Mathf.Abs(value);
            if (magnitude <= threshold)
            {
                return 0f;
            }

            return Mathf.Sign(value) * Mathf.Clamp01((magnitude - threshold) / (1f - threshold));
        }

        internal static Transform ResolveTrackingSpace(Transform configured = null)
        {
            if (configured != null)
            {
                return configured;
            }

            var camera = Camera.main;
            var origin = camera == null ? null : camera.GetComponentInParent<XROrigin>();
            if (origin == null)
            {
                return null;
            }

            return origin.CameraFloorOffsetObject == null
                ? origin.transform
                : origin.CameraFloorOffsetObject.transform;
        }

        internal static bool TryGetWorldPose(XRNode node, Transform trackingSpace, out Pose pose)
        {
            pose = default;
            var device = InputDevices.GetDeviceAtXRNode(node);
            if (!device.isValid ||
                !device.TryGetFeatureValue(CommonUsages.devicePosition, out var localPosition) ||
                !device.TryGetFeatureValue(CommonUsages.deviceRotation, out var localRotation))
            {
                return false;
            }

            pose = trackingSpace == null
                ? new Pose(localPosition, localRotation)
                : new Pose(
                    trackingSpace.TransformPoint(localPosition),
                    trackingSpace.rotation * localRotation);
            return true;
        }

        internal static bool TryGetTrackedHandPointer(
            XRNode node,
            Transform trackingSpace,
            out Pose pose,
            out bool pinch)
        {
            pose = default;
            pinch = false;
            var subsystem = FindHandSubsystem();
            if (subsystem == null)
            {
                return false;
            }

            var hand = node == XRNode.LeftHand ? subsystem.leftHand : subsystem.rightHand;
            if (!hand.isTracked ||
                !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var tip) ||
                !hand.GetJoint(XRHandJointID.IndexProximal).TryGetPose(out var proximal))
            {
                return false;
            }

            var direction = tip.position - proximal.position;
            if (direction.sqrMagnitude < .000001f)
            {
                return false;
            }

            var up = Vector3.up;
            if (hand.GetJoint(XRHandJointID.Palm).TryGetPose(out var palm))
            {
                up = palm.rotation * Vector3.up;
            }
            if (Mathf.Abs(Vector3.Dot(direction.normalized, up.normalized)) > .96f)
            {
                up = Vector3.up;
            }

            var localPose = new Pose(tip.position, Quaternion.LookRotation(direction.normalized, up));
            pose = trackingSpace == null
                ? localPose
                : new Pose(
                    trackingSpace.TransformPoint(localPose.position),
                    trackingSpace.rotation * localPose.rotation);

            if (hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumb))
            {
                pinch = Vector3.Distance(tip.position, thumb.position) <= .032f;
            }
            return true;
        }

        private static XRHandSubsystem FindHandSubsystem()
        {
            for (var index = 0; index < HandSubsystems.Count; index++)
            {
                if (HandSubsystems[index] != null && HandSubsystems[index].running)
                {
                    return HandSubsystems[index];
                }
            }

            HandSubsystems.Clear();
            SubsystemManager.GetSubsystems(HandSubsystems);
            for (var index = 0; index < HandSubsystems.Count; index++)
            {
                if (HandSubsystems[index] != null && HandSubsystems[index].running)
                {
                    return HandSubsystems[index];
                }
            }
            return null;
        }
    }
}
