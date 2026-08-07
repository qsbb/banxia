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
    [DefaultExecutionOrder(10300)]
    public sealed class AvatarNaturalIdlePose : MonoBehaviour
    {
        [SerializeField, Range(0f, 55f)] private float armDropDegrees = 32f;
        [SerializeField, Range(0f, 25f)] private float elbowBendDegrees = 10f;
        [SerializeField, Range(0f, 15f)] private float handRelaxDegrees = 5f;
        [SerializeField] private AvatarIdlePreset preset = AvatarIdlePreset.Formal;
        [SerializeField, Range(1f, 20f)] private float poseBlendSpeed = 8f;
        private const string PresetPreferenceKey = "banxia.avatar.idle_preset_v2";

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


        public AvatarController Avatar => avatar;
        public bool IsBound => avatar != null;
        public AvatarIdlePreset Preset => preset;
        public string PresetDisplayName => GetPresetDisplayName(preset);

        private void Awake()
        {
            preset = (AvatarIdlePreset)Mathf.Clamp(
                PlayerPrefs.GetInt(PresetPreferenceKey, (int)AvatarIdlePreset.Formal),
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
            BuildDirectionalRelaxedPose();
            ApplyRelaxedArms(true);
            Debug.Log($"[IdlePose] Bound natural stance; arms={(leftUpper != null ? 1 : 0) + (rightUpper != null ? 1 : 0)}/2, hands={(leftHand != null ? 1 : 0) + (rightHand != null ? 1 : 0)}/2.", this);
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
                BuildArmPose(
                    leftUpper,
                    leftLower,
                    leftHand,
                    avatar.transform.TransformDirection(GetLeftUpperDirection()),
                    avatar.transform.TransformDirection(GetLeftLowerDirection()),
                    leftUpperBase,
                    leftLowerBase,
                    out leftUpperRelaxed,
                    out leftLowerRelaxed);
            }
            if (hasRightDirectionalPose)
            {
                BuildArmPose(
                    rightUpper,
                    rightLower,
                    rightHand,
                    avatar.transform.TransformDirection(GetRightUpperDirection()),
                    avatar.transform.TransformDirection(GetRightLowerDirection()),
                    rightUpperBase,
                    rightLowerBase,
                    out rightUpperRelaxed,
                    out rightLowerRelaxed);
            }
        }

        private static void BuildArmPose(
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
                    upperBase);

                // Solve the child after the upper arm has moved. Calculating both
                // targets from the original chain can fold an MMD forearm through
                // the torso when the upper-arm correction is large.
                upper.localRotation = upperTarget;
                lowerTarget = CalculateAlignedLocalRotation(
                    lower,
                    hand,
                    desiredLowerDirection,
                    lowerBase);
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
                case AvatarIdlePreset.Casual: return new Vector3(-.22f, -.97f, .03f);
                case AvatarIdlePreset.Formal: return new Vector3(-.09f, -.995f, .01f);
                default: return new Vector3(-.16f, -.98f, .04f);
            }
        }

        private Vector3 GetLeftLowerDirection()
        {
            switch (preset)
            {
                case AvatarIdlePreset.Casual: return new Vector3(-.06f, -.995f, .05f);
                case AvatarIdlePreset.Formal: return new Vector3(-.02f, -.999f, .02f);
                default: return new Vector3(-.04f, -.996f, .04f);
            }
        }
        private Vector3 GetRightUpperDirection()
        {
            switch (preset)
            {
                case AvatarIdlePreset.Casual: return new Vector3(.22f, -.97f, .03f);
                case AvatarIdlePreset.Formal: return new Vector3(.09f, -.995f, .01f);
                default: return new Vector3(.16f, -.98f, .04f);
            }
        }

        private Vector3 GetRightLowerDirection()
        {
            switch (preset)
            {
                case AvatarIdlePreset.Casual: return new Vector3(.06f, -.995f, .05f);
                case AvatarIdlePreset.Formal: return new Vector3(.02f, -.999f, .02f);
                default: return new Vector3(.04f, -.996f, .04f);
            }
        }
        private static Quaternion CalculateAlignedLocalRotation(
            Transform bone,
            Transform child,
            Vector3 desiredWorldDirection,
            Quaternion fallback)
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
            return bone.parent == null
                ? targetWorld
                : Quaternion.Inverse(bone.parent.rotation) * targetWorld;
        }

        private void ClearBones()
        {
            leftUpper = leftLower = leftHand = rightUpper = rightLower = rightHand = null;
            hasLeftDirectionalPose = hasRightDirectionalPose = false;

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
