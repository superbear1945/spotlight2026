// 抽牌堆与墓地视图的 EditMode 测试：通过公开命令推进真实对局，再验证 uGUI 只读映射。
using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Spotlight.Tests
{
    /// <summary>
    /// 卡牌区域视图回归测试。
    /// 输入：内存对局快照与代码搭建的最小 uGUI 层级；输出：数量、弃牌提示和射线契约断言。
    /// 为什么使用 EditMode：视图只映射快照文本和颜色，不依赖真实帧循环，可以在 PR 的快速测试阶段稳定覆盖。
    /// </summary>
    public class CardZonesViewTests
    {
        /// <summary>
        /// 抽牌堆与墓地必须始终显示规则快照中的数量；进入弃牌阶段后墓地需要高亮，
        /// 且所有显示图形都不得抢走手牌点击。
        /// </summary>
        [Test]
        public void RenderMirrorsSnapshotAndHighlightsDiscardPhaseWithoutBlockingInput()
        {
            var document = ConfigPanelTests.Document();
            document.rules[0].drawCount = 5;
            document.rules[0].handLimit = 2;
            document.rules[0].playerControlsBoss = false;
            var session = new GameSession(new GameConfiguration(document), new SeededRandom(29), 50);

            using (var fixture = new CardZonesFixture())
            {
                var initial = session.GetSnapshot();
                fixture.View.Render(initial);
                Assert.That(fixture.View.DrawPileCount, Is.EqualTo(initial.DrawPile.Count));
                Assert.That(fixture.View.GraveyardCount, Is.EqualTo(initial.Graveyard.Count));
                Assert.That(TextOf(fixture.DrawPileCount), Is.EqualTo(initial.DrawPile.Count.ToString()));
                Assert.That(TextOf(fixture.GraveyardCount), Is.EqualTo(initial.Graveyard.Count.ToString()));
                Assert.That(fixture.View.IsDiscardPhase, Is.False);
                Assert.That(TextOf(fixture.GraveyardHint), Is.EqualTo("已使用卡牌"));

                var endTurn = session.Execute(new GameCommand(CommandKind.EndTurn, Side.Player));
                Assert.That(endTurn.Success, Is.True, endTurn.Error);
                var discard = session.GetSnapshot();
                Assert.That(discard.Phase, Is.EqualTo(MatchPhase.Discard), "测试夹具没有进入弃牌阶段");
                fixture.View.Render(discard);
                Assert.That(fixture.View.IsDiscardPhase, Is.True);
                Assert.That(TextOf(fixture.GraveyardHint), Is.EqualTo("点击手牌弃置"));
                Assert.That(fixture.GraveyardPanel.color, Is.EqualTo(new Color(0.55f, 0.22f, 0.12f, 0.98f)));

                var discardResult = session.Execute(new GameCommand(CommandKind.Discard, Side.Player, discard.Hand[0].Id));
                Assert.That(discardResult.Success, Is.True, discardResult.Error);
                var afterDiscard = session.GetSnapshot();
                fixture.View.Render(afterDiscard);
                Assert.That(fixture.View.GraveyardCount, Is.EqualTo(afterDiscard.Graveyard.Count));
                Assert.That(TextOf(fixture.GraveyardCount), Is.EqualTo(afterDiscard.Graveyard.Count.ToString()));
                Assert.That(fixture.Root.GetComponentsInChildren<Graphic>(true).All(graphic => !graphic.raycastTarget), Is.True,
                    "牌区显示不应阻挡手牌或棋盘点击");
            }
        }

        /// <summary>
        /// 读取一个运行时 TMP 组件的文本。
        /// 输入：通过反射创建的文本组件；输出：其公开 text 属性值。
        /// 为什么使用反射：EditMode 测试程序集不直接引用 TextMeshPro，避免为单个视图测试扩大程序集依赖。
        /// </summary>
        static string TextOf(Component text)
        {
            var property = text.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null, text.GetType().Name + " 缺少 text 属性");
            return property.GetValue(text) as string;
        }

        /// <summary>
        /// 卡牌区域视图的最小测试夹具。
        /// 输入：构造时无外部资源；输出：已装配两个面板和三个 TMP 文本的 <see cref="CardZonesView"/>。
        /// 为什么需要代码夹具：测试不依赖演示场景或字体资产，能精确验证序列化引用与渲染契约。
        /// </summary>
        sealed class CardZonesFixture : IDisposable
        {
            /// <summary>夹具根节点，销毁它即可清理所有子对象。</summary>
            public GameObject Root { get; }
            /// <summary>被测视图。</summary>
            public CardZonesView View { get; }
            /// <summary>抽牌堆数量文本。</summary>
            public Component DrawPileCount { get; }
            /// <summary>墓地数量文本。</summary>
            public Component GraveyardCount { get; }
            /// <summary>墓地状态提示文本。</summary>
            public Component GraveyardHint { get; }
            /// <summary>墓地背景，用于验证弃牌阶段颜色。</summary>
            public Image GraveyardPanel { get; }

            /// <summary>
            /// 创建并装配最小 uGUI 层级。
            /// 输入：无；输出：所有字段均可立即传入 <see cref="CardZonesView.Render"/>。
            /// </summary>
            public CardZonesFixture()
            {
                Root = new GameObject("CardZonesRoot", typeof(RectTransform), typeof(CardZonesView));
                View = Root.GetComponent<CardZonesView>();

                var drawObject = new GameObject("DrawPilePanel", typeof(RectTransform), typeof(Image));
                drawObject.transform.SetParent(Root.transform, false);
                var drawPanel = drawObject.GetComponent<Image>();
                DrawPileCount = CreateText("DrawPileCount", drawObject.transform);

                var graveyardObject = new GameObject("GraveyardPanel", typeof(RectTransform), typeof(Image));
                graveyardObject.transform.SetParent(Root.transform, false);
                GraveyardPanel = graveyardObject.GetComponent<Image>();
                GraveyardCount = CreateText("GraveyardCount", graveyardObject.transform);
                GraveyardHint = CreateText("GraveyardHint", graveyardObject.transform);

                SetField(View, "_drawPilePanel", drawPanel);
                SetField(View, "_drawPileCountText", DrawPileCount);
                SetField(View, "_graveyardPanel", GraveyardPanel);
                SetField(View, "_graveyardCountText", GraveyardCount);
                SetField(View, "_graveyardHintText", GraveyardHint);
            }

            /// <summary>
            /// 创建测试用 TMP 文本。
            /// 输入：对象名与父节点；输出：运行时加载的 TMP 文本组件。
            /// 为什么通过类型名创建：保持测试程序集不直接引用 TextMeshPro，同时仍用真实组件验证序列化字段。
            /// </summary>
            static Component CreateText(string name, Transform parent)
            {
                var textType = Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro", true);
                var textObject = new GameObject(name, typeof(RectTransform));
                textObject.transform.SetParent(parent, false);
                return textObject.AddComponent(textType);
            }

            /// <summary>
            /// 写入被测组件的私有序列化字段。
            /// 输入：目标对象、字段名和值；输出：无，字段缺失时立即使测试失败。
            /// </summary>
            static void SetField(object target, string fieldName, object value)
            {
                var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(field, Is.Not.Null, target.GetType().Name + " 缺少字段 " + fieldName);
                field.SetValue(target, value);
            }

            /// <summary>销毁测试层级。输入：无；输出：释放本用例创建的全部 Unity 对象。</summary>
            public void Dispose()
            {
                if (Root != null) UnityEngine.Object.DestroyImmediate(Root);
            }
        }
    }
}
