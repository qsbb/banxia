using System.Collections.Generic;
using UMT;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Neutral local presence only: smooth head attention, natural blinking, and
    /// subtle body sway. Conversation and touch reactions remain backend-driven.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10500)]
    public sealed class AvatarPresence : MonoBehaviour
    {
        [SerializeField] private bool attentionEnabled = true;
        [SerializeField] private bool blinkEnabled = true;
        [SerializeField] private bool breathingEnabled = true;
        [SerializeField] private float attentionRange = 4.5f;
        [SerializeField] private float turnDegrees = 32f;
        [SerializeField] private bool bodyTurnEnabled = true;
        [SerializeField, Range(25f, 100f)] private float bodyTurnThreshold = 58f;
        [SerializeField, Range(15f, 180f)] private float bodyTurnSpeed = 78f;
        [SerializeField, Range(35f, 70f)] private float bodyTurnMinimumStep = 35f;
        [SerializeField, Range(40f, 70f)] private float bodyTurnMaximumStep = 58f;
        [SerializeField, Range(0f, 35f)] private float headTurnResidualDegrees = 22f;
        [SerializeField, Range(.35f, 1.2f)] private float bodyTurnMinimumDuration = .48f;
        [SerializeField, Range(.5f, 1.5f)] private float bodyTurnMaximumDuration = .9f;
        [SerializeField] private bool avatarFacesNegativeZ = false;
        [SerializeField] private float attentionSpeed = 5f;
        [SerializeField, Range(.05f, 1f)] private float breathPitchDegrees = .28f;
        [SerializeField] private float breathCyclesPerMinute = 12f;
        [SerializeField, Range(0f, 1.5f)] private float idleSwayDegrees = .45f;
        [SerializeField, Range(1f, 8f)] private float idleSwayCyclesPerMinute = 3.6f;

        private AvatarController avatar;
        private AvatarHumanInteraction humanInteraction;
        private Transform head;
        private Transform chest;
        private Quaternion headRestRotation;
        private Quaternion chestRestRotation;
        private Transform lowerBody;
        private Transform leftLeg;
        private Transform rightLeg;
        private Quaternion lowerBodyRestRotation;
        private Quaternion leftLegRestRotation;
        private Quaternion rightLegRestRotation;
        private bool bodyTurnActive;
        private float bodyTurnClock;
        private float bodyTurnDuration;
        private float bodyTurnDirection;
        private Quaternion bodyTurnStartRotation;
        private Quaternion bodyTurnTargetRotation;
        private readonly List<BlinkShape> blinkShapes = new List<BlinkShape>();
        private float nextBlinkTime;
        private float blinkUntil;
        private float blinkStart;

        private struct BlinkShape
        {
            public SkinnedMeshRenderer renderer;
            public int index;
            public float baseWeight;
        }

        public string Status { get; private set; } = "Waiting for avatar";

        public void Bind(AvatarController targetAvatar)
        {
            Restore();
            avatar = targetAvatar;
            humanInteraction = avatar == null ? null : avatar.GetComponentInParent<AvatarHumanInteraction>();
            head = null;
            chest = null;
            lowerBody = null;
            leftLeg = null;
            rightLeg = null;
            blinkShapes.Clear();
            if (avatar == null)
            {
                Status = "Waiting for avatar";
                return;
            }

            var bones = avatar.GetComponentsInChildren<MMDBoneTransform>(true);
            head = FindBone(bones, "head", "head", "头");
            chest = FindBone(bones, "upperbody", "chest", "上半身");
            lowerBody = FindBone(bones, "lowerbody", "lower body", "hips", "center", "下半身");
            leftLeg = FindBone(bones, "leftleg", "left leg", "legl", "左足");
            rightLeg = FindBone(bones, "rightleg", "right leg", "legr", "右足");
            if (head != null) headRestRotation = head.localRotation;
            if (chest != null) chestRestRotation = chest.localRotation;
            if (lowerBody != null) lowerBodyRestRotation = lowerBody.localRotation;
            if (leftLeg != null) leftLegRestRotation = leftLeg.localRotation;
            if (rightLeg != null) rightLegRestRotation = rightLeg.localRotation;
            CacheBlinkShapes();
            ScheduleBlink(Time.unscaledTime);
            Status = $"Presence ready | head:{(head == null ? "no" : "yes")} blink:{blinkShapes.Count}";
        }

        public void SetLocalPresenceEnabled(bool enabled)
        {
            attentionEnabled = enabled;
            blinkEnabled = enabled;
            breathingEnabled = enabled;
            if (!enabled) Restore();
        }

        private void LateUpdate()
        {
            if (avatar == null) return;
            ApplyBodyTurn();
            ApplyAttention();
            ApplyBreathing();
            ApplyBlink();
        }

        private void ApplyBodyTurn()
        {
            var blocked = !bodyTurnEnabled || avatar == null || Camera.main == null ||
                (humanInteraction != null && humanInteraction.HasSemanticContact) ||
                IsActionTurnBlocked(avatar.CurrentAction);
            if (blocked)
            {
                CancelBodyTurnPose();
                return;
            }

            if (bodyTurnActive)
            {
                bodyTurnClock += Time.unscaledDeltaTime;
                var progress = SmoothTurnProgress(Mathf.Clamp01(bodyTurnClock / bodyTurnDuration));
                avatar.transform.rotation = Quaternion.Slerp(bodyTurnStartRotation, bodyTurnTargetRotation, progress);
                ApplyTurnPose(progress);
                if (bodyTurnClock >= bodyTurnDuration)
                {
                    avatar.transform.rotation = bodyTurnTargetRotation;
                    CancelBodyTurnPose();
                }
                return;
            }

            var towardUser = Vector3.ProjectOnPlane(
                Camera.main.transform.position - avatar.transform.position,
                Vector3.up);
            if (towardUser.sqrMagnitude < .01f)
            {
                return;
            }

            towardUser.Normalize();
            var currentFacing = avatarFacesNegativeZ ? -avatar.transform.forward : avatar.transform.forward;
            var yaw = Vector3.SignedAngle(currentFacing, towardUser, Vector3.up);
            var step = CalculateTurnStep(
                yaw,
                bodyTurnThreshold,
                headTurnResidualDegrees,
                bodyTurnMinimumStep,
                bodyTurnMaximumStep);
            if (Mathf.Abs(step) < .01f)
            {
                return;
            }

            bodyTurnActive = true;
            bodyTurnClock = 0f;
            bodyTurnDirection = Mathf.Sign(step);
            bodyTurnDuration = Mathf.Clamp(
                Mathf.Abs(step) / Mathf.Max(1f, bodyTurnSpeed),
                bodyTurnMinimumDuration,
                bodyTurnMaximumDuration);
            bodyTurnStartRotation = avatar.transform.rotation;
            bodyTurnTargetRotation = Quaternion.AngleAxis(step, Vector3.up) * bodyTurnStartRotation;
        }

        public static bool ShouldTurnBody(float signedYaw, float threshold)
        {
            return Mathf.Abs(signedYaw) > Mathf.Max(0f, threshold);
        }

        public static float CalculateTurnStep(
            float signedYaw,
            float threshold,
            float headResidual,
            float minimumStep,
            float maximumStep)
        {
            if (!ShouldTurnBody(signedYaw, threshold))
            {
                return 0f;
            }

            var magnitude = Mathf.Abs(signedYaw) - Mathf.Max(0f, headResidual);
            magnitude = Mathf.Clamp(
                magnitude,
                Mathf.Max(0f, minimumStep),
                Mathf.Max(minimumStep, maximumStep));
            return Mathf.Sign(signedYaw) * magnitude;
        }

        public static float SmoothTurnProgress(float normalizedTime)
        {
            var value = Mathf.Clamp01(normalizedTime);
            return value * value * (3f - 2f * value);
        }

        private void ApplyTurnPose(float progress)
        {
            var weight = Mathf.Sin(Mathf.Clamp01(progress) * Mathf.PI);
            if (lowerBody != null)
            {
                lowerBody.localRotation = lowerBodyRestRotation *
                    Quaternion.Euler(0f, -bodyTurnDirection * 2.5f * weight, bodyTurnDirection * .8f * weight);
            }
            if (leftLeg != null)
            {
                leftLeg.localRotation = leftLegRestRotation *
                    Quaternion.Euler(bodyTurnDirection * 3.5f * weight, 0f, 0f);
            }
            if (rightLeg != null)
            {
                rightLeg.localRotation = rightLegRestRotation *
                    Quaternion.Euler(-bodyTurnDirection * 2.5f * weight, 0f, 0f);
            }
        }
        private static bool IsActionTurnBlocked(string action)
        {
            switch (action)
            {
                case "wave":
                case "bow":
                case "nod":
                case "sway":
                case "vmd":
                    return true;
                default:
                    return false;
            }
        }
        private void ApplyAttention()
        {
            if ((avatar != null && IsActionTurnBlocked(avatar.CurrentAction)) ||
                (humanInteraction != null && humanInteraction.HasSemanticContact))
            {
                return;
            }
            if (!attentionEnabled || head == null || Camera.main == null)
            {
                if (head != null) head.localRotation = Quaternion.Slerp(head.localRotation, headRestRotation, Time.unscaledDeltaTime * attentionSpeed);
                return;
            }

            var offset = Camera.main.transform.position - head.position;
            if (offset.sqrMagnitude > attentionRange * attentionRange || offset.sqrMagnitude < .01f)
            {
                head.localRotation = Quaternion.Slerp(head.localRotation, headRestRotation, Time.unscaledDeltaTime * attentionSpeed);
                return;
            }

            var local = avatar.transform.InverseTransformDirection(offset.normalized);
            var yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -turnDegrees, turnDegrees);
            var pitch = Mathf.Clamp(-Mathf.Asin(local.y) * Mathf.Rad2Deg, -turnDegrees * .45f, turnDegrees * .45f);
            var target = headRestRotation * Quaternion.Euler(pitch, yaw, 0f);
            head.localRotation = Quaternion.Slerp(head.localRotation, target, Time.unscaledDeltaTime * attentionSpeed);
        }

        private void ApplyBreathing()
        {
            if (chest == null ||
                (avatar != null && IsActionTurnBlocked(avatar.CurrentAction)) ||
                (humanInteraction != null && humanInteraction.HasSemanticContact))
            {
                return;
            }
            if (!breathingEnabled)
            {
                chest.localRotation = Quaternion.Slerp(chest.localRotation, chestRestRotation, Time.unscaledDeltaTime * attentionSpeed);
                return;
            }

            var phase = Time.unscaledTime * breathCyclesPerMinute / 60f * Mathf.PI * 2f;
            var idlePhase = Time.unscaledTime * idleSwayCyclesPerMinute / 60f * Mathf.PI * 2f;
            var idleMotion = avatar != null && avatar.CurrentAction == "idle";
            var yaw = idleMotion ? Mathf.Sin(idlePhase) * idleSwayDegrees * .65f : 0f;
            var roll = idleMotion ? Mathf.Sin(idlePhase * .73f + 1.1f) * idleSwayDegrees : 0f;
            var target = chestRestRotation * Quaternion.Euler(
                Mathf.Sin(phase) * breathPitchDegrees,
                yaw,
                roll);
            chest.localRotation = Quaternion.Slerp(
                chest.localRotation,
                target,
                Time.unscaledDeltaTime * attentionSpeed);
        }

        private void ApplyBlink()
        {
            if (!blinkEnabled || blinkShapes.Count == 0) return;
            var now = Time.unscaledTime;
            if (now >= nextBlinkTime && blinkUntil <= now)
            {
                blinkStart = now;
                blinkUntil = now + .14f;
                ScheduleBlink(now);
            }

            var closing = blinkUntil > now;
            var progress = closing ? Mathf.Clamp01((now - blinkStart) / .14f) : 0f;
            var weight = progress <= .5f ? progress * 200f : (1f - progress) * 200f;
            for (var i = 0; i < blinkShapes.Count; i++)
            {
                var shape = blinkShapes[i];
                shape.renderer.SetBlendShapeWeight(shape.index, Mathf.Clamp(shape.baseWeight + weight, 0f, 100f));
            }
        }

        private void ScheduleBlink(float now)
        {
            nextBlinkTime = now + Random.Range(2.4f, 5.2f);
        }

        private void CacheBlinkShapes()
        {
            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var r = 0; r < renderers.Length; r++)
            {
                var mesh = renderers[r].sharedMesh;
                if (mesh == null) continue;
                for (var i = 0; i < mesh.blendShapeCount; i++)
                {
                    var name = Normalize(mesh.GetBlendShapeName(i));
                    if (!name.Contains("blink") && !name.Contains("wink") && !name.Contains("まばたき") && !name.Contains("瞬")) continue;
                    blinkShapes.Add(new BlinkShape { renderer = renderers[r], index = i, baseWeight = renderers[r].GetBlendShapeWeight(i) });
                }
            }
        }

        private static Transform FindBone(MMDBoneTransform[] bones, params string[] names)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                for (var i = 0; i < bones.Length; i++)
                {
                    var current = Normalize(bones[i].boneName);
                    for (var n = 0; n < names.Length; n++)
                    {
                        var wanted = Normalize(names[n]);
                        if (pass == 0 ? current == wanted : current.Contains(wanted)) return bones[i].transform;
                    }
                }
            }
            return null;
        }

        private void CancelBodyTurnPose()
        {
            bodyTurnActive = false;
            bodyTurnClock = 0f;
            if (lowerBody != null) lowerBody.localRotation = lowerBodyRestRotation;
            if (leftLeg != null) leftLeg.localRotation = leftLegRestRotation;
            if (rightLeg != null) rightLeg.localRotation = rightLegRestRotation;
        }
        private void Restore()
        {
            CancelBodyTurnPose();
            if (head != null) head.localRotation = headRestRotation;
            if (chest != null) chest.localRotation = chestRestRotation;
            for (var i = 0; i < blinkShapes.Count; i++)
            {
                var shape = blinkShapes[i];
                if (shape.renderer != null) shape.renderer.SetBlendShapeWeight(shape.index, shape.baseWeight);
            }
        }

        private void OnDisable() => Restore();

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(".", string.Empty);
        }
    }
}