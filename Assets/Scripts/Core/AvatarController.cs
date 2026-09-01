using System;
using UnityEngine;

namespace QuestMmdPlayer
{
    public readonly struct AvatarNaturalTurnSample
    {
        public AvatarNaturalTurnSample(
            float yawProgress,
            Vector3 localRootOffset,
            float pelvisYaw,
            float pelvisRoll,
            float torsoYaw,
            float torsoRoll,
            float headYaw,
            float leftStep,
            float rightStep)
        {
            YawProgress = yawProgress;
            LocalRootOffset = localRootOffset;
            PelvisYaw = pelvisYaw;
            PelvisRoll = pelvisRoll;
            TorsoYaw = torsoYaw;
            TorsoRoll = torsoRoll;
            HeadYaw = headYaw;
            LeftStep = leftStep;
            RightStep = rightStep;
        }

        public float YawProgress { get; }
        public Vector3 LocalRootOffset { get; }
        public float PelvisYaw { get; }
        public float PelvisRoll { get; }
        public float TorsoYaw { get; }
        public float TorsoRoll { get; }
        public float HeadYaw { get; }
        public float LeftStep { get; }
        public float RightStep { get; }
    }

    public readonly struct AvatarCrouchSample
    {
        public AvatarCrouchSample(
            float poseAmount,
            float pelvisDrop,
            float hipPitch,
            float kneePitch,
            float anklePitch,
            float torsoPitch)
        {
            PoseAmount = poseAmount;
            PelvisDrop = pelvisDrop;
            HipPitch = hipPitch;
            KneePitch = kneePitch;
            AnklePitch = anklePitch;
            TorsoPitch = torsoPitch;
        }

        public float PoseAmount { get; }
        public float PelvisDrop { get; }
        public float HipPitch { get; }
        public float KneePitch { get; }
        public float AnklePitch { get; }
        public float TorsoPitch { get; }
    }

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
        private AvatarActionSource currentActionSource = AvatarActionSource.Idle;
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
        private Transform leftFoot;
        private Transform rightFoot;
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
        private Quaternion leftFootBase;
        private Quaternion rightFootBase;
        private Vector3 lowerBodyBasePosition;
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
        private Quaternion transitionLeftFoot;
        private Quaternion transitionRightFoot;
        private Vector3 transitionLowerBodyPosition;

        private const float WaveDuration = 3.6f;
        private const float BowDuration = 2.2f;
        private const float NodDuration = 2.1f;
        private const float SwayDuration = 3.4f;
        private const float DanceDuration = 6.4f;
        private const float RaiseHandDuration = 3.0f;
        private const float RaiseLegDuration = 3.2f;
        private const float TurnHalfDuration = 3.2f;
        private const float RefuseDuration = 2.2f;
        private const float StepBackDuration = 2.0f;
        private const float CrouchEnterSeconds = .55f;
        private const float CrouchHoldSeconds = .9f;
        private const float CrouchExitSeconds = .65f;
        private Vector3 actionWorldStartPosition;
        private Vector3 actionWorldTargetPosition;
        private Quaternion actionWorldStartRotation = Quaternion.identity;
        private float actionTurnDirection = 1f;
        private float actionTurnDegrees = 180f;
        private float crouchDepth = .65f;
        private float crouchEnterSeconds = CrouchEnterSeconds;
        private float crouchHoldSeconds = CrouchHoldSeconds;
        private float crouchExitSeconds = CrouchExitSeconds;
        private Vector3 leftFootWorldAnchor;
        private Vector3 rightFootWorldAnchor;
        private string actionRequestSource = "local";
        private string actionStyle = "natural";

        public event Action<string> ActionChanged;

        public Transform VisualRoot => visualRoot;

