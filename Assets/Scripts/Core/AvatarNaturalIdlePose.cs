using System;
using UMT;
using UnityEngine;

namespace QuestMmdPlayer
{
    public enum AvatarIdlePreset
    {
        Relaxed = 0,
        Casual = 1,
        Formal = 2
    }

    /// <summary>
    /// Gives a runtime-loaded MMD avatar a calm standing pose before the human
    /// interaction system captures its reaction baseline. MMD imports often
    /// arrive in a presentation/T-pose; this keeps the companion relaxed while
    /// still leaving the hands and head available for interaction reactions.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(9800)]
    public sealed class AvatarNaturalIdlePose : MonoBehaviour
    {
        [SerializeField, Range(0f, 110f)] private float armDropDegrees = 78f;
        [SerializeField, Range(0f, 25f)] private float elbowBendDegrees = 8f;
        [SerializeField, Range(0f, 15f)] private float handRelaxDegrees;
        [SerializeField, Range(20f, 130f)] private float maxArmCorrectionDegrees = 105f;
        [SerializeField] private AvatarIdlePreset preset = AvatarIdlePreset.Relaxed;
        [SerializeField, Range(1f, 20f)] private float poseBlendSpeed = 8f;
        private const string PresetPreferenceKey = "banxia.avatar.idle_preset_v3";

        private AvatarController avatar;
        private Transform leftUpper;
        private Transform leftLower;
        private Transform leftHand;
        private Transform rightUpper;
        private Transform rightLower;
        private Transform rightHand;
        private Quaternion leftUpperBase;
        private Quaternion leftLowerBase;
        private Quaternion leftHandBase;
        private Quaternion rightUpperBase;
        private Quaternion rightLowerBase;
        private Quaternion rightHandBase;
        private Quaternion leftUpperRelaxed;
        private Quaternion leftLowerRelaxed;
        private Quaternion rightUpperRelaxed;
        private Quaternion rightLowerRelaxed;
        private bool hasLeftDirectionalPose;
        private bool hasRightDirectionalPose;
        private MMDRigidBody[] bodyCollisionVolumes = Array.Empty<MMDRigidBody>();
        private const float SelfCollisionMargin = .012f;


        public AvatarController Avatar => avatar;
        public bool IsBound => avatar != null;
        public AvatarIdlePreset Preset => preset;
        public string PresetDisplayName => GetPresetDisplayName(preset);

        private void Awake()
        {
            preset = (AvatarIdlePreset)Mathf.Clamp(
                PlayerPrefs.GetInt(PresetPreferenceKey, (int)AvatarIdlePreset.Relaxed),
                (int)AvatarIdlePreset.Relaxed,
                (int)AvatarIdlePreset.Formal);
        }

        public void SetPreset(AvatarIdlePreset next)
        {
            preset = next;
            PlayerPrefs.SetInt(PresetPreferenceKey, (int)preset);
            PlayerPrefs.Save();
            if (avatar != null)
            {
                BuildDirectionalRelaxedPose();
            }
        }

        public void CyclePreset(int direction = 1)
        {
            var count = 3;
            var next = ((int)preset + direction) % count;
            if (next < 0) next += count;
            SetPreset((AvatarIdlePreset)next);
        }

        private static string GetPresetDisplayName(AvatarIdlePreset value)
        {
            switch (value)
            {
                case AvatarIdlePreset.Casual: return "随意站姿";
                case AvatarIdlePreset.Formal: return "稳重站姿";
                default: return "自然放松";
            }
        }

        public void Bind(AvatarController target)
        {
            if (avatar == target)
            {
                return;
            }

            avatar = target;
            ClearBones();
            if (avatar == null)
            {
                return;
            }

            var all = avatar.GetComponentsInChildren<MMDBoneTransform>(true);
            leftUpper = Find(all, "leftupperarm", "upperarm_l", "左腕", "左肩");
            leftLower = Find(all, "leftlowerarm", "lowerarm_l", "左ひじ", "左肘");
            leftHand = Find(all, "lefthand", "hand_l", "左手首");
            rightUpper = Find(all, "rightupperarm", "upperarm_r", "右腕", "右肩");
            rightLower = Find(all, "rightlowerarm", "lowerarm_r", "右ひじ", "右肘");
            rightHand = Find(all, "righthand", "hand_r", "右手首");

            CaptureBases();
            bodyCollisionVolumes = FindBodyCollisionVolumes(avatar.GetComponentsInChildren<MMDRigidBody>(true));
            BuildDirectionalRelaxedPose();
            ApplyRelaxedArms(true);
            Debug.Log($"[IdlePose] Bound natural stance; arms={(leftUpper != null ? 1 : 0) + (rightUpper != null ? 1 : 0)}/2, hands={(leftHand != null ? 1 : 0) + (rightHand != null ? 1 : 0)}/2, bodyVolumes={bodyCollisionVolumes.Length}.", this);
        }

