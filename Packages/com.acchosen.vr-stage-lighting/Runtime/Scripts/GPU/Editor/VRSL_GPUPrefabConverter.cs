using UnityEditor;
using UnityEngine;

namespace VRSL.EditorScripts
{
    /// <summary>
    /// Editor utility that strips the legacy *_Static component and the
    /// MoverLightMesh-VolumetricPassMesh child from every GPU prefab variant
    /// shipped in the package.
    ///
    /// The GPU light path produces surface illumination via VRSLDeferredLighting
    /// and (when enabled) volumetric in-scattering via VRSLVolumetricLighting —
    /// neither needs the legacy mesh-cone shader or the Static component's
    /// per-frame MaterialPropertyBlock pushes. The Realtime light component
    /// already owns DMX addressing, pan/tilt, intensity, range, gobo, and
    /// strobe configuration; nothing on the GPU path reads through the Static
    /// sibling.
    ///
    /// Idempotent — already-stripped prefabs are skipped silently. Manager
    /// prefabs (*-LightManager*) and the original VRChat-target prefabs (those
    /// without -GPU in the name) are not touched.
    /// </summary>
    public static class VRSL_GPUPrefabConverter
    {
        const string MENU_PATH      = "VRSL/Strip Static + Volumetric Mesh from GPU Prefabs";
        const string CONE_MESH_NAME = "MoverLightMesh-VolumetricPassMesh";

        // Folders containing GPU prefab variants. Limiting the scan to these
        // folders keeps the utility from accidentally touching unrelated
        // prefabs that happen to live elsewhere in the package.
        static readonly string[] SearchFolders =
        {
            "Packages/com.acchosen.vr-stage-lighting/Runtime/Prefabs/DMX/Horizontal Mode/DMX-13CH-URP-Fixtures",
            "Packages/com.acchosen.vr-stage-lighting/Runtime/Prefabs/DMX/Horizontal Mode/5-Channel Statics/DMX-5CH-URP-Fixtures",
            "Packages/com.acchosen.vr-stage-lighting/Runtime/Prefabs/DMX/Vertical Mode/DMX-13CH-URP-Fixtures",
            "Packages/com.acchosen.vr-stage-lighting/Runtime/Prefabs/DMX/Vertical Mode/5-Channel Statics/DMX-5CH-URP-Fixtures",
            "Packages/com.acchosen.vr-stage-lighting/Runtime/Prefabs/DMX/Legacy Mode/DMX-13CH-URP-Fixtures",
            "Packages/com.acchosen.vr-stage-lighting/Runtime/Prefabs/AudioLink/AudioLink-URP-Fixtures",
        };

        [MenuItem(MENU_PATH)]
        public static void ConvertPrefabs()
        {
            int scanned      = 0;
            int converted    = 0;
            int alreadyClean = 0;
            int errors       = 0;

            try
            {
                AssetDatabase.StartAssetEditing();

                foreach (var folder in SearchFolders)
                {
                    if (!AssetDatabase.IsValidFolder(folder)) continue;

                    var guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
                    foreach (var guid in guids)
                    {
                        var path = AssetDatabase.GUIDToAssetPath(guid);
                        // Skip manager prefabs — they have no Static / cone to strip.
                        if (path.Contains("LightManager")) continue;
                        scanned++;

                        var contents = PrefabUtility.LoadPrefabContents(path);
                        if (contents == null)
                        {
                            errors++;
                            continue;
                        }

                        bool changed = false;
                        try
                        {
                            var dmxStatic = contents.GetComponent<VRStageLighting_DMX_Static>();
                            if (dmxStatic != null)
                            {
                                Object.DestroyImmediate(dmxStatic, allowDestroyingAssets: true);
                                changed = true;
                            }

                            var alStatic = contents.GetComponent<VRStageLighting_AudioLink_Static>();
                            if (alStatic != null)
                            {
                                Object.DestroyImmediate(alStatic, allowDestroyingAssets: true);
                                changed = true;
                            }

                            var cone = FindChildRecursive(contents.transform, CONE_MESH_NAME);
                            if (cone != null)
                            {
                                Object.DestroyImmediate(cone.gameObject, allowDestroyingAssets: true);
                                changed = true;
                            }

                            if (changed)
                            {
                                PrefabUtility.SaveAsPrefabAsset(contents, path);
                                converted++;
                            }
                            else
                            {
                                alreadyClean++;
                            }
                        }
                        finally
                        {
                            PrefabUtility.UnloadPrefabContents(contents);
                        }
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            string msg =
                $"Scanned:       {scanned}\n" +
                $"Converted:     {converted}\n" +
                $"Already clean: {alreadyClean}";
            if (errors > 0) msg += $"\nErrors:        {errors}";

            EditorUtility.DisplayDialog("VRSL GPU Prefab Conversion", msg, "OK");
        }

        static Transform FindChildRecursive(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
                var found = FindChildRecursive(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
