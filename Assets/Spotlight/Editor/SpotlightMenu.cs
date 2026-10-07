// 编辑器菜单入口：验证/编译配置、生成默认工作簿、生成字体与演示场景。
// 为什么集中在一处：所有入口共用同一套编译与校验流程，避免出现“某个菜单走了简化路径”。
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// Spotlight 的编辑器菜单。
    /// 输入：菜单点击；输出：控制台报告、生成资产或演示场景。
    /// </summary>
    public static class SpotlightMenu
    {
        /// <summary>演示场景路径。</summary>
        public const string DemoScenePath = "Assets/Spotlight/Scenes/Spotlight.unity";
        /// <summary>格子 Prefab 路径。</summary>
        public const string BoardCellPrefabPath = "Assets/Spotlight/UI/BoardCell.prefab";
        /// <summary>界面标记路径。</summary>
        public const string UxmlPath = "Assets/Spotlight/UI/Spotlight.uxml";

        /// <summary>验证配置（只校验不写入任何资产）。输入：无；输出：无（结果打印到 Console）。</summary>
        [MenuItem("Tools/Spotlight/验证配置", priority = 0)]
        public static void ValidateConfiguration()
        {
            var report = ConfigCompiler.Compile(null, false);
            Report(report, "验证通过：配置可用。");
        }

        /// <summary>编译全部配置并发布生成资产。输入：无；输出：无（结果打印到 Console）。</summary>
        [MenuItem("Tools/Spotlight/编译全部配置", priority = 1)]
        public static void CompileAll()
        {
            var report = ConfigCompiler.Compile();
            Report(report, $"编译完成，生成 {report.GeneratedAssetPaths.Count} 个资产。");
        }

        /// <summary>导入 TMP 基础资源（默认着色器与 TMP Settings）。输入：无；输出：无。</summary>
        [MenuItem("Tools/Spotlight/导入 TMP 基础资源", priority = 19)]
        public static void ImportTmpEssentials()
        {
            Debug.Log(CjkFontSetup.EnsureTmpEssentials() ? "TMP 基础资源已就绪。" : "TMP 基础资源未就绪，请查看错误信息。");
        }

        /// <summary>生成默认工作簿。输入：无；输出：无。已存在时需确认覆盖。</summary>
        [MenuItem("Tools/Spotlight/生成默认工作簿", priority = 20)]
        public static void GenerateDefaultWorkbook()
        {
            var full = Path.GetFullPath(ConfigCompiler.DefaultWorkbookPath);
            if (File.Exists(full) && !EditorUtility.DisplayDialog("覆盖确认",
                    $"已存在 {ConfigCompiler.DefaultWorkbookPath}，继续将覆盖其中的默认数值。是否继续？", "覆盖", "取消"))
                return;
            DefaultWorkbookWriter.WriteDefault(full);
            AssetDatabase.Refresh();
            Debug.Log("已生成默认工作簿：" + ConfigCompiler.DefaultWorkbookPath);
        }

        /// <summary>在资源管理器中打开默认工作簿。输入：无；输出：无。</summary>
        [MenuItem("Tools/Spotlight/打开默认工作簿", priority = 21)]
        public static void RevealDefaultWorkbook()
        {
            var full = Path.GetFullPath(ConfigCompiler.DefaultWorkbookPath);
            if (!File.Exists(full)) { Debug.LogWarning("默认工作簿不存在，请先生成。"); return; }
            EditorUtility.RevealInFinder(full);
        }

        /// <summary>重建卡牌外观模板（会覆盖现有模板，需确认）。输入：无；输出：无。</summary>
        [MenuItem("Tools/Spotlight/重建外观模板", priority = 22)]
        public static void RebuildTemplate()
        {
            if (!EditorUtility.DisplayDialog("覆盖确认",
                    "重建外观模板会覆盖 Assets/Spotlight/Runtime/View/CardTemplate.prefab 上的手工外观，是否继续？", "重建", "取消"))
                return;
            ViewTemplateBuilder.EnsureTemplate(true);
            Debug.Log("已重建外观模板。");
        }

        /// <summary>
        /// 重建字体资产。
        /// 输入：无；输出：无。
        /// 为什么需要：删除 .asset 但保留 .meta，可以强制重新生成图集子资产而不改变 GUID，
        /// 因此 Prefab 与场景上已有的字体引用不会断开。
        /// </summary>
        [MenuItem("Tools/Spotlight/重建字体资产", priority = 24)]
        public static void RebuildFontAssets()
        {
            if (!EditorUtility.DisplayDialog("重建确认",
                    "重建会重新生成 TMP 与 UI Toolkit 字体资产（保留 GUID，不会断开已有引用），是否继续？", "重建", "取消"))
                return;
            foreach (var path in new[] { CjkFontSetup.FontAssetPath, CjkFontSetup.TextCoreFontAssetPath })
            {
                if (File.Exists(path)) File.Delete(path);
            }
            AssetDatabase.Refresh();
            CjkFontSetup.EnsureFontAssets();
            Debug.Log("字体资产已重建。");
        }

        /// <summary>生成中文 TMP 字体资产与 UI Toolkit 文本设置。输入：无；输出：无。</summary>
        [MenuItem("Tools/Spotlight/生成中文字体资产", priority = 23)]
        public static void GenerateCjkFont()
        {
            var font = CjkFontSetup.EnsureFontAssets();
            Debug.Log(font != null ? "已生成中文字体资产。" : "中文字体资产生成失败，请查看上一条错误。");
        }

        /// <summary>
        /// 生成演示场景。
        /// 输入：无；输出：`Assets/Spotlight/Scenes/Spotlight.unity`（并写入 Build Settings 第一位）。
        /// 行为：确保字体、模板、格子 Prefab 与配置资产存在，然后搭出“引导器 + UXML + 棋盘 + 手牌”的可运行场景。
        /// </summary>
        [MenuItem("Tools/Spotlight/生成演示场景", priority = 40)]
        public static void GenerateDemoScene()
        {
            CjkFontSetup.EnsureFontAssets();
            var templatePrefab = ViewTemplateBuilder.EnsureTemplate();
            var cellPrefab = EnsureCellPrefab();

            var configReport = ConfigCompiler.Compile();
            Report(configReport, "配置编译完成。");
            if (!configReport.Success) return;

            var config = AssetDatabase.LoadAssetAtPath<SpotlightConfigAsset>(ConfigAssetPublisher.ConfigAssetPath);
            var panelSettings = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>(CjkFontSetup.PanelSettingsPath);
            var uxml = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>(UxmlPath);
            if (uxml == null) Debug.LogWarning($"界面文件缺失：{UxmlPath}，场景已生成但 HUD 不会显示。");
            if (panelSettings == null) Debug.LogWarning($"面板设置缺失：{CjkFontSetup.PanelSettingsPath}，UI Toolkit 可能无法显示中文。");

            var directory = Path.GetDirectoryName(DemoScenePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateCamera();
            CreateEventSystem();

            var root = new GameObject("Spotlight");
            var bootstrap = root.AddComponent<GameBootstrap>();
            var feedback = root.AddComponent<CardFeedback>();
            var hud = root.AddComponent<HudView>();
            var debugPanel = root.AddComponent<ConfigDebugPanel>();
            var document = root.AddComponent<UnityEngine.UIElements.UIDocument>();
            var documentSerialized = new SerializedObject(document);
            SetObject(documentSerialized, "m_PanelSettings", panelSettings);
            // UIDocument 在 2022.3 里的界面引用字段名是 sourceAsset（没有 m_ 前缀）；
            // 写错名字时 UXML 不会被加载，HUD 会完全空白。
            SetObject(documentSerialized, "sourceAsset", uxml);
            documentSerialized.ApplyModifiedPropertiesWithoutUndo();
            // 排序值用公开属性直接设置：HUD 必须画在 UGUI 棋盘之上，否则信息条与面板会被格子盖住。
            document.sortingOrder = 10;
            EditorUtility.SetDirty(document);

            var canvas = CreateCanvas();
            var board = CreateBoardView(canvas, cellPrefab, templatePrefab);
            var hand = CreateHandView(canvas, templatePrefab);

            var bootstrapSerialized = new SerializedObject(bootstrap);
            SetObject(bootstrapSerialized, "_configAsset", config);
            SetObject(bootstrapSerialized, "_boardView", board);
            SetObject(bootstrapSerialized, "_handView", hand);
            SetObject(bootstrapSerialized, "_hudView", hud);
            SetObject(bootstrapSerialized, "_feedback", feedback);
            SetObject(bootstrapSerialized, "_debugPanel", debugPanel);
            bootstrapSerialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, DemoScenePath);
            RegisterSceneInBuildSettings();
            AssetDatabase.SaveAssets();
            Debug.Log("已生成演示场景：" + DemoScenePath);
        }

        /// <summary>创建主摄像机。输入：无；输出：摄像机（正交、纯色背景，纯 UI 场景也可见）。</summary>
        static Camera CreateCamera()
        {
            var go = new GameObject("Main Camera", typeof(Camera));
            go.tag = "MainCamera";
            var camera = go.GetComponent<Camera>();
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.07f, 0.08f, 0.11f);
            return camera;
        }

        /// <summary>创建 EventSystem，保证 UGUI 按钮可以接收点击。输入：无；输出：无。</summary>
        static void CreateEventSystem()
        {
            if (Object.FindObjectOfType<EventSystem>() != null) return;
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        /// <summary>创建 UGUI 画布。输入：无；输出：画布（Screen Space Overlay + 1920×1080 缩放基准）。</summary>
        static Canvas CreateCanvas()
        {
            var go = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            return canvas;
        }

        /// <summary>创建棋盘格网容器。输入：棋盘根节点；输出：格网根节点（挂 GridLayoutGroup）。</summary>
        /// <remarks>必须作为棋盘根节点的子节点，否则 GridLayoutGroup 会按整个画布计算格子尺寸而溢出屏幕。</remarks>
        static RectTransform CreateCellsRoot(RectTransform boardRoot)
        {
            var go = new GameObject("Cells", typeof(RectTransform), typeof(GridLayoutGroup));
            var rect = (RectTransform)go.transform;
            rect.SetParent(boardRoot, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return rect;
        }

        /// <summary>
        /// 创建棋盘视图。
        /// 输入：画布、格子 Prefab 与卡牌模板；输出：BoardView（已写入全部私有引用）。
        /// 棋盘根节点占据屏幕中部，底部留给手牌与日志。
        /// </summary>
        static BoardView CreateBoardView(Canvas canvas, BoardCellView cellPrefab, GameObject templatePrefab)
        {
            var go = new GameObject("BoardRoot", typeof(RectTransform), typeof(BoardView));
            var rect = (RectTransform)go.transform;
            rect.SetParent(canvas.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(460f, 250f);
            rect.offsetMax = new Vector2(-400f, -70f);

            var cellsRoot = CreateCellsRoot(rect);
            var board = go.GetComponent<BoardView>();
            var serialized = new SerializedObject(board);
            SetObject(serialized, "_cellRoot", cellsRoot);
            SetObject(serialized, "_cellPrefab", cellPrefab);
            SetObject(serialized, "_cardFallbackPrefab", templatePrefab != null ? templatePrefab.GetComponent<Card>() : null);
            SetObject(serialized, "_homePrefab", templatePrefab != null ? templatePrefab.GetComponent<Card>() : null);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return board;
        }

        /// <summary>
        /// 创建手牌视图。
        /// 输入：画布与卡牌模板；输出：HandView（已写入容器与回退模板引用）。
        /// </summary>
        static HandView CreateHandView(Canvas canvas, GameObject templatePrefab)
        {
            var go = new GameObject("HandRoot", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(HandView));
            var rect = (RectTransform)go.transform;
            rect.SetParent(canvas.transform, false);
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 16f);
            rect.sizeDelta = new Vector2(-900f, 190f);

            var layout = go.GetComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 10f;
            // 只负责摆位，不强行改写手牌尺寸：卡牌外观模板的尺寸由模板/运行时决定。
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var hand = go.GetComponent<HandView>();
            var serialized = new SerializedObject(hand);
            SetObject(serialized, "_root", rect);
            SetObject(serialized, "_cardFallbackPrefab", templatePrefab != null ? templatePrefab.GetComponent<Card>() : null);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return hand;
        }

        /// <summary>
        /// 确保格子 Prefab 存在。
        /// 输入：无；输出：BoardCellView 引用（用于棋盘视图的实例化模板）。
        /// </summary>
        public static BoardCellView EnsureCellPrefab()
        {
            var existing = AssetDatabase.LoadAssetAtPath<BoardCellView>(BoardCellPrefabPath);
            if (existing != null) return existing;

            var directory = Path.GetDirectoryName(BoardCellPrefabPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);

            var go = new GameObject("BoardCell", typeof(RectTransform), typeof(Image), typeof(Button), typeof(BoardCellView));
            var background = go.GetComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0.10f);
            var button = go.GetComponent<Button>();
            button.targetGraphic = background;

            var highlightGo = new GameObject("Highlight", typeof(RectTransform), typeof(Image));
            var highlightRect = (RectTransform)highlightGo.transform;
            highlightRect.SetParent(go.transform, false);
            highlightRect.anchorMin = Vector2.zero;
            highlightRect.anchorMax = Vector2.one;
            highlightRect.offsetMin = Vector2.zero;
            highlightRect.offsetMax = Vector2.zero;
            var highlight = highlightGo.GetComponent<Image>();
            highlight.color = new Color(0.2f, 1f, 0.6f, 0.35f);
            highlight.raycastTarget = false;
            highlight.enabled = false;

            var serialized = new SerializedObject(go.GetComponent<BoardCellView>());
            SetObject(serialized, "_background", background);
            SetObject(serialized, "_highlight", highlight);
            SetObject(serialized, "_button", button);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            var prefab = PrefabUtility.SaveAsPrefabAsset(go, BoardCellPrefabPath);
            Object.DestroyImmediate(go);
            AssetDatabase.SaveAssets();
            return prefab.GetComponent<BoardCellView>();
        }

        /// <summary>把演示场景放到 Build Settings 第一位。输入：无；输出：无。</summary>
        static void RegisterSceneInBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            var index = scenes.FindIndex(s => s.path == DemoScenePath);
            if (index < 0) scenes.Insert(0, new EditorBuildSettingsScene(DemoScenePath, true));
            else scenes[index].enabled = true;
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        /// <summary>
        /// 写入一个对象引用类型的私有序列化字段。
        /// 输入：SerializedObject、字段名与引用；输出：无（字段缺失时打印警告，便于发现 Unity 版本差异）。
        /// </summary>
        static void SetObject(SerializedObject serialized, string fieldName, Object value)
        {
            var property = serialized.FindProperty(fieldName);
            if (property == null)
            {
                Debug.LogWarning($"字段 {fieldName} 不存在于 {serialized.targetObject.GetType().Name}，场景装配可能不完整。");
                return;
            }
            property.objectReferenceValue = value;
        }

        /// <summary>
        /// 打印编译报告。
        /// 输入：报告与成功提示；输出：无。失败时逐条打印可定位的问题。
        /// </summary>
        static void Report(CompileReport report, string successMessage)
        {
            if (report.Success) { Debug.Log(successMessage); return; }
            foreach (var issue in report.Issues) Debug.LogError(issue.ToString());
            Debug.LogError($"配置校验失败，共 {report.Issues.Count} 个问题。");
        }
    }
}