        private void LateUpdate()
        {
            if (avatar == null)
            {
                return;
            }

            // Keep both feet planted. Subtle breathing is applied as a tiny
            // upper-body rotation by AvatarPresence instead of moving the model root.
            if (avatar.CurrentAction == "idle")
            {
                ApplyRelaxedArms();
            }
        }

        private void CaptureBases()
        {
            leftUpperBase = leftUpper == null ? Quaternion.identity : leftUpper.localRotation;
            leftLowerBase = leftLower == null ? Quaternion.identity : leftLower.localRotation;
            leftHandBase = leftHand == null ? Quaternion.identity : leftHand.localRotation;
            rightUpperBase = rightUpper == null ? Quaternion.identity : rightUpper.localRotation;
            rightLowerBase = rightLower == null ? Quaternion.identity : rightLower.localRotation;
            rightHandBase = rightHand == null ? Quaternion.identity : rightHand.localRotation;
        }

        private void ApplyRelaxedArms(bool immediate = false)
        {
            var blend = immediate
                ? 1f
                : 1f - Mathf.Exp(-Mathf.Max(1f, poseBlendSpeed) * Time.unscaledDeltaTime);
            var leftUpperTarget = hasLeftDirectionalPose
                ? leftUpperRelaxed
                : leftUpperBase * Quaternion.Euler(0f, 0f, armDropDegrees);
            var leftLowerTarget = hasLeftDirectionalPose
                ? leftLowerRelaxed
                : leftLowerBase * Quaternion.Euler(0f, 0f, elbowBendDegrees);
            var rightUpperTarget = hasRightDirectionalPose
                ? rightUpperRelaxed
                : rightUpperBase * Quaternion.Euler(0f, 0f, -armDropDegrees);
            var rightLowerTarget = hasRightDirectionalPose
                ? rightLowerRelaxed
                : rightLowerBase * Quaternion.Euler(0f, 0f, -elbowBendDegrees);

            if (leftUpper != null) leftUpper.localRotation = Quaternion.Slerp(leftUpper.localRotation, leftUpperTarget, blend);
            if (leftLower != null) leftLower.localRotation = Quaternion.Slerp(leftLower.localRotation, leftLowerTarget, blend);
            if (leftHand != null) leftHand.localRotation = Quaternion.Slerp(
                leftHand.localRotation,
                leftHandBase * Quaternion.Euler(0f, 0f, handRelaxDegrees),
                blend);
            if (rightUpper != null) rightUpper.localRotation = Quaternion.Slerp(rightUpper.localRotation, rightUpperTarget, blend);
            if (rightLower != null) rightLower.localRotation = Quaternion.Slerp(rightLower.localRotation, rightLowerTarget, blend);
            if (rightHand != null) rightHand.localRotation = Quaternion.Slerp(
                rightHand.localRotation,
                rightHandBase * Quaternion.Euler(0f, 0f, -handRelaxDegrees),
                blend);
        }

        private void BuildDirectionalRelaxedPose()
        {
            hasLeftDirectionalPose = leftUpper != null && leftLower != null && leftHand != null;
            hasRightDirectionalPose = rightUpper != null && rightLower != null && rightHand != null;
            if (hasLeftDirectionalPose)
            {
                BuildArmPoseWithClearance(
                    leftUpper,
                    leftLower,
                    leftHand,
                    avatar.transform.TransformDirection(GetLeftUpperDirection()),
                    avatar.transform.TransformDirection(GetLeftLowerDirection()),
                    leftUpperBase,
                    leftLowerBase,
                    -1f,
                    out leftUpperRelaxed,
                    out leftLowerRelaxed);
            }
            if (hasRightDirectionalPose)
            {
                BuildArmPoseWithClearance(
                    rightUpper,
                    rightLower,
                    rightHand,
                    avatar.transform.TransformDirection(GetRightUpperDirection()),
                    avatar.transform.TransformDirection(GetRightLowerDirection()),
                    rightUpperBase,
                    rightLowerBase,
                    1f,
                    out rightUpperRelaxed,
                    out rightLowerRelaxed);
            }
        }

