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
        /// <summary>音量控制现为实时生效，夹具需要恢复原先的全局与分类音量。</summary>
        float _listenerVolume, _masterVolume, _musicVolume, _soundVolume;

        /// <summary>
        /// 实例化真实 HUD 资产。
        /// 输入：无；输出：可查询的 VisualElement 树与临时 HudView 宿主。
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            _listenerVolume = AudioListener.volume;
            _masterVolume = GameAudioSettings.Master; _musicVolume = GameAudioSettings.Music; _soundVolume = GameAudioSettings.Sound;
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            Assert.That(asset, Is.Not.Null, "找不到界面文件 " + UxmlPath);
            _root = asset.Instantiate();
            _host = new GameObject("UiPresentationTestHost");
        }

        /// <summary>销毁测试临时对象。输入：无；输出：无。</summary>
        [TearDown]
        public void TearDown()
        {
            GameAudioSettings.SetMaster(_masterVolume); GameAudioSettings.SetMusic(_musicVolume); GameAudioSettings.SetSound(_soundVolume);
            AudioListener.volume = _listenerVolume;
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
                "player-life-label", "boss-life-label", "boss-phase-label",
                "log-panel", "log-list", "log-content", "pause-button", "settings-button", "history-button",
                "help-button",
                "withdraw-button", "bomb-deploy-button", "bomb-skip-button",
                "spawner-deploy-button", "spawner-skip-button", "end-turn-button",
                "opponent-turn-indicator", "player-insignia", "enemy-insignia",
                "player-insignia-dots", "enemy-insignia-dots",
                "skip-deploy-button", "restart-button", "victory-overlay", "victory-label",
                "victory-art", "defeat-art", "victory-restart-button", "modal-overlay",
                "pause-overlay", "pause-panel", "pause-close-button", "pause-continue-button",
                "pause-restart-button", "pause-exit-button",
                "settings-overlay", "settings-panel", "master-volume-slider", "music-volume-slider", "sound-volume-slider",
                "master-volume-fill", "music-volume-fill", "sound-volume-fill",
                "master-volume-value", "music-volume-value", "sound-volume-value", "fullscreen-toggle",
                "settings-reset-button", "settings-close-button", "settings-x-button",
                "help-overlay", "help-panel", "help-close-button",
                "detail-left", "detail-right", "pile-left", "pile-right",
                "grave-count", "draw-count", "draw-count-tiers",
                "menu-overlay", "menu-resume-button", "menu-deck-select-button",
                "menu-restart-button", "menu-quit-button",
                "deck-select-confirm-overlay", "deck-select-mask", "deck-select-apply-button",
                "pause-title-icon",
                "pause-header-left-line", "pause-header-right-line"
            };
            foreach (var name in requiredNames)
                Assert.That(_root.Q<VisualElement>(name), Is.Not.Null, "UXML 缺少运行时绑定元素：" + name);

            var stateDependentNames = new[]
            {
                "victory-overlay", "victory-art", "defeat-art", "modal-overlay",
                "error-label", "withdraw-button", "skip-deploy-button", "restart-button",
                "log-panel", "opponent-turn-indicator", "pause-overlay",
                "settings-overlay", "help-overlay", "menu-overlay", "deck-select-confirm-overlay", "deck-select-mask"
            };
            var uxml = File.ReadAllText(UxmlPath);
            Assert.That(uxml, Does.Not.Contain("side-panel").And.Not.Contain("boss-info-label")
                .And.Not.Contain("name=\"phase-label\"").And.Not.Contain("unit-detail"),
                "右下角旧「Boss 状态 / 选中详情」侧栏已废弃：Boss 阶段看顶部摘要，选中详情看左右卡面详情区");
            Assert.That(uxml, Does.Not.Contain("pause-deck-select-button")
                .And.Not.Contain("pause-settings-icon")
                .And.Not.Contain("text=\"重新选卡\""),
                "暂停菜单必须只保留继续、重新开始和退出；不能再出现重新选卡或无效设置齿轮");
            Assert.That(uxml, Does.Not.Contain("text=\"我方\""),
                "阵营由徽记表达后，不应继续显示我方文字标签");
            Assert.That(uxml, Does.Not.Contain("text=\"敌方\""),
                "阵营由徽记表达后，不应继续显示敌方文字标签");
            var uxmlLines = File.ReadAllLines(UxmlPath);
            Assert.That(uxmlLines.Count(line => line.Contains("class=\"halftone-dot\"")), Is.EqualTo(114),
                "双方徽记垫层必须保留无方向性的椭圆半调点阵，不能在重建 UXML 时丢失");
            Assert.That(uxml, Does.Contain("width: 5px;").And.Contain("width: 21px;"),
                "半调点阵必须同时包含边缘小点和中心大点，不能退化成统一尺寸");
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
                "player_insignia_static.png", "enemy_insignia_static.png",
                "round_counter_blank_static.png", "opponent_turn_static.png",
                "原油战线_更新UI_v2/01_pause/pause_normal.png",
                "原油战线_更新UI_v2/02_settings/settings_normal.png",
                "原油战线_更新UI_v2/03_history/history_normal.png",
                "原油战线_更新UI_v2/04_help/help_normal.png", "end_turn_normal.png",
                "primary_normal.png", "secondary_normal.png", "panel_large_static.png", "dialog_static.png",
                "notice_static.png", "victory_static.png", "defeat_static.png", "UI/Loose/pause_panel.png",
                "UI/Loose/pause_icon.png", "UI/Loose/close_icon.png", "UI/Loose/set_panel.png",
                "05_settings_controls/slider_track/slider_track_static.png",
                "05_settings_controls/slider_fill/slider_fill_static.png",
                "05_settings_controls/slider_handle/slider_handle_normal.png",
                "05_settings_controls/slider_handle/slider_handle_hover.png",
                "05_settings_controls/toggle_on/toggle_on_normal.png",
                "05_settings_controls/toggle_off/toggle_off_normal.png",
                "05_settings_controls/scroll_track/scroll_track_static.png",
                "05_settings_controls/scroll_handle/scroll_handle_normal.png",
                "05_settings_controls/scroll_handle/scroll_handle_hover.png"
            };
            foreach (var fileName in requiredArtwork)
                Assert.That(uss, Does.Contain(fileName), "USS 尚未引用已导入美术：" + fileName);

            Assert.That(uss, Does.Match(@"(?s)\.action-button\s*\{[^}]*-unity-background-scale-mode:\s*scale-to-fit;"),
                "通用按钮必须等比缩放，不能再用 stretch-to-fill 拉坏美术比例");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.pause-menu-button\s*\{[^}]*width:\s*480px;[^}]*height:\s*90px;[^}]*-unity-background-scale-mode:\s*scale-and-crop;"),
                "暂停页按钮必须按原图比例裁掉透明边，不能横向拉伸或越出面板");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.pause-header-line\s*\{[^}]*top:\s*133px;")
                .And.Match(@"(?s)\.hud-root \.pause-title-icon\s*\{[^}]*top:\s*81px;[^}]*height:\s*108px;")
                .And.Match(@"(?s)\.hud-root \.pause-header-right-line\s*\{[^}]*left:\s*420px;[^}]*width:\s*98px;"),
                "暂停图标与左右蓝线必须共享 135px 的水平中心，并在移除齿轮后保持左右对称");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.pause-continue-button\s*\{[^}]*top:\s*185px;")
                .And.Match(@"(?s)\.hud-root \.pause-restart-button\s*\{[^}]*top:\s*305px;")
                .And.Match(@"(?s)\.hud-root \.pause-exit-button\s*\{[^}]*top:\s*425px;"),
                "移除重新选卡后，暂停菜单的三个按钮必须按 120px 节拍重新居中排列");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.pause-close-button\s*\{[^}]*top:\s*106px;"),
                "关闭图标必须补偿源图透明边，上移到与暂停图标及蓝线的可见中心一致");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.settings-slider-shell\s*\{[^}]*width:\s*440px;[^}]*height:\s*56px;[^}]*slider_track_static\.png[^}]*scale-and-crop;"),
                "设置页滑条轨道必须使用现有美术并按比例裁切透明边");
            Assert.That(uxml, Does.Contain("settings-slider-fill-clip").And.Contain("settings-slider-fill-art"),
                "滑条填充必须通过固定尺寸美术的裁切窗口显示，不能按百分比直接拉伸图片");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.settings-slider-fill-clip\s*\{[^}]*left:\s*34px;[^}]*overflow:\s*hidden;")
                .And.Match(@"(?s)\.hud-root \.settings-slider-fill-art\s*\{[^}]*left:\s*-34px;[^}]*width:\s*440px;"),
                "填充裁切窗口必须与轨道的有效内槽对齐，并保持美术本身宽度不变");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.settings-slider \.unity-base-slider__dragger\s*\{[^}]*width:\s*56px;[^}]*height:\s*56px;[^}]*slider_handle_normal\.png[^}]*scale:\s*2 2;"),
                "滑条手柄必须用较小布局盒保持完整行程，再用视觉缩放补偿源图透明边");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.settings-slider\s*\{[^}]*padding:\s*0 34px;")
                .And.Match(@"(?s)\.hud-root \.settings-slider \.unity-base-slider__dragger\s*\{[^}]*margin-top:\s*-28px;"),
                "滑条交互行程和滑块垂直中心必须与左右 34px 的美术内槽使用同一基线");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.help-scroll \.unity-scroller__low-button,\s*\.hud-root \.help-scroll \.unity-scroller__high-button\s*\{[^}]*display:\s*none;"),
                "帮助页必须隐藏 UI Toolkit 原生白色滚动箭头");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.help-scroll \.unity-scroller__slider\s*\{[^}]*scroll_track_static\.png")
                .And.Match(@"(?s)\.hud-root \.help-scroll \.unity-base-slider__dragger\s*\{[^}]*scroll_handle_normal\.png"),
                "帮助页滚动条必须完整使用项目中的轨道与滑块素材");
            Assert.That(uxml, Does.Contain("settings-control-row settings-master-row")
                .And.Contain("settings-control-row settings-music-row")
                .And.Contain("settings-control-row settings-sound-row"),
                "三条音量设置必须拥有独立定位类，不能继续依赖内容高度自动推挤");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.utility-panel\s*\{[^}]*width:\s*920px;[^}]*height:\s*613px;"),
                "设置面板必须放大坐标盒，为内容层级和底部操作区提供完整空间");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.settings-panel > \.settings-title-row\s*\{[^}]*top:\s*72px;")
                .And.Match(@"(?s)\.hud-root \.settings-x-button\s*\{[^}]*top:\s*72px;"),
                "标题和关闭按钮必须共享标题栏的垂直基线，不能落到内容区或压住下边框");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.settings-master-row\s*\{[^}]*top:\s*182px;")
                .And.Match(@"(?s)\.hud-root \.settings-music-row\s*\{[^}]*top:\s*246px;")
                .And.Match(@"(?s)\.hud-root \.settings-sound-row\s*\{[^}]*top:\s*310px;"),
                "三条滑条必须落在统一 64px 节拍的固定基线上");
            // 只断言“恢复默认 / 返回”按顺序同层相邻，不断言标签的闭合写法：UXML 里自闭合（<ui:Button ... />）
            // 与成对（<ui:Button ...></ui:Button>）都合法，把闭合形式写死进正则会让合法的界面文件凭空变红。
            // “开发者配置”不在 UXML 里：它只在调试构建由 HudView.CreateDebugEntryButtons 插到这一行，
            // 正式包连按钮文案都不能有（见 ReleaseBuildHygieneTests）。
            Assert.That(uxml, Does.Match(@"(?s)<ui:VisualElement class=""utility-actions"">\s*<ui:Button name=""settings-reset-button""[^>]*>\s*<ui:Button name=""settings-close-button"""),
                "恢复默认与返回必须组成同一层级的底部操作区");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.utility-actions\s*\{[^}]*top:\s*462px;[^}]*height:\s*60px;")
                .And.Match(@"(?s)\.hud-root \.utility-actions \.action-button\s*\{[^}]*width:\s*225px;[^}]*height:\s*60px;"),
                "三枚设置操作按钮必须完整落在放大后的底板安全区内");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.log-panel\s*\{[^}]*left:\s*auto;[^}]*right:\s*18px;[^}]*top:\s*104px;[^}]*bottom:\s*auto;[^}]*width:\s*360px;[^}]*height:\s*329px;[^}]*pause_panel\.png[^}]*scale-to-fit;")
                .And.Match(@"(?s)\.hud-root \.log-panel \.section-label\s*\{[^}]*font-size:\s*20px;[^}]*middle-center;")
                .And.Match(@"(?s)\.hud-root \.log-panel \.log-list \.unity-scroll-view__content-viewport\s*\{[^}]*background-color:\s*rgba\(0,\s*0,\s*0,\s*0\);")
                .And.Match(@"(?s)\.hud-root \.log-panel \.log-list \.unity-scroller__low-button,\s*\.hud-root \.log-panel \.log-list \.unity-scroller__high-button\s*\{[^}]*display:\s*none;")
                .And.Match(@"(?s)\.hud-root \.log-panel \.log-list \.unity-scroller__slider\s*\{[^}]*scroll_track_static\.png")
                .And.Match(@"(?s)\.hud-root \.log-panel \.log-list \.unity-base-slider__dragger\s*\{[^}]*width:\s*34px;[^}]*scroll_handle_normal\.png"),
                "展开后的对局日志必须紧接在右上角对局记录按钮下方，并把标题与正文完整放入内框安全区");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.help-panel\s*\{[^}]*width:\s*920px;[^}]*height:\s*613px;")
                .And.Match(@"(?s)\.hud-root \.help-scroll \.unity-scroll-view__vertical-scroller\s*\{[^}]*width:\s*44px;[^}]*translate:\s*-14px 0;")
                .And.Match(@"(?s)\.hud-root \.help-scroll \.unity-base-slider__dragger\s*\{[^}]*width:\s*44px;[^}]*min-height:\s*108px;")
                .And.Match(@"(?s)\.hud-root \.utility-close-button\s*\{[^}]*top:\s*462px;[^}]*height:\s*60px;"),
                "帮助页必须使用放大的面板与可辨识的滚动条，关闭按钮必须完整停在底框安全区内");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.help-scroll \.unity-base-slider__drag-container,\s*\.hud-root \.log-panel \.log-list \.unity-base-slider__drag-container\s*\{[^}]*left:\s*0;[^}]*right:\s*0;[^}]*margin-left:\s*0;[^}]*margin-right:\s*0;")
                .And.Match(@"(?s)\.hud-root \.help-scroll \.unity-base-slider__dragger,\s*\.hud-root \.log-panel \.log-list \.unity-base-slider__dragger\s*\{[^}]*left:\s*0;[^}]*margin-left:\s*0;[^}]*margin-right:\s*0;[^}]*translate:\s*0 0;"),
                "规则帮助与对局日志的滚动轨道、拖拽层和滑块必须共享同一水平中心线");

            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.end-turn-button:hover\s*\{[^}]*end_turn_hover\.png"),
                "结束回合按钮的高优先级 HUD 规则必须切换 Hover 美术，避免被基础布局规则覆盖");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.end-turn-button:active\s*\{[^}]*end_turn_pressed\.png"),
                "结束回合按钮按下时必须切换 Pressed 美术");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.end-turn-button:disabled\s*\{[^}]*end_turn_disabled\.png"),
                "结束回合按钮禁用时必须切换 Disabled 美术");
            Assert.That(uss, Does.Not.Contain("UI/Loose/pause.png"),
                "暂停效果示意图不能被直接用作运行时面板背景，必须用独立切图组合");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.pause-panel\s*\{[^}]*pause_panel\.png"),
                "暂停面板必须使用不含文字与控件的独立底板素材");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.round-counter\s*\{[^}]*top:\s*0;[^}]*width:\s*150px;[^}]*height:\s*150px;"),
                "回合圆盘必须保持原尺寸，不能靠放大圆盘掩盖长条比例问题");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.round-counter\.opponent-turn \.round-number-label\s*\{[^}]*color:\s*rgb\(184,\s*59,\s*34\);"),
                "对方回合数字必须使用高对比敌方色，不能继续与浅色圆盘底色混在一起");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.error-label\s*\{[^}]*width:\s*540px;[^}]*height:\s*98px;[^}]*scale-and-crop;"),
                "弃牌等顶部提示必须按 notice 素材可见比例裁切透明边，不能横向强制拉伸成细条");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.round-number-label\s*\{[^}]*top:\s*59px;[^}]*width:\s*78px;[^}]*height:\s*50px;[^}]*font-size:\s*36px;"),
                "回合数字必须位于圆盘下半部的数字区域中央");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.life-value\s*\{[^}]*top:\s*31px;[^}]*height:\s*44px;[^}]*font-size:\s*34px;"),
                "双方生命数字必须在各自生命底框中垂直居中并保持足够字号");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.resource-label\s*\{[^}]*right:\s*38px;[^}]*top:\s*32px;[^}]*height:\s*42px;[^}]*font-size:\s*30px;"),
                "玩家能量数字必须对准能量底框右侧数值槽");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.life-frame\s*\{[^}]*width:\s*290px;[^}]*height:\s*112px;[^}]*margin-top:\s*38px;"),
                "移除阵营文字后必须补偿原标签高度，生命与能量信息板不能整体上移");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.resource-chip\s*\{[^}]*width:\s*320px;[^}]*height:\s*106px;[^}]*margin-top:\s*-50px;[^}]*margin-left:\s*-7px;"),
                "生命与能量素材必须按自身透明边界左对齐，并缩小两组件之间的视觉间距");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.side-insignia\s*\{[^}]*background-color:\s*rgba\(0,\s*0,\s*0,\s*0\);"),
                "双方徽记必须移除原有深色圆形底板");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.side-insignia-dots\s*\{[^}]*position:\s*absolute;"),
                "半调点阵垫层必须使用绝对定位，不能挤动信息框布局");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.halftone-dot\s*\{[^}]*position:\s*absolute;[^}]*background-color:\s*rgba\(2,\s*15,\s*30,\s*0\.88\);"),
                "半调圆点必须使用统一的深蓝黑颜色，尺寸与透明度由 UXML 单点控制");
            Assert.That(uss, Does.Not.Contain(".insignia-dot-row").And.Not.Contain(".insignia-dot {").And.Not.Contain("side_halftone_dark.png"),
                "不得残留旧规则网格或误生成的点阵贴图方案");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.player-insignia-dots\s*\{[^}]*left:\s*-14px;[^}]*top:\s*20px;[^}]*width:\s*190px;[^}]*height:\s*190px;")
                .And.Match(@"(?s)\.hud-root \.enemy-insignia-dots\s*\{[^}]*right:\s*-14px;[^}]*top:\s*24px;[^}]*width:\s*150px;[^}]*height:\s*150px;"),
                "双方点阵垫层必须位于信息框后方，并按左右阵营镜像定位");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.player-insignia\s*\{[^}]*left:\s*-14px;[^}]*top:\s*24px;[^}]*width:\s*180px;[^}]*height:\s*180px;")
                .And.Match(@"(?s)\.hud-root \.enemy-insignia\s*\{[^}]*right:\s*-14px;[^}]*top:\s*30px;[^}]*width:\s*128px;[^}]*height:\s*128px;"),
                "我方徽记必须覆盖生命与能量两组件，敌方徽记保持镜像垫层位置");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.end-turn-button\s*\{[^}]*top:\s*23px;[^}]*margin-left:\s*2px;[^}]*width:\s*240px;[^}]*height:\s*100px;"),
                "结束回合按钮必须在当前尺寸基础上等比缩小 20%，且不改变回合圆盘");
            Assert.That(uss, Does.Match(@"(?s)\.hud-root \.opponent-turn-indicator\s*\{[^}]*top:\s*23px;[^}]*margin-left:\s*3px;[^}]*width:\s*240px;[^}]*height:\s*100px;"),
                "对方回合状态必须与缩小后的结束回合按钮使用相同尺寸并贴合圆盘右侧");
        }

        /// <summary>
        /// 顶部工具栏必须只保留四个职责互不重复的入口。
        /// 输入：真实 UXML 的 entry-toolbar；输出：按钮顺序、名称与提示文字断言。
        /// 为什么固定为四个：暂停负责对局流程，设置负责设备选项，战报负责日志，帮助负责规则，避免两个齿轮表达不同功能。
        /// </summary>
        [Test]
        public void TopToolbarHasFourDistinctFunctionalEntries()
        {
            var toolbar = _root.Q<VisualElement>(className: "entry-toolbar");
            var buttons = toolbar.Children().OfType<Button>().ToArray();

            Assert.That(buttons.Select(button => button.name), Is.EqualTo(new[]
            {
                "pause-button", "settings-button", "history-button", "help-button"
            }));
            Assert.That(buttons.Select(button => button.tooltip), Is.EqualTo(new[]
            {
                "暂停", "设置", "显示或隐藏对局日志", "规则帮助"
            }));
            Assert.That(_root.Q<Button>("menu-button"), Is.Null, "顶部不应再出现职责不清的第二个菜单入口");
            // 调试入口不进皮肤 UXML：只在调试构建里由 HudView.CreateDebugEntryButtons 用代码插入，
            // 正式包连这个按钮都不存在（这里直接实例化 UXML，没有绑定 HudView，所以查得到 0 个）。
            Assert.That(_root.Q<Button>("quick-debug-button"), Is.Null, "皮肤 UXML 不应自带调试入口");
        }

        /// <summary>
        /// 设置和帮助入口必须形成可关闭的暂停模态，并且设置页确实提供可修改控件。
        /// 输入：真实 UXML 控件与内存 HudView；输出：显隐、默认重置值和暂停事件断言。
        /// 为什么不在此测试保存：保存会切换宿主显示模式；本测试只验证 UI 状态机，设备写入由 Unity API 负责。
        /// </summary>
        [Test]
        public void SettingsAndHelpPanelsExposeControlsAndPauseTheMatch()
        {
            var hud = _host.AddComponent<HudView>();
            var settingsOverlay = _root.Q<VisualElement>("settings-overlay");
            var helpOverlay = _root.Q<VisualElement>("help-overlay");
            var masterVolume = _root.Q<Slider>("master-volume-slider");
            var musicVolume = _root.Q<Slider>("music-volume-slider");
            var soundVolume = _root.Q<Slider>("sound-volume-slider");
            var fullscreen = _root.Q<Toggle>("fullscreen-toggle");
            SetField(hud, "_settingsOverlay", settingsOverlay);
            SetField(hud, "_masterVolumeSlider", masterVolume);
            SetField(hud, "_musicVolumeSlider", musicVolume);
            SetField(hud, "_soundVolumeSlider", soundVolume);
            SetField(hud, "_masterVolumeFill", _root.Q<VisualElement>("master-volume-fill"));
            SetField(hud, "_musicVolumeFill", _root.Q<VisualElement>("music-volume-fill"));
            SetField(hud, "_soundVolumeFill", _root.Q<VisualElement>("sound-volume-fill"));
            SetField(hud, "_masterVolumeValue", _root.Q<Label>("master-volume-value"));
            SetField(hud, "_musicVolumeValue", _root.Q<Label>("music-volume-value"));
            SetField(hud, "_soundVolumeValue", _root.Q<Label>("sound-volume-value"));
            SetField(hud, "_fullscreenToggle", fullscreen);
            SetField(hud, "_helpOverlay", helpOverlay);
            var pauseStates = new List<bool>();
            hud.PauseChanged += pauseStates.Add;

            InvokeMethod(hud, "OpenSettingsPanel");
            Assert.That(settingsOverlay.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(pauseStates.Last(), Is.True);
            masterVolume.value = 15f;
            musicVolume.value = 25f;
            soundVolume.value = 35f;
            InvokeMethod(hud, "OnMasterVolumeChanged", ChangeEvent<float>.GetPooled(0f, 15f));
            InvokeMethod(hud, "OnMusicVolumeChanged", ChangeEvent<float>.GetPooled(0f, 25f));
            InvokeMethod(hud, "OnSoundVolumeChanged", ChangeEvent<float>.GetPooled(0f, 35f));
            Assert.That(AudioListener.volume, Is.EqualTo(.15f).Within(.001f));
            Assert.That(GameAudioSettings.Music, Is.EqualTo(.25f).Within(.001f));
            Assert.That(GameAudioSettings.Sound, Is.EqualTo(.35f).Within(.001f));
            Assert.That(_root.Q<Label>("master-volume-value").text, Is.EqualTo("15%"));
            fullscreen.value = false;
            InvokeMethod(hud, "ResetPlayerSettings");
            Assert.That(masterVolume.value, Is.EqualTo(100f));
            Assert.That(musicVolume.value, Is.EqualTo(100f));
            Assert.That(soundVolume.value, Is.EqualTo(100f));
            Assert.That(GameAudioSettings.Master, Is.EqualTo(1f));
            Assert.That(GameAudioSettings.Music, Is.EqualTo(1f));
            Assert.That(GameAudioSettings.Sound, Is.EqualTo(1f));
            Assert.That(fullscreen.value, Is.True);
            InvokeMethod(hud, "CloseSettingsPanel");
            Assert.That(settingsOverlay.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(pauseStates.Last(), Is.False);

            InvokeMethod(hud, "OpenHelpPanel");
            Assert.That(helpOverlay.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(pauseStates.Last(), Is.True);
            InvokeMethod(hud, "CloseHelpPanel");
            Assert.That(helpOverlay.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(pauseStates.Last(), Is.False);
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
        /// 左右详情必须使用实体卡面式竖向文字层级，并且只在对应选择存在时显示。
        /// 输入：真实 UXML/USS、无选择与手牌选择快照；输出：结构、尺寸、显隐、英文阶级和三项动态数值断言。
        /// 为什么需要：防止详情再次退回横向四格条、贴近顶部状态栏、硬边大面板或轻字重，同时保证数值仍来自当前卡种。
        /// </summary>
        [Test]
        public void DetailInformationUsesVerticalCardFaceTextLayout()
        {
            var uxml = File.ReadAllText(UxmlPath);
            var uxmlLines = File.ReadAllLines(UxmlPath);
            foreach (var name in new[] { "detail-left", "detail-right" })
            {
                var line = uxmlLines.Single(value => value.Contains("name=\"" + name + "\""));
                Assert.That(line, Does.Contain("style=\"display: none;\""),
                    "详情信息区必须在 UXML 中默认隐藏，避免 HudView 首次 Render 前出现空内容：" + name);
                Assert.That(line, Does.Contain("class=\"detail-card-info "),
                    "详情应使用竖向卡面文字结构：" + name);
                Assert.That(line, Does.Not.Contain("class=\"panel "),
                    "详情不能重新继承通用 panel_large 外框：" + name);
            }

            foreach (var name in new[]
            {
                "detail-left-meta", "detail-left-trait", "detail-left-attack", "detail-left-health", "detail-left-move",
                "detail-right-meta", "detail-right-trait", "detail-right-attack", "detail-right-health", "detail-right-move"
            })
            {
                Assert.That(_root.Q<VisualElement>(name), Is.Not.Null, "卡面式详情缺少绑定元素：" + name);
            }
            Assert.That(uxml, Does.Not.Contain("detail-left-resource"));
            Assert.That(uxml, Does.Not.Contain("detail-right-resource"));

            var uss = File.ReadAllText(UssPath);
            Assert.That(uss, Does.Match(@"(?s)\.detail-card-info\s*\{[^}]*bottom:\s*230px;[^}]*width:\s*430px;[^}]*height:\s*340px;[^}]*background-color:\s*rgba\(0,\s*0,\s*0,\s*0\);"),
                "详情文字区必须下移并贴近底部操作区，避免靠近顶部状态栏");
            Assert.That(uxmlLines.Count(value => value.Contains("detail-backdrop-blur")), Is.EqualTo(2),
                "左右详情区必须各自只使用一张真实高斯透明度衬底");
            Assert.That(uxml, Does.Not.Contain("detail-backdrop-outer"));
            Assert.That(uxml, Does.Not.Contain("detail-backdrop-core"));
            Assert.That(uss, Does.Match(@"(?s)\.detail-backdrop-blur\s*\{[^}]*left:\s*-80px;[^}]*right:\s*-80px;[^}]*top:\s*-80px;[^}]*bottom:\s*-80px;[^}]*detail_backdrop_blur\.png[^}]*-unity-slice-left:\s*112;[^}]*-unity-slice-right:\s*112;[^}]*-unity-slice-top:\s*112;[^}]*-unity-slice-bottom:\s*112;"),
                "深色衬底必须使用高斯透明度纹理并通过九宫格保持连续柔边，不能再用分层硬边矩形近似");
            Assert.That(uss, Does.Not.Contain(".detail-backdrop-outer"));
            Assert.That(uss, Does.Contain("font-size: 40px;"), "卡名必须使用更重的大标题层级");
            Assert.That(uss, Does.Contain("font-size: 50px;"), "三项属性必须使用更重的大数字层级");
            Assert.That(uss, Does.Contain("Cards/atp.png"));
            Assert.That(uss, Does.Contain("Cards/hp.png"));
            Assert.That(uss, Does.Contain("Cards/mtp.png"));
            Assert.That(uss, Does.Contain("-unity-font-style: bold;"), "卡名、副标题、数值和说明必须统一使用粗体");
            Assert.That(uss, Does.Contain("-unity-text-outline-width: 3px;"),
                "深色衬底上的文字仍需高对比描边保证清晰");

            var configuration = new GameConfiguration(ConfigPanelTests.Document());
            var session = new GameSession(configuration, new SeededRandom(17), 50);
            var snapshot = session.GetSnapshot();
            var hud = _host.AddComponent<HudView>();
            var detailLeft = _root.Q<VisualElement>("detail-left");
            var detailRight = _root.Q<VisualElement>("detail-right");

            SetField(hud, "_root", _root);
            SetField(hud, "_detailLeft", detailLeft);
            SetField(hud, "_detailRight", detailRight);
            foreach (var side in new[] { "Left", "Right" })
            {
                var prefix = "detail-" + side.ToLowerInvariant();
                SetField(hud, "_detail" + side + "Title", _root.Q<Label>(prefix + "-title"));
                SetField(hud, "_detail" + side + "Meta", _root.Q<Label>(prefix + "-meta"));
                SetField(hud, "_detail" + side + "Trait", _root.Q<Label>(prefix + "-trait"));
                SetField(hud, "_detail" + side + "Body", _root.Q<Label>(prefix + "-body"));
                SetField(hud, "_detail" + side + "Attack", _root.Q<Label>(prefix + "-attack"));
                SetField(hud, "_detail" + side + "Health", _root.Q<Label>(prefix + "-health"));
                SetField(hud, "_detail" + side + "Move", _root.Q<Label>(prefix + "-move"));
            }

            var noSelection = new Selection(SelectionKind.None);
            hud.Render(snapshot, configuration, noSelection, session.GetLegalActions(noSelection));
            Assert.That(detailLeft.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(detailRight.style.display.value, Is.EqualTo(DisplayStyle.None));

            var selectedCard = snapshot.Hand.First();
            var archetype = configuration.Cards[selectedCard.TypeId];
            var handSelection = new Selection(SelectionKind.Hand, selectedCard.Id);
            hud.Render(snapshot, configuration, handSelection, session.GetLegalActions(handSelection));
            Assert.That(detailLeft.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(detailRight.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(_root.Q<Label>("detail-left-title").text, Is.EqualTo(archetype.Name));
            var tierLabel = archetype.Tier == CardTier.Basic ? "JUNIOR"
                : archetype.Tier == CardTier.Intermediate ? "MIDDLE"
                : archetype.Tier == CardTier.Advanced ? "SENIOR" : "BOSS";
            Assert.That(_root.Q<Label>("detail-left-meta").text, Is.EqualTo($"我方·{tierLabel}"));
            Assert.That(_root.Q<Label>("detail-left-trait").text, Is.EqualTo("特性"));
            Assert.That(_root.Q<Label>("detail-left-attack").text, Is.EqualTo((archetype.Combat?.Attack ?? 0).ToString()));
            Assert.That(_root.Q<Label>("detail-left-health").text, Is.EqualTo((archetype.Combat?.Hp ?? 0).ToString()));
            Assert.That(_root.Q<Label>("detail-left-move").text, Is.EqualTo((archetype.Combat?.MoveDistance ?? 0).ToString()));

            hud.SetMenuVisible(true);
            Assert.That(detailLeft.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(detailRight.style.display.value, Is.EqualTo(DisplayStyle.None));
            hud.SetMenuVisible(false);
            Assert.That(detailLeft.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(detailRight.style.display.value, Is.EqualTo(DisplayStyle.None));
        }        /// <summary>
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
                var hand = new GameObject("HandRoot", typeof(RectTransform), typeof(UnityEngine.UI.HorizontalLayoutGroup));
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
                Assert.That(((RectTransform)hand.transform).anchoredPosition.y, Is.EqualTo(72f),
                    "手牌容器必须整体上移，让放大后的卡面越出操作区");
                var handLayout = hand.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
                Assert.That(handLayout.childAlignment, Is.EqualTo(TextAnchor.LowerCenter),
                    "放大的手牌必须底对齐，只允许上缘越出操作区");
                Assert.That(handLayout.spacing, Is.EqualTo(14f));
            }
            finally
            {
                Object.DestroyImmediate(canvasObject);
            }
        }

        /// <summary>
        /// 手牌运行时尺寸必须大于卡牌模板，并通过 LayoutElement 保持稳定布局。
        /// 输入：内存创建的 HandView 与 120×160 卡牌模板实例；
        /// 输出：断言手牌被放大为 180×240，RectTransform 与布局建议尺寸完全一致。
        /// 为什么需要：模板同时用于棋盘，手牌只能在 HandView 内单独放大；否则改模板会让棋盘卡牌也溢出格子。
        /// </summary>
        [Test]
        public void HandViewUsesLargeBottomOverflowCardSize()
        {
            var handObject = new GameObject("HandRoot", typeof(RectTransform), typeof(HandView));
            var cardObject = new GameObject("Card", typeof(RectTransform), typeof(UnityEngine.UI.Image),
                typeof(UnityEngine.UI.Button), typeof(Card));
            try
            {
                var cardRect = (RectTransform)cardObject.transform;
                cardRect.sizeDelta = new Vector2(120f, 160f);
                var hand = handObject.GetComponent<HandView>();
                InvokeMethod(hand, "EnsureLayoutSize", cardObject.GetComponent<Card>());

                Assert.That(cardRect.sizeDelta, Is.EqualTo(new Vector2(180f, 240f)));
                var layout = cardObject.GetComponent<UnityEngine.UI.LayoutElement>();
                Assert.That(layout, Is.Not.Null);
                Assert.That(layout.preferredWidth, Is.EqualTo(180f));
                Assert.That(layout.preferredHeight, Is.EqualTo(240f));
                Assert.That(layout.minWidth, Is.EqualTo(180f));
                Assert.That(layout.minHeight, Is.EqualTo(240f));
            }
            finally
            {
                Object.DestroyImmediate(cardObject);
                Object.DestroyImmediate(handObject);
            }
        }
        /// <summary>
        /// 底部抽牌堆与墓地只能使用 UI Toolkit 卡背显示，不能再生成旧 UGUI 矩形面板。
        /// 输入：真实 UXML/USS、场景生成器、GameBootstrap 与演示场景文本；
        /// 输出：卡背结构存在，且旧 CardZonesRoot、DrawPilePanel、GraveyardPanel 与运行绑定均不存在。
        /// 为什么需要：两套牌区同时启用会在操作区重复显示同一份计数，并遮挡卡背与手牌底板。
        /// </summary>
        [Test]
        public void BottomCardZonesUseOnlyToolkitCardBacks()
        {
            var uxml = File.ReadAllText(UxmlPath);
            var uss = File.ReadAllText(UssPath);
            var generator = File.ReadAllText("Assets/Spotlight/Editor/SpotlightMenu.cs");
            var bootstrap = File.ReadAllText("Assets/Spotlight/Runtime/Game/GameBootstrap.cs");
            var scene = File.ReadAllText("Assets/Spotlight/Scenes/Spotlight.unity");

            Assert.That(File.ReadAllLines(UxmlPath).Count(line => line.Contains("class=\"pile-panel ")), Is.EqualTo(2),
                "墓地与抽牌堆必须各保留一张 UI Toolkit 卡背");
            Assert.That(uss, Does.Match(@"(?s)\.pile-panel\s*\{[^}]*CardArt/backside\.png"),
                "两侧牌区必须共用卡背美术，不能退回纯色矩形");
            Assert.That(uss, Does.Match(@"(?s)\.pile-panel\s*\{[^}]*bottom:\s*42px;[^}]*width:\s*165px;[^}]*height:\s*244px;")
                .And.Match(@"(?s)\.pile-left\s*\{[^}]*left:\s*80px;")
                .And.Match(@"(?s)\.pile-right\s*\{[^}]*right:\s*80px;"),
                "两侧牌堆必须放大、整体上移并同时向屏幕中间收拢");
            Assert.That(generator, Does.Not.Contain("CardZonesRoot").And.Not.Contain("DrawPilePanel").And.Not.Contain("GraveyardPanel"),
                "场景生成器不能重新创建旧 UGUI 牌区");
            Assert.That(bootstrap, Does.Not.Contain("CardZonesView").And.Not.Contain("_cardZonesView"),
                "GameBootstrap 不应继续绑定或刷新旧 UGUI 牌区");
            Assert.That(scene, Does.Not.Contain("m_Name: CardZonesRoot").And.Not.Contain("m_Name: DrawPilePanel").And.Not.Contain("m_Name: GraveyardPanel"),
                "演示场景不能残留会与卡背重复显示的旧牌区对象");
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
