using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Phone-form orbit camera: single-finger drag orbits around the avatar,
    /// pinch zooms, double-tap resets to the default frontal view.
    /// Attached only in BANXIA_PHONE builds by QuestMmdPlayerBootstrap.
    /// </summary>
    public sealed class PhoneOrbitCamera : MonoBehaviour
    {
        private const float DefaultDistance = 2.6f;
        private const float MinDistance = 0.9f;
        private const float MaxDistance = 5.5f;
        private const float OrbitDegreesPerPixel = 0.22f;
        private const float ZoomDistancePerPixel = 0.012f;
        private const float MinPitch = -35f;
        private const float MaxPitch = 55f;
        private const float DoubleTapMaxInterval = 0.35f;
        private const float DoubleTapMaxMove = 40f;
        private const float TargetHeight = 1.25f;

        private Vector3 orbitTarget = new Vector3(0f, TargetHeight, 2.2f);
        private float yaw;
        private float pitch = 8f;
        private float distance = DefaultDistance;
        private float lastTapTime = float.NegativeInfinity;
        private Vector2 lastTapPosition;

        private Camera cachedCamera;
        private Vector2 previousTouchPosition;
        private float previousPinchDistance;
        private bool hasGestureState;

        public float Distance => distance;

        private void Start()
        {
            cachedCamera = GetComponent<Camera>();
            ApplyTransform();
        }

        public void SetOrbitTarget(Vector3 position)
        {
            orbitTarget = new Vector3(position.x, position.y + TargetHeight, position.z);
            ApplyTransform();
        }

        /// <summary>
        /// True while a single finger rests on the avatar region (touch
        /// interaction owns the gesture) so the camera must not orbit.
        /// </summary>
        public bool GestureCapturedByTouch { get; set; }

        private void Update()
        {
            if (Input.touchCount == 2)
            {
                var a = Input.GetTouch(0);
                var b = Input.GetTouch(1);
                var pinchDistance = Vector2.Distance(a.position, b.position);
                if (hasGestureState)
                {
                    distance = Mathf.Clamp(
                        distance - (pinchDistance - previousPinchDistance) * ZoomDistancePerPixel,
                        MinDistance, MaxDistance);
                    ApplyTransform();
                }
                previousPinchDistance = pinchDistance;
                hasGestureState = true;
                return;
            }

            if (Input.touchCount == 1)
            {
                var touch = Input.GetTouch(0);
                if (touch.phase == TouchPhase.Began)
                {
                    hasGestureState = false;
                    previousTouchPosition = touch.position;
                    return;
                }

                if (touch.phase == TouchPhase.Moved)
                {
                    if (!GestureCapturedByTouch)
                    {
                        var delta = touch.position - previousTouchPosition;
                        yaw += delta.x * OrbitDegreesPerPixel;
                        pitch = Mathf.Clamp(pitch - delta.y * OrbitDegreesPerPixel, MinPitch, MaxPitch);
                        ApplyTransform();
                    }
                    previousTouchPosition = touch.position;
                    return;
                }

                if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                {
                    HandleTapReset(touch.position);
                    hasGestureState = false;
                    return;
                }
                return;
            }

            hasGestureState = false;
        }

        private void HandleTapReset(Vector2 position)
        {
            var now = Time.unscaledTime;
            if (now - lastTapTime <= DoubleTapMaxInterval &&
                Vector2.Distance(position, lastTapPosition) <= DoubleTapMaxMove)
            {
                yaw = 0f;
                pitch = 8f;
                distance = DefaultDistance;
                ApplyTransform();
                lastTapTime = float.NegativeInfinity;
                return;
            }
            lastTapTime = now;
            lastTapPosition = position;
        }

        private void ApplyTransform()
        {
            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            transform.rotation = rotation;
            transform.position = orbitTarget - rotation * Vector3.forward * distance;
        }

        public Ray ScreenPointToWorldRay(Vector2 screenPosition)
        {
            var cam = cachedCamera != null ? cachedCamera : Camera.main;
            return cam == null
                ? new Ray(orbitTarget + Vector3.back * distance, Vector3.forward)
                : cam.ScreenPointToRay(screenPosition);
        }
    }
}
