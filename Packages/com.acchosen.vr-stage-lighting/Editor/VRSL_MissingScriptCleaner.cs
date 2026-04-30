#if UNITY_EDITOR && !UDONSHARP
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRSL.EditorScripts
{
    /// <summary>
    /// Suppresses "missing script" noise on VRSL prefabs in projects that
    /// don't have the VRChat Worlds SDK installed.
    ///
    /// VRSL's legacy DMX/AudioLink fixture prefabs and the AudioLink controller
    /// UI prefab serialise UdonBehaviour / UdonSharp scripts alongside their
    /// (dual-compiling) VRSL components. In a VRChat-less project those
    /// references can't bind, so every prefab instance in every scene logs
    /// missing-script warnings. The prefabs are non-functional in that
    /// environment anyway, but the warnings clutter the console and fail
    /// Play Mode entry checks.
    ///
    /// This utility walks each scene as it loads and silently removes the
    /// missing-script slots from any GameObject in a VRSL subtree — once a
    /// VRSL-namespaced component is found anywhere in the tree, the entire
    /// descendant subtree is treated as VRSL territory. That covers prefabs
    /// like AudioLinkController-WithVRSLSmoothing where the root carries the
    /// VRSL component but child sliders/handles carry UdonSharp UI scripts
    /// that go missing without the VRChat SDK.
    ///
    /// It does NOT modify the prefab or scene assets on disk: scenes aren't
    /// marked dirty, so no save prompts appear and the canonical prefabs
    /// continue to ship the UdonBehaviour references for VRChat consumers.
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
                CleanRecursive(root, insideVRSLSubtree: false);
        }

        // Once a VRSL-namespaced component is found anywhere on the path from
        // a scene root down to this GameObject, every descendant from that
        // point on is treated as VRSL territory and gets its missing-script
        // slots scrubbed. This covers prefabs whose VRSL component sits on
        // the root while child GameObjects (UI sliders, handles, etc.) carry
        // UdonSharp scripts that go missing without the VRChat SDK.
        static void CleanRecursive(GameObject go, bool insideVRSLSubtree)
        {
            bool inSubtree = insideVRSLSubtree || HasVRSLComponent(go);

            if (inSubtree)
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);

            foreach (Transform child in go.transform)
                CleanRecursive(child.gameObject, inSubtree);
        }

        // Restricts cleanup to subtrees rooted under a recognisable VRSL
        // GameObject so unrelated missing scripts the user is investigating
        // elsewhere in the scene are left alone.
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