        private void BuildArmPoseWithClearance(
            Transform upper,
            Transform lower,
            Transform hand,
            Vector3 desiredUpperDirection,
            Vector3 desiredLowerDirection,
            Quaternion upperBase,
            Quaternion lowerBase,
            float side,
            out Quaternion upperTarget,
            out Quaternion lowerTarget)
        {
            var outward = avatar.transform.right * Mathf.Sign(side);
            var forward = avatar.transform.forward;
            var handRadius = EstimateHandRadius(hand);
            upperTarget = upperBase;
            lowerTarget = lowerBase;
            for (var attempt = 0; attempt < 7; attempt++)
            {
                var upperDirection = (desiredUpperDirection + outward * (attempt * .025f) + forward * (attempt * .012f)).normalized;
                var lowerDirection = (desiredLowerDirection + outward * (attempt * .045f) + forward * (attempt * .025f)).normalized;
                BuildArmPose(
                    upper,
                    lower,
                    hand,
                    upperDirection,
                    lowerDirection,
                    upperBase,
                    lowerBase,
                    out upperTarget,
                    out lowerTarget);
                if (bodyCollisionVolumes.Length == 0 ||
                    MeasureHandClearance(upper, lower, hand, upperTarget, lowerTarget) >= handRadius + SelfCollisionMargin)
                {
                    break;
                }
            }
        }

        private float MeasureHandClearance(
            Transform upper,
            Transform lower,
            Transform hand,
            Quaternion upperTarget,
            Quaternion lowerTarget)
        {
            var originalUpper = upper.localRotation;
            var originalLower = lower.localRotation;
            try
            {
                upper.localRotation = upperTarget;
                lower.localRotation = lowerTarget;
                var minimum = float.PositiveInfinity;
                for (var index = 0; index < bodyCollisionVolumes.Length; index++)
                {
                    minimum = Mathf.Min(minimum, SignedDistanceToRigidBody(bodyCollisionVolumes[index], hand.position));
                }
                return minimum;
            }
            finally
            {
                upper.localRotation = originalUpper;
                lower.localRotation = originalLower;
            }
        }

        private float EstimateHandRadius(Transform hand)
        {
            var radius = .035f;
            var bodies = avatar.GetComponentsInChildren<MMDRigidBody>(true);
            for (var index = 0; index < bodies.Length; index++)
            {
                var body = bodies[index];
                if (body == null || body.relatedBone == null || body.relatedBone.transform != hand)
                {
                    continue;
                }
                radius = Mathf.Max(radius, ShapeRadius(body));
            }
            return Mathf.Clamp(radius, .025f, .08f);
        }

        private static MMDRigidBody[] FindBodyCollisionVolumes(MMDRigidBody[] bodies)
        {
            if (bodies == null || bodies.Length == 0)
            {
                return Array.Empty<MMDRigidBody>();
            }
            return Array.FindAll(bodies, body =>
            {
                if (body == null || body.relatedBone == null)
                {
                    return false;
                }
                var name = Normalize(body.relatedBone.boneName);
                return name.Contains("leg") || name.Contains("knee") || name.Contains("thigh") ||
                       name.Contains("hip") || name.Contains("pelvis") || name.Contains("lowerbody") ||
                       name.Contains("足") || name.Contains("ひざ") || name.Contains("膝") ||
                       name.Contains("腰") || name.Contains("下半身");
            });
        }

