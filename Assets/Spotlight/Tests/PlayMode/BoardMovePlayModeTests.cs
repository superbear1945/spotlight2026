// 棋盘（UGUI）与规则快照一致性的 PlayMode 回归测试。
// 为什么必须在 PlayMode 验证：移动是「视图重新挂载 + DOTween 动画」的组合，
// 只有真实帧循环才能暴露“规则层已经移动、但卡牌仍停留在原格 / 被缩放到不可见”这类只在运行时出现的问题。
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Spotlight;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Spotlight.Tests
{
    /// <summary>
    /// 棋盘交互与渲染的端到端测试。
    /// 输入：演示场景（真实配置资产 + 真实视图装配）；输出：卡牌父子关系、坐标、缩放与点击落点断言。
    /// 用例通过 GameBootstrap 的私有输入入口驱动，等于复现玩家“点手牌 → 点格 → 结束回合 → 点单位 → 点目标格”的完整路径。
    /// </summary>
    public class BoardMovePlayModeTests
    {
        /// <summary>演示场景路径（与 SpotlightMenu.DemoScenePath 一致）。</summary>
        const string ScenePath = "Assets/Spotlight/Scenes/Spotlight.unity";

        /// <summary>等待动画结束的实时秒数（配置的移动动画为 0.2s，留足余量）。</summary>
        const float SettleSeconds = 1f;

        /// <summary>卡牌偏移小于该值即视为已经落在格子中心。</summary>
        const float CenterTolerance = 0.5f;

        /// <summary>
        /// 移动单位后，卡牌必须挂到目标格、在配置的动画时长内回到格心，并且不会停在旧格。
        /// 输入：无；输出：NUnit 断言结果。
        /// </summary>
        [UnityTest]
        public IEnumerator MovePutsCardInTargetCell()
        {
            var context = new SceneContext();
            yield return context.Load();
            var bootstrap = context.Bootstrap;
            var session = context.Session;
            var board = context.Board;

            var deployed = default(CardRecord);
            var deployCell = default(Cell);
            foreach (var record in session.GetSnapshot().Hand)
            {
                var options = session.GetLegalActions(new Selection(SelectionKind.Hand, record.Id));
                if (options.Deploy.Count <= 0) continue;
                deployed = record;
                deployCell = options.Deploy[0];
                break;
            }
            Assert.That(deployed, Is.Not.Null, "初始手牌里没有可部署的卡");
            Invoke(bootstrap, "OnCardClicked", context.Hand.GetCard(deployed.Id));
            Invoke(bootstrap, "OnCellClicked", deployCell);
            yield return WaitSeconds(SettleSeconds);

            var deployedUnit = session.GetSnapshot().Units.FirstOrDefault(u => u.SourceCardId == deployed.Id);
            Assert.That(deployedUnit, Is.Not.Null, "部署后棋盘上没有对应单位");

            // 推进回合直到有己方单位可以移动（覆盖召唤失调与 Boss AI 回合）。
            // 不绑定刚部署的单位：它可能在等待期间被 AI 击杀，因此每轮都补一张带移动距离的牌，
            // 断言只关心“界面上的某个棋子确实跟着规则移动”。
            UnitState live = null;
            for (var turn = 0; turn < 10 && live == null; turn++)
            {
                TryDeploy(bootstrap, session, context.Hand, requireMoveDistance: true);
                yield return WaitSeconds(SettleSeconds);
                live = session.GetSnapshot().Units.FirstOrDefault(u =>
                    u.Owner == Side.Player &&
                    session.GetLegalActions(new Selection(SelectionKind.Unit, u.Id)).Move.Count > 0);
                if (live != null) break;
                Invoke(bootstrap, "OnEndTurnRequested");
                yield return null;
                yield return FinishDiscard(bootstrap, session, context.Hand);
            }

            Assert.That(live, Is.Not.Null, "推进 10 个回合后仍没有可移动的己方单位");
            var moveTargets = session.GetLegalActions(new Selection(SelectionKind.Unit, live.Id)).Move;
            Assert.That(moveTargets.Count, Is.GreaterThan(0), "单位始终无法移动，无法验证移动渲染");

            var from = live.Position;
            var target = moveTargets[0];
            var card = board.GetCard(live.Id);
            Assert.That(card, Is.Not.Null, "棋盘上缺少该单位的卡牌视图");
            Assert.That(card.transform.parent, Is.EqualTo(board.CellRect(from)), "移动前卡牌应挂在起始格");

            Invoke(bootstrap, "OnCellClicked", from);
            Invoke(bootstrap, "OnCellClicked", target);
            var moved = session.GetSnapshot().Units.FirstOrDefault(u => u.Id == live.Id);
            Assert.That(moved, Is.Not.Null, "移动后单位消失了");
            Assert.That(moved.Position, Is.EqualTo(target), "规则层没有移动到目标格");

            var targetRect = board.CellRect(target);
            Assert.That(targetRect, Is.Not.Null, $"棋盘上找不到目标格 {target}");
            var rect = (RectTransform)card.transform;
            // 动画必须真的播放：移动当帧卡牌仍在起点側的偏移位置（否则说明视图只在硬切位置）。
            Assert.That(rect.anchoredPosition.magnitude, Is.GreaterThan(1f), "移动没有播放动画：卡牌在移动当帧就直接落在格心");
            var deadline = Time.realtimeSinceStartup + SettleSeconds;
            while (rect.anchoredPosition.magnitude > CenterTolerance && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.That(card.transform.parent, Is.EqualTo(targetRect), "移动后卡牌没有挂到目标格");
            Assert.That(rect.anchoredPosition.magnitude, Is.LessThan(CenterTolerance), $"移动动画结束后卡牌没有回到格心（偏移 {rect.anchoredPosition}）");
            Assert.That(rect.localScale.x, Is.GreaterThan(0.5f), $"移动后卡牌缩放异常：{rect.localScale}");

            // 再看几帧：动画结束后不允许被补间重新推到旧格附近。
            for (var frame = 0; frame < 10; frame++) yield return null;
            Assert.That(rect.anchoredPosition.magnitude, Is.LessThan(CenterTolerance), "动画结束后卡牌又偏离了格心");
        }

        /// <summary>
        /// 连续推进若干回合，每一步都断言“棋盘上每张卡牌都挂在规则快照对应的格子里”。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么需要：这条不变式同时能抓住“卡牌留在原格”“卡牌消失”“卡牌被缩放到不可见”三类视图失步问题。
        /// </summary>
        [UnityTest]
        public IEnumerator BoardCardsFollowSnapshotAcrossTurns()
        {
            var context = new SceneContext();
            yield return context.Load();
            var bootstrap = context.Bootstrap;
            var session = context.Session;
            var board = context.Board;

            for (var step = 0; step < 10; step++)
            {
                yield return WaitSeconds(SettleSeconds);
                AssertCardsMatchSnapshot(board, session, $"步骤 {step}（回合 {session.GetSnapshot().Round}）");

                TryDeploy(bootstrap, session, context.Hand, requireMoveDistance: false);
                yield return WaitSeconds(SettleSeconds);
                AssertCardsMatchSnapshot(board, session, $"步骤 {step} 部署后");

                if (session.GetSnapshot().Phase == MatchPhase.Finished) break;
                Invoke(bootstrap, "OnEndTurnRequested");
                yield return null;
                yield return FinishDiscard(bootstrap, session, context.Hand);
            }
        }

        /// <summary>
        /// 点击棋盘上的棋子时，UGUI 事件必须落到棋子的格子上。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么需要：卡牌模板自带 Image/文本/Button，默认会抢走射线；
        /// 一旦被抢走，玩家“点棋子选单位 / 点相邻牌换位 / 点自己的家”都会毫无反应。
        /// </summary>
        [UnityTest]
        public IEnumerator ClickingAPieceReachesItsCell()
        {
            var context = new SceneContext();
            yield return context.Load();
            var bootstrap = context.Bootstrap;
            var session = context.Session;
            var board = context.Board;

            TryDeploy(bootstrap, session, context.Hand, requireMoveDistance: true);
            yield return WaitSeconds(SettleSeconds);

            var unit = session.GetSnapshot().Units.FirstOrDefault(u => u.Owner == Side.Player);
            Assert.That(unit, Is.Not.Null, "没有部署成功任何己方单位");
            var card = board.GetCard(unit.Id);
            var cellRect = board.CellRect(unit.Position);
            Assert.That(card, Is.Not.Null, "棋盘上缺少该单位的卡牌视图");
            Assert.That(cellRect, Is.Not.Null, $"棋盘上找不到格子 {unit.Position}");

            var canvas = Object.FindObjectOfType<Canvas>();
            var raycaster = Object.FindObjectOfType<GraphicRaycaster>();
            Assert.That(EventSystem.current, Is.Not.Null, "场景缺少 EventSystem");
            Assert.That(raycaster, Is.Not.Null, "场景缺少 GraphicRaycaster");

            var pointer = new PointerEventData(EventSystem.current)
            {
                position = RectTransformUtility.WorldToScreenPoint(canvas != null ? canvas.worldCamera : null, card.transform.position)
            };
            var hits = new List<RaycastResult>();
            raycaster.Raycast(pointer, hits);
            var names = string.Join(" | ", hits.Select(h => h.gameObject.name));
            Debug.Log($"[点击落点] 屏幕 {Screen.width}x{Screen.height} 命中：{names}");
            Assert.That(hits.Count, Is.GreaterThan(0), "棋子上没有任何可点击的图形");

            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject);
            Assert.That(handler, Is.Not.Null, "最上层命中对象没有点击处理者");
            Assert.That(handler, Is.EqualTo(cellRect.gameObject),
                $"点击棋子被 {hits[0].gameObject.name} 吞掉了，格子收不到点击（命中顺序：{names}）");
        }

        /// <summary>
        /// 部署第一张可部署的手牌。
        /// 输入：引导器、对局、手牌视图，以及是否只考虑带移动距离的卡；输出：是否成功部署。
        /// 为什么需要：移动相关用例必须保证场上有“能移动”的己方单位，
        /// 而资源卡移动距离为 0，拿来验证移动渲染会得到“没有可移动单位”的假失败。
        /// </summary>
        static bool TryDeploy(GameBootstrap bootstrap, GameSession session, HandView hand, bool requireMoveDistance)
        {
            foreach (var record in session.GetSnapshot().Hand)
            {
                if (requireMoveDistance && !HasMoveDistance(session, record.TypeId)) continue;
                var options = session.GetLegalActions(new Selection(SelectionKind.Hand, record.Id));
                if (options.Deploy.Count <= 0) continue;
                Invoke(bootstrap, "OnCardClicked", hand.GetCard(record.Id));
                Invoke(bootstrap, "OnCellClicked", options.Deploy[0]);
                return true;
            }
            return false;
        }

        /// <summary>判断某卡种是否具备移动能力。输入：对局与卡种 ID；输出：是否配置了移动距离。</summary>
        static bool HasMoveDistance(GameSession session, string typeId)
        {
            return session.Configuration.Cards.TryGetValue(typeId, out var archetype) &&
                   archetype.Combat != null && archetype.Combat.MoveDistance > 0;
        }

        /// <summary>
        /// 断言棋盘上每个单位与家的卡牌都挂在快照坐标对应的格子里，且没有被缩放到不可见。
        /// 输入：棋盘视图、对局与步骤说明；输出：NUnit 断言结果（失败信息包含完整棋盘对照表）。
        /// </summary>
        static void AssertCardsMatchSnapshot(BoardView board, GameSession session, string step)
        {
            var snapshot = session.GetSnapshot();
            var report = new StringBuilder();
            var problems = new List<string>();
            report.Append($"[棋盘对照] {step}｜单位 {snapshot.Units.Count} 个｜家 {snapshot.Homes.Count} 个");

            foreach (var unit in snapshot.Units)
            {
                var card = board.GetCard(unit.Id);
                var expected = board.CellRect(unit.Position);
                var actualParent = card != null ? card.transform.parent : null;
                var rect = card != null ? card.transform as RectTransform : null;
                report.Append($"\n  单位 {unit.TypeId}#{unit.Id} 快照={unit.Position} 格子={expected?.name} 卡牌父节点={actualParent?.name} 偏移={rect?.anchoredPosition} 缩放={rect?.localScale}");
                if (card == null) { problems.Add($"单位 {unit.Id} 在棋盘上没有卡牌"); continue; }
                if (actualParent != expected) problems.Add($"单位 {unit.Id}（{unit.TypeId}）卡牌挂在 {actualParent?.name}，快照坐标是 {unit.Position}（应为 {expected?.name}）");
                if (rect != null && rect.anchoredPosition.magnitude > CenterTolerance) problems.Add($"单位 {unit.Id} 卡牌偏移未归零：{rect.anchoredPosition}");
                if (rect != null && rect.localScale.x < 0.5f) problems.Add($"单位 {unit.Id} 卡牌被缩放到不可见：{rect.localScale}");
            }

            foreach (var home in snapshot.Homes)
            {
                var card = board.GetCard(home.Id);
                var expected = board.CellRect(home.Position);
                var actualParent = card != null ? card.transform.parent : null;
                var rect = card != null ? card.transform as RectTransform : null;
                report.Append($"\n  家 {home.Owner} 快照={home.Position} 格子={expected?.name} 卡牌父节点={actualParent?.name} 偏移={rect?.anchoredPosition} 缩放={rect?.localScale}");
                if (card == null) { problems.Add($"家 {home.Owner} 在棋盘上没有卡牌"); continue; }
                if (actualParent != expected) problems.Add($"家 {home.Owner} 卡牌挂在 {actualParent?.name}，快照坐标是 {home.Position}（应为 {expected?.name}）");
                if (rect != null && rect.anchoredPosition.magnitude > CenterTolerance) problems.Add($"家 {home.Owner} 卡牌偏移未归零：{rect.anchoredPosition}");
            }

            Debug.Log(report.ToString());
            Assert.That(problems, Is.Empty, step + "\n" + string.Join("\n", problems) + "\n" + report);
        }

        /// <summary>
        /// 完成超限弃牌阶段。
        /// 输入：引导器、对局与手牌视图；输出：协程（弃到上限为止）。
        /// </summary>
        static IEnumerator FinishDiscard(GameBootstrap bootstrap, GameSession session, HandView hand)
        {
            for (var guard = 0; guard < 12 && session.GetSnapshot().Phase == MatchPhase.Discard; guard++)
            {
                var discard = session.GetSnapshot().Hand.FirstOrDefault();
                if (discard == null) break;
                Invoke(bootstrap, "OnCardClicked", hand.GetCard(discard.Id));
                yield return null;
            }
        }

        /// <summary>按真实时间等待若干秒。输入：秒数；输出：协程。</summary>
        static IEnumerator WaitSeconds(float seconds)
        {
            var deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline) yield return null;
        }

        /// <summary>
        /// 调用 GameBootstrap 的私有输入入口。
        /// 输入：目标对象、方法名与参数；输出：无（方法缺失时断言失败）。
        /// 为什么要这样做：输入入口是私有的，测试只能以“和 UGUI 事件一样的方式”调用它，避免为了测试放宽可见性。
        /// </summary>
        static void Invoke(object target, string methodName, params object[] args)
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"{target.GetType().Name} 缺少方法 {methodName}");
            method.Invoke(target, args);
        }

        /// <summary>
        /// 演示场景上下文：负责加载场景并在加载完成后取出引导器、对局与视图。
        /// 输入：Load 协程；输出：四个引用。为什么需要：加载场景必须跨帧等待，无法写在静态工具函数里。
        /// </summary>
        sealed class SceneContext
        {
            /// <summary>对局引导器。</summary>
            public GameBootstrap Bootstrap { get; private set; }
            /// <summary>当前对局（唯一状态所有者）。</summary>
            public GameSession Session { get; private set; }
            /// <summary>棋盘视图。</summary>
            public BoardView Board { get; private set; }
            /// <summary>手牌视图。</summary>
            public HandView Hand { get; private set; }

            /// <summary>加载演示场景并等待 Start 完成。输入：无；输出：协程。</summary>
            public IEnumerator Load()
            {
                SceneManager.LoadScene(ScenePath);
                yield return null;
                yield return null;
                Bootstrap = Object.FindObjectOfType<GameBootstrap>();
                Assert.That(Bootstrap, Is.Not.Null, "演示场景未装配 GameBootstrap");
                Session = Bootstrap.Session;
                Assert.That(Session, Is.Not.Null, "对局未启动（配置门禁或配置资产缺失）");
                Board = Object.FindObjectOfType<BoardView>();
                Hand = Object.FindObjectOfType<HandView>();
                Assert.That(Board, Is.Not.Null, "场景缺少 BoardView");
                Assert.That(Hand, Is.Not.Null, "场景缺少 HandView");
            }
        }
    }
}
