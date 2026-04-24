using UnityEditor;
using UnityEngine;

namespace VRSL
{
    [CustomEditor(typeof(VRStageLighting_DMX_RealtimeLight))]
    [CanEditMultipleObjects]
    public class VRStageLighting_DMX_RealtimeLightEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            bool inheritsAddressing =
                ((VRStageLighting_DMX_RealtimeLight)target)
                    .GetComponent<VRStageLighting_DMX_Static>() != null;

            SerializedProperty useLegacy = serializedObject.FindProperty("useLegacySectorMode");

            SerializedProperty prop = serializedObject.GetIterator();
            prop.NextVisible(true); // skip m_Script

            while (prop.NextVisible(false))
            {
                switch (prop.name)
                {
                    case "useLegacySectorMode":
                        if (inheritsAddressing)
                        {
                            EditorGUILayout.HelpBox(
                                "DMX addressing inherited from the sibling "
                                + "VRStageLighting_DMX_Static component. Edit addressing "
                                + "there (the Static component) to keep both paths in sync.",
                                MessageType.Info);
                        }
                        else
                        {
                            EditorGUILayout.PropertyField(prop);
                        }
                        break;

                    case "sector":
                        if (!inheritsAddressing && useLegacy.boolValue)
                            EditorGUILayout.PropertyField(prop);
                        break;

                    case "dmxChannel":
                    case "dmxUniverse":
                        if (!inheritsAddressing && !useLegacy.boolValue)
                            EditorGUILayout.PropertyField(prop);
                        break;

                    default:
                        EditorGUILayout.PropertyField(prop, true);
                        break;
                }
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
