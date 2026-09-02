using System;
using UnityEngine;

namespace QuestMmdPlayer
{
    public enum CoPresenceMode
    {
        VirtualScene = 0,
        VideoCall = 1,
        ArReality = 2,
    }

    public enum VirtualEnvironment
    {
        NightStreet = 0,
        StarrySky = 1,
        Bedroom = 2,
        Seaside = 3,
    }

    /// <summary>
    /// Shared presentation snapshot. World-space anchor points remain available to
    /// Unity diagnostics; the Flutter projection consumes the same values after
    /// converting them to normalized screen coordinates.
    /// </summary>
    [Serializable]
    public struct CoPresenceFraming
    {
        public bool Valid;
        public bool HeadAnchor;
        public bool Degraded;
        public float ScreenHeight;
        public float TopPx;
        public float BottomPx;
        public float Distance;
        public float CameraY;
        public float EyeY;
        public float HeadTopY;
        public float LowCutY;
        public float FootY;
        public Vector3 EyeWorld;
        public Vector3 HeadTopWorld;
        public Vector3 LowCutWorld;
        public Vector3 FootWorld;
    }

    /// <summary>
    /// Platform-neutral co-presence state contract. Implementations own their
    /// camera or passthrough presentation, but commands and state events stay
    /// identical on phone and Quest.
    /// </summary>
    public interface ICoPresenceDirector
    {
        Camera MainCamera { get; }
        bool ArActive { get; }
        CoPresenceMode CurrentMode { get; }
        VirtualEnvironment CurrentEnvironment { get; }
        bool VideoCallActive { get; }
        string CallDurationText { get; }
        bool ArCameraAvailable { get; }
        bool ArPlaced { get; }
        CoPresenceFraming CurrentFraming { get; }
        event Action<CoPresenceMode> ModeChanged;
        event Action<VirtualEnvironment> EnvironmentChanged;
        void Initialize(Camera camera);
        void ApplyOnEnterScene();
        void Suspend();
        bool SwitchMode(CoPresenceMode mode);
        void SwitchEnvironment(VirtualEnvironment environment);
        void SetChromeInsets(float top, float bottom);
        bool PlaceAvatarAtScreenPoint(Vector2 screenPoint);
        void SetAvatar(Transform avatar);
    }
}
