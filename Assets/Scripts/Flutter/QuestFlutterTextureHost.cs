using System;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Compile-safe seam for a future offscreen Quest → Flutter texture path.
    ///
    /// Flutter can render Unity content through its <c>Texture</c> widget by
    /// consuming an external texture id from the platform's graphics API. On
    /// Quest/IL2CPP that means: render a <see cref="RenderTexture"/> offscreen
    /// (a dedicated camera or GL.IssuePluginEvent), obtain its native texture
    /// pointer, and publish it to the Android plugin so it can call
    /// <c>TextureRegistry.registerTexture(id, SurfaceTexture(...))</c>.
    ///
    /// That implementation does not exist yet. This component therefore reports
    /// an explicit <see cref="RenderHostState.Unsupported"/> state and refuses to
    /// fake success: no texture is allocated and every "begin" call returns
    /// false. It exists so callers (the facade, the bridge, the Android plugin)
    /// can already depend on a stable API surface without a compile-time
    /// Flutter or graphics-backend dependency.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class QuestFlutterTextureHost : MonoBehaviour
    {
        public enum RenderHostState
        {
            Unsupported = 0,
            Idle = 1,
            Rendering = 2,
            Stopped = 3,
            Failed = 4
        }

        private const string UnsupportedMessage =
            "Offscreen Quest→Flutter texture rendering is not implemented; this host is a compile-safe seam only.";

        [SerializeField] private int preferredWidth = 1024;
        [SerializeField] private int preferredHeight = 1024;

        /// <summary>Always <see cref="RenderHostState.Unsupported"/> until the renderer is implemented.</summary>
        public RenderHostState State { get; private set; } = RenderHostState.Unsupported;

        /// <summary>Whether a real offscreen renderer is available. Hard-coded false for now.</summary>
        public bool IsSupported => false;

        /// <summary>Human-readable status for diagnostics / the Flutter Settings page.</summary>
        public string Status => UnsupportedMessage;

        /// <summary>Requested output resolution; informational until implemented.</summary>
        public int PreferredWidth => Mathf.Max(1, preferredWidth);
        public int PreferredHeight => Mathf.Max(1, preferredHeight);

        /// <summary>The offscreen render target. Always null until implemented.</summary>
        public RenderTexture OutputRenderTexture => null;

        /// <summary>A CPU-readable copy of the latest frame. Always null until implemented.</summary>
        public Texture2D OutputTexture => null;

        /// <summary>The platform texture id to publish to Flutter. Always 0 until implemented.</summary>
        public long NativeTextureId => 0L;

        /// <summary>
        /// Begins (or resizes) the offscreen renderer. Returns false with the
        /// unsupported reason rather than pretending to start. The future
        /// implementation allocates a <see cref="RenderTexture"/> here.
        /// </summary>
        public bool TryBegin()
        {
            return TryBegin(preferredWidth, preferredHeight);
        }

        public bool TryBegin(int width, int height)
        {
            LogUnsupported("TryBegin");
            return false;
        }

        /// <summary>Submits the latest offscreen frame to Flutter. Always false until implemented.</summary>
        public bool TrySubmitFrame()
        {
            LogUnsupported("TrySubmitFrame");
            return false;
        }

        /// <summary>Stops rendering and releases the offscreen target (no-op today).</summary>
        public void Stop()
        {
            // Deliberately a no-op: there is nothing to release in the
            // unsupported state, and silently setting State to Stopped would
            // imply a renderer existed.
        }

        /// <summary>Reports the current state for the diagnostics snapshot path.</summary>
        public string Describe()
        {
            return $"QuestFlutterTextureHost: {State} ({Status})";
        }

        private void LogUnsupported(string operation)
        {
            // Throttled so repeated calls from a poller do not spam logcat.
            if (Time.unscaledTime < nextUnsupportedLogAt)
            {
                return;
            }
            nextUnsupportedLogAt = Time.unscaledTime + 5f;
            Debug.LogWarning($"[QuestFlutterTextureHost] {operation} unavailable: {UnsupportedMessage}");
        }

        private float nextUnsupportedLogAt;
    }
}
