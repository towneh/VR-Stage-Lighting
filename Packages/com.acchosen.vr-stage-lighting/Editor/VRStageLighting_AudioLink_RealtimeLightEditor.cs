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
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            VRSL_EditorHeader.Draw();

            var rt = (VRStageLighting_AudioLink_RealtimeLight)target;
            var sibling = rt.GetComponent<VRStageLighting_AudioLink_Static>();

            // ── AudioLink Settings ────────────────────────────────────────────
            GUILayout.Label("AudioLink Settings", _sectionLabel);

            if (sibling != null)
            {
                EditorGUILayout.HelpBox(
                    "AudioLink reaction settings (enable, band, delay, band multiplier) "
                    + "are inherited from the sibling VRStageLighting_AudioLink_Static "
                    + "component. Edit those values there to keep both paths in sync.",
                    MessageType.Info);

                // Render via GetEffective*() accessors as disabled widgets to bypass
                // the sibling's [Header] attributes that would otherwise duplicate
                // our section titles (enableAudioLink has [Header("Audio Link Settings")]).
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.Toggle("Enable AudioLink (inherited)", rt.GetEffectiveEnableAudioLink());
                    EditorGUILayout.EnumPopup("Band (inherited)",          rt.GetEffectiveBand());
                    EditorGUILayout.IntSlider("Delay (inherited)",         rt.GetEffectiveDelay(), 0, 127);
                    EditorGUILayout.Slider("Band Multiplier (inherited)",  rt.GetEffectiveBandMultiplier(), 1f, 15f);
                }
            }
            else
            {
                EditorGUILayout.PropertyField(_enableAudioLink);
                EditorGUILayout.PropertyField(_band);
                EditorGUILayout.PropertyField(_delay);
                EditorGUILayout.PropertyField(_bandMultiplier);
            }

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── General Settings ──────────────────────────────────────────────
            GUILayout.Label("General Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_maxIntensity);

            if (sibling != null)
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.Slider("Final Intensity (inherited)", rt.GetEffectiveFinalIntensity(), 0f, 1f);
            }
            else
            {
                EditorGUILayout.PropertyField(_finalIntensity);
            }

            EditorGUILayout.PropertyField(_colorMode);

            if (sibling != null)
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ColorField(
                        new GUIContent("Emission Color (inherited)"),
                        rt.GetEffectiveEmissionColor(),
                        showEyedropper: true,
                        showAlpha: false,
                        hdr: true);
            }
            else
            {
                EditorGUILayout.PropertyField(_emissionColor);
            }

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

            if (sibling != null)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.IntSlider("Gobo Index (inherited)",       rt.GetEffectiveGoboIndex(), 1, 8);
                    EditorGUILayout.Slider("Gobo Spin Speed (inherited)",     rt.GetEffectiveGoboSpinSpeed(), -10f, 10f);
                }
            }
            else
            {
                EditorGUILayout.PropertyField(_goboIndex);
                EditorGUILayout.PropertyField(_goboSpinSpeed);
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
