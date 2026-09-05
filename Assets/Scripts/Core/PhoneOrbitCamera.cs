using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Phone-form orbit camera: single-finger drag orbits around the avatar,
    /// pinch zooms, double-tap re-frames the avatar, and a two-finger common
    /// drag moves the avatar on the ground plane (visible position adjustment).
    /// Attached only in BANXIA_PHONE builds by QuestMmdPlayerBootstrap.
    /// </summary>
    public sealed class PhoneOrbitCamera : MonoBehaviour
    {
        private const float DefaultDistance = 2.6f;
        // 下限与 CallFramingSolver.DistanceMin（0.55m）对齐：否则胸像闭式解
        // （竖屏通话 d≈0.6–0.8m）会被轨道相机静默夹到 0.9m，构图失准。
        private const float MinDistance = 0.55f;
        private const float MaxDistance = 5.5f;
        private const float OrbitDegreesPerPixel = 0.22f;
        private const float ZoomDistancePerPixel = 0.012f;
        private const float MinPitch = -35f;
        private const float MaxPitch = 55f;
        private const float DoubleTapMaxInterval = 0.35f;
        private const float DoubleTapMaxMove = 40f;
        private const float DefaultPitch = 8f;
        /// <summary>两指共拖：每像素移动的地面距离，随相机距离缩放（远处移动更快）。</summary>
        private const float GroundPanUnitsPerPixel = 0.0022f;
        /// <summary>角色可被拖离原点的最大水平半径（米）。</summary>
        private const float MaxAvatarGroundRadius = 6f;

        private Vector3 orbitTarget = new Vector3(0f, 1.25f, 2.2f);
        private float yaw;
        private float pitch = DefaultPitch;
        private float distance = DefaultDistance;
        private float lastTapTime = float.NegativeInfinity;
        private Vector2 lastTapPosition;

        private Camera cachedCamera;
        private Vector2 previousTouchPosition;
        private Vector2 previousTwoFingerCenter;
        private float previousPinchDistance;
        private bool hasGestureState;

        /// <summary>当前被取景/移动的角色根（AvatarLoaded 接线设置）。</summary>
        private Transform trackedAvatar;
        /// <summary>场景工具条「移动模式」：单指拖动直接移动角色而非环绕。</summary>
        public bool SingleFingerMovesAvatar { get; set; }

        /// <summary>
        /// 视频通话构图由闭式求解器接管时关闭，避免双击取景恢复俯仰角后破坏投影前提。
        /// </summary>
        public bool GestureReframeEnabled { get; set; } = true;

        public float Distance => distance;

        /// <summary>轨道距离（夹到 [MinDistance, MaxDistance]）。供同框导演做胸像构图。</summary>
        public float OrbitDistance
        {
            get => distance;
            set
            {
                distance = Mathf.Clamp(value, MinDistance, MaxDistance);
                ApplyTransform();
            }
        }

        /// <summary>轨道目标点（只读快照）。</summary>
        public Vector3 OrbitTargetPoint => orbitTarget;

        /// <summary>水平环绕角（度）。供同框导演保存/恢复视角。</summary>
        public float OrbitYawAngle
        {
            get => yaw;
            set
            {
                yaw = value;
                ApplyTransform();
            }
        }

        /// <summary>俯仰角（度，夹到合法范围）。</summary>
        public float OrbitPitchAngle
        {
            get => pitch;
            set
            {
                pitch = Mathf.Clamp(value, MinPitch, MaxPitch);
                ApplyTransform();
            }
        }

        private void Start()
        {
            cachedCamera = GetComponent<Camera>();
            ApplyTransform();
        }

        public void SetOrbitTarget(Vector3 position)
        {
            orbitTarget = position;
            ApplyTransform();
        }

        public void SetTrackedAvatar(Transform avatarRoot)
        {
            trackedAvatar = avatarRoot;
        }

        /// <summary>
        /// 按模型渲染包围盒自动取景：目标 = 盒中心，距离 = 盒高自适应。
        /// 修复旧版固定 (出生点+1.25m) 目标与实际模型错位导致的"角色卡上半屏"。
        /// </summary>
        public void FrameModel(GameObject root)
        {
            if (root == null)
            {
                return;
            }
            var bounds = ComputeRenderBounds(root);
            if (bounds.size.sqrMagnitude < 1e-8f)
            {
                return;
            }
            // Full-body framing uses visible renderer bounds for both anchored and
            // bounds-only PMX models, so a missing HeadBone cannot silently restore
            // the old geometric-center framing.
            var avatar = root.GetComponent<AvatarController>();
            var head = avatar != null ? avatar.HeadBone : null;
            var cam = cachedCamera != null ? cachedCamera : Camera.main;
            float s = cam != null && cam.pixelHeight > 1 ? cam.pixelHeight : Screen.height;
            float theta = cam != null && cam.fieldOfView > 0.1f ? cam.fieldOfView : 60f;
            float eyeY = head != null
                ? head.position.y + CallFramingSolver.EyeOffset
                : bounds.min.y + bounds.size.y * 0.83f;
            var solve = CallFramingSolver.SolveFullBody(new CallFramingSolver.Inputs
            {
                S = s,
                ThetaDeg = theta,
                TopPx = 0f,
                BottomPx = s,
                EyeY = eyeY,
                // Use visible bounds for the top anchor. Hair, bows and models
                // without a recognized head bone are covered by the same solver.
                HeadTopY = bounds.max.y,
                FootY = bounds.min.y,
                LowCutY = eyeY - CallFramingSolver.EyeToWaist,
            });
            orbitTarget = new Vector3(bounds.center.x, solve.CameraY, bounds.center.z);
            // SolveFullBody owns the [DistanceMin, DistanceMax] contract and
            // marks any clamp as degraded; keep this assignment unmodified so
            // diagnostics cannot disagree with the camera state.
            distance = solve.Distance;
            if (solve.Degraded)
            {
                Debug.LogWarning("[CallFraming] full-body framing degraded by distance clamp.", this);
            }
            // SolveFullBody uses the pitch=0 projection. Keep the runtime camera
            // on that contract instead of applying the previous orbit tilt.
            yaw = 0f;
            pitch = 0f;
            Debug.Log($"[PhoneFrame] root={root.name} boundsY={bounds.min.y:F3}-{bounds.max.y:F3} " +
                      $"targetY={orbitTarget.y:F3} distance={distance:F3} pitch={pitch:F1}", this);
            ApplyTransform();
        }

        /// <summary>重置视角并重新取景当前角色（双击触发）。</summary>
        public void Reframe()
        {
            yaw = 0f;
            if (trackedAvatar != null)
            {
                FrameModel(trackedAvatar.gameObject);
                return;
            }
            pitch = DefaultPitch;
            distance = DefaultDistance;
            ApplyTransform();
        }

        /// <summary>合并根下全部 Renderer（跳过粒子）的世界包围盒，供取景与构图共享。</summary>
        public static Bounds ComputeRenderBounds(GameObject root)
        {
            return RenderBoundsUtility.Compute(root);
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
                var center = (a.position + b.position) * 0.5f;
                if (hasGestureState)
                {
                    // 双指共拖：两指同向平移 → 地面平面移动角色（屏幕位置调整）。
                    var centerDelta = center - previousTwoFingerCenter;
                    var pinchDelta = pinchDistance - previousPinchDistance;
                    // 捏合变化占主导 → 缩放；否则按平移处理（阈值避免同时误触）。
                    if (Mathf.Abs(pinchDelta) * ZoomDistancePerPixel >
                        centerDelta.magnitude * GroundPanUnitsPerPixel * distance * 3f)
                    {
                        distance = Mathf.Clamp(
                            distance - pinchDelta * ZoomDistancePerPixel,
                            MinDistance, MaxDistance);
                    }
                    else
                    {
                        MoveAvatarByScreenDelta(centerDelta);
                    }
                    ApplyTransform();
                }
                previousPinchDistance = pinchDistance;
                previousTwoFingerCenter = center;
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
                        if (SingleFingerMovesAvatar)
                        {
                            MoveAvatarByScreenDelta(delta);
                        }
                        else
                        {
                            yaw += delta.x * OrbitDegreesPerPixel;
                            pitch = Mathf.Clamp(pitch - delta.y * OrbitDegreesPerPixel, MinPitch, MaxPitch);
                        }
                        ApplyTransform();
                    }
                    previousTouchPosition = touch.position;
                    return;
                }

                if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                {
                    if (!GestureCapturedByTouch)
                    {
                        HandleTapReset(touch.position);
                    }
                    hasGestureState = false;
                    return;
                }
                return;
            }

            hasGestureState = false;
        }

        /// <summary>
        /// 屏幕拖动增量 → 地面平面(XZ)位移。已追踪角色时只移动角色根，
        /// 不同步移动轨道目标，这样拖动会真正改变角色在屏幕中的位置；
        /// 需要重新居中时再双击或点「取景」。
        /// </summary>
        private void MoveAvatarByScreenDelta(Vector2 screenDelta)
        {
            if (screenDelta.sqrMagnitude < 1f)
            {
                return;
            }
            var camRight = transform.right;
            camRight.y = 0f;
            camRight.Normalize();
            var camForward = transform.forward;
            camForward.y = 0f;
            camForward.Normalize();
            var panScale = GroundPanUnitsPerPixel * distance;
            var groundDelta = (camRight * screenDelta.x + camForward * screenDelta.y) * panScale;
            if (trackedAvatar != null)
            {
                var position = trackedAvatar.position + groundDelta;
                var horizontal = new Vector3(position.x, 0f, position.z);
                if (horizontal.magnitude > MaxAvatarGroundRadius)
                {
                    horizontal = horizontal.normalized * MaxAvatarGroundRadius;
                }
                trackedAvatar.position = new Vector3(horizontal.x, position.y, horizontal.z);
            }
            else
            {
                orbitTarget += groundDelta;
            }
        }

        private void HandleTapReset(Vector2 position)
        {
            if (!GestureReframeEnabled)
            {
                return;
            }
            var now = Time.unscaledTime;
            if (now - lastTapTime <= DoubleTapMaxInterval &&
                Vector2.Distance(position, lastTapPosition) <= DoubleTapMaxMove)
            {
                Reframe();
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
