#if UNITY_EDITOR && !UDONSHARP
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRSL.EditorScripts
{
    /// <summary>
    /// Suppresses "missing script" noise on VRSL legacy mesh-shader fixtures
    /// in projects that don't have the VRChat Worlds SDK installed.
    ///
    /// VRSL's legacy DMX/AudioLink fixture prefabs serialise a UdonBehaviour
    /// alongside the (dual-compiling) VRSL script. In a VRChat-less project
    /// the UdonBehaviour reference can't bind, so every fixture instance in
    /// every scene logs a missing-script warning. The fixture is non-functional
    /// in that environment anyway — DMX is decoded from a networked video
    /// stream that VRChat owns — but the warnings clutter the console and
    /// fail Play Mode entry checks.
    ///
    /// This utility walks each scene as it loads and silently removes the
    /// missing-script slots from GameObjects that carry a VRSL-namespaced
    /// component. It does NOT modify the prefab or scene assets on disk:
    /// scenes aren't marked dirty, so no save prompts appear and the canonical
    /// prefabs continue to ship the UdonBehaviour for VRChat consumers.
    /// Cleanup runs again on every scene open.
    ///
    /// The whole class is compile-gated to !UDONSHARP so it disappears
    /// entirely once UdonSharp / the VRChat Worlds SDK is installed.
    /// </summary>
    [InitializeOnLoad]
    internal static class VRSL_MissingScriptCleaner
    {
        static VRSL_MissingScriptCleaner()
        {
            EditorSceneManager.sceneOpened += OnSceneOpened;
            // delayCall covers the scene that was already loaded at editor
            // startup or domain reload — sceneOpened doesn't fire for those.
            EditorApplication.delayCall += CleanAllOpenScenes;
        }

        static void OnSceneOpened(Scene scene, OpenSceneMode mode) => CleanScene(scene);

        static void CleanAllOpenScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                CleanScene(SceneManager.GetSceneAt(i));
        }

        static void CleanScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            foreach (var root in scene.GetRootGameObjects())
                CleanRecursive(root);
        }

        static void CleanRecursive(GameObject go)
        {
            if (HasVRSLComponent(go))
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);

            foreach (Transform child in go.transform)
                CleanRecursive(child.gameObject);
        }

        // Restricts cleanup to GameObjects that are recognisably VRSL fixtures
        // so unrelated missing scripts the user is investigating are left alone.
        static bool HasVRSLComponent(GameObject go)
        {
            var comps = go.GetComponents<MonoBehaviour>();
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;            // missing script
                var ns = c.GetType().Namespace;
                if (ns != null && (ns == "VRSL" || ns.StartsWith("VRSL.")))
                    return true;
            }
            return false;
        }
    }
}
#endif
