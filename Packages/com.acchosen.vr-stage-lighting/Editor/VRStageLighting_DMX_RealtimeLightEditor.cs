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
        SerializedProperty _globalIntensity;
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

        // Fixture Shell
        SerializedProperty _fixtureShellRenderers;
        SerializedProperty _shellEmissionTint;

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
            _globalIntensity     = serializedObject.FindProperty("globalIntensity");
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

            _fixtureShellRenderers = serializedObject.FindProperty("fixtureShellRenderers");
            _shellEmissionTint     = serializedObject.FindProperty("shellEmissionTint");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            VRSL_EditorHeader.Draw();

            // ── DMX Settings ──────────────────────────────────────────────────
            GUILayout.Label("DMX Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_enableDMXChannels);
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

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── General Settings ──────────────────────────────────────────────
            GUILayout.Label("General Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_maxIntensity);
            EditorGUILayout.PropertyField(_finalIntensity);
            EditorGUILayout.PropertyField(_globalIntensity);
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

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── Fixture Shell ─────────────────────────────────────────────────
            GUILayout.Label("Fixture Shell", _sectionLabel);
            EditorGUILayout.PropertyField(_fixtureShellRenderers, true);
            EditorGUILayout.PropertyField(_shellEmissionTint);

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
