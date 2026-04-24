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

        // Color
        SerializedProperty _colorMode;
        SerializedProperty _emissionColor;

        // Light Settings
        SerializedProperty _maxIntensity;
        SerializedProperty _spotAngle;
        SerializedProperty _range;
        SerializedProperty _isPointLight;
        SerializedProperty _goboIndex;
        SerializedProperty _goboSpinSpeed;
        SerializedProperty _finalIntensity;

        // Pan / Tilt
        SerializedProperty _enablePanTilt;
        SerializedProperty _panTransform;
        SerializedProperty _tiltTransform;

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

            _colorMode       = serializedObject.FindProperty("colorMode");
            _emissionColor   = serializedObject.FindProperty("emissionColor");

            _maxIntensity    = serializedObject.FindProperty("maxIntensity");
            _spotAngle       = serializedObject.FindProperty("spotAngle");
            _range           = serializedObject.FindProperty("range");
            _isPointLight    = serializedObject.FindProperty("isPointLight");
            _goboIndex       = serializedObject.FindProperty("goboIndex");
            _goboSpinSpeed   = serializedObject.FindProperty("goboSpinSpeed");
            _finalIntensity  = serializedObject.FindProperty("finalIntensity");

            _enablePanTilt   = serializedObject.FindProperty("enablePanTilt");
            _panTransform    = serializedObject.FindProperty("panTransform");
            _tiltTransform   = serializedObject.FindProperty("tiltTransform");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var rt = (VRStageLighting_AudioLink_RealtimeLight)target;
            var sibling = rt.GetComponent<VRStageLighting_AudioLink_Static>();
            SerializedObject siblingSO = sibling != null ? new SerializedObject(sibling) : null;
            if (siblingSO != null) siblingSO.Update();

            // ── AudioLink Settings ────────────────────────────────────────────
            GUILayout.Label("AudioLink Settings", _sectionLabel);

            if (sibling != null)
            {
                EditorGUILayout.HelpBox(
                    "AudioLink reaction settings (enable, band, delay, band multiplier) "
                    + "are inherited from the sibling VRStageLighting_AudioLink_Static "
                    + "component. Edit those values there to keep both paths in sync.",
                    MessageType.Info);

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(
                        siblingSO.FindProperty("enableAudioLink"),
                        new GUIContent("Enable AudioLink (inherited)"));
                    EditorGUILayout.PropertyField(
                        siblingSO.FindProperty("band"),
                        new GUIContent("Band (inherited)"));
                    EditorGUILayout.PropertyField(
                        siblingSO.FindProperty("delay"),
                        new GUIContent("Delay (inherited)"));
                    EditorGUILayout.PropertyField(
                        siblingSO.FindProperty("bandMultiplier"),
                        new GUIContent("Band Multiplier (inherited)"));
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

            // ── Color ─────────────────────────────────────────────────────────
            GUILayout.Label("Color", _sectionLabel);
            EditorGUILayout.PropertyField(_colorMode);

            if (sibling != null)
            {
                EditorGUILayout.HelpBox(
                    "Emission colour is inherited from the sibling AudioLink_Static's "
                    + "lightColorTint when Color Mode is Emission. Theme/ColorChord modes "
                    + "sample AudioLink directly and don't use this value.",
                    MessageType.Info);
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(
                        siblingSO.FindProperty("lightColorTint"),
                        new GUIContent("Emission Color (inherited)"));
                }
            }
            else
            {
                EditorGUILayout.PropertyField(_emissionColor);
            }

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── Light Settings ────────────────────────────────────────────────
            GUILayout.Label("Light Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_maxIntensity);
            EditorGUILayout.PropertyField(_spotAngle);
            EditorGUILayout.PropertyField(_range);
            EditorGUILayout.PropertyField(_isPointLight);

            if (sibling != null)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(
                        siblingSO.FindProperty("selectGOBO"),
                        new GUIContent("Gobo Index (inherited)"));
                    EditorGUILayout.PropertyField(
                        siblingSO.FindProperty("spinSpeed"),
                        new GUIContent("Gobo Spin Speed (inherited)"));
                    EditorGUILayout.PropertyField(
                        siblingSO.FindProperty("finalIntensity"),
                        new GUIContent("Final Intensity (inherited)"));
                }
            }
            else
            {
                EditorGUILayout.PropertyField(_goboIndex);
                EditorGUILayout.PropertyField(_goboSpinSpeed);
                EditorGUILayout.PropertyField(_finalIntensity);
            }

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── Pan / Tilt (Moving Head) ──────────────────────────────────────
            GUILayout.Label("Pan / Tilt (Moving Head)", _sectionLabel);
            EditorGUILayout.PropertyField(_enablePanTilt);
            EditorGUILayout.PropertyField(_panTransform);
            EditorGUILayout.PropertyField(_tiltTransform);

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