        public string CurrentAction => currentAction;
        public AvatarActionSource CurrentActionSource => currentActionSource;
        public string CurrentEmotion => currentEmotion;
        public bool IsPlaying => isPlaying;
        public bool SupportsCrouch => actionPoseCaptured && lowerBody != null &&
            leftUpperLeg != null && rightUpperLeg != null &&
            leftLowerLeg != null && rightLowerLeg != null &&
            leftFoot != null && rightFoot != null;
        public bool SupportsRaiseLeg => actionPoseCaptured && lowerBody != null &&
            leftUpperLeg != null && rightUpperLeg != null &&
            leftLowerLeg != null && rightLowerLeg != null &&
            leftFoot != null && rightFoot != null;

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
                    PlayActionFromSource("idle", AvatarActionSource.System);
                }
            }
            else if (currentAction == "bow")
            {
                ApplyBow();
                if (actionClock >= BowDuration)
                {
                    PlayActionFromSource("idle", AvatarActionSource.System);
                }
            }
            else if (currentAction == "nod")
            {
                ApplyNod();
                if (actionClock >= NodDuration)
                {
                    PlayActionFromSource("idle", AvatarActionSource.System);
                }
            }
            else if (currentAction == "sway")
            {
                ApplySway();
                if (actionClock >= SwayDuration)
                {
                    PlayActionFromSource("idle", AvatarActionSource.System);
                }
            }
            else if (currentAction == "dance")
            {
                ApplyDance();
                if (actionClock >= DanceDuration)
                {
                    PlayActionFromSource("idle", AvatarActionSource.System);
                }
            }
            else if (currentAction == "raise_hand")
            {
                ApplyRaiseHand();
                if (actionClock >= RaiseHandDuration)
                {
                    PlayActionFromSource("idle", AvatarActionSource.System);
                }
            }
            else if (currentAction == "raise_leg")
            {
                ApplyRaiseLeg();
                if (actionClock >= RaiseLegDuration)
                {
                    PlayActionFromSource("idle", AvatarActionSource.System);
                }
            }
            else if (currentAction == "turn_half")
            {
                ApplyTurnHalf();
                if (actionClock >= TurnHalfDuration)
                {
                    PlayActionFromSource("idle", AvatarActionSource.System);
                }
            }
            else if (currentAction == "refuse")
            {
                ApplyRefuse();
                if (actionClock >= RefuseDuration)
                {
                    PlayActionFromSource("idle", AvatarActionSource.System);
                }
            }
            else if (currentAction == "step_back")
            {
                ApplyStepBack();
                if (actionClock >= StepBackDuration)
                {
                    PlayActionFromSource("idle", AvatarActionSource.System);
                }
            }
            else if (currentAction == "crouch")
            {
                ApplyCrouch();
                if (actionClock >= CrouchDuration)
                {
                    PlayActionFromSource("idle", AvatarActionSource.System);
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
                // Blend window closed: stop writing the skeleton so the MMD
                // physics solver and VMD playback own the bones. The old
                // per-frame base restore fought the solver and looped the
                // legs (bend-straight-bend flicker on physics-enabled models).
                if (actionTransitionActive)
                {
                    RestoreActionPose();
                }
                else
                {
                    isPlaying = false;
                    return;
                }
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
            leftFoot = rightFoot = null;
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
            leftFoot = FindBone(bones, "leftfoot", "footl", "anklel", "\u5de6\u8db3\u9996");
            rightFoot = FindBone(bones, "rightfoot", "footr", "ankler", "\u53f3\u8db3\u9996");

            upperBodyBase = RotationOf(upperBody);
            headBase = RotationOf(head);
            rightUpperArmBase = RotationOf(rightUpperArm);
            rightLowerArmBase = RotationOf(rightLowerArm);
            rightHandBase = RotationOf(rightHand);
            lowerBodyBase = RotationOf(lowerBody);
            lowerBodyBasePosition = lowerBody == null ? Vector3.zero : lowerBody.localPosition;
            leftUpperLegBase = RotationOf(leftUpperLeg);
            rightUpperLegBase = RotationOf(rightUpperLeg);
            leftLowerLegBase = RotationOf(leftLowerLeg);
            rightLowerLegBase = RotationOf(rightLowerLeg);
            leftFootBase = RotationOf(leftFoot);
            rightFootBase = RotationOf(rightFoot);
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

        private void ApplyRaiseLeg()
        {
            if (!actionPoseCaptured)
            {
                CaptureActionPose();
            }

            var blend = ActionBlend(actionClock, RaiseLegDuration, .62f);
            // Keep the left foot planted and lift the right leg in a compact,
            // balance-friendly pose. The action is deliberately deterministic
            // so it works without an imported animation clip.
            SetRotation(upperBody, upperBodyBase, Quaternion.Euler(0f, -2f, -4f), blend);
            SetRotation(head, headBase, Quaternion.Euler(1.5f, 2f, 1f), blend);
            SetRotation(leftUpperLeg, leftUpperLegBase, Quaternion.Euler(2f, 0f, 1f), blend);
            SetRotation(leftLowerLeg, leftLowerLegBase, Quaternion.Euler(-3f, 0f, 0f), blend);
            SetRotation(rightUpperLeg, rightUpperLegBase, Quaternion.Euler(-24f, 6f, 4f), blend);
            SetRotation(rightLowerLeg, rightLowerLegBase, Quaternion.Euler(48f, 0f, 0f), blend);
            SetRotation(rightFoot, rightFootBase, Quaternion.Euler(-22f, -3f, 0f), blend);
        }

        private void ApplyTurnHalf()
        {
            if (!actionPoseCaptured)
            {
                CaptureActionPose();
            }

            var normalized = Mathf.Clamp01(actionClock / TurnHalfDuration);
            var turnDirection = actionTurnDirection;
            var sample = SampleNaturalTurn(normalized, turnDirection);
            var position = actionWorldStartPosition +
                actionWorldStartRotation * sample.LocalRootOffset;
            var rotation = NaturalTurnRotation(
                actionWorldStartRotation,
                turnDirection,
                sample.YawProgress,
                actionTurnDegrees);
            transform.SetPositionAndRotation(position, rotation);

            SetRotation(lowerBody, lowerBodyBase,
                Quaternion.Euler(0f, sample.PelvisYaw, sample.PelvisRoll), 1f);
            SetRotation(upperBody, upperBodyBase,
                Quaternion.Euler(0f, sample.TorsoYaw, sample.TorsoRoll), 1f);
            SetRotation(head, headBase, Quaternion.Euler(0f, sample.HeadYaw, 0f), 1f);
            ApplyTurnLegPose(
                leftUpperLeg,
                leftLowerLeg,
                leftFoot,
                leftUpperLegBase,
                leftLowerLegBase,
                leftFootBase,
                sample.LeftStep,
                -turnDirection);
            ApplyTurnLegPose(
                rightUpperLeg,
                rightLowerLeg,
                rightFoot,
                rightUpperLegBase,
                rightLowerLegBase,
                rightFootBase,
                sample.RightStep,
                turnDirection);
        }

        public static AvatarNaturalTurnSample SampleNaturalTurn(
            float normalizedTime,
            float turnDirection)
        {
            var time = Mathf.Clamp01(normalizedTime);
            var direction = turnDirection < 0f ? -1f : 1f;
            float yawProgress;
            if (time < .08f)
            {
                yawProgress = 0f;
            }
            else if (time < .40f)
            {
                yawProgress = .46f * SmoothSegment(time, .08f, .40f);
            }
            else if (time < .57f)
            {
                yawProgress = Mathf.Lerp(.46f, .52f, SmoothSegment(time, .40f, .57f));
            }
            else if (time < .92f)
            {
                yawProgress = Mathf.Lerp(.52f, 1f, SmoothSegment(time, .57f, .92f));
            }
            else
            {
                yawProgress = 1f;
            }

            var firstStep = WindowPulse(time, .035f, .57f);
            var secondStep = WindowPulse(time, .45f, .985f);
            var leftStep = direction > 0f ? firstStep : secondStep;
            var rightStep = direction > 0f ? secondStep : firstStep;
            var localRootOffset = new Vector3(
                direction * (firstStep * .095f - secondStep * .080f),
                -(firstStep + secondStep) * .012f,
                firstStep * .060f + secondStep * .075f);
            var settle = WindowPulse(time, .76f, 1f);
            return new AvatarNaturalTurnSample(
                yawProgress,
                localRootOffset,
                direction * (firstStep * 12f + secondStep * 9f),
                direction * (-firstStep * 3.8f + secondStep * 3.0f),
                direction * (-firstStep * 8f + secondStep * 6f),
                direction * (firstStep * 2.4f - secondStep * 2f),
                direction * (firstStep * 12f - secondStep * 9f - settle * 2f),
                leftStep,
                rightStep);
        }

        public static Quaternion NaturalTurnRotation(
            Quaternion startRotation,
            float turnDirection,
            float yawProgress,
            float turnDegrees = 180f)
        {
            var direction = turnDirection < 0f ? -1f : 1f;
            return Quaternion.AngleAxis(
                direction * Mathf.Clamp(Mathf.Abs(turnDegrees), 15f, 180f) * Mathf.Clamp01(yawProgress),
                Vector3.up) * startRotation;
        }

        private static void ApplyTurnLegPose(
            Transform upperLeg,
            Transform lowerLeg,
            Transform foot,
            Quaternion upperBase,
            Quaternion lowerBase,
            Quaternion footBase,
            float step,
            float side)
        {
            SetRotation(upperLeg, upperBase,
                Quaternion.Euler(-step * 17f, side * step * 8f, side * step * 3f), 1f);
            SetRotation(lowerLeg, lowerBase, Quaternion.Euler(step * 27f, 0f, 0f), 1f);
            SetRotation(foot, footBase,
                Quaternion.Euler(-step * 11f, -side * step * 6f, 0f), 1f);
        }

        private static float SmoothSegment(float value, float start, float end)
        {
            return NaturalMotionTransition.Smooth01(Mathf.InverseLerp(start, end, value));
        }

        private static float WindowPulse(float value, float start, float end)
        {
            if (value <= start || value >= end)
            {
                return 0f;
            }
            return Mathf.Sin(Mathf.InverseLerp(start, end, value) * Mathf.PI);
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

        private float CrouchDuration => crouchEnterSeconds + crouchHoldSeconds + crouchExitSeconds;

        private void ApplyCrouch()
        {
            if (!SupportsCrouch)
            {
                PlayActionFromSource("idle", AvatarActionSource.System);
                return;
            }

            var sample = SampleCrouch(
                actionClock,
                crouchEnterSeconds,
                crouchHoldSeconds,
                crouchExitSeconds,
                crouchDepth,
                EstimateVisualHeight());
            lowerBody.localPosition = lowerBodyBasePosition + Vector3.down * sample.PelvisDrop;
            SetRotation(lowerBody, lowerBodyBase, Quaternion.Euler(-sample.HipPitch * .14f, 0f, 0f), 1f);
            SetRotation(upperBody, upperBodyBase, Quaternion.Euler(sample.TorsoPitch, 0f, 0f), 1f);
            SetRotation(head, headBase, Quaternion.Euler(-sample.TorsoPitch * .35f, 0f, 0f), 1f);
            SetRotation(leftUpperLeg, leftUpperLegBase,
                Quaternion.Euler(-sample.HipPitch, 1.5f * sample.PoseAmount, -1.5f * sample.PoseAmount), 1f);
            SetRotation(rightUpperLeg, rightUpperLegBase,
                Quaternion.Euler(-sample.HipPitch, -1.5f * sample.PoseAmount, 1.5f * sample.PoseAmount), 1f);
            SetRotation(leftLowerLeg, leftLowerLegBase, Quaternion.Euler(sample.KneePitch, 0f, 0f), 1f);
            SetRotation(rightLowerLeg, rightLowerLegBase, Quaternion.Euler(sample.KneePitch, 0f, 0f), 1f);
            SetRotation(leftFoot, leftFootBase, Quaternion.Euler(sample.AnklePitch, 0f, 0f), 1f);
            SetRotation(rightFoot, rightFootBase, Quaternion.Euler(sample.AnklePitch, 0f, 0f), 1f);

            var leftPole = leftLowerLeg.position + transform.forward * .25f - transform.right * .04f;
            var rightPole = rightLowerLeg.position + transform.forward * .25f + transform.right * .04f;
            AvatarHumanInteraction.SolveTwoBoneIk(
                leftUpperLeg,
                leftLowerLeg,
                leftFoot,
                leftFootWorldAnchor,
                leftPole,
                .995f,
                sample.PoseAmount);
            AvatarHumanInteraction.SolveTwoBoneIk(
                rightUpperLeg,
                rightLowerLeg,
                rightFoot,
                rightFootWorldAnchor,
                rightPole,
                .995f,
                sample.PoseAmount);
        }

        public static AvatarCrouchSample SampleCrouch(
            float elapsedSeconds,
            float enterSeconds = CrouchEnterSeconds,
            float holdSeconds = CrouchHoldSeconds,
            float exitSeconds = CrouchExitSeconds,
            float depth = .65f,
            float avatarHeight = 1.6f)
        {
            var enter = Mathf.Max(.25f, enterSeconds);
            var hold = Mathf.Max(.1f, holdSeconds);
            var exit = Mathf.Max(.25f, exitSeconds);
            var elapsed = Mathf.Max(0f, elapsedSeconds);
            float amount;
            if (elapsed < enter)
            {
                amount = NaturalMotionTransition.Smooth01(elapsed / enter);
            }
            else if (elapsed < enter + hold)
            {
                amount = 1f;
            }
            else
            {
                amount = 1f - NaturalMotionTransition.Smooth01(
                    (elapsed - enter - hold) / exit);
            }
            amount = Mathf.Clamp01(amount);
            var strength = Mathf.Clamp(depth <= 0f ? .65f : depth, .2f, 1f);
            return new AvatarCrouchSample(
                amount,
                Mathf.Clamp(avatarHeight, .8f, 2.4f) * .075f * strength * amount,
                31f * strength * amount,
                54f * strength * amount,
                -19f * strength * amount,
                7f * strength * amount);
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
            transitionLowerBodyPosition = lowerBody == null ? Vector3.zero : lowerBody.localPosition;
            transitionLeftUpperLeg = RotationOf(leftUpperLeg);
            transitionRightUpperLeg = RotationOf(rightUpperLeg);
            transitionLeftLowerLeg = RotationOf(leftLowerLeg);
            transitionRightLowerLeg = RotationOf(rightLowerLeg);
            transitionLeftFoot = RotationOf(leftFoot);
            transitionRightFoot = RotationOf(rightFoot);
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
            if (lowerBody != null)
            {
                lowerBody.localPosition = Vector3.Lerp(
                    transitionLowerBodyPosition,
                    lowerBody.localPosition,
                    amount);
            }
            BlendRotation(leftUpperLeg, transitionLeftUpperLeg, amount);
            BlendRotation(rightUpperLeg, transitionRightUpperLeg, amount);
            BlendRotation(leftLowerLeg, transitionLeftLowerLeg, amount);
            BlendRotation(rightLowerLeg, transitionRightLowerLeg, amount);
            BlendRotation(leftFoot, transitionLeftFoot, amount);
            BlendRotation(rightFoot, transitionRightFoot, amount);
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
            if (lowerBody != null) lowerBody.localPosition = lowerBodyBasePosition;
            if (leftUpperLeg != null) leftUpperLeg.localRotation = leftUpperLegBase;
            if (rightUpperLeg != null) rightUpperLeg.localRotation = rightUpperLegBase;
            if (leftLowerLeg != null) leftLowerLeg.localRotation = leftLowerLegBase;
            if (rightLowerLeg != null) rightLowerLeg.localRotation = rightLowerLegBase;
            if (leftFoot != null) leftFoot.localRotation = leftFootBase;
            if (rightFoot != null) rightFoot.localRotation = rightFootBase;
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
            PlayActionFromSource(actionName, AvatarActionSource.Manual);
        }

        public bool PlayActionFromSource(
            string actionName,
            AvatarActionSource source,
            AvatarActionParameters parameters = null,
            AvatarActionTransition transition = null,
            string requestSource = "local")
        {
            var previous = currentAction;
            var normalized = AvatarMotionArbiter.Normalize(actionName);
            var importedBusy = string.Equals(currentAction, "vmd", StringComparison.Ordinal) &&
                source != AvatarActionSource.Imported && source != AvatarActionSource.System;
            var decision = AvatarMotionArbiter.Decide(
                currentActionSource,
                source,
                currentAction,
                normalized,
                importedBusy);
            if (!decision.Accepted)
            {
                Debug.Log("[AvatarAction] rejected=" + normalized + " source=" + source +
                    " current=" + currentAction + " reason=" + decision.Reason, this);
                return false;
            }
            if (normalized == "crouch" && !SupportsCrouch)
            {
                Debug.Log("[AvatarAction] rejected=crouch source=" + source +
                    " current=" + currentAction + " reason=asset_missing", this);
                return false;
            }
            if (normalized == "raise_leg" && !SupportsRaiseLeg)
            {
                Debug.Log("[AvatarAction] rejected=raise_leg source=" + source +
                    " current=" + currentAction + " reason=asset_missing", this);
                return false;
            }
            CaptureTransitionPose();
            currentAction = string.IsNullOrWhiteSpace(normalized) ? "idle" : normalized;
            currentActionSource = currentAction == "idle" ? AvatarActionSource.Idle : source;
            actionClock = 0f;
            actionWorldStartPosition = transform.position;
            actionWorldTargetPosition = currentAction == "step_back"
                ? transform.position - transform.forward * .12f
                : transform.position;
            actionWorldStartRotation = transform.rotation;
            ConfigureActionRequest(parameters, transition, requestSource);
            if (currentAction == "crouch")
            {
                leftFootWorldAnchor = leftFoot.position;
                rightFootWorldAnchor = rightFoot.position;
            }
            isPlaying = true;
            ActionChanged?.Invoke(currentAction);
            Debug.Log("[AvatarAction] transition=" + previous + "->" + currentAction +
                " source=" + source + " request_source=" + actionRequestSource +
                " style=" + actionStyle +
                " blend_ms=" + Mathf.RoundToInt(ActionTransitionSeconds * 1000f), this);
            return true;
        }

        private void ConfigureActionRequest(
            AvatarActionParameters parameters,
            AvatarActionTransition transition,
            string requestSource)
        {
            actionRequestSource = string.IsNullOrWhiteSpace(requestSource)
                ? "local"
                : requestSource.Trim().ToLowerInvariant();
            actionStyle = parameters == null
                ? "natural"
                : AstrBotProtocol.SanitizeActionStyle(parameters.Style);
            var requestedAngle = parameters == null ? 0f : parameters.AngleDegrees;
            actionTurnDirection = requestedAngle < 0f ? -1f : 1f;
            actionTurnDegrees = Mathf.Abs(requestedAngle) < 15f
                ? 180f
                : Mathf.Clamp(Mathf.Abs(requestedAngle), 15f, 180f);
            crouchDepth = parameters == null || parameters.Depth <= 0f
                ? .65f
                : Mathf.Clamp(parameters.Depth, .2f, 1f);
            crouchEnterSeconds = transition == null || transition.EnterMs <= 0
                ? CrouchEnterSeconds
                : Mathf.Clamp(transition.EnterMs / 1000f, .25f, 1.5f);
            var requestedHoldMs = parameters == null ? 0 : parameters.HoldMs;
            crouchHoldSeconds = requestedHoldMs <= 0
                ? CrouchHoldSeconds
                : Mathf.Clamp(requestedHoldMs / 1000f, .1f, 5f);
            crouchExitSeconds = transition == null || transition.ExitMs <= 0
                ? CrouchExitSeconds
                : Mathf.Clamp(transition.ExitMs / 1000f, .25f, 1.5f);
            var tempo = actionStyle == "gentle" ? 1.15f : actionStyle == "energetic" ? .82f : 1f;
            crouchEnterSeconds = Mathf.Clamp(crouchEnterSeconds * tempo, .25f, 1.5f);
            crouchExitSeconds = Mathf.Clamp(crouchExitSeconds * tempo, .25f, 1.5f);
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
            PlayActionFromSource("idle", AvatarActionSource.System);
        }
    }
}
