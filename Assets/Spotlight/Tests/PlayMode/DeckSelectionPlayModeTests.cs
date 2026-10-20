// 携带选择（选卡）流程的 PlayMode 测试。
// 为什么必须在 PlayMode 验证：选卡是“启动即弹窗 → 确认后才建对局 → 菜单里换卡会重开”的时序流程，
// 涉及真实场景装配、UI Toolkit 显示状态与 GameBootstrap 的输入闸门，EditMode 无法覆盖。
using System.Collections;
using System.Collections.Generic;
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

        /// <summary>
        /// 选卡界面必须能一眼分辨阶级：分成三组（初/中/高），每组带阶级徽章、组内只放该阶级卡种，
        /// 行内还有短标（初/中/高），且三个分组的左侧竖条颜色互不相同。
        /// 输入：演示场景启动后的选卡面板；输出：分组数量、徽章文本、组内行数与配色断言。
        /// </summary>
        [UnityTest]
        public IEnumerator SelectionGroupsCardsByTierWithDistinctColors()
        {
            var context = new Context();
            yield return Load(context);
            var hud = context.Hud;
            var panel = hud.DeckSelectionPanel;
            yield return null;

            var sections = hud.Root.Query<VisualElement>(className: "deck-tier").ToList();
            Assert.That(sections.Count, Is.EqualTo(3), "选卡界面应按初级/中级/高级分成三组");
            var expected = new[]
            {
                (Class: "tier-basic", Tier: CardTier.Basic),
                (Class: "tier-intermediate", Tier: CardTier.Intermediate),
                (Class: "tier-advanced", Tier: CardTier.Advanced)
            };
            var borderColors = new List<Color>();
            for (var i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                Assert.That(section.ClassListContains(expected[i].Class), Is.True, $"第 {i + 1} 组缺少类 {expected[i].Class}");
                var badge = section.Q<Label>(className: "deck-tier-badge");
                Assert.That(badge, Is.Not.Null, "每个分组都应有阶级徽章");
                Assert.That(badge.text, Is.EqualTo(CardText.TierLabel(expected[i].Tier)), "徽章文本应为阶级中文名");

                var rows = section.Query<VisualElement>(className: "deck-card-row").ToList();
                Assert.That(rows.Count, Is.EqualTo(panel.State.Pool(expected[i].Tier).Count()),
                    $"{expected[i].Class} 组内应只包含该阶级的卡种");
                foreach (var row in rows)
                {
                    var tag = row.Q<Label>(className: "deck-card-tier-tag");
                    Assert.That(tag, Is.Not.Null, "每行都应带阶级短标");
                    Assert.That(tag.text.Length, Is.EqualTo(1), "短标是单字（初/中/高）");
                }
                borderColors.Add(section.resolvedStyle.borderLeftColor);
            }
            Assert.That(borderColors.Distinct().Count(), Is.EqualTo(3), "三个阶级的分组竖条颜色应互不相同");
        }

        /// <summary>
        /// 选卡界面每行必须能直接读到属性与特性，而不是只有卡名：
        /// 属性行必须与 CardText.StatLine 同源，特性短标必须与 CardText.TraitTags 逐项一致。
        /// 输入：演示场景启动后的选卡面板；输出：逐行属性/短标断言与“至少一张卡带特性”的守卫。
        /// </summary>
        [UnityTest]
        public IEnumerator SelectionRowsShowStatsAndTraitChips()
        {
            var context = new Context();
            yield return Load(context);
            var hud = context.Hud;
            var configuration = hud.DeckSelectionPanel.State.Configuration;
            var rows = hud.Root.Query<VisualElement>(className: "deck-card-row").ToList();
            Assert.That(rows, Is.Not.Empty, "选卡界面没有任何卡种行");
            var rowsWithTraits = 0;
            foreach (var row in rows)
            {
                var id = row.userData as string;
                Assert.That(id, Is.Not.Null, "每个卡种行都应记录卡种 ID，供详情区定位");
                var archetype = configuration.FindCard(id);
                Assert.That(archetype, Is.Not.Null, $"卡种 {id} 不在配置里");

                var stats = row.Q<Label>(className: "deck-card-stats");
                Assert.That(stats, Is.Not.Null, "每行都应显示属性行（生命/攻/程/移/产/耗）");
                Assert.That(stats.text, Is.EqualTo(CardText.StatLine(archetype)), "行内属性必须与对局卡面同源");
                Assert.That(stats.text, Does.Contain("生命"), "属性行至少要能被读懂");

                var chips = row.Query<Label>(className: "deck-card-trait").ToList().Select(chip => chip.text);
                Assert.That(chips, Is.EqualTo(CardText.TraitTags(archetype)), $"{archetype.Name} 的特性短标应与配置一致");
                if (chips.Any()) rowsWithTraits++;
            }
            Assert.That(rowsWithTraits, Is.GreaterThan(0), "正式配置里应至少有一张带特性的卡，否则界面等于没写");
        }

        /// <summary>
        /// 右侧详情区必须能把“当前查看的卡种”完整展开：名称 / 阶级与分类 / 属性 / 特性短标 / 规则正文，
        /// 并在同一时刻只高亮那一行；切换查看目标时内容跟随。
        /// 输入：启动后直接调用 ShowDetail（与悬停/点击行共用同一个入口）；输出：详情区文本与高亮行断言。
        /// </summary>
        [UnityTest]
        public IEnumerator DetailPaneExplainsPreviewedCard()
        {
            var context = new Context();
            yield return Load(context);
            var hud = context.Hud;
            var panel = hud.DeckSelectionPanel;
            var configuration = panel.State.Configuration;
            var target = configuration.CardOrder.FirstOrDefault(card => card != null && CardText.TraitTags(card).Count > 0);
            Assert.That(target, Is.Not.Null, "正式配置里应至少有一张带特性的卡，否则详情区无法展示特性");

            panel.ShowDetail(target.Id);
            yield return null;

            Assert.That(panel.DetailCardId, Is.EqualTo(target.Id));
            Assert.That(hud.Root.Q<Label>("deck-select-detail-title").text, Is.EqualTo(target.Name), "详情标题应为卡名");
            Assert.That(hud.Root.Q<Label>("deck-select-detail-meta").text,
                Is.EqualTo($"{CardText.TierLabel(target.Tier)} · {CardText.CategoryLabel(target.Category)}"), "详情标题下应是阶级与分类");
            Assert.That(hud.Root.Q<Label>("deck-select-detail-stats").text, Is.EqualTo(CardText.StatLine(target)),
                "详情区属性行必须与行内文本一致，不能两套数字");

            var detailTraits = hud.Root.Q<VisualElement>("deck-select-detail-traits");
            Assert.That(detailTraits, Is.Not.Null, "UXML 缺少 deck-select-detail-traits");
            var chips = detailTraits.Query<Label>(className: "deck-card-trait").ToList().Select(chip => chip.text);
            Assert.That(chips, Is.EqualTo(CardText.TraitTags(target)), "详情区特性短标应与配置一致");

            var body = hud.Root.Q<Label>("deck-select-detail-body").text;
            var expectedBody = CardText.DetailBody(target);
            Assert.That(body, Is.EqualTo(expectedBody), "详情正文应展开为完整规则文本");
            Assert.That(body, Does.Not.Contain("生命 "), "详情正文不再重复属性行");

            var inspected = hud.Root.Query<VisualElement>(className: "deck-card-row").ToList()
                .Where(row => row.ClassListContains("inspected")).ToList();
            Assert.That(inspected.Count, Is.EqualTo(1), "同一时刻只应高亮正在查看的一行");
            Assert.That(inspected[0].userData as string, Is.EqualTo(target.Id));

            // 切换到另一张卡种（这里用表格第一张），详情区必须跟着换，而不是停在上一张。
            var other = configuration.CardOrder.First(card => card != null && card.Id != target.Id);
            panel.ShowDetail(other.Id);
            yield return null;
            Assert.That(hud.Root.Q<Label>("deck-select-detail-title").text, Is.EqualTo(other.Name));
            Assert.That(panel.DetailCardId, Is.EqualTo(other.Id));
            var rechecked = hud.Root.Query<VisualElement>(className: "deck-card-row").ToList()
                .Where(row => row.ClassListContains("inspected")).ToList();
            Assert.That(rechecked.Count, Is.EqualTo(1), "切换后旧行应取消高亮");
            Assert.That(rechecked[0].userData as string, Is.EqualTo(other.Id));
        }

        /// <summary>
        /// 资源卡的详情必须直接给出产出与维持费：玩家选卡时最需要比较的就是这两项。
        /// 输入：选卡面板里的第一张资源卡；输出：详情区属性文本包含“产 / 耗”的断言。
        /// </summary>
        [UnityTest]
        public IEnumerator DetailPaneShowsResourceOutputAndUpkeep()
        {
            var context = new Context();
            yield return Load(context);
            var hud = context.Hud;
            var panel = hud.DeckSelectionPanel;
            var resource = panel.State.Configuration.CardOrder
                .FirstOrDefault(card => card != null && card.Category == CardCategory.Resource);
            Assert.That(resource, Is.Not.Null, "正式配置里应有资源卡");

            panel.ShowDetail(resource.Id);
            yield return null;
            var stats = hud.Root.Q<Label>("deck-select-detail-stats").text;
            Assert.That(stats, Does.Contain("产 ").And.Contain("耗 "), "资源卡必须显示产出与维持费");
            Assert.That(stats, Is.EqualTo(CardText.StatLine(resource)), "详情属性应与 CardText 同源");
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
        public IEnumerator DrawPileShowsTotalAndTierBreakdown()
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

            var total = hud.Root.Q<Label>("draw-count");
            var tiers = hud.Root.Q<Label>("draw-count-tiers");
            Assert.That(total, Is.Not.Null, "UXML 缺少 draw-count");
            Assert.That(tiers, Is.Not.Null, "UXML 缺少 draw-count-tiers");

            var expected = new[] { CardTier.Basic, CardTier.Intermediate, CardTier.Advanced }
                .ToDictionary(tier => tier, tier => snapshot.DrawPile.Count(card => configuration.FindCard(card.TypeId)?.Tier == tier));
            Assert.That(total.text, Is.EqualTo(snapshot.DrawPile.Count.ToString()), "牌堆正中应显示剩余总张数");
            Assert.That(tiers.text,
                Is.EqualTo($"高 {expected[CardTier.Advanced]} · 中 {expected[CardTier.Intermediate]} · 初 {expected[CardTier.Basic]}"),
                "底部阶级小字应与快照一致");
            Assert.That(expected.Values.Sum(), Is.EqualTo(snapshot.DrawPile.Count), "三个阶级之和应等于抽牌堆剩余张数");
        }
    }
}
