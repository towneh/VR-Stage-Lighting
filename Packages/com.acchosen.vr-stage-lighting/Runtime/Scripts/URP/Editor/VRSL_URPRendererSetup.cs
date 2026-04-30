using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VRSL.EditorScripts
{
    /// <summary>
    /// Editor utilities that wire up the VRSL DMX URP realtime light path.
    /// Two independent menu actions so authors can configure the project
    /// without modifying the active scene, and vice versa:
    ///
    ///   • <b>Configure URP Renderer for VRSL Realtime Lights (DMX)</b> —
    ///     project-level only. Sets the active URP renderer to Forward+,
    ///     disables Depth Priming, enables the URP asset's Depth Texture, and
    ///     appends VRSLRealtimeLightFeature as a sub-asset.
    ///
    ///   • <b>Add VRSL URP Light Manager to Active Scene</b> — scene-level
    ///     only. Creates a VRSL_URPLightManager in the active scene if missing
    ///     and auto-assigns its compute, lighting, and volumetric shader
    ///     references from the package.
    ///
    /// Both actions are idempotent — safe to re-run. Each reports what
    /// changed via a single dialog.
    /// </summary>
    public static class VRSL_URPRendererSetup
    {
        const string MENU_PROJECT = "VRSL/Configure URP Renderer for VRSL Realtime Lights (DMX)";
        const string MENU_SCENE   = "VRSL/Add VRSL URP Light Manager to Active Scene";

        const string FEATURE_NAME           = "VRSL Realtime Light Feature";
        const string LIGHTING_SHADER_NAME   = "Hidden/VRSL/DeferredLighting";
        const string VOLUMETRIC_SHADER_NAME = "Hidden/VRSL/VolumetricLighting";
        const string COMPUTE_SHADER_FILTER  = "VRSLDMXLightUpdate t:ComputeShader";

        // ── Project-level: URP renderer + URP asset configuration ─────────────

        [MenuItem(MENU_PROJECT)]
        public static void ConfigureRenderer()
        {
            var report = new List<string>();

            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp == null)
            {
                EditorUtility.DisplayDialog("VRSL URP Setup",
                    "The active render pipeline is not URP. Assign a Universal Render " +
                    "Pipeline asset in Project Settings → Graphics first.", "OK");
                return;
            }

            var rendererData = ResolveRendererData(urp);
            if (rendererData == null) return;

            // Renderer asset: rendering path + depth priming
            using (var so = new SerializedObject(rendererData))
            {
                var renderingMode = so.FindProperty("m_RenderingMode");
                if (renderingMode != null
                    && renderingMode.enumValueIndex != (int)RenderingMode.ForwardPlus)
                {
                    renderingMode.enumValueIndex = (int)RenderingMode.ForwardPlus;
                    report.Add("Rendering Path → Forward+");
                }

                var depthPriming = so.FindProperty("m_DepthPrimingMode");
                if (depthPriming != null
                    && depthPriming.enumValueIndex != (int)DepthPrimingMode.Disabled)
                {
                    depthPriming.enumValueIndex = (int)DepthPrimingMode.Disabled;
                    report.Add("Depth Priming Mode → Disabled");
                }

                so.ApplyModifiedProperties();
            }

            // Renderer feature
            if (!HasFeature<VRSLRealtimeLightFeature>(rendererData))
            {
                AddFeature<VRSLRealtimeLightFeature>(rendererData, FEATURE_NAME);
                report.Add($"Added '{FEATURE_NAME}' to renderer.");
            }

            // URP asset: depth texture
            using (var so = new SerializedObject(urp))
            {
                var requireDepth = so.FindProperty("m_RequireDepthTexture");
                if (requireDepth != null && !requireDepth.boolValue)
                {
                    requireDepth.boolValue = true;
                    so.ApplyModifiedProperties();
                    report.Add("URP asset Depth Texture → enabled");
                }
            }

            EditorUtility.SetDirty(rendererData);
            EditorUtility.SetDirty(urp);
            AssetDatabase.SaveAssets();

            string msg = report.Count == 0
                ? "Renderer already configured — nothing to change."
                : "Renderer configured.\n\n• " + string.Join("\n• ", report) +
                  "\n\nNext: run '" + MENU_SCENE + "' to drop a manager into a scene.";
            EditorUtility.DisplayDialog("VRSL URP Setup", msg, "OK");
        }

        // ── Scene-level: URP light manager GameObject ─────────────────────────

        [MenuItem(MENU_SCENE)]
        public static void AddManagerToScene()
        {
            var report = new List<string>();

            var manager = Object.FindFirstObjectByType<VRSL_URPLightManager>();
            if (manager == null)
            {
                var go = new GameObject("VRSL URP Light Manager");
                Undo.RegisterCreatedObjectUndo(go, "Create VRSL URP Light Manager");
                manager = Undo.AddComponent<VRSL_URPLightManager>(go);
                EditorSceneManager.MarkSceneDirty(go.scene);
                report.Add("Created 'VRSL URP Light Manager' in active scene.");
            }
            else
            {
                report.Add("Manager already in scene; refreshing shader references.");
            }

            bool assignedAny = false;

            if (manager.computeShader == null)
            {
                var compute = FindAsset<ComputeShader>(COMPUTE_SHADER_FILTER);
                if (compute != null)
                {
                    Undo.RecordObject(manager, "Assign VRSL Compute Shader");
                    manager.computeShader = compute;
                    assignedAny = true;
                    report.Add("Assigned compute shader.");
                }
            }

            if (manager.lightingShader == null)
            {
                var sh = Shader.Find(LIGHTING_SHADER_NAME);
                if (sh != null)
                {
                    Undo.RecordObject(manager, "Assign VRSL Lighting Shader");
                    manager.lightingShader = sh;
                    assignedAny = true;
                    report.Add("Assigned lighting shader.");
                }
            }

            if (manager.volumetricShader == null)
            {
                var sh = Shader.Find(VOLUMETRIC_SHADER_NAME);
                if (sh != null)
                {
                    Undo.RecordObject(manager, "Assign VRSL Volumetric Shader");
                    manager.volumetricShader = sh;
                    assignedAny = true;
                    report.Add("Assigned volumetric shader.");
                }
            }

            if (assignedAny) EditorUtility.SetDirty(manager);

            EditorUtility.DisplayDialog("VRSL URP Setup",
                "Done.\n\n• " + string.Join("\n• ", report) +
                "\n\nReminder: assign the four DMX RenderTextures on the manager " +
                "(scene-specific — not auto-discoverable).", "OK");

            Selection.activeObject = manager.gameObject;
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        static UniversalRendererData ResolveRendererData(UniversalRenderPipelineAsset urp)
        {
            using var so = new SerializedObject(urp);
            var list = so.FindProperty("m_RendererDataList");
            if (list == null || list.arraySize == 0)
            {
                EditorUtility.DisplayDialog("VRSL URP Setup",
                    "The active URP asset has no renderer data assigned.", "OK");
                return null;
            }

            var candidates = new List<UniversalRendererData>();
            for (int i = 0; i < list.arraySize; i++)
            {
                var data = list.GetArrayElementAtIndex(i).objectReferenceValue
                           as UniversalRendererData;
                if (data != null) candidates.Add(data);
            }

            if (candidates.Count == 0)
            {
                EditorUtility.DisplayDialog("VRSL URP Setup",
                    "The URP asset has no UniversalRendererData (only 2D or custom " +
                    "renderers). VRSL requires the Universal Renderer.", "OK");
                return null;
            }

            if (candidates.Count > 1)
            {
                Debug.LogWarning(
                    "[VRSL] Multiple Universal renderers on the active URP asset; " +
                    "applying setup to the first one: " +
                    AssetDatabase.GetAssetPath(candidates[0]));
            }

            return candidates[0];
        }

        static bool HasFeature<T>(ScriptableRendererData rendererData)
            where T : ScriptableRendererFeature
        {
            foreach (var f in rendererData.rendererFeatures)
                if (f is T) return true;
            return false;
        }

        static void AddFeature<T>(ScriptableRendererData rendererData, string name)
            where T : ScriptableRendererFeature
        {
            var feature = ScriptableObject.CreateInstance<T>();
            feature.name = name;

            // Persist the feature as a sub-asset of the renderer data and
            // register it in the rendererFeatures list. The internal
            // ValidateRendererFeatures call refreshes URP's GUID map so the
            // feature shows up in the inspector immediately.
            rendererData.rendererFeatures.Add(feature);
            AssetDatabase.AddObjectToAsset(feature, rendererData);

            var validate = typeof(ScriptableRendererData).GetMethod(
                "ValidateRendererFeatures",
                BindingFlags.NonPublic | BindingFlags.Instance);
            validate?.Invoke(rendererData, null);

            EditorUtility.SetDirty(rendererData);
            EditorUtility.SetDirty(feature);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(rendererData));
        }

        static T FindAsset<T>(string filter) where T : Object
        {
            var guids = AssetDatabase.FindAssets(filter);
            foreach (var guid in guids)
            {
                var path  = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset != null) return asset;
            }
            return null;
        }
    }
}
