#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace VRSL
{
    [CustomEditor(typeof(VRStageLighting_DMX_RealtimeLight))]
    [CanEditMultipleObjects]
    public class VRStageLighting_DMX_RealtimeLightEditor : Editor
    {
        GUIStyle _sectionLabel;

        // DMX Settings
        SerializedProperty _enableDMXChannels;
        SerializedProperty _enableFineChannels;
        SerializedProperty _useLegacySectorMode;
        SerializedProperty _sector;
        SerializedProperty _dmxChannel;
        SerializedProperty _dmxUniverse;

        // General Settings
        SerializedProperty _maxIntensity;
        SerializedProperty _finalIntensity;
        SerializedProperty _isPointLight;
        SerializedProperty _enableConeWidth;
        SerializedProperty _minSpotAngle;
        SerializedProperty _maxSpotAngle;
        SerializedProperty _range;

        // Movement Settings
        SerializedProperty _enablePanTilt;
        SerializedProperty _invertPan;
        SerializedProperty _invertTilt;
        SerializedProperty _maxMinPan;
        SerializedProperty _maxMinTilt;
        SerializedProperty _panOffset;
        SerializedProperty _tiltOffset;

        // Fixture Settings
        SerializedProperty _enableStrobe;
        SerializedProperty _enableGobo;
        SerializedProperty _enableGoboSpin;

        // Light Output Axis
        SerializedProperty _localLightDirection;

        static GUIStyle MakeSectionLabel()
        {
            var g = new GUIStyle
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
            };
            g.normal.textColor = Color.white;
            return g;
        }

        void OnEnable()
        {
            _sectionLabel = MakeSectionLabel();

            _enableDMXChannels   = serializedObject.FindProperty("enableDMXChannels");
            _enableFineChannels  = serializedObject.FindProperty("enableFineChannels");
            _useLegacySectorMode = serializedObject.FindProperty("useLegacySectorMode");
            _sector              = serializedObject.FindProperty("sector");
            _dmxChannel          = serializedObject.FindProperty("dmxChannel");
            _dmxUniverse         = serializedObject.FindProperty("dmxUniverse");

            _maxIntensity        = serializedObject.FindProperty("maxIntensity");
            _finalIntensity      = serializedObject.FindProperty("finalIntensity");
            _isPointLight        = serializedObject.FindProperty("isPointLight");
            _enableConeWidth     = serializedObject.FindProperty("enableConeWidth");
            _minSpotAngle        = serializedObject.FindProperty("minSpotAngle");
            _maxSpotAngle        = serializedObject.FindProperty("maxSpotAngle");
            _range               = serializedObject.FindProperty("range");

            _enablePanTilt       = serializedObject.FindProperty("enablePanTilt");
            _invertPan           = serializedObject.FindProperty("invertPan");
            _invertTilt          = serializedObject.FindProperty("invertTilt");
            _maxMinPan           = serializedObject.FindProperty("maxMinPan");
            _maxMinTilt          = serializedObject.FindProperty("maxMinTilt");
            _panOffset           = serializedObject.FindProperty("panOffset");
            _tiltOffset          = serializedObject.FindProperty("tiltOffset");

            _enableStrobe        = serializedObject.FindProperty("enableStrobe");
            _enableGobo          = serializedObject.FindProperty("enableGobo");
            _enableGoboSpin      = serializedObject.FindProperty("enableGoboSpin");

            _localLightDirection = serializedObject.FindProperty("localLightDirection");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            VRSL_EditorHeader.Draw();

            var rt = (VRStageLighting_DMX_RealtimeLight)target;
            var sibling = rt.GetComponent<VRStageLighting_DMX_Static>();
            SerializedObject siblingSO = sibling != null ? new SerializedObject(sibling) : null;
            if (siblingSO != null) siblingSO.Update();

            // ── DMX Settings ──────────────────────────────────────────────────
            GUILayout.Label("DMX Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_enableDMXChannels);

            if (sibling != null)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(
                        siblingSO.FindProperty("enableFineChannels"),
                        new GUIContent("Enable Fine Channels (inherited)"));
                }

                EditorGUILayout.HelpBox(
                    "DMX addressing (legacy sector mode, sector, channel, and universe) "
                    + "and fine-channel mode are inherited from the sibling "
                    + "VRStageLighting_DMX_Static component. Edit those values there "
                    + "to keep both paths in sync.",
                    MessageType.Info);

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(
                        siblingSO.FindProperty("useLegacySectorMode"),
                        new GUIContent("Use Legacy Sector Mode (inherited)"));
                    if (sibling.useLegacySectorMode)
                    {
                        EditorGUILayout.PropertyField(
                            siblingSO.FindProperty("sector"),
                            new GUIContent("Sector (inherited)"));
                    }
                    else
                    {
                        EditorGUILayout.PropertyField(
                            siblingSO.FindProperty("dmxChannel"),
                            new GUIContent("DMX Channel (inherited)"));
                        EditorGUILayout.PropertyField(
                            siblingSO.FindProperty("dmxUniverse"),
                            new GUIContent("DMX Universe (inherited)"));
                    }
                }
            }
            else
            {
                EditorGUILayout.PropertyField(_enableFineChannels);
                EditorGUILayout.PropertyField(_useLegacySectorMode);
                if (_useLegacySectorMode.boolValue)
                {
                    EditorGUILayout.PropertyField(_sector);
                }
                else
                {
                    EditorGUILayout.PropertyField(_dmxChannel);
                    EditorGUILayout.PropertyField(_dmxUniverse);
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── General Settings ──────────────────────────────────────────────
            GUILayout.Label("General Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_maxIntensity);
            EditorGUILayout.PropertyField(_finalIntensity);
            EditorGUILayout.PropertyField(_isPointLight);
            EditorGUILayout.PropertyField(_enableConeWidth);
            EditorGUILayout.PropertyField(_minSpotAngle);
            EditorGUILayout.PropertyField(_maxSpotAngle);
            EditorGUILayout.PropertyField(_range);

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── Movement Settings ─────────────────────────────────────────────
            GUILayout.Label("Movement Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_enablePanTilt);

            if (sibling != null)
            {
                EditorGUILayout.HelpBox(
                    "Pan/tilt inversion, range, and offset are inherited from the "
                    + "sibling VRStageLighting_DMX_Static component. Edit those values "
                    + "there to keep both paths in sync.",
                    MessageType.Info);

                // Render the inherited values as raw disabled widgets rather than
                // using PropertyField on the sibling's SerializedProperty — the
                // sibling's invertPan field carries a [Header("Movement Settings")]
                // that would duplicate our section title.
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.Toggle("Invert Pan (inherited)",      rt.GetEffectiveInvertPan());
                    EditorGUILayout.Toggle("Invert Tilt (inherited)",     rt.GetEffectiveInvertTilt());
                    EditorGUILayout.FloatField("Min/Max Pan Range (inherited)",  rt.GetEffectiveMaxMinPan());
                    EditorGUILayout.FloatField("Min/Max Tilt Range (inherited)", rt.GetEffectiveMaxMinTilt());
                    EditorGUILayout.FloatField("Pan Offset (inherited)",  rt.GetEffectivePanOffset());
                    EditorGUILayout.FloatField("Tilt Offset (inherited)", rt.GetEffectiveTiltOffset());
                }
            }
            else
            {
                EditorGUILayout.PropertyField(_invertPan);
                EditorGUILayout.PropertyField(_invertTilt);
                EditorGUILayout.PropertyField(
                    _maxMinPan,
                    new GUIContent("Min/Max Pan Range", _maxMinPan.tooltip));
                EditorGUILayout.PropertyField(
                    _maxMinTilt,
                    new GUIContent("Min/Max Tilt Range", _maxMinTilt.tooltip));
                EditorGUILayout.PropertyField(_panOffset);
                EditorGUILayout.PropertyField(_tiltOffset);
            }

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── Fixture Settings ──────────────────────────────────────────────
            GUILayout.Label("Fixture Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_enableStrobe);
            EditorGUILayout.PropertyField(_enableGobo);
            EditorGUILayout.PropertyField(_enableGoboSpin);

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── Light Output Axis ─────────────────────────────────────────────
            GUILayout.Label("Light Output Axis", _sectionLabel);
            EditorGUILayout.PropertyField(_localLightDirection);

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
