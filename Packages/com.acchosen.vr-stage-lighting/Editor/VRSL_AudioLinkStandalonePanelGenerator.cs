#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.UI;

namespace VRSL.EditorScripts
{
    /// <summary>
    /// Generates a UdonSharp-free prefab for the VRSL AudioLink smoothing
    /// controls so the smoothing panel can ship into projects that don't have
    /// the VRChat Worlds SDK installed (Basis, plain Unity, etc.). Builds the
    /// UI hierarchy programmatically using Unity's DefaultControls factory so
    /// the prefab YAML is authored by the editor rather than hand-rolled.
    ///
    /// The prefab carries only the smoothing controls — five band sliders, a
    /// reset button, and the existing VRSL_AudioLink_SmoothingPanel script
    /// (which already dual-compiles to a plain MonoBehaviour). It does NOT
    /// re-implement AudioLink's main controller UI; users supply that
    /// separately from whatever AudioLink build their project uses.
    ///
    /// The menu is intentionally one-shot: re-running it overwrites the
    /// prefab (with a confirmation prompt). Layout iteration is therefore a
    /// matter of editing this script and regenerating, rather than touching
    /// the prefab YAML by hand.
    /// </summary>
    public static class VRSL_AudioLinkStandalonePanelGenerator
    {
        const string MENU =
            "VRSL/Generate AudioLink Smoothing Panel (Standalone) Prefab";

        const string PREFAB_DIR =
            "Packages/com.acchosen.vr-stage-lighting/Runtime/Prefabs/AudioLink/" +
            "VRSL-AudioLinkSmoothingPanel-Standalone";

        const string PREFAB_PATH =
            PREFAB_DIR + "/VRSL-AudioLinkSmoothingPanel-Standalone.prefab";

        // Layout
        const float PanelWidth     = 400f;
        const float PanelHeight    = 460f;
        const float TitleHeight    =  50f;
        const float RowHeight      =  46f;
        const float RowSpacing     =  10f;
        const float ButtonHeight   =  44f;
        const float SidePadding    =  20f;
        const float TopPadding     =  16f;
        const float BottomPadding  =  16f;

        static readonly string[] BandNames =
        {
            "Bass",
            "Lower Mid",
            "Upper Mid",
            "Treble",
            "Color Chord",
        };

