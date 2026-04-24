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

        // Light Settings
        SerializedProperty _maxIntensity;
        SerializedProperty _finalIntensity;
        SerializedProperty _enableStrobe;
        SerializedProperty _enableConeWidth;
        SerializedProperty _minSpotAngle;
        SerializedProperty _maxSpotAngle;
        SerializedProperty _range;
        SerializedProperty _isPointLight;
        SerializedProperty _enableGobo;
        SerializedProperty _enableGoboSpin;

        // Pan / Tilt
        SerializedProperty _enablePanTilt;
        SerializedProperty _maxMinPan;
        SerializedProperty _maxMinTilt;
        SerializedProperty _invertPan;
        SerializedProperty _invertTilt;
        SerializedProperty _panOffset;
        SerializedProperty _tiltOffset;

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
            _enableStrobe        = serializedObject.FindProperty("enableStrobe");
            _enableConeWidth     = serializedObject.FindProperty("enableConeWidth");
            _minSpotAngle        = serializedObject.FindProperty("minSpotAngle");
            _maxSpotAngle        = serializedObject.FindProperty("maxSpotAngle");
            _range               = serializedObject.FindProperty("range");
            _isPointLight        = serializedObject.FindProperty("isPointLight");
            _enableGobo          = serializedObject.FindProperty("enableGobo");
            _enableGoboSpin      = serializedObject.FindProperty("enableGoboSpin");

            _enablePanTilt       = serializedObject.FindProperty("enablePanTilt");
            _maxMinPan           = serializedObject.FindProperty("maxMinPan");
            _maxMinTilt          = serializedObject.FindProperty("maxMinTilt");
            _invertPan           = serializedObject.FindProperty("invertPan");
            _invertTilt          = serializedObject.FindProperty("invertTilt");
            _panOffset           = serializedObject.FindProperty("panOffset");
            _tiltOffset          = serializedObject.FindProperty("tiltOffset");

            _localLightDirection = serializedObject.FindProperty("localLightDirection");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var rt = (VRStageLighting_DMX_RealtimeLight)target;
            var sibling = rt.GetComponent<VRStageLighting_DMX_Static>();

            // ── DMX Settings ──────────────────────────────────────────────────
            GUILayout.Label("DMX Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_enableDMXChannels);
            EditorGUILayout.PropertyField(_enableFineChannels);

            if (sibling != null)
            {
                EditorGUILayout.HelpBox(
                    "DMX addressing (legacy sector mode, sector, channel, and universe) "
                    + "is inherited from the sibling VRStageLighting_DMX_Static component. "
                    + "Edit addressing there to keep both paths in sync.",
                    MessageType.Info);

                // Show what's being inherited, read-only, so the effective addressing
                // is visible at a glance without opening the sibling component.
                var siblingSO = new SerializedObject(sibling);
                siblingSO.Update();
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

            // ── Light Settings ────────────────────────────────────────────────
            GUILayout.Label("Light Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_maxIntensity);
            EditorGUILayout.PropertyField(_finalIntensity);
            EditorGUILayout.PropertyField(_enableStrobe);
            EditorGUILayout.PropertyField(_enableConeWidth);
            EditorGUILayout.PropertyField(_minSpotAngle);
            EditorGUILayout.PropertyField(_maxSpotAngle);
            EditorGUILayout.PropertyField(_range);
            EditorGUILayout.PropertyField(_isPointLight);
            EditorGUILayout.PropertyField(_enableGobo);
            EditorGUILayout.PropertyField(_enableGoboSpin);

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── Pan / Tilt (Moving Head) ──────────────────────────────────────
            GUILayout.Label("Pan / Tilt (Moving Head)", _sectionLabel);
            EditorGUILayout.PropertyField(_enablePanTilt);

            if (sibling != null)
            {
                EditorGUILayout.HelpBox(
                    "Pan/tilt range, inversion, and offset are inherited from the "
                    + "sibling VRStageLighting_DMX_Static component. Edit those values "
                    + "there to keep both paths in sync.",
                    MessageType.Info);

                var siblingSO = new SerializedObject(sibling);
                siblingSO.Update();
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(
                        siblingSO.FindProperty("maxMinPan"),
                        new GUIContent("Max/Min Pan (inherited)"));
                    EditorGUILayout.PropertyField(
                        siblingSO.FindProperty("maxMinTilt"),
                        new GUIContent("Max/Min Tilt (inherited)"));
                    EditorGUILayout.PropertyField(
                        siblingSO.FindProperty("invertPan"),
                        new GUIContent("Invert Pan (inherited)"));
                    EditorGUILayout.PropertyField(
                        siblingSO.FindProperty("invertTilt"),
                        new GUIContent("Invert Tilt (inherited)"));
                    EditorGUILayout.PropertyField(
                        siblingSO.FindProperty("panOffsetBlueGreen"),
                        new GUIContent("Pan Offset (inherited)"));
                    EditorGUILayout.PropertyField(
                        siblingSO.FindProperty("tiltOffsetBlue"),
                        new GUIContent("Tilt Offset (inherited)"));
                }
            }
            else
            {
                EditorGUILayout.PropertyField(_maxMinPan);
                EditorGUILayout.PropertyField(_maxMinTilt);
                EditorGUILayout.PropertyField(_invertPan);
                EditorGUILayout.PropertyField(_invertTilt);
                EditorGUILayout.PropertyField(_panOffset);
                EditorGUILayout.PropertyField(_tiltOffset);
            }

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
