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
        /// <summary>战场全屏背景 Sprite 路径。</summary>
        public const string BattlefieldBackgroundPath = "Assets/Imported/SpotlightArt/Backgrounds/翡翠异星巨冠原野-1.png";
        /// <summary>手牌区域背后的装饰底板 Sprite 路径。</summary>
        public const string HandBackdropArtworkPath = "Assets/Imported/SpotlightArt/UI/Loose/operation.png";

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

        /// <summary>
        /// 给现有外观模板补齐卡图节点（幂等，不会覆盖手工外观）。
        /// 输入：无；输出：无（结果打印到 Console）。
        /// 为什么单独开一个入口：换皮（重建外观模板）会覆盖手工调好的层级，
        /// 而“加卡图节点”只增补一个子节点；分开之后升级模板不必冒重建的风险。
        /// 改完后卡种 Prefab 作为模板变体会自动继承该节点，不需要重新编译配置。
        /// </summary>
        [MenuItem("Tools/Spotlight/升级卡牌外观模板", priority = 26)]
        public static void UpgradeTemplateArtNode()
        {
            ViewTemplateBuilder.EnsureTemplate();
            Debug.Log(ViewTemplateBuilder.HasArtNode()
                ? "外观模板已包含卡图节点（Art）。"
                : "外观模板仍缺少卡图节点，请先执行“重建外观模板”后重试。");
        }

        /// <summary>
        /// 把品牌图标写入 PlayerSettings 的 Standalone / Android 图标槽。
        /// 输入：无；输出：无（结论打印到 Console）。
        /// 为什么需要菜单入口：打包前会由 ConfigBuildProcessor 兜底应用一次，
        /// 但 ProjectSettings 被重置或换了图标源图时，需要能手动刷一次并在 Console 看到结果。
        /// </summary>
        [MenuItem("Tools/Spotlight/应用游戏图标", priority = 18)]
        public static void ApplyBrandingIcon()
        {
            string message;
            BrandingIconSetup.Apply(out message);
            Debug.Log(message);
        }

        /// <summary>生成中文 TMP 字体资产与 UI Toolkit 文本设置。输入：无；输出：无。</summary>
        [MenuItem("Tools/Spotlight/生成中文字体资产", priority = 23)]
        public static void GenerateCjkFont()
        {
            var font = CjkFontSetup.EnsureFontAssets();
            Debug.Log(font != null ? "已生成中文字体资产。" : "中文字体资产生成失败，请查看上一条错误。");
        }

        /// <summary>
        /// 把非交互 UGUI 美术应用到当前场景。
        /// 输入：无；输出：当前场景中的全屏背景、棋盘框与手牌底板。
        /// 为什么提供独立入口：换皮不应要求重建整个演示场景，也不应触碰棋盘格、卡牌或手牌的交互组件。
        /// </summary>
        [MenuItem("Tools/Spotlight/应用界面美术", priority = 25)]
        public static void ApplyUiArtwork()
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("请退出 Play 模式后再应用界面美术。");
                return;
            }

            var canvas = Object.FindObjectOfType<Canvas>();
            if (canvas == null)
            {
                Debug.LogError("当前场景中没有 Canvas，无法应用 UGUI 美术。");
                return;
            }

            EnsurePassiveArtwork(canvas);
            var scene = canvas.gameObject.scene;
            EditorSceneManager.MarkSceneDirty(scene);
            if (!string.IsNullOrEmpty(scene.path)) EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("已应用非交互 UGUI 美术：战场背景、棋盘框、手牌底板。");
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
            // 调试配置面板刻意不挂在场景上：场景里一旦有这个组件，构建时 globalgamemanagers 会留下
            // “ConfigDebugPanel”的 MonoScript 名字，拆包就能看出有配置编辑器。
            // 面板改由 GameBootstrap.EnsureDebugPanel 在调试构建里运行时创建（见 UNITY_EDITOR || DEVELOPMENT_BUILD）。
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
            EnsurePassiveArtwork(canvas);

            var bootstrapSerialized = new SerializedObject(bootstrap);
            SetObject(bootstrapSerialized, "_configAsset", config);
            SetObject(bootstrapSerialized, "_boardView", board);
            SetObject(bootstrapSerialized, "_handView", hand);
            SetObject(bootstrapSerialized, "_hudView", hud);
            SetObject(bootstrapSerialized, "_feedback", feedback);
            bootstrapSerialized.ApplyModifiedPropertiesWithoutUndo();
            GameAudioInstaller.Install(bootstrap);

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
            rect.offsetMin = new Vector2(300f, 300f);
            rect.offsetMax = new Vector2(-300f, -210f);

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
            rect.anchoredPosition = new Vector2(0f, 72f);
            rect.sizeDelta = new Vector2(-960f, 220f);

            var layout = go.GetComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.LowerCenter;
            layout.spacing = 14f;
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
        /// 确保画布中存在示意图要求的两层非交互美术，并同步棋盘与手牌的基准布局。
        /// 输入：已包含 BoardRoot 与 HandRoot 的画布；输出：幂等更新后的全屏战场背景与整条手牌底板。
        /// 为什么没有棋盘面板：示意图的格子直接铺在战场背景上，通用功能面板只用于弹窗，
        /// 不能误当棋盘边框。所有 Image 都关闭 raycastTarget，点击仍落到 BoardCellView 或手牌 Card 上。
        /// </summary>
        public static void EnsurePassiveArtwork(Canvas canvas)
        {
            if (canvas == null)
            {
                Debug.LogError("EnsurePassiveArtwork：Canvas 为空。");
                return;
            }

            var canvasRect = canvas.transform as RectTransform;
            var background = EnsureArtworkImage(canvasRect, "BattlefieldBackground", BattlefieldBackgroundPath, false);
            Stretch(background.rectTransform, Vector2.zero, Vector2.zero);
            background.color = Color.white;
            background.transform.SetAsFirstSibling();

            var boardRoot = canvas.transform.Find("BoardRoot") as RectTransform;
            if (boardRoot != null)
            {
                boardRoot.anchorMin = Vector2.zero;
                boardRoot.anchorMax = Vector2.one;
                boardRoot.offsetMin = new Vector2(300f, 300f);
                boardRoot.offsetMax = new Vector2(-300f, -210f);

                // 旧实现曾把通用弹窗面板误作棋盘框；若场景里已生成则只清理这个工具创建的固定名称对象。
                var staleBoardFrame = boardRoot.Find("BoardFrame");
                if (staleBoardFrame != null) Object.DestroyImmediate(staleBoardFrame.gameObject);
            }
            else
            {
                Debug.LogWarning("未找到 BoardRoot，无法同步棋盘布局。");
            }

            var handRoot = canvas.transform.Find("HandRoot") as RectTransform;
            if (handRoot != null)
            {
                var handBackdrop = EnsureArtworkImage(canvasRect, "HandBackdrop", HandBackdropArtworkPath, false);
                var rect = handBackdrop.rectTransform;
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(1f, 0f);
                rect.pivot = new Vector2(0.5f, 0f);
                // operation.png 的主不透明区域约占原图纵向 224..493 / 728。
                // 用 650 高并向下偏移 210，可让约 240 像素高的可见框从屏幕底边开始，
                // 而不是把透明画布当作内容高度导致底板只露出一条窄带。
                rect.anchoredPosition = new Vector2(0f, -210f);
                rect.sizeDelta = new Vector2(0f, 650f);
                handBackdrop.color = Color.white;

                handRoot.anchorMin = new Vector2(0f, 0f);
                handRoot.anchorMax = new Vector2(1f, 0f);
                handRoot.pivot = new Vector2(0.5f, 0f);
                handRoot.anchoredPosition = new Vector2(0f, 72f);
                handRoot.sizeDelta = new Vector2(-960f, 220f);

                // 手牌使用 180×240 的运行时尺寸并整体上移 72 像素；底对齐保证卡底完整，
                // 卡牌只向上越出操作区，不会被挤到屏幕下方。
                var handLayout = handRoot.GetComponent<HorizontalLayoutGroup>();
                if (handLayout != null)
                {
                    handLayout.childAlignment = TextAnchor.LowerCenter;
                    handLayout.spacing = 14f;
                }

                // 手牌必须始终画在底板之后；先把手牌放到最后，再把底板插到它前一位，重复调用仍保持稳定。
                handRoot.SetAsLastSibling();
                handBackdrop.transform.SetSiblingIndex(Mathf.Max(0, handRoot.GetSiblingIndex() - 1));

            }
            else
            {
                Debug.LogWarning("未找到 HandRoot，已跳过手牌装饰底板。");
            }
        }

        /// <summary>
        /// 创建或更新一个只显示美术的 UGUI Image。
        /// 输入：父节点、对象名、Sprite 路径与是否保持图片比例；输出：已配置的 Image。
        /// 为什么集中处理：三个装饰层必须统一关闭射线并允许重复应用，避免场景里不断产生重名副本。
        /// </summary>
        static Image EnsureArtworkImage(RectTransform parent, string name, string spritePath, bool preserveAspect)
        {
            var existing = parent != null ? parent.Find(name) : null;
            var go = existing != null ? existing.gameObject : new GameObject(name, typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            if (rect.parent != parent) rect.SetParent(parent, false);

            var image = go.GetComponent<Image>();
            if (image == null) image = go.AddComponent<Image>();
            image.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
            image.type = Image.Type.Simple;
            image.preserveAspect = preserveAspect;
            image.raycastTarget = false;
            if (image.sprite == null) Debug.LogWarning("找不到界面美术 Sprite：" + spritePath);
            return image;
        }

        /// <summary>
        /// 把矩形铺满父节点，并允许额外向外扩展。
        /// 输入：矩形、左下与右上偏移；输出：更新后的 RectTransform。
        /// </summary>
        static void Stretch(RectTransform rect, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
        }

        /// <summary>
        /// 在运行中打开对局配置面板。
        /// 输入：无（仅在 Play 模式下有效）；输出：无。
        /// 为什么需要：面板只能由界面按钮打开，开发时想直接在运行中查看/调整配置比较麻烦，
        /// 因此提供一个仅编辑器可用的快捷入口，不改变正式构建的入口限制。
        /// </summary>
        [MenuItem("Tools/Spotlight/在运行中打开配置面板", priority = 41)]
        public static void OpenConfigPanelInPlayMode()
        {
            // 面板的创建与接线都在 ConfigPanelInstaller 里（它是编辑器侧的唯一安装点）。
            ConfigPanelInstaller.OpenPanelInPlayMode();
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
