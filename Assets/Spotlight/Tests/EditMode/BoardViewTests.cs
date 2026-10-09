// 棋盘视图契约测试（EditMode）：不需要进入 Play 模式即可验证“界面与规则一致”的不变式。
// 为什么放在 EditMode：位置校准与点击穿透都是纯视图逻辑，用真实 GameObject 就能覆盖，
// 因此它们可以随现有的 GitHub Action 流水线在每次 PR 上运行。
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Spotlight;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Spotlight.Tests
{
    /// <summary>
    /// 棋盘卡牌定位与点击穿透的回归测试。
    /// 输入：内存构造的配置与对局、代码搭建的 BoardView；输出：NUnit 断言结果。
    /// </summary>
    public class BoardViewTests
    {
        /// <summary>
        /// 构造一份最小可用配置：士兵可以移动两格，玩家先手且由玩家控制 Boss，避免 AI 干扰断言。
        /// </summary>
        static ConfigDocument Document()
        {
            return new ConfigDocument
            {
                cards = new[]
                {
                    new CardRow { id = "miner", name = "矿机", category = "resource", hp = 4, produce = 1, moveDistance = 0, resourceComponent = true, viewKey = "default" },
                    new CardRow { id = "soldier", name = "士兵", category = "attack", hp = 5, atk = 2, range = 1, moveDistance = 2, viewKey = "default" },
                    new CardRow { id = "boss", name = "敌兵", category = "attack", hp = 3, atk = 1, range = 1, moveDistance = 1, viewKey = "default" }
                },
                deck = new[] { new DeckRow { cardType = "soldier", count = 4 }, new DeckRow { cardType = "miner", count = 2 } },
                rules = new[]
                {
                    new RulesRow
                    {
                        rows = 5, columns = 10, homeRegionWidth = 2, playerPlays = 20, bossPlays = 1, drawCount = 6, handLimit = 30,
                        initialResources = 0, bossRespawnDelay = 3, playerSide = "left", firstSide = "player", bossCardType = "boss",
                        playerControlsBoss = true, summoningSickness = false, adjacentSwap = true
                    }
                },
                homes = new[] { new HomeRow { owner = "player", hp = 10, produce = 2 }, new HomeRow { owner = "boss", hp = 10, produce = 0 } },
                bossSlots = new[]
                {
                    new BossSlotRow { id = "a", rowFraction = 0, edgeOffset = 1 },
                    new BossSlotRow { id = "b", rowFraction = 0.5f, edgeOffset = 1 },
                    new BossSlotRow { id = "c", rowFraction = 1, edgeOffset = 1 }
                },
                presentation = new[]
                {
                    new PresentationRow
                    {
                        referenceWidth = 1920, referenceHeight = 1080, fontSize = 18, logLimit = 50, moveSeconds = 0.2f,
                        feedbackSeconds = 0.1f, selectedScale = 1.05f, playerColor = "#3366FF", bossColor = "#FF3333",
                        resourceColor = "#33AA66", attackColor = "#EE8833", specialColor = "#AA55FF", moveColor = "#33AAFF",
                        attackTargetColor = "#FF3333", swapColor = "#AA33FF", deployColor = "#33FF99"
                    }
                }
            };
        }

        /// <summary>
        /// 移动单位后，卡牌必须挂到目标格并且偏移归零；被动画中断留下的偏移必须在下一次渲染自动纠正。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么需要：这正是“日志说移动了、棋子却还在原地”的直接防线。
        /// </summary>
        [Test]
        public void MoveKeepsCardInTargetCellAndClearsStaleOffset()
        {
            var session = new GameSession(new GameConfiguration(Document()), new SeededRandom(7), 50);
            using (var fixture = new BoardFixture())
            {
                fixture.Build(session.Configuration);

                var record = session.GetSnapshot().Hand.First(r => r.TypeId == "soldier");
                var deployCell = new Cell(0, 1);
                Assert.That(session.Execute(new GameCommand(CommandKind.Deploy, Side.Player, record.Id, deployCell)).Success, Is.True);
                fixture.Render(session);

                var unit = session.GetSnapshot().Units.Single(u => u.Source.Id == record.Id);
                var view = fixture.Board.GetCard(unit.Id);
                Assert.That(view, Is.Not.Null, "部署后棋盘上没有生成卡牌");
                var rect = (RectTransform)view.transform;
                Assert.That(view.transform.parent, Is.EqualTo(fixture.Board.CellRect(deployCell)), "部署后卡牌没有挂在部署格");
                Assert.That(rect.anchoredPosition, Is.EqualTo(Vector2.zero), "部署后卡牌不在格心");

                var target = new Cell(0, 3);
                Assert.That(session.Execute(new GameCommand(CommandKind.Move, Side.Player, unit.Id, target)).Success, Is.True);
                fixture.Render(session);

                Assert.That(view.transform.parent, Is.EqualTo(fixture.Board.CellRect(target)), "移动后卡牌没有挂到目标格");
                Assert.That(rect.anchoredPosition, Is.EqualTo(Vector2.zero), "移动后卡牌不在格心");

                // 模拟“移动动画被中断、补间没有跑完”留下的偏移：下一次渲染必须自动校准。
                rect.anchoredPosition = new Vector2(87f, -42f);
                fixture.Render(session);
                Assert.That(rect.anchoredPosition, Is.EqualTo(Vector2.zero), "残留偏移没有被下一次渲染纠正，卡牌会看起来停在旧格");
            }
        }

        /// <summary>
        /// 棋盘上的卡牌不能挡住格子的点击。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么需要：卡牌模板上的 Image/文本默认参与 UGUI 射线检测，
        /// 会抢走本该落到 BoardCellView 上的点击，导致“点棋子没有任何反应”。
        /// </summary>
        [Test]
        public void BoardCardsDoNotSwallowClicks()
        {
            var session = new GameSession(new GameConfiguration(Document()), new SeededRandom(11), 50);
            using (var fixture = new BoardFixture())
            {
                fixture.Build(session.Configuration);

                var record = session.GetSnapshot().Hand.First(r => r.TypeId == "soldier");
                var deployCell = new Cell(0, 1);
                Assert.That(session.Execute(new GameCommand(CommandKind.Deploy, Side.Player, record.Id, deployCell)).Success, Is.True);
                fixture.Render(session);

                var unit = session.GetSnapshot().Units.Single(u => u.Source.Id == record.Id);
                var view = fixture.Board.GetCard(unit.Id);
                Assert.That(view, Is.Not.Null, "部署后棋盘上没有生成卡牌");

                var blockers = view.GetComponentsInChildren<Graphic>(true).Where(g => g.raycastTarget).ToArray();
                Assert.That(blockers, Is.Empty,
                    "棋盘卡牌仍然会抢走点击：" + string.Join("、", blockers.Select(b => b.name)));
                Assert.That(view.GetComponentInChildren<CanvasRenderer>(true), Is.Not.Null, "卡牌缺少渲染组件，测试夹具不成立");

                // 家同样只是显示层，不能让点击落不到格子上。
                foreach (var home in session.GetSnapshot().Homes)
                {
                    var homeCard = fixture.Board.GetCard(home.Id);
                    Assert.That(homeCard, Is.Not.Null, "棋盘上缺少家的卡牌");
                    Assert.That(homeCard.GetComponentsInChildren<Graphic>(true).Any(g => g.raycastTarget), Is.False, "家的卡牌会抢走点击");
                }

                // 格子必须保持可点击：点击处理者解析到格子自身（BoardCellView 通过 Button 上报坐标）。
                var cellGo = fixture.Board.CellRect(deployCell).gameObject;
                var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(cellGo);
                Assert.That(handler, Is.EqualTo(cellGo), "格子没有可接收点击的按钮");
            }
        }

        /// <summary>
        /// 棋盘视图测试夹具：用代码搭建 BoardView 与外层容器，并在结束时清理。
        /// 输入：Build 时传入本局配置；输出：可 Render 的 BoardView 与两个模板对象。
        /// 为什么需要：EditMode 下不能使用场景资产，必须自建最小视图依赖（格子模板、卡牌模板）。
        /// </summary>
        sealed class BoardFixture : System.IDisposable
        {
            /// <summary>棋盘根节点（挂 BoardView）。</summary>
            readonly GameObject _root;
            /// <summary>格子模板，用于实例化每个棋盘格。</summary>
            readonly GameObject _cellTemplate;
            /// <summary>卡牌模板：保留 Button 与 Image，和真实模板一样会抢点击，用于验证修复。</summary>
            readonly GameObject _cardTemplate;

            /// <summary>被测棋盘视图。</summary>
            public BoardView Board => _root.GetComponent<BoardView>();

            /// <summary>
            /// 搭建视图：创建根节点、格网容器、格子模板与卡牌模板，并写入 BoardView 的私有引用。
            /// </summary>
            public BoardFixture()
            {
                _root = new GameObject("BoardRoot", typeof(RectTransform), typeof(BoardView));
                var cells = new GameObject("Cells", typeof(RectTransform), typeof(GridLayoutGroup));
                cells.transform.SetParent(_root.transform, false);

                _cellTemplate = new GameObject("CellTemplate", typeof(RectTransform), typeof(Image), typeof(Button), typeof(BoardCellView));
                _cardTemplate = new GameObject("CardTemplate", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Card));

                var board = Board;
                SetField(board, "_cellRoot", (RectTransform)cells.transform);
                SetField(board, "_cellPrefab", _cellTemplate.GetComponent<BoardCellView>());
                SetField(board, "_cardFallbackPrefab", _cardTemplate.GetComponent<Card>());
                SetField(board, "_homePrefab", _cardTemplate.GetComponent<Card>());
            }

            /// <summary>按配置生成棋盘格网。输入：本局配置；输出：无（配置资产传 null，走到模板回退路径）。</summary>
            public void Build(GameConfiguration configuration) => Board.Build(configuration, null);

            /// <summary>按当前对局状态渲染一帧。输入：对局；输出：无。</summary>
            public void Render(GameSession session)
            {
                var snapshot = session.GetSnapshot();
                Board.Render(snapshot, session.GetLegalActions(new Selection(SelectionKind.None)), new Selection(SelectionKind.None));
            }

            /// <summary>销毁夹具创建的全部对象。输入：无；输出：无。</summary>
            public void Dispose()
            {
                Object.DestroyImmediate(_root);
                Object.DestroyImmediate(_cellTemplate);
                Object.DestroyImmediate(_cardTemplate);
            }

            /// <summary>
            /// 写入私有序列化字段。
            /// 输入：目标组件、字段名与值；输出：无（字段缺失时断言失败）。
            /// 为什么需要：这些引用平时由编辑器菜单装配，测试必须在代码里等价地写进去。
            /// </summary>
            static void SetField(object target, string name, object value)
            {
                var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(field, Is.Not.Null, $"{target.GetType().Name} 缺少字段 {name}");
                field.SetValue(target, value);
            }
        }
    }
}
