using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRSL
{
    /// <summary>
    /// Singleton manager that reads the three VRSL DMX RenderTextures back to CPU memory
    /// via AsyncGPUReadback each frame. Fixture scripts (VRStageLighting_DMX_RealtimeLight)
    /// sample the cached pixel arrays to drive Unity realtime lights without GPU overhead
    /// on the per-fixture level.
    ///
    /// Place one instance of this component in your scene alongside the existing VRSL
    /// CRT/DMX camera setup.
    /// </summary>
    [AddComponentMenu("VRSL/DMX Reader (Realtime Lights)")]
    public class VRSLDMXReader : MonoBehaviour
    {
        public static VRSLDMXReader Instance { get; private set; }

        [Header("DMX Render Textures")]
        [Tooltip("Main DMX data texture. Carries intensity, color, gobo, and motor-speed channels.")]
        public RenderTexture dmxMainTexture;

        [Tooltip("Movement data texture. Carries pan and tilt channels.")]
        public RenderTexture dmxMovementTexture;

        [Tooltip("Strobe output texture. Carries the pre-computed strobe gate (0 or 1).")]
        public RenderTexture dmxStrobeTexture;

        Color[] _mainPixels;
        Color[] _movementPixels;
        Color[] _strobePixels;

        int _mainW, _mainH;
        int _movementW, _movementH;
        int _strobeW, _strobeH;

        bool _mainPending, _movementPending, _strobePending;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void LateUpdate()
        {
            IssueReadback(dmxMainTexture,      ref _mainPending,     OnMainComplete);
            IssueReadback(dmxMovementTexture,  ref _movementPending, OnMovementComplete);
            IssueReadback(dmxStrobeTexture,    ref _strobePending,   OnStrobeComplete);
        }

        void IssueReadback(RenderTexture tex, ref bool pending, Action<AsyncGPUReadbackRequest> cb)
        {
            if (tex == null || pending) return;
            pending = true;
            AsyncGPUReadback.Request(tex, 0, TextureFormat.RGBAFloat, cb);
        }

        void OnMainComplete(AsyncGPUReadbackRequest req)
        {
            _mainPending = false;
            if (req.hasError) return;
            var data = req.GetData<Color>();
            if (_mainPixels == null || _mainPixels.Length != data.Length)
            {
                _mainPixels = new Color[data.Length];
                _mainW = req.width;
                _mainH = req.height;
            }
            data.CopyTo(_mainPixels);
        }

        void OnMovementComplete(AsyncGPUReadbackRequest req)
        {
            _movementPending = false;
            if (req.hasError) return;
            var data = req.GetData<Color>();
            if (_movementPixels == null || _movementPixels.Length != data.Length)
            {
                _movementPixels = new Color[data.Length];
                _movementW = req.width;
                _movementH = req.height;
            }
            data.CopyTo(_movementPixels);
        }

        void OnStrobeComplete(AsyncGPUReadbackRequest req)
        {
            _strobePending = false;
            if (req.hasError) return;
            var data = req.GetData<Color>();
            if (_strobePixels == null || _strobePixels.Length != data.Length)
            {
                _strobePixels = new Color[data.Length];
                _strobeW = req.width;
                _strobeH = req.height;
            }
            data.CopyTo(_strobePixels);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Public accessors — called per-fixture each LateUpdate
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>Returns 0-1 value for the given absolute DMX channel from the main texture.</summary>
        public float GetMainValue(int absoluteChannel)
        {
            if (_mainPixels == null || _mainW == 0) return 0f;
            return SampleChannel(absoluteChannel, _mainPixels, _mainW, _mainH);
        }

        /// <summary>Returns 0-1 value for the given absolute DMX channel from the movement texture.</summary>
        public float GetMovementValue(int absoluteChannel)
        {
            if (_movementPixels == null || _movementW == 0) return 0f;
            return SampleChannel(absoluteChannel, _movementPixels, _movementW, _movementH);
        }

        /// <summary>
        /// Returns 0-1 strobe gate value. Returns 1 (always-on) when the strobe texture
        /// has not yet been read, so lights behave correctly even without the strobe CRT.
        /// </summary>
        public float GetStrobeValue(int absoluteChannel)
        {
            if (_strobePixels == null || _strobeW == 0) return 1f;
            return SampleChannel(absoluteChannel, _strobePixels, _strobeW, _strobeH);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // C# port of getValueAtCoords() from VRSL-DMXFunctions.cginc
        // Supports the standard IndustryRead mode only (non-Legacy, non-NineUniverse).
        // ──────────────────────────────────────────────────────────────────────────
        static float SampleChannel(int dmxChannel, Color[] pixels, int w, int h)
        {
            int x = dmxChannel % 13;
            if (x == 0) x = 13;

            // Matches: half y = DMXChannel/13.0; y = frac(y)==0 ? y-1 : y;
            float y = dmxChannel % 13 == 0
                ? dmxChannel / 13f - 1f
                : dmxChannel / 13f;

            // Corrections for the 13th column in specific channel ranges (matches CGINC exactly)
            if (x == 13)
            {
                if (dmxChannel >=   90 && dmxChannel <=  101) y -= 1f;
                if (dmxChannel >=  160 && dmxChannel <=  205) y -= 1f;
                if (dmxChannel >=  326 && dmxChannel <=  404) y -= 1f;
                if (dmxChannel >=  676 && dmxChannel <=  819) y -= 1f;
                if (dmxChannel >= 1339)                        y -= 1f;
            }

            // IndustryRead UV calculation (matches VRSL-DMXFunctions.cginc IndustryRead())
            float resMultX = w / 13f;
            float u = (x * resMultX / w) - 0.015f;
            float v = ((y + 1f) * resMultX / h) - 0.001915f;

            int px = Mathf.Clamp(Mathf.FloorToInt(u * w), 0, w - 1);
            int py = Mathf.Clamp(Mathf.FloorToInt(v * h), 0, h - 1);

            Color c = pixels[py * w + px];
            // LinearRgbToLuminance — matches CGINC standard (non-NineUniverse) path
            return c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
        }
    }
}