        public static float SignedDistanceToRigidBody(MMDRigidBody body, Vector3 worldPoint)
        {
            if (body == null)
            {
                return float.PositiveInfinity;
            }
            var local = body.transform.InverseTransformPoint(worldPoint);
            var size = new Vector3(body.size.x, body.size.y, body.size.z);
            float localDistance;
            switch (body.shape)
            {
                case PMXRigidBody.Shape.Sphere:
                    localDistance = local.magnitude - Mathf.Max(.0001f, size.x);
                    break;
                case PMXRigidBody.Shape.Box:
                    var delta = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z)) - size;
                    var outside = new Vector3(Mathf.Max(0f, delta.x), Mathf.Max(0f, delta.y), Mathf.Max(0f, delta.z)).magnitude;
                    var inside = Mathf.Min(Mathf.Max(delta.x, Mathf.Max(delta.y, delta.z)), 0f);
                    localDistance = outside + inside;
                    break;
                default:
                    var halfCylinder = Mathf.Max(0f, size.y) * .5f;
                    var nearestY = Mathf.Clamp(local.y, -halfCylinder, halfCylinder);
                    localDistance = Vector3.Distance(local, new Vector3(0f, nearestY, 0f)) - Mathf.Max(.0001f, size.x);
                    break;
            }
            var scale = body.transform.lossyScale;
            return localDistance * Mathf.Max(.0001f, Mathf.Min(Mathf.Abs(scale.x), Mathf.Min(Mathf.Abs(scale.y), Mathf.Abs(scale.z))));
        }

        private static float ShapeRadius(MMDRigidBody body)
        {
            var size = new Vector3(body.size.x, body.size.y, body.size.z);
            switch (body.shape)
            {
                case PMXRigidBody.Shape.Sphere: return Mathf.Abs(size.x);
                case PMXRigidBody.Shape.Box: return Mathf.Max(.01f, Mathf.Min(Mathf.Abs(size.x), Mathf.Abs(size.z)));
                default: return Mathf.Abs(size.x);
            }
        }

        private void BuildArmPose(
            Transform upper,
            Transform lower,
            Transform hand,
            Vector3 desiredUpperDirection,
            Vector3 desiredLowerDirection,
            Quaternion upperBase,
            Quaternion lowerBase,
            out Quaternion upperTarget,
            out Quaternion lowerTarget)
        {
            var originalUpper = upper.localRotation;
            var originalLower = lower.localRotation;
            try
            {
                upper.localRotation = upperBase;
                lower.localRotation = lowerBase;
                upperTarget = CalculateAlignedLocalRotation(
                    upper,
                    lower,
                    desiredUpperDirection,
                    upperBase,
                    maxArmCorrectionDegrees);

                // Solve the child after the upper arm has moved. Calculating both
                // targets from the original chain can fold an MMD forearm through
                // the torso when the upper-arm correction is large.
                upper.localRotation = upperTarget;
                lowerTarget = CalculateAlignedLocalRotation(
                    lower,
                    hand,
                    desiredLowerDirection,
                    lowerBase,
                    maxArmCorrectionDegrees);
            }
            finally
            {
                upper.localRotation = originalUpper;
                lower.localRotation = originalLower;
            }
        }
        private Vector3 GetLeftUpperDirection()
        {
            switch (preset)
            {
                case AvatarIdlePreset.Casual: return new Vector3(-.27f, -.955f, .12f);
                case AvatarIdlePreset.Formal: return new Vector3(-.14f, -.988f, .06f);
                default: return new Vector3(-.20f, -.975f, .09f);
            }
        }

        private Vector3 GetLeftLowerDirection()
        {
            switch (preset)
            {
                case AvatarIdlePreset.Casual: return new Vector3(-.22f, -.965f, .14f);
                case AvatarIdlePreset.Formal: return new Vector3(-.12f, -.990f, .08f);
                default: return new Vector3(-.18f, -.976f, .12f);
            }
        }
        private Vector3 GetRightUpperDirection()
        {
            switch (preset)
            {
                case AvatarIdlePreset.Casual: return new Vector3(.27f, -.955f, .12f);
                case AvatarIdlePreset.Formal: return new Vector3(.14f, -.988f, .06f);
                default: return new Vector3(.20f, -.975f, .09f);
            }
        }

        private Vector3 GetRightLowerDirection()
        {
            switch (preset)
            {
                case AvatarIdlePreset.Casual: return new Vector3(.22f, -.965f, .14f);
                case AvatarIdlePreset.Formal: return new Vector3(.12f, -.990f, .08f);
                default: return new Vector3(.18f, -.976f, .12f);
            }
        }
        private static Quaternion CalculateAlignedLocalRotation(
            Transform bone,
            Transform child,
            Vector3 desiredWorldDirection,
            Quaternion fallback,
            float maxCorrectionDegrees = 90f)
        {
            if (bone == null || child == null || desiredWorldDirection.sqrMagnitude < .000001f)
            {
                return fallback;
            }

            var currentDirection = child.position - bone.position;
            if (currentDirection.sqrMagnitude < .000001f)
            {
                return fallback;
            }

            var targetWorld = Quaternion.FromToRotation(
                currentDirection.normalized,
                desiredWorldDirection.normalized) * bone.rotation;
            var targetLocal = bone.parent == null
                ? targetWorld
                : Quaternion.Inverse(bone.parent.rotation) * targetWorld;
            return Quaternion.RotateTowards(
                fallback,
                targetLocal,
                Mathf.Clamp(maxCorrectionDegrees, 0f, 180f));
        }

        private void ClearBones()
        {
            leftUpper = leftLower = leftHand = rightUpper = rightLower = rightHand = null;
            hasLeftDirectionalPose = hasRightDirectionalPose = false;
            bodyCollisionVolumes = Array.Empty<MMDRigidBody>();

        }

        private static Transform Find(MMDBoneTransform[] all, params string[] names)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                for (var nameIndex = 0; nameIndex < names.Length; nameIndex++)
                {
                    var wanted = Normalize(names[nameIndex]);
                    for (var index = 0; index < all.Length; index++)
                    {
                        var bone = all[index];
                        if (bone == null) continue;
                        var actual = Normalize(bone.boneName);
                        if (pass == 0 ? actual == wanted : actual.Contains(wanted)) return bone.transform;
                    }
                }
            }
            return null;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(".", string.Empty);
        }
    }
}