        [MenuItem(MENU)]
        public static void Generate()
        {
            if (File.Exists(PREFAB_PATH) && !EditorUtility.DisplayDialog(
                    "Regenerate Smoothing Panel Prefab?",
                    "The standalone smoothing panel prefab already exists.\n\n" +
                    PREFAB_PATH + "\n\nRegenerating overwrites it. Continue?",
                    "Regenerate", "Cancel"))
            {
                return;
            }

            if (!Directory.Exists(PREFAB_DIR))
                Directory.CreateDirectory(PREFAB_DIR);

            var resources = new DefaultControls.Resources();

            // Root (Canvas, scaler, raycaster). World-space scale is small so
            // the panel reads as a wall-sized control surface in scene units.
            var root = new GameObject("VRSL-AudioLinkSmoothingPanel-Standalone",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rootRT = root.GetComponent<RectTransform>();
            rootRT.sizeDelta  = new Vector2(PanelWidth, PanelHeight);
            rootRT.localScale = new Vector3(0.001f, 0.001f, 0.001f);

            // Background panel
            var bg = CreateChild(root.transform, "Background", typeof(Image));
            var bgImg = bg.GetComponent<Image>();
            bgImg.color = new Color(0.07f, 0.07f, 0.08f, 0.92f);
            FillParent(bg.GetComponent<RectTransform>());

            // Title
            var title = CreateText(root.transform, "Title",
                "VRSL AUDIOLINK SMOOTHING", 20, FontStyle.Bold,
                TextAnchor.MiddleCenter);
            AnchorTopStretch(title.GetComponent<RectTransform>(),
                yOffset: -TopPadding, height: TitleHeight,
                xPad: SidePadding);

            // Five rows
            var sliders    = new Slider[5];
            var valueTexts = new Text[5];
            float rowY = -(TopPadding + TitleHeight + RowSpacing);
            for (int i = 0; i < 5; i++)
            {
                BuildBandRow(root.transform, BandNames[i], rowY, resources,
                    out sliders[i], out valueTexts[i]);
                rowY -= (RowHeight + RowSpacing);
            }

            // Reset button
            var btnGO = DefaultControls.CreateButton(resources);
            btnGO.name = "Reset Button";
            btnGO.transform.SetParent(root.transform, false);
            var btnText = btnGO.GetComponentInChildren<Text>();
            btnText.text     = "RESET";
            btnText.fontSize = 16;
            var btnRT = btnGO.GetComponent<RectTransform>();
            AnchorBottomCentre(btnRT, yOffset: BottomPadding,
                width: PanelWidth - 2 * SidePadding, height: ButtonHeight);

            // Panel script — already dual-compiles to MonoBehaviour when
            // UDONSHARP isn't defined, so it works in standalone projects.
            var panel = root.AddComponent<VRSL_AudioLink_SmoothingPanel>();
            panel.bassSmoothingSlider     = sliders[0];
            panel.lowerMidSmoothingSlider = sliders[1];
            panel.upperMidSmoothingSlider = sliders[2];
            panel.trebleSmoothingSlider   = sliders[3];
            panel.colorChordSmoothingSlider = sliders[4];
            panel.bassSmoothingText       = valueTexts[0];
            panel.lowerMidSmoothingText   = valueTexts[1];
            panel.upperMidSmoothingText   = valueTexts[2];
            panel.trebleSmoothingText     = valueTexts[3];
            panel.colorChordSmoothingText = valueTexts[4];

            // Wire slider events as persistent listeners so they survive prefab
            // serialization. The float arg is discarded — UpdateSettings reads
            // every slider value directly.
            for (int i = 0; i < 5; i++)
            {
                UnityEventTools.AddVoidPersistentListener(
                    sliders[i].onValueChanged, panel.UpdateSettings);
            }
            UnityEventTools.AddVoidPersistentListener(
                btnGO.GetComponent<Button>().onClick, panel.ResetSettings);

            // Save and clean up
            PrefabUtility.SaveAsPrefabAsset(root, PREFAB_PATH);
            Object.DestroyImmediate(root);
            AssetDatabase.Refresh();

            var savedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PREFAB_PATH);
            Selection.activeObject = savedPrefab;
            EditorGUIUtility.PingObject(savedPrefab);

            EditorUtility.DisplayDialog("VRSL",
                "Standalone smoothing panel prefab generated.\n\n" + PREFAB_PATH +
                "\n\nNext: assign these references on the prefab " +
                "(scene-specific, can't be auto-discovered):\n" +
                "  • Smoothing Material — your AudioLink smoothing CRT material\n" +
                "  • Smoothing Texture — your AudioLink smoothing CRT\n" +
                "  • AudioLink Camera — your AudioLink camera",
                "OK");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        static void BuildBandRow(Transform parent, string bandName, float rowY,
            DefaultControls.Resources resources,
            out Slider slider, out Text valueText)
        {
            var row = CreateChild(parent, bandName + " Row");
            var rowRT = row.GetComponent<RectTransform>();
            AnchorTopStretch(rowRT, yOffset: rowY, height: RowHeight,
                xPad: SidePadding);

            // Label (left third)
            var label = CreateText(row.transform, "Label", bandName, 14,
                FontStyle.Normal, TextAnchor.MiddleLeft);
            var labelRT = label.GetComponent<RectTransform>();
            labelRT.anchorMin = new Vector2(0f,  0f);
            labelRT.anchorMax = new Vector2(0.3f, 1f);
            labelRT.offsetMin = Vector2.zero;
            labelRT.offsetMax = Vector2.zero;

            // Slider (middle ~55%)
            var sliderGO = DefaultControls.CreateSlider(resources);
            sliderGO.name = "Slider";
            sliderGO.transform.SetParent(row.transform, false);
            var sliderRT = sliderGO.GetComponent<RectTransform>();
            sliderRT.anchorMin = new Vector2(0.3f, 0f);
            sliderRT.anchorMax = new Vector2(0.85f, 1f);
            sliderRT.offsetMin = new Vector2(0f, 14f);   // vertical centring
            sliderRT.offsetMax = new Vector2(0f, -14f);
            slider = sliderGO.GetComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value    = 0.7f;

            // Value text (right ~15%)
            valueText = CreateText(row.transform, "Value", "30", 14,
                FontStyle.Normal, TextAnchor.MiddleCenter);
            var valRT = valueText.GetComponent<RectTransform>();
            valRT.anchorMin = new Vector2(0.85f, 0f);
            valRT.anchorMax = new Vector2(1f, 1f);
            valRT.offsetMin = Vector2.zero;
            valRT.offsetMax = Vector2.zero;
        }

        static GameObject CreateChild(Transform parent, string name,
            params System.Type[] components)
        {
            var types = new System.Type[components.Length + 1];
            types[0] = typeof(RectTransform);
            for (int i = 0; i < components.Length; i++)
                types[i + 1] = components[i];
            var go = new GameObject(name, types);
            go.transform.SetParent(parent, false);
            return go;
        }

        static Text CreateText(Transform parent, string name, string content,
            int fontSize, FontStyle style, TextAnchor alignment)
        {
            var go = CreateChild(parent, name, typeof(Text));
            var t  = go.GetComponent<Text>();
            t.text      = content;
            t.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize  = fontSize;
            t.fontStyle = style;
            t.alignment = alignment;
            t.color     = Color.white;
            return t;
        }

        static void FillParent(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        // Anchored at top, stretching horizontally with side padding.
        static void AnchorTopStretch(RectTransform rt, float yOffset,
            float height, float xPad)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(xPad, -(yOffset + (-height)));
            rt.offsetMax = new Vector2(-xPad, yOffset);
        }

        // Anchored at bottom-centre, fixed width and height.
        static void AnchorBottomCentre(RectTransform rt, float yOffset,
            float width, float height)
        {
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot     = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(0f, yOffset);
        }
    }
}
#endif
