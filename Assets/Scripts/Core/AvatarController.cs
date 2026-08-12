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
        private Transform lowerBody;
        private Transform leftUpperLeg;
        private Transform rightUpperLeg;
        private Transform leftLowerLeg;
        private Transform rightLowerLeg;
        private Quaternion upperBodyBase;
        private Quaternion headBase;
        private Quaternion rightUpperArmBase;
        private Quaternion rightLowerArmBase;
        private Quaternion rightHandBase;
        private Quaternion lowerBodyBase;
        private Quaternion leftUpperLegBase;
        private Quaternion rightUpperLegBase;
        private Quaternion leftLowerLegBase;
        private Quaternion rightLowerLegBase;
        private bool actionPoseCaptured;
        private bool actionTransitionActive;
        private float actionTransitionClock;
        private const float ActionTransitionSeconds = .42f;
        private Quaternion transitionUpperBody;
        private Quaternion transitionHead;
        private Quaternion transitionRightUpperArm;
        private Quaternion transitionRightLowerArm;
        private Quaternion transitionRightHand;
        private Quaternion transitionLowerBody;
        private Quaternion transitionLeftUpperLeg;
        private Quaternion transitionRightUpperLeg;
        private Quaternion transitionLeftLowerLeg;
        private Quaternion transitionRightLowerLeg;

        private const float WaveDuration = 3.6f;
        private const float BowDuration = 2.2f;
        private const float NodDuration = 2.1f;
        private const float SwayDuration = 3.4f;
        private const float DanceDuration = 6.4f;
        private const float RaiseHandDuration = 3.0f;
        private const float TurnHalfDuration = 2.4f;
        private const float RefuseDuration = 2.2f;
        private const float StepBackDuration = 2.0f;
        private Vector3 actionWorldStartPosition;
        private Vector3 actionWorldTargetPosition;
        private Quaternion actionWorldStartRotation = Quaternion.identity;
        private Quaternion actionWorldTargetRotation = Quaternion.identity;

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
            else if (currentAction == "dance")
            {
                ApplyDance();
                if (actionClock >= DanceDuration)
                {
                    PlayAction("idle");
                }
            }
            else if (currentAction == "raise_hand")
            {
                ApplyRaiseHand();
                if (actionClock >= RaiseHandDuration)
                {
                    PlayAction("idle");
                }
            }
            else if (currentAction == "turn_half")
            {
                ApplyTurnHalf();
                if (actionClock >= TurnHalfDuration)
                {
                    PlayAction("idle");
                }
            }
            else if (currentAction == "refuse")
            {
                ApplyRefuse();
                if (actionClock >= RefuseDuration)
                {
                    PlayAction("idle");
                }
            }
            else if (currentAction == "step_back")
            {
                ApplyStepBack();
                if (actionClock >= StepBackDuration)
                {
                    PlayAction("idle");
                }
            }
            else if (currentAction == "sit")
            {
                ApplySit();
            }
            else if (currentAction == "lie_down")
            {
                ApplyLieDown();
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
            CaptureActionPose(true);
        }

        public void CaptureCurrentActionPose()
        {
            CaptureActionPose(false);
        }

        private void CaptureActionPose(bool restorePreviousPose)
        {
            if (restorePreviousPose)
            {
                RestoreActionPose();
            }
            actionPoseCaptured = false;
            upperBody = head = rightUpperArm = rightLowerArm = rightHand = null;
            lowerBody = leftUpperLeg = rightUpperLeg = leftLowerLeg = rightLowerLeg = null;
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
            lowerBody = FindBone(bones, "lowerbody", "hips", "pelvis", "\u4e0b\u534a\u8eab");
            leftUpperLeg = FindBone(bones, "leftupperleg", "upperlegl", "\u5de6\u8db3", "\u5de6\u5927\u817f");
            rightUpperLeg = FindBone(bones, "rightupperleg", "upperlegr", "\u53f3\u8db3", "\u53f3\u5927\u817f");
            leftLowerLeg = FindBone(bones, "leftlowerleg", "lowerlegl", "\u5de6\u3072\u3056", "\u5de6\u819d");
            rightLowerLeg = FindBone(bones, "rightlowerleg", "lowerlegr", "\u53f3\u3072\u3056", "\u53f3\u819d");

            upperBodyBase = RotationOf(upperBody);
            headBase = RotationOf(head);
            rightUpperArmBase = RotationOf(rightUpperArm);
            rightLowerArmBase = RotationOf(rightLowerArm);
            rightHandBase = RotationOf(rightHand);
            lowerBodyBase = RotationOf(lowerBody);
            leftUpperLegBase = RotationOf(leftUpperLeg);
            rightUpperLegBase = RotationOf(rightUpperLeg);
            leftLowerLegBase = RotationOf(leftLowerLeg);
            rightLowerLegBase = RotationOf(rightLowerLeg);
            actionPoseCaptured = true;
        }

        private void ApplyWave()
        {
            if (!actionPoseCaptured)
            {
                CaptureActionPose();
            }

            var blend = ActionBlend(actionClock, WaveDuration, .52f);
            var gestureClock = Mathf.Max(0f, actionClock - .52f);
            var wave = Mathf.Sin(gestureClock * 6.4f);
            SetRotation(upperBody, upperBodyBase,
                Quaternion.Euler(0f, -2.2f, -1.4f), blend);
            SetRotation(head, headBase,
                Quaternion.Euler(1.2f, 1.8f, 1.4f), blend);
            SetRotation(rightUpperArm, rightUpperArmBase,
                Quaternion.Euler(-5f, -4f, 36f), blend);
            SetRotation(rightLowerArm, rightLowerArmBase,
                Quaternion.Euler(0f, -14f, -48f), blend);

            if (rightUpperArm != null && rightLowerArm != null && rightHand != null)
            {
                var viewer = Camera.main;
                var towardViewer = viewer == null
                    ? transform.forward
                    : Vector3.ProjectOnPlane(
                        viewer.transform.position - rightUpperArm.position,
                        Vector3.up).normalized;
                if (towardViewer.sqrMagnitude < .0001f)
                {
                    towardViewer = transform.forward;
                }

                var side = transform.right;
                var armLength = Vector3.Distance(rightUpperArm.position, rightLowerArm.position) +
                    Vector3.Distance(rightLowerArm.position, rightHand.position);
                if (armLength > .04f)
                {
                    var target = rightUpperArm.position +
                        transform.up * (armLength * .62f) +
                        side * (armLength * (.42f + wave * .045f)) +
                        towardViewer * (armLength * .12f);
                    var pole = rightUpperArm.position +
                        side * (armLength * .34f) -
                        transform.up * (armLength * .08f) +
                        towardViewer * (armLength * .24f);
                    AvatarHumanInteraction.SolveTwoBoneIk(
                        rightUpperArm,
                        rightLowerArm,
                        rightHand,
                        target,
                        pole,
                        .94f,
                        blend);
                }
            }

            SetRotation(rightHand, rightHandBase,
                Quaternion.Euler(3f, wave * 16f, 6f), blend);
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

            var progress = Mathf.Clamp01(actionClock / NodDuration);
            var blend = ActionBlend(actionClock, NodDuration, .32f);
            var pitch = NaturalNodPitch(progress);
            SetRotation(head, headBase, Quaternion.Euler(pitch, 0f, 0f), blend);
            SetRotation(upperBody, upperBodyBase, Quaternion.Euler(pitch * .12f, 0f, 0f), blend);
        }

        public static float NaturalNodPitch(float normalizedTime)
        {
            var progress = Mathf.Clamp01(normalizedTime);
            var first = GesturePulse(progress, .18f, .48f) * 8.5f;
            var second = GesturePulse(progress, .52f, .78f) * 4.5f;
            return first + second;
        }

        private static float GesturePulse(float progress, float start, float end)
        {
            if (progress <= start || progress >= end)
            {
                return 0f;
            }

            var normalized = Mathf.InverseLerp(start, end, progress);
            return Mathf.Sin(normalized * Mathf.PI);
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

        private void ApplyDance()
        {
            if (!actionPoseCaptured)
            {
                CaptureActionPose();
            }

            var blend = ActionBlend(actionClock, DanceDuration, .65f);
            var phase = actionClock * 3.1f;
            var sway = Mathf.Sin(phase);
            var counter = Mathf.Sin(phase + Mathf.PI * .5f);
            SetRotation(upperBody, upperBodyBase,
                Quaternion.Euler(counter * 2.8f, sway * 4.5f, sway * 3.6f), blend);
            SetRotation(head, headBase,
                Quaternion.Euler(-counter * 2.2f, -sway * 3.2f, -sway * 2.4f), blend);
            SetRotation(rightUpperArm, rightUpperArmBase,
                Quaternion.Euler(-8f + counter * 5f, -10f + sway * 8f, 18f + sway * 12f), blend);
            SetRotation(rightLowerArm, rightLowerArmBase,
                Quaternion.Euler(0f, -18f + counter * 12f, -22f + sway * 14f), blend);
            SetRotation(rightHand, rightHandBase,
                Quaternion.Euler(0f, sway * 10f, counter * 8f), blend);
        }

        private void ApplyRaiseHand()
        {
            if (!actionPoseCaptured)
            {
                CaptureActionPose();
            }

            var blend = ActionBlend(actionClock, RaiseHandDuration, .55f);
            SetRotation(upperBody, upperBodyBase, Quaternion.Euler(0f, -1.5f, -1f), blend);
            SetRotation(head, headBase, Quaternion.Euler(1f, 1.5f, 1f), blend);
            SetRotation(rightUpperArm, rightUpperArmBase, Quaternion.Euler(-7f, -5f, 42f), blend);
            SetRotation(rightLowerArm, rightLowerArmBase, Quaternion.Euler(0f, -10f, -46f), blend);
            SetRotation(rightHand, rightHandBase, Quaternion.Euler(2f, 4f, 4f), blend);
        }

        private void ApplyTurnHalf()
        {
            var progress = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(actionClock / TurnHalfDuration));
            transform.rotation = Quaternion.Slerp(actionWorldStartRotation, actionWorldTargetRotation, progress);
            if (!actionPoseCaptured)
            {
                CaptureActionPose();
            }
            var step = Mathf.Sin(Mathf.Clamp01(actionClock / TurnHalfDuration) * Mathf.PI * 2f);
            SetRotation(upperBody, upperBodyBase, Quaternion.Euler(0f, step * 4f, step * 2f), 1f);
            SetRotation(head, headBase, Quaternion.Euler(0f, -step * 3f, -step), 1f);
        }

        private void ApplyRefuse()
        {
            if (!actionPoseCaptured)
            {
                CaptureActionPose();
            }
            var blend = ActionBlend(actionClock, RefuseDuration, .4f);
            SetRotation(upperBody, upperBodyBase, Quaternion.Euler(-3f, -5f, -2f), blend);
            SetRotation(head, headBase, Quaternion.Euler(-2f, -12f, -2f), blend);
        }

        private void ApplyStepBack()
        {
            var progress = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(actionClock / StepBackDuration));
            transform.position = Vector3.Lerp(actionWorldStartPosition, actionWorldTargetPosition, progress);
            ApplyRefuse();
        }

        private void ApplySit()
        {
            if (!actionPoseCaptured)
            {
                CaptureActionPose();
            }
            var blend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(actionClock / .7f));
            SetRotation(lowerBody, lowerBodyBase, Quaternion.Euler(-7f, 0f, 0f), blend);
            SetRotation(upperBody, upperBodyBase, Quaternion.Euler(8f, 0f, 0f), blend);
            SetRotation(head, headBase, Quaternion.Euler(-3f, 0f, 0f), blend);
            SetRotation(leftUpperLeg, leftUpperLegBase, Quaternion.Euler(-72f, 2f, -2f), blend);
            SetRotation(rightUpperLeg, rightUpperLegBase, Quaternion.Euler(-72f, -2f, 2f), blend);
            SetRotation(leftLowerLeg, leftLowerLegBase, Quaternion.Euler(76f, 0f, 0f), blend);
            SetRotation(rightLowerLeg, rightLowerLegBase, Quaternion.Euler(76f, 0f, 0f), blend);
        }

        private void ApplyLieDown()
        {
            if (!actionPoseCaptured)
            {
                CaptureActionPose();
            }
            var blend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(actionClock / .85f));
            SetRotation(lowerBody, lowerBodyBase, Quaternion.Euler(-4f, 0f, 0f), blend);
            SetRotation(upperBody, upperBodyBase, Quaternion.Euler(3f, 0f, -2f), blend);
            SetRotation(head, headBase, Quaternion.Euler(-5f, 0f, 3f), blend);
            SetRotation(leftUpperLeg, leftUpperLegBase, Quaternion.Euler(-5f, 0f, -2f), blend);
            SetRotation(rightUpperLeg, rightUpperLegBase, Quaternion.Euler(4f, 0f, 2f), blend);
            SetRotation(leftLowerLeg, leftLowerLegBase, Quaternion.Euler(8f, 0f, 0f), blend);
            SetRotation(rightLowerLeg, rightLowerLegBase, Quaternion.Euler(4f, 0f, 0f), blend);
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
            transitionLowerBody = RotationOf(lowerBody);
            transitionLeftUpperLeg = RotationOf(leftUpperLeg);
            transitionRightUpperLeg = RotationOf(rightUpperLeg);
            transitionLeftLowerLeg = RotationOf(leftLowerLeg);
            transitionRightLowerLeg = RotationOf(rightLowerLeg);
            actionTransitionClock = 0f;
            actionTransitionActive = true;
        }

        private void ApplyActionTransition()
        {
            if (!actionTransitionActive)
            {
                return;
            }
            var amount = NaturalMotionTransition.Smooth01(actionTransitionClock / ActionTransitionSeconds);
            BlendRotation(upperBody, transitionUpperBody, amount);
            BlendRotation(head, transitionHead, amount);
            BlendRotation(rightUpperArm, transitionRightUpperArm, amount);
            BlendRotation(rightLowerArm, transitionRightLowerArm, amount);
            BlendRotation(rightHand, transitionRightHand, amount);
            BlendRotation(lowerBody, transitionLowerBody, amount);
            BlendRotation(leftUpperLeg, transitionLeftUpperLeg, amount);
            BlendRotation(rightUpperLeg, transitionRightUpperLeg, amount);
            BlendRotation(leftLowerLeg, transitionLeftLowerLeg, amount);
            BlendRotation(rightLowerLeg, transitionRightLowerLeg, amount);
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
            if (lowerBody != null) lowerBody.localRotation = lowerBodyBase;
            if (leftUpperLeg != null) leftUpperLeg.localRotation = leftUpperLegBase;
            if (rightUpperLeg != null) rightUpperLeg.localRotation = rightUpperLegBase;
            if (leftLowerLeg != null) leftLowerLeg.localRotation = leftLowerLegBase;
            if (rightLowerLeg != null) rightLowerLeg.localRotation = rightLowerLegBase;
        }

        public float EstimateVisualHeight()
        {
            if (visualRoot == null)
            {
                return 1.6f;
            }
            var renderers = visualRoot.GetComponentsInChildren<Renderer>(true);
            var found = false;
            var bounds = new Bounds(transform.position, Vector3.zero);
            for (var index = 0; index < renderers.Length; index++)
            {
                if (renderers[index] == null || !renderers[index].enabled)
                {
                    continue;
                }
                if (!found)
                {
                    bounds = renderers[index].bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(renderers[index].bounds);
                }
            }
            return found ? Mathf.Clamp(bounds.size.y, .8f, 2.4f) : 1.6f;
        }

        public float EstimateHipHeight()
        {
            if (lowerBody != null)
            {
                var height = Vector3.Dot(lowerBody.position - transform.position, transform.up);
                if (height > .25f && height < 1.6f)
                {
                    return height;
                }
            }
            return EstimateVisualHeight() * .52f;
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
            var previous = currentAction;
            CaptureTransitionPose();
            currentAction = string.IsNullOrWhiteSpace(actionName) ? "idle" : actionName.ToLowerInvariant();
            actionClock = 0f;
            actionWorldStartPosition = transform.position;
            actionWorldTargetPosition = currentAction == "step_back"
                ? transform.position - transform.forward * .12f
                : transform.position;
            actionWorldStartRotation = transform.rotation;
            actionWorldTargetRotation = currentAction == "turn_half"
                ? transform.rotation * Quaternion.Euler(0f, 180f, 0f)
                : transform.rotation;
            isPlaying = true;
            ActionChanged?.Invoke(currentAction);
            Debug.Log("[AvatarAction] transition=" + previous + "->" + currentAction +
                " blend_ms=" + Mathf.RoundToInt(ActionTransitionSeconds * 1000f), this);
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
