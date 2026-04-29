#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace VRSL
{
    [CustomEditor(typeof(VRStageLighting_AudioLink_RealtimeLight))]
    [CanEditMultipleObjects]
    public class VRStageLighting_AudioLink_RealtimeLightEditor : Editor
    {
        GUIStyle _sectionLabel;

        // AudioLink Settings
        SerializedProperty _enableAudioLink;
        SerializedProperty _band;
        SerializedProperty _delay;
        SerializedProperty _bandMultiplier;

        // General Settings
        SerializedProperty _maxIntensity;
        SerializedProperty _finalIntensity;
        SerializedProperty _colorMode;
        SerializedProperty _emissionColor;
        SerializedProperty _isPointLight;
        SerializedProperty _spotAngle;
        SerializedProperty _range;

        // Movement Settings
        SerializedProperty _enablePanTilt;
        SerializedProperty _panTransform;
        SerializedProperty _tiltTransform;

        // Fixture Settings
        SerializedProperty _goboIndex;
        SerializedProperty _goboSpinSpeed;

        // Fixture Shell
        SerializedProperty _fixtureShellRenderers;

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

            _enableAudioLink = serializedObject.FindProperty("enableAudioLink");
            _band            = serializedObject.FindProperty("band");
            _delay           = serializedObject.FindProperty("delay");
            _bandMultiplier  = serializedObject.FindProperty("bandMultiplier");

            _maxIntensity    = serializedObject.FindProperty("maxIntensity");
            _finalIntensity  = serializedObject.FindProperty("finalIntensity");
            _colorMode       = serializedObject.FindProperty("colorMode");
            _emissionColor   = serializedObject.FindProperty("emissionColor");
            _isPointLight    = serializedObject.FindProperty("isPointLight");
            _spotAngle       = serializedObject.FindProperty("spotAngle");
            _range           = serializedObject.FindProperty("range");

            _enablePanTilt   = serializedObject.FindProperty("enablePanTilt");
            _panTransform    = serializedObject.FindProperty("panTransform");
            _tiltTransform   = serializedObject.FindProperty("tiltTransform");

            _goboIndex       = serializedObject.FindProperty("goboIndex");
            _goboSpinSpeed   = serializedObject.FindProperty("goboSpinSpeed");

            _fixtureShellRenderers = serializedObject.FindProperty("fixtureShellRenderers");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            VRSL_EditorHeader.Draw();

            // ── AudioLink Settings ────────────────────────────────────────────
            GUILayout.Label("AudioLink Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_enableAudioLink);
            EditorGUILayout.PropertyField(_band);
            EditorGUILayout.PropertyField(_delay);
            EditorGUILayout.PropertyField(_bandMultiplier);

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── General Settings ──────────────────────────────────────────────
            GUILayout.Label("General Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_maxIntensity);
            EditorGUILayout.PropertyField(_finalIntensity);
            EditorGUILayout.PropertyField(_colorMode);
            EditorGUILayout.PropertyField(_emissionColor);
            EditorGUILayout.PropertyField(_isPointLight);
            EditorGUILayout.PropertyField(_spotAngle);
            EditorGUILayout.PropertyField(_range, new GUIContent("Spot Range", _range.tooltip));

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── Movement Settings ─────────────────────────────────────────────
            GUILayout.Label("Movement Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_enablePanTilt);
            EditorGUILayout.PropertyField(_panTransform);
            EditorGUILayout.PropertyField(_tiltTransform);

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── Fixture Settings ──────────────────────────────────────────────
            GUILayout.Label("Fixture Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_goboIndex);
            EditorGUILayout.PropertyField(_goboSpinSpeed);

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── Fixture Shell ─────────────────────────────────────────────────
            GUILayout.Label("Fixture Shell", _sectionLabel);
            EditorGUILayout.PropertyField(_fixtureShellRenderers, true);

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
