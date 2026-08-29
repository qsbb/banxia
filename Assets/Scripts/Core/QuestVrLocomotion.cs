using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;

namespace QuestMmdPlayer
{
    /// <summary>Keeps head tracking intact while moving the XR origin.</summary>
    [DisallowMultipleComponent]
    public sealed class QuestVrLocomotion : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 1.8f;
        [SerializeField] private float deadZone = 0.2f;
        [SerializeField] private float smoothTurnDegreesPerSecond = 90f;
        [SerializeField, Min(0f)] private float turnInputSmoothingSeconds = .055f;
        private Transform rig;
        private Camera headCamera;
        private float smoothedTurnInput;
        public string Status { get; private set; } = "Waiting for XR input";

        private void Awake() => EnsureRig();

        private void Update()
        {
            EnsureRig();
            if (rig == null || headCamera == null) return;

            var leftAxis = ReadAxis(XRNode.LeftHand);
            var rightAxis = ReadAxis(XRNode.RightHand);
#if UNITY_EDITOR || UNITY_STANDALONE
            leftAxis += new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            rightAxis.x += Input.GetKey(KeyCode.C) ? 1f : Input.GetKey(KeyCode.Z) ? -1f : 0f;
#endif
            Move(Vector2.ClampMagnitude(leftAxis, 1f));
            smoothedTurnInput = SmoothTurnInput(
                smoothedTurnInput,
                rightAxis.x,
                Time.unscaledDeltaTime,
                turnInputSmoothingSeconds);
            Turn(smoothedTurnInput);
            Status = $"Move {leftAxis.x:F1}, {leftAxis.y:F1} | Turn {rightAxis.x:F1}";
        }

        private void EnsureRig()
        {
            headCamera = Camera.main;
            if (headCamera == null)
            {
                return;
            }

            var existingRig = FindExistingRig(headCamera);
            if (existingRig != null)
            {
                rig = existingRig;
                return;
            }

            if (rig != null)
            {
                return;
            }

            var originObject = new GameObject("XR Origin (Runtime Fallback)");
            rig = originObject.transform;
            rig.position = new Vector3(headCamera.transform.position.x, 0f, headCamera.transform.position.z);
            rig.rotation = Quaternion.identity;

            var cameraOffset = new GameObject("Camera Offset");
            cameraOffset.transform.SetParent(rig, false);
            headCamera.transform.SetParent(cameraOffset.transform, true);

            var origin = originObject.AddComponent<XROrigin>();
            origin.CameraFloorOffsetObject = cameraOffset;
            origin.Camera = headCamera;
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
            origin.CameraYOffset = 0f;
            Debug.LogWarning("[QuestVrLocomotion] Created a runtime XR Origin because the scene did not contain one.");
        }

        public static Transform FindExistingRig(Camera camera)
        {
            var origin = camera == null ? null : camera.GetComponentInParent<XROrigin>();
            return origin == null ? null : origin.transform;
        }

        private static Vector2 ReadAxis(XRNode node)
        {
            var device = InputDevices.GetDeviceAtXRNode(node);
            return device.isValid && device.TryGetFeatureValue(CommonUsages.primary2DAxis, out var axis) ? axis : Vector2.zero;
        }

        private void Move(Vector2 input)
        {
            if (input.sqrMagnitude < deadZone * deadZone) return;

            var forward = ProjectOnGround(headCamera.transform.forward);
            if (forward.sqrMagnitude < .0001f) forward = ProjectOnGround(rig.forward);
            var right = Vector3.Cross(Vector3.up, forward).normalized;
            var direction = (right * input.x + forward * input.y).normalized;
            rig.position += direction * moveSpeed * Time.unscaledDeltaTime;
        }

        private void Turn(float input)
        {
            var delta = CalculateTurnDelta(input, deadZone, smoothTurnDegreesPerSecond, Time.unscaledDeltaTime);
            if (Mathf.Approximately(delta, 0f)) return;
            rig.RotateAround(headCamera.transform.position, Vector3.up, delta);
        }

        public static float SmoothTurnInput(float current, float target, float deltaTime, float smoothingSeconds)
        {
            if (smoothingSeconds <= 0f || deltaTime <= 0f)
            {
                return target;
            }

            var blend = 1f - Mathf.Exp(-Mathf.Max(0f, deltaTime) / smoothingSeconds);
            var next = Mathf.Lerp(current, target, Mathf.Clamp01(blend));
            return Mathf.Abs(next) < .002f && Mathf.Abs(target) < .002f ? 0f : next;
        }

        public static float CalculateTurnDelta(float input, float axisDeadZone, float degreesPerSecond, float deltaTime)
        {
            return QuestXrInputUtility.RemapAxisOutsideDeadZone(input, axisDeadZone) *
                   Mathf.Max(0f, degreesPerSecond) *
                   Mathf.Max(0f, deltaTime);
        }

        internal static Vector3 ProjectOnGround(Vector3 direction)
        {
            direction.y = 0f;
            return direction.sqrMagnitude < .0001f ? Vector3.zero : direction.normalized;
        }
    }
}
