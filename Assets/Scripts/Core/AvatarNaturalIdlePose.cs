using System;
using UMT;
using UnityEngine;

namespace QuestMmdPlayer
{
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
        private Vector3 visualRootBasePosition;
        private float breathingClock;

        public AvatarController Avatar => avatar;
        public bool IsBound => avatar != null;

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
            visualRootBasePosition = avatar.VisualRoot == null
                ? Vector3.zero
                : avatar.VisualRoot.localPosition;
            ApplyRelaxedArms();
            Debug.Log($"[IdlePose] Bound natural stance; arms={(leftUpper != null ? 1 : 0) + (rightUpper != null ? 1 : 0)}/2, hands={(leftHand != null ? 1 : 0) + (rightHand != null ? 1 : 0)}/2.", this);
        }

        private void LateUpdate()
        {
            if (avatar == null)
            {
                return;
            }

            // Keep the pose still enough for touch targeting, with only a small
            // breath-like vertical motion on the whole visual root.
            breathingClock += Time.unscaledDeltaTime;
            var visualRoot = avatar.VisualRoot;
            if (visualRoot != null && avatar.CurrentAction == "idle")
            {
                ApplyRelaxedArms();
                var breath = Mathf.Sin(breathingClock * 1.35f) * .0035f;
                visualRoot.localPosition = visualRootBasePosition + Vector3.up * breath;
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

        private void ApplyRelaxedArms()
        {
            if (leftUpper != null) leftUpper.localRotation = leftUpperBase * Quaternion.Euler(0f, 0f, armDropDegrees);
            if (leftLower != null) leftLower.localRotation = leftLowerBase * Quaternion.Euler(0f, 0f, elbowBendDegrees);
            if (leftHand != null) leftHand.localRotation = leftHandBase * Quaternion.Euler(0f, 0f, handRelaxDegrees);
            if (rightUpper != null) rightUpper.localRotation = rightUpperBase * Quaternion.Euler(0f, 0f, -armDropDegrees);
            if (rightLower != null) rightLower.localRotation = rightLowerBase * Quaternion.Euler(0f, 0f, -elbowBendDegrees);
            if (rightHand != null) rightHand.localRotation = rightHandBase * Quaternion.Euler(0f, 0f, -handRelaxDegrees);
        }

        private void ClearBones()
        {
            leftUpper = leftLower = leftHand = rightUpper = rightLower = rightHand = null;
            visualRootBasePosition = Vector3.zero;
            breathingClock = 0f;
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
