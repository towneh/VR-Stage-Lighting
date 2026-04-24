using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRSL;

namespace VRSL.EditorScripts
{
    /// <summary>
    /// One-shot Editor utility that wires up the AudioLink GPU realtime light
    /// components on every AudioLink mover spotlight in the active scene.
    ///
    /// Identifies fixtures by the presence of the child chain:
    ///   root → MoverLightMesh-LampFixture-Base → MoverLightMesh-LampFixture-Head
    ///
    /// For each fixture found the utility:
    ///   • Adds VRStageLighting_AudioLink_RealtimeLight.
    ///   • Sets enablePanTilt = true, panTransform = Base, tiltTransform = Head.
    ///
    /// All operations are registered with Undo so they can be reverted in one step.
    /// </summary>
    public static class VRSL_AudioLinkGPUSetup
    {
        const string BASE_NAME = "MoverLightMesh-LampFixture-Base";
        const string HEAD_NAME = "MoverLightMesh-LampFixture-Head";

        [MenuItem("VRSL/Setup AudioLink GPU Realtime Lights in Scene")]
        static void SetupAudioLinkGPULights()
        {
            var roots = FindMoverRoots(out var headMap, out var baseMap);

            if (roots.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "VRSL GPU Setup",
                    $"No AudioLink mover spotlights found in the active scene.\n\n" +
                    $"Fixtures are identified by the child chain:\n" +
                    $"  root → {BASE_NAME} → {HEAD_NAME}",
                    "OK");
                return;
            }

            int added = 0;
            int skipped = 0;

            Undo.SetCurrentGroupName("VRSL: Setup AudioLink GPU Realtime Lights");
            int undoGroup = Undo.GetCurrentGroup();

            foreach (var root in roots)
            {
                Transform baseXform = baseMap[root];
                Transform headXform = headMap[root];

                // Skip if already configured.
                if (root.GetComponent<VRStageLighting_AudioLink_RealtimeLight>() != null)
                {
                    skipped++;
                    continue;
                }

                // Add per-fixture realtime light component.
                var rtLight = Undo.AddComponent<VRStageLighting_AudioLink_RealtimeLight>(root.gameObject);
                Undo.RecordObject(rtLight, "Configure VRSL AudioLink Realtime Light");
                rtLight.spotAngle     = 45f;
                rtLight.range         = 20f;
                rtLight.enablePanTilt = true;
                rtLight.panTransform  = baseXform;
                rtLight.tiltTransform = headXform;

                EditorUtility.SetDirty(root.gameObject);
                added++;
            }

            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkAllScenesDirty();

            string msg = $"Done.\n\n" +
                         $"Configured: {added}\n" +
                         $"Skipped (already set up): {skipped}";

            if (added > 0)
                msg += "\n\nAll operations can be undone with Ctrl+Z.";

            EditorUtility.DisplayDialog("VRSL GPU Setup", msg, "OK");
        }

        static List<Transform> FindMoverRoots(
            out Dictionary<Transform, Transform> headMap,
            out Dictionary<Transform, Transform> baseMap)
        {
            var roots   = new List<Transform>();
            headMap = new Dictionary<Transform, Transform>();
            baseMap = new Dictionary<Transform, Transform>();

            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                if (t.name != HEAD_NAME) continue;

                Transform baseXform = t.parent;
                if (baseXform == null || baseXform.name != BASE_NAME) continue;

                Transform root = baseXform.parent;
                if (root == null) continue;

                if (!roots.Contains(root))
                {
                    roots.Add(root);
                    headMap[root] = t;
                    baseMap[root] = baseXform;
                }
            }

            return roots;
        }
    }
}
