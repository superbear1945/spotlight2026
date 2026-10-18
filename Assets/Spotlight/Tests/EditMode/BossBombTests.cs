using System;
using System.Linq;
using NUnit.Framework;
using Spotlight.EditorTools;

namespace Spotlight.Tests
{
    /// <summary>
    /// 炸弹规则回归：仅通过公开命令推进对局。输入为独立配置夹具，输出为时序、预算、身份和权限断言。
    /// 不修改 GameSession 私有状态，避免测试绕过实际规则。
    /// </summary>
    public class BossBombTests
    {
        /// <summary>构造隔离夹具。输入：部署间隔；输出：Boss 普通单位无攻击的配置，避免干扰炸弹断言。</summary>
        static ConfigDocument Document(int interval = 2)
        {
            var d = GameSessionTests.Document();
            d.rules[0].bossBombInterval = interval;
            d.rules[0].bossBombDamage = 3;
            d.rules[0].bossBombColumn = 5;
            d.cards.Single(c => c.id == "boss").atk = 0;
            return d;
        }

        /// <summary>
        /// 建立真实对局。输入：配置与固定随机种子；输出：可执行公开命令的规则内核。
        /// 抽牌堆固定使用 <see cref="GameSessionTests.DefaultDeck"/>（16 张），
        /// 因为本文件的用例断言“手牌 + 抽牌堆 + 墓地”总数不变，需要与迁移前完全一致的牌堆张数。
        /// </summary>
        static GameSession Start(ConfigDocument d, int seed = 17) =>
            new GameSession(new GameConfiguration(d).WithDeck(GameSessionTests.DefaultDeck(), false), new SeededRandom(seed), 1000);

        /// <summary>执行并断言成功。输入：对局、命令；输出：有序事件结果，失败时显示规则错误。</summary>
        static CommandResult Do(GameSession g, GameCommand command)
        {
            var result = g.Execute(command);
            Assert.That(result.Success, Is.True, result.Error);
            return result;
        }

        /// <summary>结束当前回合。输入：对局；输出：结束命令结果，不隐式跳过炸弹以便检验技能闸门。</summary>
        static CommandResult End(GameSession g) => Do(g, new GameCommand(CommandKind.EndTurn, g.GetSnapshot().ActiveSide, skipDeployment: true));

        /// <summary>在玩家回合部署资源单位。输入：对局与坐标；输出：新单位 ID，用于爆炸后的身份断言。</summary>
        static long Put(GameSession g, Cell cell)
        {
            var card = g.GetSnapshot().Hand.First(c => c.TypeId == "miner");
            Do(g, new GameCommand(CommandKind.Deploy, Side.Player, card.Id, cell));
            return g.GetSnapshot().Units.Single(u => u.Position == cell).Id;
        }

        /// <summary>推进到第二回合玩家准备阶段部署，再经过一次玩家行动。输入：首回合与炸弹格；输出：第三回合玩家开始时的爆炸结果。</summary>
        static CommandResult Explode(GameSession g, Cell position)
        {
            End(g);
            End(g);
            Do(g, new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: position));
            End(g);
            return End(g);
        }

