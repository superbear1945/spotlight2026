// 界面美术契约测试：固定 UI Toolkit 的运行时绑定名称，并验证非交互 UGUI 图层不会拦截点击。
// 为什么需要：界面换皮最容易出现“看起来正确但按钮失联”或“背景 Image 抢走棋盘点击”的回归。
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using NUnit.Framework;
using Spotlight;
using Spotlight.EditorTools;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UguiImage = UnityEngine.UI.Image;

namespace Spotlight.Tests
{
    /// <summary>
    /// UI Toolkit 与非交互 UGUI 美术的回归测试。
    /// 输入：项目内 UXML/USS 与内存创建的 Canvas；输出：NUnit 断言结果。
    /// </summary>
    public class UiPresentationTests
    {
        /// <summary>HUD UXML 的固定资产路径。</summary>
        const string UxmlPath = "Assets/Spotlight/UI/Spotlight.uxml";
        /// <summary>HUD USS 的固定资产路径。</summary>
        const string UssPath = "Assets/Spotlight/UI/Spotlight.uss";

        /// <summary>每个测试使用的 UXML 实例。</summary>
        VisualElement _root;
        /// <summary>承载 HudView 的临时对象。</summary>
        GameObject _host;

        /// <summary>
        /// 实例化真实 HUD 资产。
        /// 输入：无；输出：可查询的 VisualElement 树与临时 HudView 宿主。
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            Assert.That(asset, Is.Not.Null, "找不到界面文件 " + UxmlPath);
            _root = asset.Instantiate();
            _host = new GameObject("UiPresentationTestHost");
        }

        /// <summary>销毁测试临时对象。输入：无；输出：无。</summary>
        [TearDown]
        public void TearDown()
        {
            if (_host != null) Object.DestroyImmediate(_host);
            _root = null;
        }

