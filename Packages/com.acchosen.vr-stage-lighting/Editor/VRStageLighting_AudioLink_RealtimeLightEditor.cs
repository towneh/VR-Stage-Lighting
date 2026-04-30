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

        // Fixture Type
        SerializedProperty _fixtureType;

        // AudioLink Settings
        SerializedProperty _enableAudioLink;
        SerializedProperty _band;
        SerializedProperty _delay;
        SerializedProperty _bandMultiplier;

        // General Settings
        SerializedProperty _maxIntensity;
        SerializedProperty _finalIntensity;
        SerializedProperty _globalIntensity;
        SerializedProperty _colorMode;
        SerializedProperty _emissionColor;
        SerializedProperty _textureSamplingCoordinates;
        SerializedProperty _isPointLight;
        SerializedProperty _spotAngle;
        SerializedProperty _range;
        SerializedProperty _emitterDepth;

        // Movement Settings
        SerializedProperty _enablePanTilt;
        SerializedProperty _panTransform;
        SerializedProperty _tiltTransform;
        SerializedProperty _targetToFollow;

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

            _fixtureType     = serializedObject.FindProperty("fixtureType");

            _enableAudioLink = serializedObject.FindProperty("enableAudioLink");
            _band            = serializedObject.FindProperty("band");
            _delay           = serializedObject.FindProperty("delay");
            _bandMultiplier  = serializedObject.FindProperty("bandMultiplier");

            _maxIntensity              = serializedObject.FindProperty("maxIntensity");
            _finalIntensity            = serializedObject.FindProperty("finalIntensity");
            _globalIntensity           = serializedObject.FindProperty("globalIntensity");
            _colorMode                 = serializedObject.FindProperty("colorMode");
            _emissionColor             = serializedObject.FindProperty("emissionColor");
            _textureSamplingCoordinates = serializedObject.FindProperty("textureSamplingCoordinates");
            _isPointLight              = serializedObject.FindProperty("isPointLight");
            _spotAngle                 = serializedObject.FindProperty("spotAngle");
            _range                     = serializedObject.FindProperty("range");
            _emitterDepth              = serializedObject.FindProperty("emitterDepth");

            _enablePanTilt   = serializedObject.FindProperty("enablePanTilt");
            _panTransform    = serializedObject.FindProperty("panTransform");
            _tiltTransform   = serializedObject.FindProperty("tiltTransform");
            _targetToFollow  = serializedObject.FindProperty("targetToFollow");

            _goboIndex       = serializedObject.FindProperty("goboIndex");
            _goboSpinSpeed   = serializedObject.FindProperty("goboSpinSpeed");

            _fixtureShellRenderers = serializedObject.FindProperty("fixtureShellRenderers");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            VRSL_EditorHeader.Draw();

            // ── Fixture Type ──────────────────────────────────────────────────
            // Top-level archetype dropdown — drives which sections below are shown.
            GUILayout.Label("Fixture Type", _sectionLabel);
            EditorGUILayout.PropertyField(_fixtureType, new GUIContent("Type", _fixtureType.tooltip));

            var type = (AudioLinkFixtureType)_fixtureType.enumValueIndex;
            bool isMover     = type == AudioLinkFixtureType.MoverSpotlight
                            || type == AudioLinkFixtureType.MoverWashlight;
            bool showMovement = isMover || type == AudioLinkFixtureType.Custom;
            bool showGobo     = type == AudioLinkFixtureType.MoverSpotlight
                             || type == AudioLinkFixtureType.Custom;

            EditorGUILayout.Space();
            EditorGUILayout.Space();

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
            EditorGUILayout.PropertyField(_globalIntensity);
            EditorGUILayout.PropertyField(_colorMode);

            // Show only the field relevant to the selected color mode.
            var mode = (ALRealtimeColorMode)_colorMode.enumValueIndex;
            if (mode == ALRealtimeColorMode.Emission)
                EditorGUILayout.PropertyField(_emissionColor);
            else if (mode == ALRealtimeColorMode.ColorTexture
                  || mode == ALRealtimeColorMode.ColorTextureTraditional)
                EditorGUILayout.PropertyField(_textureSamplingCoordinates);

            EditorGUILayout.PropertyField(_isPointLight);
            EditorGUILayout.PropertyField(_spotAngle);
            EditorGUILayout.PropertyField(_range, new GUIContent("Spot Range", _range.tooltip));
            // Emitter depth only meaningfully affects spot cones; hide for point lights
            // since the math collapses back to a point source regardless.
            if (!_isPointLight.boolValue)
                EditorGUILayout.PropertyField(_emitterDepth);

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── Movement Settings ─────────────────────────────────────────────
            // Movers (Spotlight, Washlight) and Custom show pan/tilt + target follow.
            // Static fixtures hide this section since they don't pan or tilt.
            if (showMovement)
            {
                GUILayout.Label("Movement Settings", _sectionLabel);
                EditorGUILayout.PropertyField(_enablePanTilt);
                EditorGUILayout.PropertyField(_panTransform);
                EditorGUILayout.PropertyField(_tiltTransform);
                EditorGUILayout.PropertyField(_targetToFollow);

                EditorGUILayout.Space();
                EditorGUILayout.Space();
            }

            // ── Fixture Settings ──────────────────────────────────────────────
            // Spotlights and Custom show gobo selection. Washlights, Blinders,
            // ParLights don't have gobos — hide the section entirely.
            if (showGobo)
            {
                GUILayout.Label("Fixture Settings", _sectionLabel);
                EditorGUILayout.PropertyField(_goboIndex);
                EditorGUILayout.PropertyField(_goboSpinSpeed);

                EditorGUILayout.Space();
                EditorGUILayout.Space();
            }

            // ── Fixture Shell ─────────────────────────────────────────────────
            GUILayout.Label("Fixture Shell", _sectionLabel);
            EditorGUILayout.PropertyField(_fixtureShellRenderers, true);

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
