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

            bool inheritsAddressing =
                ((VRStageLighting_DMX_RealtimeLight)target)
                    .GetComponent<VRStageLighting_DMX_Static>() != null;

            // ── DMX Settings ──────────────────────────────────────────────────
            GUILayout.Label("DMX Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_enableDMXChannels);
            EditorGUILayout.PropertyField(_enableFineChannels);

            if (inheritsAddressing)
            {
                EditorGUILayout.HelpBox(
                    "DMX addressing inherited from the sibling "
                    + "VRStageLighting_DMX_Static component. Edit addressing there "
                    + "(the Static component) to keep both paths in sync.",
                    MessageType.Info);
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
            EditorGUILayout.PropertyField(_maxMinPan);
            EditorGUILayout.PropertyField(_maxMinTilt);
            EditorGUILayout.PropertyField(_invertPan);
            EditorGUILayout.PropertyField(_invertTilt);
            EditorGUILayout.PropertyField(_panOffset);
            EditorGUILayout.PropertyField(_tiltOffset);

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
