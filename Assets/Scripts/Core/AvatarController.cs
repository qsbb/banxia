using System;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Prototype avatar controller. It deliberately exposes provider-neutral
    /// methods so Meta Interaction SDK events can be connected later without
    /// changing the animation and state layer.
    /// </summary>
    [DefaultExecutionOrder(10400)]
    public sealed class AvatarController : MonoBehaviour
    {
        [SerializeField] private Transform visualRoot;
        [SerializeField] private float moveSpeed = 0.9f;
        [SerializeField] private float rotateSpeed = 70f;
        [SerializeField] private float scaleSpeed = 0.75f;

        private Vector3 initialPosition;
        private Quaternion initialRotation;
        private Vector3 initialScale;
        private float actionClock;
        private bool isPlaying = true;
        private string currentAction = "idle";
        private string currentEmotion = "neutral";
        private Transform upperBody;
        private Transform head;
        private Transform rightUpperArm;
        private Transform rightLowerArm;
        private Transform rightHand;
        private Quaternion upperBodyBase;
        private Quaternion headBase;
        private Quaternion rightUpperArmBase;
        private Quaternion rightLowerArmBase;
        private Quaternion rightHandBase;
        private bool actionPoseCaptured;
        private bool actionTransitionActive;
        private float actionTransitionClock;
        private const float ActionTransitionSeconds = .32f;
        private Quaternion transitionUpperBody;
        private Quaternion transitionHead;
        private Quaternion transitionRightUpperArm;
        private Quaternion transitionRightLowerArm;
        private Quaternion transitionRightHand;

        private const float WaveDuration = 3.1f;
        private const float BowDuration = 2.2f;
        private const float NodDuration = 2.1f;
        private const float SwayDuration = 3.4f;

        public event Action<string> ActionChanged;

        public Transform VisualRoot => visualRoot;

        public string CurrentAction => currentAction;
        public string CurrentEmotion => currentEmotion;
        public bool IsPlaying => isPlaying;

        public void Initialize(Transform modelRoot)
        {
            visualRoot = modelRoot;
            initialPosition = transform.position;
            initialRotation = transform.rotation;
            initialScale = transform.localScale;
            CaptureActionPose();
        }

        private void Awake()
        {
            initialPosition = transform.position;
            initialRotation = transform.rotation;
            initialScale = transform.localScale;
        }

        private void Update()
        {
#if UNITY_EDITOR && ENABLE_LEGACY_INPUT_MANAGER
            if (Application.isPlaying)
            {
                HandleEditorInput();
            }
#endif
        }

        private void LateUpdate()
        {
            if (!isPlaying || visualRoot == null)
            {
                return;
            }

            actionClock += Time.deltaTime;
            if (actionTransitionActive) actionTransitionClock += Time.unscaledDeltaTime;
            if (currentAction == "wave")
            {
                ApplyWave();
                if (actionClock >= WaveDuration)
                {
                    PlayAction("idle");
                }
            }
            else if (currentAction == "bow")
            {
                ApplyBow();
                if (actionClock >= BowDuration)
                {
                    PlayAction("idle");
                }
            }
            else if (currentAction == "nod")
            {
                ApplyNod();
                if (actionClock >= NodDuration)
                {
                    PlayAction("idle");
                }
            }
            else if (currentAction == "sway")
            {
                ApplySway();
                if (actionClock >= SwayDuration)
                {
                    PlayAction("idle");
                }
            }
            else if (currentAction == "idle")
            {
                // Idle is the captured natural pose. Applying it before the
                // transition lets an action return smoothly without a bind-pose pop.
                RestoreActionPose();
            }
            ApplyActionTransition();
        }

        public void CaptureActionPose()
        {
            RestoreActionPose();
            actionPoseCaptured = false;
            upperBody = head = rightUpperArm = rightLowerArm = rightHand = null;
            if (visualRoot == null)
            {
                return;
            }

            var bones = visualRoot.GetComponentsInChildren<UMT.MMDBoneTransform>(true);
            upperBody = FindBone(bones, "upperbody", "spine", "\u4e0a\u534a\u8eab", "\u4e0a\u534a\u8eab2");
            head = FindBone(bones, "head", "\u982d", "\u5934");
            rightUpperArm = FindBone(bones, "rightupperarm", "upperarmr", "\u53f3\u8155", "\u53f3\u80a9");
            rightLowerArm = FindBone(bones, "rightlowerarm", "lowerarmr", "\u53f3\u3072\u3058", "\u53f3\u8098");
            rightHand = FindBone(bones, "righthand", "handr", "\u53f3\u624b\u9996");

            upperBodyBase = RotationOf(upperBody);
            headBase = RotationOf(head);
            rightUpperArmBase = RotationOf(rightUpperArm);
            rightLowerArmBase = RotationOf(rightLowerArm);
            rightHandBase = RotationOf(rightHand);
            actionPoseCaptured = true;
        }

        private void ApplyWave()
        {
            if (!actionPoseCaptured)
            {
                CaptureActionPose();
            }
            var blend = ActionBlend(actionClock, WaveDuration, .38f);
            SetRotation(rightUpperArm, rightUpperArmBase,
                Quaternion.Euler(-10f, -8f, 58f), blend);
            SetRotation(rightLowerArm, rightLowerArmBase,
                Quaternion.Euler(0f, -22f, -64f), blend);

            if (rightUpperArm != null && rightLowerArm != null && rightHand != null)
            {
                var viewer = Camera.main;
                var towardViewer = viewer == null
                    ? transform.forward
                    : Vector3.ProjectOnPlane(viewer.transform.position - rightUpperArm.position, Vector3.up).normalized;
                if (towardViewer.sqrMagnitude < .0001f)
                {
                    towardViewer = transform.forward;
                }

                var side = transform.right;
                var headPosition = head == null
                    ? rightUpperArm.position + transform.up * .34f
                    : head.position - transform.up * .04f;
                var phase = Mathf.Sin(actionClock * 8.7f);
                var target = headPosition + side * (.22f + phase * .045f) +
                    transform.up * (Mathf.Sin(actionClock * 17.4f) * .012f) + towardViewer * .07f;
                var pole = rightUpperArm.position + side * .26f - transform.up * .12f + towardViewer * .12f;
                AvatarHumanInteraction.SolveTwoBoneIk(
                    rightUpperArm,
                    rightLowerArm,
                    rightHand,
                    target,
                    pole,
                    .96f,
                    blend);
            }

            var swing = Mathf.Sin(actionClock * 8.7f) * 28f;
            SetRotation(rightHand, rightHandBase,
                Quaternion.Euler(4f, swing, 10f), blend);
        }

        private void ApplyBow()
        {
            if (!actionPoseCaptured)
            {
                CaptureActionPose();
            }
            var blend = ActionBlend(actionClock, BowDuration, .42f);
            SetRotation(upperBody, upperBodyBase, Quaternion.Euler(24f, 0f, 0f), blend);
            SetRotation(head, headBase, Quaternion.Euler(-7f, 0f, 0f), blend);
        }

        private void ApplyNod()
        {
            if (!actionPoseCaptured)
            {
                CaptureActionPose();
            }
            var blend = ActionBlend(actionClock, NodDuration, .28f);
            var pitch = Mathf.Sin(actionClock * 7.2f - .6f) * 8.5f + 3f;
            SetRotation(head, headBase, Quaternion.Euler(pitch, 0f, 0f), blend);
            SetRotation(upperBody, upperBodyBase, Quaternion.Euler(pitch * .16f, 0f, 0f), blend);
        }

        private void ApplySway()
        {
            if (!actionPoseCaptured)
            {
                CaptureActionPose();
            }
            var blend = ActionBlend(actionClock, SwayDuration, .45f);
            var phase = Mathf.Sin(actionClock * 2.05f);
            SetRotation(upperBody, upperBodyBase, Quaternion.Euler(0f, phase * 2.2f, phase * 3.2f), blend);
            SetRotation(head, headBase, Quaternion.Euler(0f, -phase * 1.5f, -phase * 1.8f), blend);
        }

        private void CaptureTransitionPose()
        {
            if (!actionPoseCaptured)
            {
                CaptureActionPose();
            }
            transitionUpperBody = RotationOf(upperBody);
            transitionHead = RotationOf(head);
            transitionRightUpperArm = RotationOf(rightUpperArm);
            transitionRightLowerArm = RotationOf(rightLowerArm);
            transitionRightHand = RotationOf(rightHand);
            actionTransitionClock = 0f;
            actionTransitionActive = true;
        }

        private void ApplyActionTransition()
        {
            if (!actionTransitionActive)
            {
                return;
            }
            var amount = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(actionTransitionClock / ActionTransitionSeconds));
            BlendRotation(upperBody, transitionUpperBody, amount);
            BlendRotation(head, transitionHead, amount);
            BlendRotation(rightUpperArm, transitionRightUpperArm, amount);
            BlendRotation(rightLowerArm, transitionRightLowerArm, amount);
            BlendRotation(rightHand, transitionRightHand, amount);
            if (actionTransitionClock >= ActionTransitionSeconds)
            {
                actionTransitionActive = false;
            }
        }

        private static void BlendRotation(Transform bone, Quaternion from, float amount)
        {
            if (bone != null)
            {
                bone.localRotation = Quaternion.Slerp(from, bone.localRotation, amount);
            }
        }
        private static float ActionBlend(float clock, float duration, float transition)
        {
            var enter = Mathf.Clamp01(clock / transition);
            var exit = Mathf.Clamp01((duration - clock) / transition);
            return Mathf.SmoothStep(0f, 1f, Mathf.Min(enter, exit));
        }

        private static void SetRotation(
            Transform bone,
            Quaternion baseline,
            Quaternion offset,
            float amount)
        {
            if (bone != null)
            {
                bone.localRotation = Quaternion.Slerp(baseline, baseline * offset, amount);
            }
        }

        private void RestoreActionPose()
        {
            if (!actionPoseCaptured)
            {
                return;
            }
            if (upperBody != null) upperBody.localRotation = upperBodyBase;
            if (head != null) head.localRotation = headBase;
            if (rightUpperArm != null) rightUpperArm.localRotation = rightUpperArmBase;
            if (rightLowerArm != null) rightLowerArm.localRotation = rightLowerArmBase;
            if (rightHand != null) rightHand.localRotation = rightHandBase;
        }

        private static Transform FindBone(UMT.MMDBoneTransform[] bones, params string[] names)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                for (var nameIndex = 0; nameIndex < names.Length; nameIndex++)
                {
                    var wanted = NormalizeBoneName(names[nameIndex]);
                    for (var boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                    {
                        var bone = bones[boneIndex];
                        if (bone == null) continue;
                        var actual = NormalizeBoneName(string.IsNullOrWhiteSpace(bone.boneName)
                            ? bone.name
                            : bone.boneName);
                        if (pass == 0 ? actual == wanted : actual.Contains(wanted))
                        {
                            return bone.transform;
                        }
                    }
                }
            }
            return null;
        }

        private static Quaternion RotationOf(Transform value)
        {
            return value == null ? Quaternion.identity : value.localRotation;
        }

        private static string NormalizeBoneName(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.ToLowerInvariant()
                    .Replace(" ", string.Empty)
                    .Replace("_", string.Empty)
                    .Replace("-", string.Empty)
                    .Replace(".", string.Empty);
        }

        private void HandleEditorInput()
        {
            // The fallback input makes the prototype testable before a headset
            // and Meta Interaction SDK are available. It is not the Quest input
            // implementation used by the final build.
            var movement = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
            if (movement.sqrMagnitude > 0.001f)
            {
                Move(movement.normalized * moveSpeed * Time.deltaTime);
            }

            if (Input.GetKey(KeyCode.Q))
            {
                Rotate(-rotateSpeed * Time.deltaTime);
            }

            if (Input.GetKey(KeyCode.E))
            {
                Rotate(rotateSpeed * Time.deltaTime);
            }

            if (Input.GetKey(KeyCode.R))
            {
                Scale(1f + scaleSpeed * Time.deltaTime);
            }

            if (Input.GetKey(KeyCode.F))
            {
                Scale(1f - scaleSpeed * Time.deltaTime);
            }

            if (Input.GetKeyDown(KeyCode.Space))
            {
                TogglePlayback();
            }

            if (Input.GetKeyDown(KeyCode.Alpha1))
            {
                PlayAction("idle");
            }

            if (Input.GetKeyDown(KeyCode.Alpha2))
            {
                PlayAction("wave");
            }

            if (Input.GetKeyDown(KeyCode.Alpha3))
            {
                PlayAction("bow");
            }
        }

        public void Move(Vector3 worldDelta)
        {
            transform.position += worldDelta;
        }

        public void Rotate(float degrees)
        {
            transform.Rotate(Vector3.up, degrees, Space.World);
        }

        public void Scale(float multiplier)
        {
            var next = Mathf.Clamp(transform.localScale.x * multiplier, 0.25f, 3f);
            transform.localScale = Vector3.one * next;
        }

        public void PlayAction(string actionName)
        {
            CaptureTransitionPose();
            currentAction = string.IsNullOrWhiteSpace(actionName) ? "idle" : actionName.ToLowerInvariant();
            actionClock = 0f;
            isPlaying = true;
            ActionChanged?.Invoke(currentAction);
        }

        public void SetEmotion(string emotion)
        {
            currentEmotion = string.IsNullOrWhiteSpace(emotion) ? "neutral" : emotion.ToLowerInvariant();
        }

        public void TogglePlayback()
        {
            isPlaying = !isPlaying;
        }

        public void SetPlacementPose(Pose pose)
        {
            transform.SetPositionAndRotation(pose.position, pose.rotation);
            initialPosition = pose.position;
            initialRotation = pose.rotation;
            initialScale = transform.localScale;
        }

        public void ResetTransform()
        {
            transform.position = initialPosition;
            transform.rotation = initialRotation;
            transform.localScale = initialScale;
            PlayAction("idle");
        }
    }
}
