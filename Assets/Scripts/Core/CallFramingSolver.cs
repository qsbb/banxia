using System;

namespace QuestMmdPlayer
{
    /// <summary>
    /// 通话/全身构图闭式解（平台无关，纯静态，无 UnityEngine 依赖）。
    /// 坐标约定：屏幕 y 自顶部向下（0 = 顶，S = 底），世界 Y 向上。
    /// pitch=0 的针孔投影：s = S/2 + (h - y) * k / d，
    /// 其中 k = (S/2) / tan(theta/2)，h = 相机高度，d = 相机到被摄平面的前向距离。
    /// </summary>
    public static class CallFramingSolver
    {
        // 语义常数：构图逻辑只能引用这些含义明确的模型/版式参数。
        public const float EyeOffset = 0.03f;
        public const float HeadHeight = 0.20f;
        public const float EyeToWaist = 2.2f * HeadHeight;
        public const float EyeToChest = 1.5f * HeadHeight;
        public const float HeadTopAboveEye = 0.10f;
        public const float FrameBandTop = 0.08f;
        public const float FrameBandBottom = 0.92f;
        public const float EyeLineRatio = 1f / 3f;
        public const float DistanceMin = 0.55f;
        public const float DistanceMax = 2.4f;
        public const float FallbackDistance = 0.9f;

        public struct Inputs
        {
            // 实测屏幕量；y 从屏幕顶部向下。
            public float S;
            public float ThetaDeg;
            public float TopPx;
            public float BottomPx;
            // 语义锚点（世界 Y）。
            public float EyeY;
            public float HeadTopY;
            public float FootY;
            public float LowCutY;
        }

        public struct Result
        {
            public float Distance;
            public float CameraY;
            public bool Degraded;
        }

        public static Result SolveBust(in Inputs i)
        {
            return SolveBust(i, DistanceMax);
        }

        /// <summary>
        /// 胸像：眼线落可视区上 1/3 线，腰线落在底控件上缘。
        /// maxDistance 允许 bounds 退化路径收紧距离上限。
        /// </summary>
        public static Result SolveBust(in Inputs i, float maxDistance)
        {
            var fallback = new Result
            {
                Distance = FallbackDistance,
                CameraY = IsFinite(i.EyeY) ? i.EyeY : 0f,
                Degraded = true,
            };
            if (!TryGetK(i.S, i.ThetaDeg, out float k) ||
                !IsFinite(i.TopPx) || !IsFinite(i.BottomPx) ||
                !IsFinite(i.EyeY) || !IsFinite(i.LowCutY) ||
                i.TopPx < 0f || i.BottomPx > i.S)
            {
                return fallback;
            }

            var band = i.BottomPx - i.TopPx;
            var span = i.EyeY - i.LowCutY;
            if (band <= 1e-3f || span <= 1e-4f)
            {
                return fallback;
            }

            var sEye = i.TopPx + band * EyeLineRatio;
            var bottomSpanPx = i.BottomPx - sEye;
            if (bottomSpanPx <= 1e-3f)
            {
                return fallback;
            }

            var dRaw = k * span / bottomSpanPx;
            if (!IsFinite(dRaw) || dRaw <= 0f)
            {
                return fallback;
            }

            var lo = DistanceMin;
            var hi = IsFinite(maxDistance) && maxDistance > lo ? maxDistance : DistanceMax;
            var d = Clamp(dRaw, lo, hi);
            float h;
            if (dRaw > hi)
            {
                // 可视带过短时优先保证腰线落在底缘，脸部仍留在画面中。
                h = i.LowCutY + (i.BottomPx - i.S * 0.5f) * d / k;
            }
            else
            {
                // 正常解以及距离下限夹取都锁定眼线，保证上部构图稳定。
                h = i.EyeY + (sEye - i.S * 0.5f) * d / k;
            }

            if (!IsFinite(h))
            {
                return fallback;
            }
            return new Result
            {
                Distance = d,
                CameraY = h,
                Degraded = dRaw < lo || dRaw > hi,
            };
        }

        /// <summary>
        /// 全身像：头顶落 8%、脚落 92% 安全带。
        /// 返回距离交由轨道相机的硬件量程再次夹取。
        /// </summary>
        public static Result SolveFullBody(in Inputs i)
        {
            var fallback = new Result
            {
                Distance = FallbackDistance,
                CameraY = IsFinite(i.HeadTopY) && IsFinite(i.FootY)
                    ? 0.5f * (i.HeadTopY + i.FootY)
                    : 0f,
                Degraded = true,
            };
            if (!TryGetK(i.S, i.ThetaDeg, out float k) ||
                !IsFinite(i.HeadTopY) || !IsFinite(i.FootY))
            {
                return fallback;
            }

            var span = i.HeadTopY - i.FootY;
            var bandRatio = FrameBandBottom - FrameBandTop;
            if (span <= 1e-4f || bandRatio <= 1e-4f)
            {
                return fallback;
            }

            var dRaw = k * span / (bandRatio * i.S);
            var h = 0.5f * (i.HeadTopY + i.FootY);
            if (!IsFinite(dRaw) || dRaw <= 0f || !IsFinite(h))
            {
                return fallback;
            }
            var d = Clamp(dRaw, DistanceMin, DistanceMax);
            return new Result
            {
                Distance = d,
                CameraY = h,
                Degraded = dRaw < DistanceMin || dRaw > DistanceMax,
            };
        }

        private static bool TryGetK(float s, float thetaDeg, out float k)
        {
            k = 0f;
            if (!IsFinite(s) || !IsFinite(thetaDeg) || s <= 1f ||
                thetaDeg <= 0.1f || thetaDeg >= 179.9f)
            {
                return false;
            }
            var half = thetaDeg * 0.5f * (float)Math.PI / 180f;
            var tan = (float)Math.Tan(half);
            if (!IsFinite(tan) || tan <= 1e-4f)
            {
                return false;
            }
            k = s * 0.5f / tan;
            return IsFinite(k) && k > 0f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static float Clamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }
}