        /// <summary>第 10 回合部署、第 11 回合爆炸，对两种先手和控制模式验证同一完整回合定义。</summary>
        [TestCase("player", true)] [TestCase("boss", true)]
        [TestCase("player", false)] [TestCase("boss", false)]
        public void IntervalUsesCompleteRoundsAndExplodesBeforeNextPlayerAction(string first, bool manual)
        {
            var d = Document(10); d.rules[0].firstSide = first; d.rules[0].playerControlsBoss = manual;
            var g = Start(d);
            while (g.GetSnapshot().Round <= 21)
            {
                var s = g.GetSnapshot();
                if (s.BossBombPending)
                {
                    Assert.That(s.ActiveSide, Is.EqualTo(Side.Player));
                    Do(g, new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: new Cell(0, 4)));
                }
                if (s.ActiveSide == Side.Player && (s.Round == 10 || s.Round == 20))
                    Assert.That(g.GetSnapshot().BossBombs.Single().DeployedRound, Is.EqualTo(s.Round));
                if (s.ActiveSide == Side.Player && (s.Round == 11 || s.Round == 21))
                    Assert.That(g.GetSnapshot().BossBombs, Is.Empty, "玩家尚未行动时已爆炸");
                End(g);
            }
            var log = g.GetSnapshot().Log;
            Assert.That(log.Where(e => e.Kind == "bomb-deploy").Select(e => e.Bomb.DeployedRound), Is.EqualTo(new long[] { 10, 20 }));
            Assert.That(log.Where(e => e.Kind == "bomb-explode").Select(e => e.Bomb.DueRound), Is.EqualTo(new long[] { 11, 21 }));
        }

        /// <summary>中心格先消耗预算，死亡只移除战斗单位，旧快照与牌堆三区保持独立。</summary>
        [Test]
        public void CenterDiesBeforeRaysAndCardsAreNotRecycledAgain()
        {
            var d = Document(); d.cards.Single(c => c.id == "miner").hp = 2;
            var g = Start(d); var center = Put(g, new Cell(2, 4)); var up = Put(g, new Cell(3, 4));
            var old = g.GetSnapshot();
            var result = Explode(g, new Cell(2, 4));
            var blast = result.Events.Single(e => e.Kind == "bomb-explode");
            Assert.That(blast.BlastSteps.Select(s => s.Position), Is.EqualTo(new[] { new Cell(2, 4), new Cell(3, 4) }));
            Assert.That(blast.BlastSteps.Select(s => s.Damage), Is.EqualTo(new[] { 2, 1 }));
            Assert.That(g.GetSnapshot().Units.Any(u => u.Id == center), Is.False);
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == up).Hp, Is.EqualTo(1));
            Assert.That(old.Units.Single(u => u.Id == center).Hp, Is.EqualTo(2));
            // 卡片守恒：手牌 + 抽牌堆 + 墓地始终等于配置的抽牌堆张数（Deck 表废除后由 drawPileSize 决定）。
            // 夹具用 WithDeck(..., false) 显式指定牌堆，因此这里只断言守恒，不断言“等于 drawPileSize”。
            var pileSize = g.Configuration.Deck.Sum(e => e.Count);
            Assert.That(g.GetSnapshot().Hand.Count + g.GetSnapshot().DrawPile.Count + g.GetSnapshot().Graveyard.Count, Is.EqualTo(pileSize));
            var cards = g.GetSnapshot().Hand.Concat(g.GetSnapshot().DrawPile).Concat(g.GetSnapshot().Graveyard).ToArray();
            Assert.That(cards.Select(c => c.Id).Distinct().Count(), Is.EqualTo(pileSize));
        }

        /// <summary>同距离按屏幕上右下左共享预算，镜像后仍保持相同视觉优先级。</summary>
        [TestCase("left", 4, 5, 3)] [TestCase("right", 5, 4, 6)]
        public void RingsShareBudgetInScreenDirectionOrder(string side, int col, int right, int left)
        {
            var d = Document(); d.rules[0].playerSide = side; d.cards.Single(c => c.id == "miner").hp = 1;
            var g = Start(d);
            Put(g, new Cell(3, col)); Put(g, new Cell(2, right)); Put(g, new Cell(1, col));
            var survivor = Put(g, new Cell(2, left));
            var blast = Explode(g, new Cell(2, col)).Events.Single(e => e.Kind == "bomb-explode");
            Assert.That(blast.BlastSteps.Select(s => s.Position), Is.EqualTo(new[] { new Cell(2, col), new Cell(3, col), new Cell(2, right), new Cell(1, col) }));
            Assert.That(g.GetSnapshot().Units.Single().Id, Is.EqualTo(survivor));
        }

        /// <summary>击杀后可沿同射线继续搜索；高血量目标耗尽预算，远处和斜向单位不受伤。</summary>
        [Test]
        public void ContinuesAfterKillAndStopsAtBudgetWithoutDiagonalDamage()
        {
            var d = Document(); d.cards.Single(c => c.id == "miner").hp = 1;
            var g = Start(d); Put(g, new Cell(1, 4)); Put(g, new Cell(2, 4)); Put(g, new Cell(3, 4));
            var diagonal = Put(g, new Cell(1, 5));
            var blast = Explode(g, new Cell(0, 4)).Events.Single(e => e.Kind == "bomb-explode");
            Assert.That(blast.BlastSteps.Sum(s => s.Damage), Is.EqualTo(3));
            Assert.That(g.GetSnapshot().Units.Single().Id, Is.EqualTo(diagonal));
            var strong = Start(Document()); var id = Put(strong, new Cell(2, 4));
            var highBlast = Explode(strong, new Cell(2, 4)).Events.Single(e => e.Kind == "bomb-explode");
            Assert.That(highBlast.BlastSteps.Count, Is.EqualTo(1));
            Assert.That(strong.GetSnapshot().Units.Single(u => u.Id == id).Hp, Is.EqualTo(1));
        }

        /// <summary>空格、Boss 单位和双方的家都不消耗预算，不阻断路径，每个合法十字格只访问一次。</summary>
        [Test]
        public void IgnoresHomesAndBossUnitsAndFinishesAllBoundaryRays()
        {
            var d = Document(); d.rules[0].homeRegionWidth = 10;
            var g = Start(d); var victim = Put(g, new Cell(2, 8)); End(g); End(g);
            Do(g, new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: new Cell(2, 4)));
            End(g);
            Do(g, new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(2, 5)));
            var blast = End(g).Events.Single(e => e.Kind == "bomb-explode");
            Assert.That(g.GetSnapshot().Units.Single(u => u.Owner == Side.Boss).Hp, Is.EqualTo(3));
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == victim).Hp, Is.EqualTo(1));
            Assert.That(g.GetSnapshot().Homes.All(h => h.Hp == 10), Is.True);
            var empty = Start(Document()); var emptyBlast = Explode(empty, new Cell(2, 4)).Events.Single(e => e.Kind == "bomb-explode");
            Assert.That(emptyBlast.BlastSteps.Count, Is.EqualTo(14));
            Assert.That(emptyBlast.BlastSteps.Select(s => s.Position).Distinct().Count(), Is.EqualTo(14));
            Assert.That(emptyBlast.BlastSteps.Sum(s => s.Damage), Is.Zero);
            Assert.That(empty.GetSnapshot().BossBombs, Is.Empty);
        }

        /// <summary>待处理技能阻断全部普通命令，错误目标和重复部署失败且不消耗普通放牌次数。</summary>
        [Test]
        public void ManualGateRevalidatesCommandsAndSkipConsumesOnlySkill()
        {
            var g = Start(Document(1)); var before = g.GetSnapshot();
            Assert.That(before.ActiveSide, Is.EqualTo(Side.Player));
            foreach (CommandKind kind in Enum.GetValues(typeof(CommandKind)))
                if (kind != CommandKind.DeployBossBomb && kind != CommandKind.SkipBossBomb)
                    foreach (var actor in new[] { Side.Player, Side.Boss })
                        Assert.That(g.Execute(new GameCommand(kind, actor, skipDeployment: true)).Success, Is.False, kind.ToString());
            Assert.That(g.Execute(new GameCommand(CommandKind.DeployBossBomb, Side.Player, target: new Cell(0, 4))).Success, Is.False);
            Assert.That(g.Execute(new GameCommand(CommandKind.SkipBossBomb, Side.Player)).Success, Is.False);
            Assert.That(g.Execute(new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: new Cell(0, 3))).Success, Is.False);
            Assert.That(g.Execute(new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: new Cell(-1, 4))).Success, Is.False);
            g.SetInputBlocked(true);
            Assert.That(g.GetLegalActions(new Selection(SelectionKind.BossBombDeployment)).Deploy, Is.Empty);
            Assert.That(g.Execute(new GameCommand(CommandKind.SkipBossBomb, Side.Boss)).Success, Is.False);
            g.SetInputBlocked(false);
            Do(g, new GameCommand(CommandKind.SkipBossBomb, Side.Boss));
            Assert.That(g.GetSnapshot().PlaysRemaining, Is.EqualTo(before.PlaysRemaining));
            Put(g, new Cell(0, 0));
            Assert.That(g.Execute(new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: new Cell(0, 4))).Success, Is.False);
            End(g); End(g);
            Do(g, new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: new Cell(0, 4)));
            Assert.That(g.Execute(new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: new Cell(1, 4))).Success, Is.False);
            Assert.That(g.GetSnapshot().PlaysRemaining, Is.EqualTo(before.PlaysRemaining));
        }

        /// <summary>列限幅与镜像换算覆盖最小、奇偶及超大配置，合法高亮始终只包含目标列的全部行。</summary>
        [TestCase(10, 5, "left", 4)] [TestCase(9, 5, "left", 3)]
        [TestCase(6, 5, "left", 2)] [TestCase(2, 5, "left", 0)]
        [TestCase(10, 2, "right", 8)] [TestCase(9, int.MaxValue, "right", 5)]
        public void ColumnIsClampedAndMirrored(int columns, int configured, string side, int expected)
        {
            var d = Document(1); d.rules[0].columns = columns; d.rules[0].bossBombColumn = configured; d.rules[0].playerSide = side;
            var g = Start(d);
            var targets = g.GetLegalActions(new Selection(SelectionKind.BossBombDeployment)).Deploy;
            Assert.That(targets.Count, Is.EqualTo(5)); Assert.That(targets.All(c => c.Col == expected), Is.True);
            Do(g, new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: targets[2]));
            var old = g.GetSnapshot(); End(g); End(g);
            Assert.That(old.BossBombs.Count, Is.EqualTo(1)); Assert.That(g.GetSnapshot().BossBombs, Is.Empty);
        }

        /// <summary>间隔为一时先引爆旧炸弹再布置新炸弹，两者均在玩家行动前完成，随机部署可复现。</summary>
        [Test]
        public void AiExplodesBeforeNewDeploymentAndRandomSequenceIsRepeatable()
        {
            var d = Document(1); d.rules[0].playerControlsBoss = false;
            var a = Start(d); var b = Start(d);
            Assert.That(a.GetSnapshot().BossBombs.Single().DeployedRound, Is.EqualTo(1));
            for (var i = 0; i < 5; i++)
            {
                var events = End(a).Events; End(b);
                Assert.That(a.GetSnapshot().BossBombs.Single().Position, Is.EqualTo(b.GetSnapshot().BossBombs.Single().Position));
                Assert.That(a.GetSnapshot().ActiveSide, Is.EqualTo(Side.Player));
                Assert.That(a.GetSnapshot().BossBombs.Single().DeployedRound, Is.EqualTo(i + 2));
                var kinds = events.Select(e => e.Kind).ToList();
                Assert.That(kinds.IndexOf("bomb-explode"), Is.LessThan(kinds.IndexOf("bomb-deploy")));
            }
            Assert.That(a.Execute(new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: new Cell(0, 4))).Success, Is.False);
        }

        /// <summary>新对局无旧炸弹；对局结束后不能再推进到技能或引爆，非技能回合拒绝部署。</summary>
        [Test]
        public void NewSessionAndFinishedMatchDoNotTriggerBombs()
        {
            var d = Document(10); var g = Start(d); End(g);
            Assert.That(g.Execute(new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: new Cell(0, 4))).Success, Is.False);
            Assert.That(Start(d).GetSnapshot().BossBombs, Is.Empty);
            var lethal = Document(); var miner = lethal.cards.Single(c => c.id == "miner"); miner.atk = 10; miner.range = 10;
            var match = Start(lethal); var id = Put(match, new Cell(0, 8));
            Do(match, new GameCommand(CommandKind.Attack, Side.Player, id, targetId: -2));
            Assert.That(match.Execute(new GameCommand(CommandKind.EndTurn, Side.Player)).Success, Is.False);
            Assert.That(match.GetSnapshot().BossBombs, Is.Empty);
        }

        /// <summary>阶段保留真实部署/爆炸回合，完整事件只分配一次，原有同步自动交回合行为不变。</summary>
        [TestCase("player")] [TestCase("boss")]
        public void AiPresentationKeepsBombRoundsAndPartitionsEvents(string first)
        {
            var d = Document(10); d.rules[0].playerControlsBoss = false; d.rules[0].firstSide = first;
            var g = Start(d);
            PresentationFrame deployed = null, exploded = null;
            while (g.GetSnapshot().Round < 12)
            {
                var result = End(g);
                Assert.That(g.GetSnapshot().ActiveSide, Is.EqualTo(Side.Player));
                Assert.That(result.PresentationFrames.SelectMany(f => f.Events), Is.EqualTo(result.Events));
                foreach (var frame in result.PresentationFrames)
                {
                    if (frame.Events.Any(e => e.Kind == "bomb-deploy")) deployed = frame;
                    if (frame.Events.Any(e => e.Kind == "bomb-explode")) exploded = frame;
                }
                Assert.That(result.PresentationFrames.Last().Snapshot.Round, Is.EqualTo(g.GetSnapshot().Round));
            }
            Assert.That(deployed, Is.Not.Null); Assert.That(exploded, Is.Not.Null);
            Assert.That(deployed.Snapshot.Round, Is.EqualTo(10));
            Assert.That(deployed.Snapshot.ActiveSide, Is.EqualTo(Side.Player));
            Assert.That(deployed.Snapshot.BossBombs.Single().DeployedRound, Is.EqualTo(10));
            Assert.That(deployed.Snapshot.Log.Any(e => e.Kind == "bomb-explode"), Is.False);
            Assert.That(exploded.Snapshot.Round, Is.EqualTo(11));
            Assert.That(exploded.Snapshot.ActiveSide, Is.EqualTo(Side.Player));
            Assert.That(exploded.Snapshot.BossBombs, Is.Empty);
            Assert.That(deployed.Snapshot.BossBombs.Count, Is.EqualTo(1), "后续命令不能改变旧展示快照");
        }

        /// <summary>Boss 先手开局记录不会丢失；间隔一的爆炸阶段尚无新炸弹，后续部署另成阶段。</summary>
        [TestCase("player")] [TestCase("boss")]
        public void InitialAiAndIntervalOneHaveSeparateImmutableFrames(string first)
        {
            var d = Document(1); d.rules[0].playerControlsBoss = false; d.rules[0].firstSide = first;
            var g = Start(d);
            var initial = g.InitialPresentationFrames;
            Assert.That(initial.First(f => f.Events.Any(e => e.Kind == "bomb-deploy")).Snapshot.Round, Is.EqualTo(1));
            Assert.That(initial.Last().Snapshot.ActiveSide, Is.EqualTo(Side.Player));
            var result = End(g);
            var explosion = result.PresentationFrames.Single(f => f.Events.Any(e => e.Kind == "bomb-explode"));
            var deployment = result.PresentationFrames.Single(f => f.Events.Any(e => e.Kind == "bomb-deploy"));
            Assert.That(explosion.Snapshot.BossBombs, Is.Empty);
            Assert.That(deployment.Snapshot.BossBombs.Single().DeployedRound, Is.EqualTo(2));
            Assert.That(result.PresentationFrames.SelectMany(f => f.Events), Is.EqualTo(result.Events));
            Assert.That(initial.First().Snapshot.Round, Is.EqualTo(1));
        }

        /// <summary>手动模式两种先手均只在玩家准备阶段授权 Boss 技能，正常 Boss 回合不能提前部署。</summary>
        [TestCase("player")] [TestCase("boss")]
        public void ManualInitialWindowBelongsToPlayerTurn(string first)
        {
            var d = Document(1); d.rules[0].firstSide = first;
            var g = Start(d);
            if (first == "boss")
            {
                Assert.That(g.GetSnapshot().BossBombPending, Is.False);
                Assert.That(g.Execute(new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: new Cell(0, 4))).Success, Is.False);
                End(g);
            }
            Assert.That(g.GetSnapshot().Round, Is.EqualTo(1));
            Assert.That(g.GetSnapshot().ActiveSide, Is.EqualTo(Side.Player));
            Assert.That(g.GetSnapshot().BossBombPending, Is.True);
            var result = Do(g, new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: new Cell(0, 4)));
            Assert.That(result.PresentationFrames.Single().Snapshot.ActiveSide, Is.EqualTo(Side.Player));
            Put(g, new Cell(0, 0));
        }

        /// <summary>玩家开始时先爆炸，阵亡资源单位不再产出；爆炸到开放操作之间没有额外玩家行动。</summary>
        [Test]
        public void ExplosionRemovesResourceUnitBeforeProduction()
        {
            var d = Document(); var miner = d.cards.Single(c => c.id == "miner");
            miner.hp = 1; miner.produce = 7;
            var g = Start(d); var id = Put(g, new Cell(2, 4));
            End(g); End(g);
            Do(g, new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: new Cell(2, 4)));
            var before = g.GetSnapshot();
            End(g); var result = End(g);
            var after = g.GetSnapshot();
            Assert.That(after.Round, Is.EqualTo(3));
            Assert.That(after.ActiveSide, Is.EqualTo(Side.Player));
            Assert.That(after.Units.Any(u => u.Id == id), Is.False);
            Assert.That(after.Resources, Is.EqualTo(before.Resources + 2), "仅存活的家产出资源");
            Assert.That(after.BossBombs, Is.Empty);
            var kinds = result.Events.Select(e => e.Kind).ToList();
            Assert.That(kinds.IndexOf("bomb-explode"), Is.LessThan(kinds.IndexOf("draw")));
        }

        /// <summary>部署后玩家有一次完整行动机会，可移出十字范围；下一次玩家行动开始前按新位置爆炸。</summary>
        [Test]
        public void PlayerCanEscapeBombDuringDeploymentRound()
        {
            var d = Document(); var miner = d.cards.Single(c => c.id == "miner"); miner.hp = 1; miner.moveDistance = 2;
            var g = Start(d); var id = Put(g, new Cell(2, 4));
            End(g); End(g);
            Do(g, new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: new Cell(2, 4)));
            Do(g, new GameCommand(CommandKind.Move, Side.Player, id, target: new Cell(3, 5)));
            End(g); End(g);
            Assert.That(g.GetSnapshot().Round, Is.EqualTo(3));
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == id).Hp, Is.EqualTo(1));
            Assert.That(g.GetSnapshot().BossBombs, Is.Empty);
        }

        /// <summary>缺失/非法炸弹参数被业务校验拒绝；默认表声明三个必填整数字段。</summary>
        [TestCase(0, 3, 5)] [TestCase(10, -1, 5)] [TestCase(10, 3, 0)]
        public void InvalidConfigurationIsRejected(int interval, int damage, int column)
        {
            var d = Document(); d.rules[0].bossBombInterval = interval; d.rules[0].bossBombDamage = damage; d.rules[0].bossBombColumn = column;
            Assert.That(ConfigValidation.Validate(d).Any(e => e.Contains("bossBomb")), Is.True);
        }
    }
}