        /// <summary>
        /// 换皮后必须保留全部规则层按钮名，并补齐生命、回合、美术结算与日志入口。
        /// 输入：真实 UXML；输出：元素存在性与 USS 素材引用断言。
        /// </summary>
        [Test]
        public void SkinnedHudKeepsBindingsAndReferencesImportedArtwork()
        {
            var requiredNames = new[]
            {
                "turn-label", "resource-label", "plays-label", "round-number-label",
                "player-life-label", "boss-life-label", "boss-info-label", "phase-label", "unit-detail",
                "log-panel", "log-list", "log-content", "pause-button", "history-button", "debug-button", "help-button",
                "withdraw-button", "bomb-deploy-button", "bomb-skip-button",
                "spawner-deploy-button", "spawner-skip-button", "end-turn-button",
                "opponent-turn-indicator",
                "skip-deploy-button", "restart-button", "victory-overlay", "victory-label",
                "victory-art", "defeat-art", "victory-restart-button", "modal-overlay", "debug-panel",
                "pause-overlay", "pause-panel", "pause-close-button", "pause-continue-button",
                "pause-restart-button", "pause-exit-button"
            };
            foreach (var name in requiredNames)
                Assert.That(_root.Q<VisualElement>(name), Is.Not.Null, "UXML 缺少运行时绑定元素：" + name);

            var stateDependentNames = new[]
            {
                "victory-overlay", "victory-art", "defeat-art", "modal-overlay",
                "error-label", "withdraw-button", "skip-deploy-button", "restart-button",
                "log-panel", "side-panel", "opponent-turn-indicator", "pause-overlay"
            };
            var uxmlLines = File.ReadAllLines(UxmlPath);
            foreach (var name in stateDependentNames)
            {
                var elementLine = uxmlLines.Single(line => line.Contains("name=\"" + name + "\""));
                Assert.That(elementLine, Does.Contain("style=\"display: none;\""),
                    "状态界面必须在 UXML 中默认隐藏，不能在非运行状态一股脑叠到 Game 视图：" + name);
            }

            var uss = File.ReadAllText(UssPath);
            var requiredArtwork = new[]
            {
                "player_life_static.png", "enemy_life_static.png", "player_energy_static.png",
                "round_counter_blank_static.png", "opponent_turn_static.png",
                "原油战线_更新UI_v2/01_pause/pause_normal.png",
                "原油战线_更新UI_v2/02_settings/settings_normal.png",
                "原油战线_更新UI_v2/03_history/history_normal.png",
                "原油战线_更新UI_v2/04_help/help_normal.png", "end_turn_normal.png",
                "primary_normal.png", "secondary_normal.png", "panel_large_static.png",
                "notice_static.png", "victory_static.png", "defeat_static.png", "UI/Loose/pause.png",
                "03_entry_buttons/close/close_hover.png"
            };
            foreach (var fileName in requiredArtwork)
                Assert.That(uss, Does.Contain(fileName), "USS 尚未引用已导入美术：" + fileName);

            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.end-turn-button:hover\s*\{[^}]*end_turn_hover\.png"),
                "结束回合按钮的高优先级 HUD 规则必须切换 Hover 美术，避免被基础布局规则覆盖");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.end-turn-button:active\s*\{[^}]*end_turn_pressed\.png"),
                "结束回合按钮按下时必须切换 Pressed 美术");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.end-turn-button:disabled\s*\{[^}]*end_turn_disabled\.png"),
                "结束回合按钮禁用时必须切换 Disabled 美术");
        }

        /// <summary>
        /// 暂停面板必须能打开、继续关闭，并把重开与退出意图交给对局层。
        /// 输入：真实 UXML 中的暂停节点与内存 HudView；输出：显隐状态和事件序列断言。
        /// 为什么直接调用私有处理器：EditMode 不依赖真实指针设备，测试只验证按钮最终绑定到的业务处理行为。
        /// </summary>
        [Test]
        public void PausePanelTogglesAndRaisesRequests()
        {
            var hud = _host.AddComponent<HudView>();
            var pauseOverlay = _root.Q<VisualElement>("pause-overlay");
            SetField(hud, "_pauseOverlay", pauseOverlay);
            var pauseStates = new List<bool>();
            var restartRequested = false;
            var exitRequested = false;
            hud.PauseChanged += pauseStates.Add;
            hud.RestartRequested += () => restartRequested = true;
            hud.ExitRequested += () => exitRequested = true;

            InvokeMethod(hud, "OpenPausePanel");
            Assert.That(pauseOverlay.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(pauseStates.Last(), Is.True);

            InvokeMethod(hud, "ClosePausePanel");
            Assert.That(pauseOverlay.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(pauseStates.Last(), Is.False);

            InvokeMethod(hud, "OpenPausePanel");
            InvokeMethod(hud, "RestartFromPause");
            Assert.That(pauseOverlay.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(restartRequested, Is.True);
            Assert.That(pauseStates.Last(), Is.False);

            InvokeMethod(hud, "ExitFromPause");
            Assert.That(exitRequested, Is.True);

            var originalTimeScale = Time.timeScale;
            try
            {
                var bootstrap = _host.AddComponent<GameBootstrap>();
                Time.timeScale = 0.5f;
                InvokeMethod(bootstrap, "OnPauseChanged", true);
                Assert.That(Time.timeScale, Is.EqualTo(0f));
                InvokeMethod(bootstrap, "OnPauseChanged", false);
                Assert.That(Time.timeScale, Is.EqualTo(0.5f));
            }
            finally
            {
                Time.timeScale = originalTimeScale;
            }
        }

        /// <summary>
        /// HudView 必须从快照显示回合、资源以及双方家的实时生命。
        /// 输入：最小可用对局的首帧快照；输出：对应标签文本和玩家回合圆盘状态。
        /// </summary>
        [Test]
        public void HudRenderShowsRoundResourcesAndHomeLifeFromSnapshot()
        {
            var configuration = new GameConfiguration(ConfigPanelTests.Document());
            var session = new GameSession(configuration, new SeededRandom(17), 50);
            var snapshot = session.GetSnapshot();
            var hud = _host.AddComponent<HudView>();

            SetField(hud, "_root", _root);
            SetField(hud, "_turnLabel", _root.Q<Label>("turn-label"));
            SetField(hud, "_resourceLabel", _root.Q<Label>("resource-label"));
            SetField(hud, "_playsLabel", _root.Q<Label>("plays-label"));
            SetField(hud, "_roundNumberLabel", _root.Q<Label>("round-number-label"));
            SetField(hud, "_playerLifeLabel", _root.Q<Label>("player-life-label"));
            SetField(hud, "_bossLifeLabel", _root.Q<Label>("boss-life-label"));
            SetField(hud, "_roundCounter", _root.Q<VisualElement>("round-counter"));
            SetField(hud, "_opponentTurnIndicator", _root.Q<VisualElement>("opponent-turn-indicator"));
            SetField(hud, "_endTurnButton", _root.Q<Button>("end-turn-button"));

            var selection = new Selection(SelectionKind.None);
            hud.Render(snapshot, configuration, selection, session.GetLegalActions(selection));

            Assert.That(_root.Q<Label>("turn-label").text, Is.EqualTo("第 1 回合 · 玩家行动"));
            Assert.That(_root.Q<Label>("round-number-label").text, Is.EqualTo("01"));
            Assert.That(_root.Q<Label>("resource-label").text, Is.EqualTo("2"));
            Assert.That(_root.Q<Label>("plays-label").text, Is.EqualTo("剩余出牌 5"));
            Assert.That(_root.Q<Label>("player-life-label").text, Is.EqualTo("10"));
            Assert.That(_root.Q<Label>("boss-life-label").text, Is.EqualTo("10"));
            Assert.That(_root.Q<VisualElement>("round-counter").ClassListContains("opponent-turn"), Is.False);
            Assert.That(_root.Q<Button>("end-turn-button").style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(_root.Q<VisualElement>("opponent-turn-indicator").style.display.value, Is.EqualTo(DisplayStyle.None));
        }

        /// <summary>
        /// Boss 自动行动的展示帧必须把长条切到圆盘右侧，并隐藏左侧可点击的结束回合按钮。
        /// 输入：Boss 先手配置产生的首段展示快照；输出：两种回合长条的互斥显隐断言。
        /// </summary>
        [Test]
        public void AutomaticBossTurnShowsRightIndicatorAndHidesEndTurnButton()
        {
            var document = ConfigPanelTests.Document();
            document.rules[0].firstSide = "boss";
            var configuration = new GameConfiguration(document);
            var session = new GameSession(configuration, new SeededRandom(17), 50);
            var snapshot = session.InitialPresentationFrames.Select(frame => frame.Snapshot)
                .First(frame => frame.ActiveSide == Side.Boss);
            var hud = _host.AddComponent<HudView>();
            SetField(hud, "_root", _root);
            SetField(hud, "_roundCounter", _root.Q<VisualElement>("round-counter"));
            SetField(hud, "_roundNumberLabel", _root.Q<Label>("round-number-label"));
            SetField(hud, "_opponentTurnIndicator", _root.Q<VisualElement>("opponent-turn-indicator"));
            SetField(hud, "_endTurnButton", _root.Q<Button>("end-turn-button"));

            var selection = new Selection(SelectionKind.None);
            hud.Render(snapshot, configuration, selection, session.GetLegalActions(selection));

            Assert.That(_root.Q<VisualElement>("round-counter").ClassListContains("opponent-turn"), Is.True);
            Assert.That(_root.Q<Button>("end-turn-button").style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(_root.Q<VisualElement>("opponent-turn-indicator").style.display.value, Is.EqualTo(DisplayStyle.Flex));
        }

        /// <summary>
        /// 非交互 UGUI 美术必须可重复应用、顺序正确且完全不参与射线检测。
        /// 输入：代码搭建的 Canvas、BoardRoot 与 HandRoot；输出：两层 Image 的资产、层级、锚点与射线断言。
        /// </summary>
        [Test]
        public void PassiveUguiArtworkIsIdempotentAndNeverBlocksInput()
        {
            var canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            try
            {
                var canvas = canvasObject.GetComponent<Canvas>();
                var board = new GameObject("BoardRoot", typeof(RectTransform));
                board.transform.SetParent(canvas.transform, false);
                var cells = new GameObject("Cells", typeof(RectTransform));
                cells.transform.SetParent(board.transform, false);
                var hand = new GameObject("HandRoot", typeof(RectTransform));
                hand.transform.SetParent(canvas.transform, false);

                SpotlightMenu.EnsurePassiveArtwork(canvas);
                SpotlightMenu.EnsurePassiveArtwork(canvas);

                Assert.That(canvas.transform.Cast<Transform>().Count(t => t.name == "BattlefieldBackground"), Is.EqualTo(1));
                Assert.That(canvas.transform.Cast<Transform>().Count(t => t.name == "HandBackdrop"), Is.EqualTo(1));
                Assert.That(board.transform.Cast<Transform>().Count(t => t.name == "BoardFrame"), Is.EqualTo(0),
                    "通用功能面板不能再被误用为棋盘边框");

                var background = canvas.transform.Find("BattlefieldBackground").GetComponent<UguiImage>();
                var handBackdrop = canvas.transform.Find("HandBackdrop").GetComponent<UguiImage>();
                foreach (var image in new[] { background, handBackdrop })
                    Assert.That(image.raycastTarget, Is.False, image.name + " 不应拦截点击");

                Assert.That(AssetDatabase.GetAssetPath(background.sprite), Is.EqualTo(SpotlightMenu.BattlefieldBackgroundPath));
                Assert.That(AssetDatabase.GetAssetPath(handBackdrop.sprite), Is.EqualTo(SpotlightMenu.HandBackdropArtworkPath));
                Assert.That(background.transform.GetSiblingIndex(), Is.EqualTo(0), "战场背景必须在所有可交互内容后面");
                Assert.That(handBackdrop.transform.GetSiblingIndex(), Is.LessThan(hand.transform.GetSiblingIndex()), "手牌底板必须画在手牌后面");
                Assert.That(handBackdrop.rectTransform.anchorMin, Is.EqualTo(new Vector2(0f, 0f)));
                Assert.That(handBackdrop.rectTransform.anchorMax, Is.EqualTo(new Vector2(1f, 0f)));
                Assert.That(handBackdrop.rectTransform.anchoredPosition.y, Is.EqualTo(-210f));
                Assert.That(handBackdrop.rectTransform.sizeDelta.y, Is.EqualTo(650f));
                Assert.That(((RectTransform)board.transform).offsetMin, Is.EqualTo(new Vector2(300f, 300f)));
                Assert.That(((RectTransform)board.transform).offsetMax, Is.EqualTo(new Vector2(-300f, -210f)));
            }
            finally
            {
                Object.DestroyImmediate(canvasObject);
            }
        }

        /// <summary>
        /// 写入私有视图字段以构造最小 HudView 测试夹具。
        /// 输入：目标对象、字段名和值；输出：无，字段缺失时直接使测试失败。
        /// </summary>
        static void SetField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, target.GetType().Name + " 缺少字段 " + fieldName);
            field.SetValue(target, value);
        }

        /// <summary>
        /// 调用视图的私有按钮处理器以验证纯界面状态机。
        /// 输入：目标对象、方法名与可选参数；输出：无，方法缺失时直接使测试失败。
        /// </summary>
        static void InvokeMethod(object target, string methodName, params object[] arguments)
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, target.GetType().Name + " 缺少方法 " + methodName);
            method.Invoke(target, arguments);
        }
    }
}
