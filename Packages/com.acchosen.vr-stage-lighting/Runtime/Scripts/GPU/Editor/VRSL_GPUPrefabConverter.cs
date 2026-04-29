using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VRSL.EditorScripts
{
    /// <summary>
    /// Editor utility that converts every GPU prefab variant shipped in the
    /// package to the standalone (Static-free) shape. For each prefab it:
    ///
    ///   • Strips the legacy *_Static component.
    ///   • Strips the MoverLightMesh-VolumetricPassMesh child.
    ///   • Wires the Realtime light component's fixtureShellRenderers list
    ///     to the fixture body's MeshRenderers — preferring the renderers
    ///     Static's objRenderers[] used to drive (filtered to exclude the
    ///     cone we're destroying), falling back to a name-based search for
    ///     "MoverLightMesh-LampFixture*" children when Static has already
    ///     been stripped on a prior run.
    ///
    /// The GPU light path produces surface illumination via VRSLDeferredLighting
    /// and (when assigned) volumetric in-scattering via VRSLVolumetricLighting —
    /// neither needs the legacy mesh-cone shader or the Static component's
    /// per-frame MaterialPropertyBlock pushes. The Realtime light owns the
    /// fixture shell drive instead via DriveFixtureShells().
    ///
    /// Idempotent — re-running on already-converted prefabs only fills in
    /// fixtureShellRenderers if it was empty. Manager prefabs (*-LightManager*)
    /// and the original VRChat-target prefabs (those without -GPU in the name)
    /// are not touched.
    /// </summary>
    public static class VRSL_GPUPrefabConverter
    {
        const string MENU_PATH        = "VRSL/Strip Static + Volumetric Mesh from GPU Prefabs";
        const string CONE_MESH_NAME   = "MoverLightMesh-VolumetricPassMesh";
        const string SHELL_NAME_PREFIX = "MoverLightMesh-LampFixture";

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
                            // Capture the body MeshRenderers BEFORE destroying anything —
                            // Static's objRenderers[] is the authoritative source when
                            // Static is present, and the cone's children must be excluded
                            // before the cone GameObject is destroyed (their references
                            // would become invalid afterward).
                            var dmxStatic = contents.GetComponent<VRStageLighting_DMX_Static>();
                            var alStatic  = contents.GetComponent<VRStageLighting_AudioLink_Static>();
                            var cone      = FindChildRecursive(contents.transform, CONE_MESH_NAME);

                            MeshRenderer[] capturedShellRenderers = ResolveShellRenderers(
                                contents,
                                dmxStatic != null ? dmxStatic.objRenderers : null,
                                alStatic  != null ? alStatic.objRenderers  : null,
                                cone);

                            if (dmxStatic != null)
                            {
                                Object.DestroyImmediate(dmxStatic, allowDestroyingAssets: true);
                                changed = true;
                            }

                            if (alStatic != null)
                            {
                                Object.DestroyImmediate(alStatic, allowDestroyingAssets: true);
                                changed = true;
                            }

                            if (cone != null)
                            {
                                Object.DestroyImmediate(cone.gameObject, allowDestroyingAssets: true);
                                changed = true;
                            }

                            // Wire the shell renderers onto whichever Realtime component
                            // is on this prefab. Only fill if the field is currently empty
                            // — preserves any manual override the prefab author may have
                            // already set.
                            var dmxRT = contents.GetComponent<VRStageLighting_DMX_RealtimeLight>();
                            if (dmxRT != null
                                && IsEmpty(dmxRT.fixtureShellRenderers)
                                && capturedShellRenderers.Length > 0)
                            {
                                dmxRT.fixtureShellRenderers = capturedShellRenderers;
                                changed = true;
                            }

                            var alRT = contents.GetComponent<VRStageLighting_AudioLink_RealtimeLight>();
                            if (alRT != null
                                && IsEmpty(alRT.fixtureShellRenderers)
                                && capturedShellRenderers.Length > 0)
                            {
                                alRT.fixtureShellRenderers = capturedShellRenderers;
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

        // Decide which MeshRenderers should drive the fixture body. Preference
        // order: (1) Static.objRenderers minus anything under the cone that's
        // about to be destroyed, (2) name-based search for any GameObject whose
        // name starts with "MoverLightMesh-LampFixture" (covers DMX's single
        // LampFixture mesh and AudioLink's LampFixture-Base/-Legs/-Head split).
        static MeshRenderer[] ResolveShellRenderers(
            GameObject root,
            MeshRenderer[] dmxStaticObjRenderers,
            MeshRenderer[] alStaticObjRenderers,
            Transform cone)
        {
            var coneRenderers = new HashSet<MeshRenderer>();
            if (cone != null)
            {
                foreach (var r in cone.GetComponentsInChildren<MeshRenderer>(includeInactive: true))
                    coneRenderers.Add(r);
            }

            var staticSource = dmxStaticObjRenderers ?? alStaticObjRenderers;
            if (staticSource != null && staticSource.Length > 0)
            {
                var keep = new List<MeshRenderer>();
                foreach (var r in staticSource)
                {
                    if (r == null) continue;
                    if (coneRenderers.Contains(r)) continue;
                    keep.Add(r);
                }
                if (keep.Count > 0) return keep.ToArray();
            }

            // Fallback: name-based lookup for prefabs that have already been
            // stripped on a prior run. Walks the whole hierarchy and picks
            // every MeshRenderer on a "MoverLightMesh-LampFixture*" GameObject.
            var byName = new List<MeshRenderer>();
            CollectShellByName(root.transform, coneRenderers, byName);
            return byName.ToArray();
        }

        static void CollectShellByName(Transform t, HashSet<MeshRenderer> exclude, List<MeshRenderer> dest)
        {
            if (t.name.StartsWith(SHELL_NAME_PREFIX))
            {
                var r = t.GetComponent<MeshRenderer>();
                if (r != null && !exclude.Contains(r)) dest.Add(r);
            }
            for (int i = 0; i < t.childCount; i++)
                CollectShellByName(t.GetChild(i), exclude, dest);
        }

        static bool IsEmpty(MeshRenderer[] arr)
        {
            if (arr == null || arr.Length == 0) return true;
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] != null) return false;
            return true;
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
