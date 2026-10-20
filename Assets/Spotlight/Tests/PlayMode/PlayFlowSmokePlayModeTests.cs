// 端到端游玩流程冒烟（PlayMode）：在真实场景与真实帧循环里把一整轮主流程走通。
// 为什么需要：既有 PlayMode 用例都是“单能力 / 单面板”的回归（点一个格子、拖一次牌、看一个面板），
// 没有任何一条用例验证“拖拽部署 → 互换 → 移动 → 攻击 → 离场 → 结束回合 → Boss 回合 → 回到玩家”
// 这条主流程仍然连通。规则算得对不对由 EditMode 的 GameSessionTests / Card*Tests 负责，
// 本用例只回答一个问题：主流程还能不能从头走到尾，且每一步之后视图都没跟丢规则快照。
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Spotlight.Tests
{
    /// <summary>
    /// 主流程冒烟测试。输入：真实演示场景 + 只含单一攻击卡种的确定性夹具牌堆；
    /// 输出：每一步之后的视图/快照一致性断言，以及“卡牌总数守恒”不变式。
    /// 所有对局变化都经真实 UGUI 手势或 <see cref="GameBootstrap"/> 的公开输入入口发生，
    /// 不直接改写 <see cref="GameSession"/> 私有状态。
    /// </summary>
    public class PlayFlowSmokePlayModeTests
    {
        /// <summary>演示场景路径（与 SpotlightMenu.DemoScenePath 一致）。</summary>
        const string ScenePath = "Assets/Spotlight/Scenes/Spotlight.unity";

        /// <summary>等待动画与阶段演示落地的实时秒数（夹具把动画压到 0.02 秒，这里留足余量）。</summary>
        const float SettleSeconds = 0.4f;

        /// <summary>对局引导器，提供公开会话与重开入口。</summary>
        GameBootstrap _bootstrap;

        /// <summary>棋盘视图，用于核对卡牌挂载位置与射线设置。</summary>
        BoardView _board;

        /// <summary>手牌视图，用于发起真实拖拽。</summary>
        HandView _hand;

        /// <summary>跳过“携带选择”面板直接进入对局。输入：无；输出：无。</summary>
        [OneTimeSetUp]
        public void SuppressDeckSelectionOnStart() => GameBootstrap.SuppressDeckSelectionOnStart = true;

        /// <summary>恢复正式启动行为，避免影响同进程的其它 PlayMode 用例。输入：无；输出：无。</summary>
        [OneTimeTearDown]
        public void RestoreDeckSelectionOnStart() => GameBootstrap.SuppressDeckSelectionOnStart = false;

        /// <summary>
        /// 主流程冒烟：部署 → 互换 → 移动 → 攻击 → 离场 → 结束回合（Boss 回合）→ 回到玩家。
        /// 输入：无；输出：NUnit 断言结果；每个阶段后都核对“棋盘卡牌 ⇄ 快照单位”与卡牌总数守恒。
        /// </summary>
        [UnityTest]
        public IEnumerator FullTurnLoopKeepsRulesAndViewsInSync()
        {
            yield return Load();
            var session = _bootstrap.Session;
            var totalCards = CardTotal(session);
            var bossHome = session.GetSnapshot().Homes.Single(h => h.Owner == Side.Boss);

            // ① 真实拖拽部署：走 HandView → HandCardDrag → 棋盘命中这条真实输入链。
            var first = FirstDeployable(session);
            var firstCell = session.GetLegalActions(new Selection(SelectionKind.Hand, first.Id)).Deploy[0];
            HandDragInput.Deploy(_hand.GetCard(first.Id), _board, firstCell);
            yield return WaitSeconds(SettleSeconds);
            var firstUnit = session.GetSnapshot().Units.Single(u => u.SourceCardId == first.Id);
            Assert.That(_board.GetCard(firstUnit.Id), Is.Not.Null, "拖拽部署后棋盘上没有卡牌视图");
            AssertCardsMatchSnapshot(session, "拖拽部署后");
            Assert.That(CardTotal(session), Is.EqualTo(totalCards), "部署后卡牌总数必须守恒（牌堆 + 手牌 + 墓地）");

            // ② 再部署一个相邻单位，为互换准备。
            var secondId = DeployNear(session, firstUnit.Position, 1);
            yield return WaitSeconds(SettleSeconds);
            var swapCells = session.GetLegalActions(new Selection(SelectionKind.Unit, secondId)).Swap;
            Assert.That(swapCells.Count, Is.GreaterThan(0), "相邻且都未行动的两个非资源单位应当可以互换");
            var partner = session.GetSnapshot().Units.Single(u => u.Position == swapCells[0]);
            var swap = Submit(new GameCommand(CommandKind.Swap, Side.Player, secondId, targetId: partner.Id));
            Assert.That(swap, Is.Not.Null, "互换命令没有执行（上一个命令的演示未结束）");
            Assert.That(swap.Success, Is.True, swap.Error);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(session.GetSnapshot().Units.Single(u => u.Id == secondId).Position,
                Is.EqualTo(partner.Position), "互换后位置没有交换");
            AssertCardsMatchSnapshot(session, "互换后");

            // ③ 移动：换一个还没行动的单位走一格。
            var moverId = DeployNear(session, firstUnit.Position, 2);
            yield return WaitSeconds(SettleSeconds);
            var moveCells = session.GetLegalActions(new Selection(SelectionKind.Unit, moverId)).Move;
            Assert.That(moveCells.Count, Is.GreaterThan(0), "新部署的攻击单位应当有合法移动格（召唤失调已关闭）");
            var move = Submit(new GameCommand(CommandKind.Move, Side.Player, moverId, moveCells[0]));
            Assert.That(move.Success, Is.True, move.Error);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(session.GetSnapshot().Units.Single(u => u.Id == moverId).Position,
                Is.EqualTo(moveCells[0]), "移动后位置没有改变");
            AssertCardsMatchSnapshot(session, "移动后");

            // ④ 攻击：把一个单位部署到 Boss 家旁边，直接打家（不依赖 AI 是否出兵，保证必定发生）。
            var attackerId = DeployNear(session, bossHome.Position, 1);
            yield return WaitSeconds(SettleSeconds);
            var attackCells = session.GetLegalActions(new Selection(SelectionKind.Unit, attackerId)).Attack;
            Assert.That(attackCells.Any(c => c.Equals(bossHome.Position)), Is.True, "贴着 Boss 家的单位应当可以攻击家");
            var hpBefore = session.GetSnapshot().Homes.Single(h => h.Owner == Side.Boss).Hp;
            var attack = Submit(new GameCommand(CommandKind.Attack, Side.Player, attackerId, targetId: bossHome.Id));
            Assert.That(attack.Success, Is.True, attack.Error);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(session.GetSnapshot().Homes.Single(h => h.Owner == Side.Boss).Hp, Is.LessThan(hpBefore),
                "攻击 Boss 家之后家的生命没有下降");
            AssertCardsMatchSnapshot(session, "攻击后");

            // ⑤ 离场：来源卡在部署时已进墓地，离场既不回收卡牌也不消耗行动。
            var graveyardBefore = session.GetSnapshot().Graveyard.Count;
            var withdraw = Submit(new GameCommand(CommandKind.Withdraw, Side.Player, moverId));
            Assert.That(withdraw.Success, Is.True, withdraw.Error);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(session.GetSnapshot().Units.Any(u => u.Id == moverId), Is.False, "离场后单位仍在场上");
            Assert.That(session.GetSnapshot().Graveyard.Count, Is.EqualTo(graveyardBefore), "离场不得回收来源卡");
            Assert.That(CardTotal(session), Is.EqualTo(totalCards), "离场后卡牌总数必须守恒");
            AssertCardsMatchSnapshot(session, "离场后");

            // ⑥ 结束回合：Boss（AI）回合整段跑完，回到玩家。
            var roundBefore = session.GetSnapshot().Round;
            var endTurn = Submit(new GameCommand(CommandKind.EndTurn, Side.Player, skipDeployment: true));
            Assert.That(endTurn.Success, Is.True, endTurn.Error);
            yield return WaitForPresentation();
            yield return WaitSeconds(SettleSeconds);
            var after = session.GetSnapshot();
            var state = $"回合={after.Round}，行动方={after.ActiveSide}，阶段={after.Phase}，演示中={_bootstrap.IsPresentingTurn}";
            Assert.That(after.Round, Is.GreaterThan(roundBefore), $"结束回合没有推进回合数：{state}");
            Assert.That(after.ActiveSide, Is.EqualTo(Side.Player), $"Boss 回合结束后没有回到玩家：{state}");
            Assert.That(after.Phase, Is.EqualTo(MatchPhase.Action), $"没有回到行动阶段：{state}");
            Assert.That(CardTotal(session), Is.EqualTo(totalCards), $"整轮结束后卡牌总数必须守恒：{state}");
            AssertCardsMatchSnapshot(session, "整轮结束后");

            // 任何阶段都不允许出现异常日志（NRE、日志断言失败等），否则冒烟等于没跑。
            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>
        /// 加载演示场景，并用独立确定性夹具重开一局。
        /// 输入：无；输出：可交互的 <see cref="GameBootstrap"/> / <see cref="BoardView"/> / <see cref="HandView"/>。
        /// 夹具要点：只保留 blade1 一种攻击卡（免费维持、可互换、移动 1 格）、Boss 侧攻击与移动置零
        /// （冒烟只看主流程是否连通，不让 AI 的输出影响判定）、家区域铺满整块棋盘（可贴着 Boss 家部署）。
        /// </summary>
        IEnumerator Load()
        {
            yield return SceneManager.LoadSceneAsync(ScenePath);
            yield return null;
            _bootstrap = Object.FindObjectOfType<GameBootstrap>();
            _board = Object.FindObjectOfType<BoardView>();
            _hand = Object.FindObjectOfType<HandView>();
            Assert.That(_bootstrap, Is.Not.Null, "演示场景未装配 GameBootstrap");
            Assert.That(_board, Is.Not.Null, "场景缺少 BoardView");
            Assert.That(_hand, Is.Not.Null, "场景缺少 HandView");

            var document = JsonUtility.FromJson<ConfigDocument>(JsonUtility.ToJson(_bootstrap.CurrentAsset.ToDocument()));
            var rules = document.rules[0];
            rules.firstSide = "player";
            rules.playerSide = "left";
            rules.playerControlsBoss = false;
            rules.playerPlays = 100;
            rules.bossPlays = 2;
            rules.drawCount = 6;
            rules.handLimit = 20;
            rules.homeRegionWidth = 10;
            rules.initialResources = 20;
            rules.summoningSickness = false;
            rules.adjacentSwap = true;
            rules.bossBombInterval = 1000;
            rules.bossSpawnerAttackThreshold = 1000;
            // 抽牌堆构成规则显式写死：Deck 表已废除，这里的牌堆由下面的 WithDeck 给定，
            // 但配置校验仍要求三比例合法、每阶级至少一种卡，写死可以不受 Excel 当前取值影响。
            rules.drawPileSize = 24;
            rules.tierRatioBasic = 70;
            rules.tierRatioIntermediate = 20;
            rules.tierRatioAdvanced = 10;
            rules.maxTypesBasic = 5;
            rules.maxTypesIntermediate = 3;
            rules.maxTypesAdvanced = 2;
            document.presentation[0].moveSeconds = 0.02f;
            document.presentation[0].feedbackSeconds = 0.02f;

            var blade = document.cards.Single(c => c.id == "blade1");
            blade.upkeep = 0;
            blade.deployCost = 0;
            blade.moveDistance = 1;
            blade.range = 1;
            blade.canSwap = true;
            blade.canAttackResources = true;

            var boss = document.cards.Single(c => c.id == rules.bossCardType);
            boss.atk = 0;
            boss.moveDistance = 0;

            typeof(GameBootstrap).GetField("_seed", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_bootstrap, 7);
            // 第二个参数 false：8 张的夹具牌堆不等于 drawPileSize（24），走测试夹具专供的豁免路径。
            _bootstrap.RestartWith(new GameConfiguration(document)
                .WithDeck(new[] { new DeckEntry("blade1", 8) }, false));
            yield return null;
            yield return WaitForPresentation();
            Assert.That(_bootstrap.Session, Is.Not.Null, "对局未启动（配置门禁或配置资产缺失）");
            Canvas.ForceUpdateCanvases();
        }

        /// <summary>
        /// 取第一张有合法部署格的手牌。输入：会话；输出：手牌记录（没有可部署卡时断言失败）。
        /// </summary>
        static CardRecord FirstDeployable(GameSession session)
        {
            foreach (var record in session.GetSnapshot().Hand)
                if (session.GetLegalActions(new Selection(SelectionKind.Hand, record.Id)).Deploy.Count > 0) return record;
            Assert.Fail("初始手牌里没有可部署的卡");
            return null;
        }

        /// <summary>
        /// 在锚点附近部署一张手牌。输入：会话、锚点格、允许的最大曼哈顿距离；
        /// 输出：新单位的 ID（找不到足够近的合法格时以诊断信息断言失败）。
        /// 为什么按“最小距离”选格：主流程需要“相邻（距离 1）”与“贴家（距离 1）”这类布局前提，
        /// 一旦夹具或规则改变让前提不成立，应该在部署这一步就报出可读原因，而不是在后面莫名失败。
        /// </summary>
        long DeployNear(GameSession session, Cell anchor, int maxDistance)
        {
            var best = default(Cell?);
            var bestRecord = default(CardRecord);
            var bestDistance = int.MaxValue;
            foreach (var record in session.GetSnapshot().Hand)
            {
                foreach (var cell in session.GetLegalActions(new Selection(SelectionKind.Hand, record.Id)).Deploy)
                {
                    var distance = cell.Distance(anchor);
                    if (distance >= bestDistance) continue;
                    best = cell;
                    bestRecord = record;
                    bestDistance = distance;
                }
            }
            Assert.That(best, Is.Not.Null, "手里没有更多可部署的卡");
            Assert.That(bestDistance, Is.LessThanOrEqualTo(maxDistance),
                $"找不到距 {anchor} ≤{maxDistance} 的合法部署格（最近为 {best} 距离 {bestDistance}）");
            var result = Submit(new GameCommand(CommandKind.Deploy, Side.Player, bestRecord.Id, best.Value));
            Assert.That(result, Is.Not.Null, "部署命令没有执行（上一个命令的演示未结束）");
            Assert.That(result.Success, Is.True, result.Error);
            return session.GetSnapshot().Units.Single(u => u.SourceCardId == bestRecord.Id).Id;
        }

        /// <summary>
        /// 核对“棋盘卡牌视图 ⇄ 规则快照单位”。输入：会话与阶段名；输出：NUnit 断言。
        /// 为什么单列一条不变式：它同时能抓住“卡牌留在原格”“卡牌消失”“卡牌拦截射线”三类视图失步问题。
        /// </summary>
        void AssertCardsMatchSnapshot(GameSession session, string stage)
        {
            var snapshot = session.GetSnapshot();
            Assert.That(_board.UnitCards.Count, Is.EqualTo(snapshot.Units.Count),
                $"{stage}：棋盘卡牌数量（{_board.UnitCards.Count}）与快照单位数（{snapshot.Units.Count}）不一致");
            foreach (var unit in snapshot.Units)
            {
                var card = _board.GetCard(unit.Id);
                Assert.That(card, Is.Not.Null, $"{stage}：单位 {unit.Id} 缺少卡牌视图");
                Assert.That(card.transform.parent, Is.EqualTo(_board.CellRect(unit.Position)),
                    $"{stage}：单位 {unit.Id} 的卡牌没有挂在快照位置 {unit.Position}");
                Assert.That(card.GetComponentsInChildren<Graphic>().All(g => !g.raycastTarget), Is.True,
                    $"{stage}：棋盘卡牌不得拦截点击");
            }
        }

        /// <summary>
        /// 卡牌总数：抽牌堆 + 手牌 + 墓地。输入：会话；输出：张数。
        /// 为什么是守恒量：部署时来源卡进墓地、离场不回收、墓地洗回抽牌堆，三者之和在整局中恒定。
        /// </summary>
        static int CardTotal(GameSession session)
        {
            var snapshot = session.GetSnapshot();
            return snapshot.DrawPile.Count + snapshot.Hand.Count + snapshot.Graveyard.Count;
        }

        /// <summary>提交规则命令。输入：命令；输出：引导器的结算结果（演示期间返回 null）。</summary>
        CommandResult Submit(GameCommand command) =>
            (CommandResult)typeof(GameBootstrap).GetMethod("Submit", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_bootstrap, new object[] { command });

        /// <summary>等待阶段演示结束。输入：无；输出：有限帧等待并断言输入闸门已解除。</summary>
        IEnumerator WaitForPresentation()
        {
            var deadline = Time.realtimeSinceStartup + 20f;
            while (_bootstrap.IsPresentingTurn && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(_bootstrap.IsPresentingTurn, Is.False, "阶段演示超时（输入闸门未解除）");
        }

        /// <summary>按真实时间等待若干秒。输入：秒数；输出：协程。</summary>
        static IEnumerator WaitSeconds(float seconds)
        {
            var deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline) yield return null;
        }
    }
}
