// 携带选择（选卡）流程的 PlayMode 测试。
// 为什么必须在 PlayMode 验证：选卡是“启动即弹窗 → 确认后才建对局 → 菜单里换卡会重开”的时序流程，
// 涉及真实场景装配、UI Toolkit 显示状态与 GameBootstrap 的输入闸门，EditMode 无法覆盖。
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Spotlight;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Spotlight.Tests
{
    /// <summary>
    /// 覆盖启动必经选卡、每阶级下限阻止确认、菜单选卡的二次确认与重开、以及抽牌堆三行计数。
    /// 输入：演示场景（真实配置资产与视图）；输出：界面显隐、会话创建与抽牌堆张数断言。
    /// </summary>
    public class DeckSelectionPlayModeTests
    {
        /// <summary>演示场景路径（与 SpotlightMenu.DemoScenePath 一致）。</summary>
        const string ScenePath = "Assets/Spotlight/Scenes/Spotlight.unity";

        /// <summary>本类要验证“启动弹选卡”，因此每个用例前都把测试开关恢复为 false。</summary>
        [SetUp]
        public void SetUp() => GameBootstrap.SuppressDeckSelectionOnStart = false;

        /// <summary>用例结束后恢复为“其它 PlayMode 用例默认跳选卡”，避免开关泄漏到别的测试类。</summary>
        [TearDown]
        public void TearDown() => GameBootstrap.SuppressDeckSelectionOnStart = true;

        /// <summary>
        /// 加载演示场景时用到的引用集合。
        /// 为什么抽成类：协程方法不能带 out/ref 参数，而每个用例都需要引导器与 HUD，
        /// 用一个可填充的上下文对象既避免重复 LoadScene，也避免到处 FindObjectOfType。
        /// </summary>
        sealed class Context
        {
            /// <summary>对局引导器。</summary>
            public GameBootstrap Bootstrap;
            /// <summary>HUD 视图（含携带选择面板）。</summary>
            public HudView Hud;
        }

        /// <summary>
        /// 加载演示场景并填充上下文。
        /// 输入：待填充的上下文；输出：协程，返回时场景已 Start 完毕（选卡面板已弹出）。
        /// </summary>
        static IEnumerator Load(Context context)
        {
            SceneManager.LoadScene(ScenePath);
            yield return null;
            yield return null;
            context.Bootstrap = Object.FindObjectOfType<GameBootstrap>();
            context.Hud = Object.FindObjectOfType<HudView>();
            Assert.That(context.Bootstrap, Is.Not.Null, "演示场景未装配 GameBootstrap");
            Assert.That(context.Hud, Is.Not.Null, "演示场景未装配 HudView");
        }

        /// <summary>
        /// 调用私有方法（复用既有 PlayMode 测试的反射约定，避免为测试开放生产 API）。
        /// 输入：目标对象、方法名与参数；输出：方法返回值（无返回值时为 null）。
        /// </summary>
        static object Invoke(object target, string method, params object[] args)
        {
            var info = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(info, Is.Not.Null, $"{target.GetType().Name} 缺少方法 {method}");
            return info.Invoke(target, args);
        }

        /// <summary>按元素名取显示状态，缺失时明确报错而不是空引用。</summary>
        static DisplayStyle Display(HudView hud, string name)
        {
            var element = hud.Root.Q<VisualElement>(name);
            Assert.That(element, Is.Not.Null, $"UXML 缺少 {name}");
            return element.style.display.value;
        }

        [UnityTest]
        public IEnumerator StartupShowsDeckSelectionBeforeAnyMatch()
        {
            var context = new Context();
            yield return Load(context);
            var bootstrap = context.Bootstrap;
            var hud = context.Hud;
            Assert.That(bootstrap.Session, Is.Null, "启动时必须先进入携带选择，不能直接建对局");
            Assert.That(hud.DeckSelectionPanel, Is.Not.Null, "HUD 没有创建携带选择面板");
            Assert.That(hud.DeckSelectionPanel.IsVisible, Is.True, "选卡面板应在启动时自动打开");
            Assert.That(Display(hud, "deck-select-mask"), Is.EqualTo(DisplayStyle.Flex), "选卡遮罩应可见");
            Assert.That(Display(hud, "modal-overlay"), Is.EqualTo(DisplayStyle.None), "选卡打开时不应叠加通用模态遮罩");

            var apply = hud.Root.Q<Button>("deck-select-apply-button");
            Assert.That(apply, Is.Not.Null, "UXML 缺少 deck-select-apply-button");
            Assert.That(apply.enabledSelf, Is.True, "默认选卡合法，确认按钮应可用");
            var preview = hud.Root.Q<Label>("deck-select-preview");
            Assert.That(preview, Is.Not.Null, "UXML 缺少 deck-select-preview");
            Assert.That(preview.text, Does.Contain("抽牌堆"), "底部应显示抽牌堆张数预览");

            // 确认 → 用选卡结果开局。
            Invoke(hud.DeckSelectionPanel, "OnApplyClicked");
            yield return null;
            Assert.That(bootstrap.Session, Is.Not.Null, "确认选卡后应建立对局");
            Assert.That(Display(hud, "deck-select-mask"), Is.EqualTo(DisplayStyle.None), "开局后应关闭选卡面板");
            var snapshot = bootstrap.Session.GetSnapshot();
            Assert.That(snapshot.DrawPile.Count + snapshot.Hand.Count, Is.EqualTo(bootstrap.DeckSelection.Configuration.DeckConfig.DrawPileSize),
                "开局后抽牌堆与手牌之和应等于 drawPileSize");
        }

        [UnityTest]
        public IEnumerator EmptyTierSelectionBlocksConfirm()
        {
            var emptyTierContext = new Context();
            yield return Load(emptyTierContext);
            var hud = emptyTierContext.Hud;
            var panel = hud.DeckSelectionPanel;
            var state = panel.State;
            Assert.That(state, Is.Not.Null, "选卡面板没有状态");
            var apply = hud.Root.Q<Button>("deck-select-apply-button");
            var error = hud.Root.Q<Label>("deck-select-error");

            // 把初级全部取消：违反“每阶级至少 1 种”，确认按钮必须不可用并给出原因。
            // 注意只取消“已选中”的卡：初级池有 6 种但上限是 5，全量 Toggle 会把唯一没选中的那张重新勾上。
            foreach (var card in state.Pool(CardTier.Basic).Where(c => state.IsSelected(c.Id)).ToArray()) state.Toggle(card.Id);
            panel.Render();
            Assert.That(state.SelectedCount(CardTier.Basic), Is.EqualTo(0));
            Assert.That(apply.enabledSelf, Is.False, "缺初级卡时不应允许确认");
            Assert.That(error.text, Does.Contain("初级"), "应给出“请至少选择一种初级卡”的可读原因");
            Assert.That(error.style.display.value, Is.EqualTo(DisplayStyle.Flex), "错误提示应可见");

            // 补回一种即可确认，且该卡种拿走全部初级张数。
            state.Toggle(state.Pool(CardTier.Basic).First().Id);
            panel.Render();
            Assert.That(apply.enabledSelf, Is.True, "补足每阶级 1 种后应允许确认");
            Assert.That(error.style.display.value, Is.EqualTo(DisplayStyle.None), "合法后应隐藏错误提示");
        }

        [UnityTest]
        public IEnumerator MenuDeckSelectAsksConfirmationAndRestartsOnApply()
        {
            var context = new Context();
            yield return Load(context);
            var bootstrap = context.Bootstrap;
            var hud = context.Hud;
            Invoke(hud.DeckSelectionPanel, "OnApplyClicked");
            yield return null;
            var running = bootstrap.Session;
            Assert.That(running, Is.Not.Null, "确认选卡后应建立对局");

            // 进菜单 → 点“选卡” → 先看到二次确认，而不是直接进选卡面板。
            Invoke(bootstrap, "OnMenuRequested");
            yield return null;
            Assert.That(Display(hud, "menu-overlay"), Is.EqualTo(DisplayStyle.Flex), "应显示齿轮菜单");
            Invoke(bootstrap, "OnMenuDeckSelectRequested");
            yield return null;
            Assert.That(Display(hud, "deck-select-confirm-overlay"), Is.EqualTo(DisplayStyle.Flex), "菜单选卡应先弹二次确认");
            Assert.That(Display(hud, "deck-select-mask"), Is.EqualTo(DisplayStyle.None), "二次确认阶段不应显示选卡面板");

            // “继续当前对局” → 回到对局，会话不变。
            Invoke(hud.DeckSelectionPanel, "OnConfirmNoClicked");
            yield return null;
            Assert.That(Display(hud, "deck-select-confirm-overlay"), Is.EqualTo(DisplayStyle.None), "继续后应关闭确认层");
            Assert.That(bootstrap.Session, Is.SameAs(running), "取消选卡不应重开对局");

            // 再来一次，确认进入选卡 → 进面板 → 取消 → 仍是对局。
            Invoke(bootstrap, "OnMenuRequested");
            Invoke(bootstrap, "OnMenuDeckSelectRequested");
            yield return null;
            Invoke(hud.DeckSelectionPanel, "OnConfirmYesClicked");
            yield return null;
            Assert.That(Display(hud, "deck-select-mask"), Is.EqualTo(DisplayStyle.Flex), "确认后应显示选卡面板");
            Assert.That(hud.Root.Q<Label>("deck-select-title").text, Does.Contain("重开"), "从菜单进入时标题应提示会重开对局");
            Invoke(hud.DeckSelectionPanel, "OnCancelClicked");
            yield return null;
            Assert.That(Display(hud, "deck-select-mask"), Is.EqualTo(DisplayStyle.None), "取消后应关闭选卡面板");
            Assert.That(bootstrap.Session, Is.SameAs(running), "取消选卡不应重开对局");

            // 真正确认 → 用新卡组重开，会话对象被替换。
            Invoke(bootstrap, "OnMenuDeckSelectRequested");
            yield return null;
            Invoke(hud.DeckSelectionPanel, "OnConfirmYesClicked");
            yield return null;
            Invoke(hud.DeckSelectionPanel, "OnApplyClicked");
            yield return null;
            Assert.That(bootstrap.Session, Is.Not.Null);
            Assert.That(bootstrap.Session, Is.Not.SameAs(running), "确认选卡后应重开对局");
            var snapshot = bootstrap.Session.GetSnapshot();
            Assert.That(snapshot.Round, Is.EqualTo(1L), "重开后回合数应回到第 1 回合");
        }

        [UnityTest]
        public IEnumerator DrawPileShowsRemainingCardsByTier()
        {
            var context = new Context();
            yield return Load(context);
            var bootstrap = context.Bootstrap;
            var hud = context.Hud;
            Invoke(hud.DeckSelectionPanel, "OnApplyClicked");
            yield return null;
            var session = bootstrap.Session;
            var configuration = session.Configuration;
            var snapshot = session.GetSnapshot();

            var basic = hud.Root.Q<Label>("draw-count-basic");
            var intermediate = hud.Root.Q<Label>("draw-count-intermediate");
            var advanced = hud.Root.Q<Label>("draw-count-advanced");
            Assert.That(basic, Is.Not.Null, "UXML 缺少 draw-count-basic");
            Assert.That(intermediate, Is.Not.Null, "UXML 缺少 draw-count-intermediate");
            Assert.That(advanced, Is.Not.Null, "UXML 缺少 draw-count-advanced");

            var expected = new[] { CardTier.Basic, CardTier.Intermediate, CardTier.Advanced }
                .ToDictionary(tier => tier, tier => snapshot.DrawPile.Count(card => configuration.FindCard(card.TypeId)?.Tier == tier));
            Assert.That(basic.text, Is.EqualTo($"初级 {expected[CardTier.Basic]}"), "初级剩余张数应与快照一致");
            Assert.That(intermediate.text, Is.EqualTo($"中级 {expected[CardTier.Intermediate]}"), "中级剩余张数应与快照一致");
            Assert.That(advanced.text, Is.EqualTo($"高级 {expected[CardTier.Advanced]}"), "高级剩余张数应与快照一致");
            Assert.That(expected.Values.Sum(), Is.EqualTo(snapshot.DrawPile.Count), "三行之和应等于抽牌堆剩余张数");
        }
    }
}
