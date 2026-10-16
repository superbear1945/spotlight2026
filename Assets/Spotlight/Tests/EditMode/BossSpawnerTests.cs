using System;
using System.Linq;
using NUnit.Framework;

namespace Spotlight.Tests
{
    /// <summary>
    /// 刷怪技能规则回归。输入为独立配置及公开命令；输出为条件、身份、时机和权限断言。
    /// 所有棋盘布置均通过 Execute，不直接修改对局私有状态，随机固定种子以便复现。
    /// </summary>
    public class BossSpawnerTests
    {
        /// <summary>创建可在全盘布置的测试配置。输入：控制模式、先手及召唤失调；输出：与正式配置隔离的夹具。</summary>
        internal static ConfigDocument Document(bool manual = true, string first = "player", bool sickness = false)
        {
            var d = GameSessionTests.Document();
            var r = d.rules[0];
            r.bossSpawnerAttackThreshold = 12; r.bossSpawnerUnitThreshold = 2;
            r.bossSpawnerCountFromColumn = 4; r.bossSpawnerDeployFromColumn = 5;
            r.homeRegionWidth = 10; r.playerPlays = 100; r.bossPlays = 100;
            r.drawCount = 60; r.handLimit = 100; r.firstSide = first;
            r.playerControlsBoss = manual; r.summoningSickness = sickness; r.bossBombInterval = 1000;
            var soldier = d.cards.Single(c => c.id == "soldier");
            soldier.atk = 6; soldier.range = 10; soldier.moveDistance = 10;
            var boss = d.cards.Single(c => c.id == "boss");
            boss.atk = 0; boss.moveDistance = 0;
            d.deck = new[] { new DeckRow { cardType = "soldier", count = 30 }, new DeckRow { cardType = "miner", count = 30 } };
            return d;
        }

        /// <summary>建立独立对局。输入：可选配置与种子；输出：真实规则入口。</summary>
        internal static GameSession Start(ConfigDocument d = null, int seed = 42) => new GameSession(new GameConfiguration(d ?? Document()), new SeededRandom(seed), 2000);

        /// <summary>执行公开命令并检查成功。输入：对局和命令；输出：包含阶段快照的结果，失败直接定位原因。</summary>
        internal static CommandResult Do(GameSession game, GameCommand command)
        {
            var result = game.Execute(command);
            Assert.That(result.Success, Is.True, result.Error);
            return result;
        }

        /// <summary>交出行动权。输入：对局；输出：结束回合的真实事件，不隐式跳过技能。</summary>
        internal static CommandResult End(GameSession game) => Do(game, new GameCommand(CommandKind.EndTurn, game.GetSnapshot().ActiveSide, skipDeployment: true));

        /// <summary>部署玩家测试单位。输入：卡种与内部坐标；输出：新单位 ID，来源卡正常进入墓地。</summary>
        internal static long Put(GameSession game, string type, Cell cell)
        {
            var card = game.GetSnapshot().Hand.First(c => c.TypeId == type);
            Do(game, new GameCommand(CommandKind.Deploy, Side.Player, card.Id, cell));
            return game.GetSnapshot().Units.Single(u => u.Position == cell).Id;
        }

        /// <summary>布置两名达到默认阈值的前线攻击单位。输入：对局；输出：无，仍停留在玩家回合。</summary>
        internal static void Arm(GameSession game)
        {
            Put(game, "soldier", new Cell(0, 3));
            Put(game, "soldier", new Cell(4, 3));
        }

        /// <summary>部署技能据点。输入：对局与格子；输出：刷怪卡 ID，必须已经获得真实部署机会。</summary>
        internal static long DeploySpawner(GameSession game, Cell cell)
        {
            Do(game, new GameCommand(CommandKind.DeployBossSpawner, Side.Boss, target: cell));
            return game.GetSnapshot().Units.Single(u => u.IsBossSpawner).Id;
        }

        /// <summary>阈值包含等号；攻击类按 category 判断，即使攻击为零仍可计数，资源卡只贡献总攻击力。</summary>
        [TestCase(11, 2, false)] [TestCase(12, 1, false)] [TestCase(12, 2, true)]
        public void TriggerUsesAllAttackButOnlyAttackCategoryForCount(int attack, int count, bool expected)
        {
            var d = Document(); d.cards.Single(c => c.id == "soldier").atk = 0;
            d.cards.Single(c => c.id == "miner").atk = attack;
            var game = Start(d);
            for (var i = 0; i < count; i++) Put(game, "soldier", new Cell(i, 3));
            Put(game, "miner", new Cell(4, 8));
            End(game);
            Assert.That(game.GetSnapshot().BossSpawnerPending, Is.EqualTo(expected));
        }

        /// <summary>列阈值从玩家一侧起算并含边界，欠费与已行动不影响攻击统计。</summary>
        [TestCase("left", 3, true)] [TestCase("left", 2, false)]
        [TestCase("right", 6, true)] [TestCase("right", 7, false)]
        public void ColumnsMirrorAndIncludeBoundary(string side, int column, bool expected)
        {
            var d = Document(); d.rules[0].playerSide = side;
            var soldier = d.cards.Single(c => c.id == "soldier"); soldier.resourceComponent = true; soldier.upkeep = 100;
            var game = Start(d);
            Put(game, "soldier", new Cell(0, column)); Put(game, "soldier", new Cell(4, column));
            End(game);
            Assert.That(game.GetSnapshot().BossSpawnerPending, Is.EqualTo(expected));
            if (expected)
            {
                var cells = game.GetLegalActions(new Selection(SelectionKind.BossSpawnerDeployment)).Deploy;
                Assert.That(cells.All(c => side == "left" ? c.Col >= 4 : c.Col <= 5), Is.True);
                Assert.That(cells.Any(c => c.Col == (side == "left" ? 4 : 5)), Is.True);
            }
        }

        /// <summary>手动机会阻挡普通操作，拒绝区域外、占位、越界和重复部署，失败不改变快照。</summary>
        [Test]
        public void ManualOpportunityIsValidatedAndDoesNotSpendPlays()
        {
            var game = Start(); Arm(game); End(game);
            var before = game.GetSnapshot();
            Assert.That(before.BossSpawnerPending, Is.True);
            Assert.That(game.GetLegalActions(new Selection(SelectionKind.None)).Deploy, Is.Empty);
            foreach (var command in new[] {
                new GameCommand(CommandKind.EndTurn, Side.Boss, skipDeployment:true),
                new GameCommand(CommandKind.BossDeploy, Side.Boss, target:new Cell(1, 8)),
                new GameCommand(CommandKind.MoveHome, Side.Boss, -2, new Cell(1, 9)),
                new GameCommand(CommandKind.DeployBossSpawner, Side.Player, target:new Cell(2, 5)),
                new GameCommand(CommandKind.DeployBossSpawner, Side.Boss, target:new Cell(0, 3)),
                new GameCommand(CommandKind.DeployBossSpawner, Side.Boss, target:new Cell(2, 9)),
                new GameCommand(CommandKind.DeployBossSpawner, Side.Boss, target:new Cell(-1, 5)) })
                Assert.That(game.Execute(command).Success, Is.False);
            Assert.That(game.GetSnapshot().Units.Select(u => u.Id), Is.EqualTo(before.Units.Select(u => u.Id)));
            Assert.That(game.GetSnapshot().PlaysRemaining, Is.EqualTo(before.PlaysRemaining));
            game.SetInputBlocked(true);
            Assert.That(game.GetLegalActions(new Selection(SelectionKind.BossSpawnerDeployment)).Deploy, Is.Empty);
            Assert.That(game.Execute(new GameCommand(CommandKind.SkipBossSpawner, Side.Boss)).Success, Is.False);
            game.SetInputBlocked(false);
            DeploySpawner(game, new Cell(2, 5));
            Assert.That(game.GetSnapshot().PlaysRemaining, Is.EqualTo(before.PlaysRemaining));
            Assert.That(game.Execute(new GameCommand(CommandKind.DeployBossSpawner, Side.Boss, target:new Cell(1, 5))).Success, Is.False);
            Assert.That(before.BossSpawnerPending, Is.True, "旧快照不能被后续命令改变");
        }

        /// <summary>跳过后普通行动恢复，回合内不重新检查，下次 Boss 回合可重新获得机会。</summary>
        [Test]
        public void SkipDoesNotAccumulateAndNextTurnRechecks()
        {
            var game = Start(); Arm(game); End(game);
            Do(game, new GameCommand(CommandKind.SkipBossSpawner, Side.Boss));
            Assert.That(game.GetSnapshot().BossSpawnerPending, Is.False);
            Assert.That(game.Execute(new GameCommand(CommandKind.DeployBossSpawner, Side.Boss, target:new Cell(2, 5))).Success, Is.False);
            End(game); End(game);
            Assert.That(game.GetSnapshot().BossSpawnerPending, Is.True);
        }

        /// <summary>两种先手下均只在之后的 Boss 回合生成；旧据点不重置，战机遵守召唤失调。</summary>
        [TestCase("player", false)] [TestCase("player", true)]
        [TestCase("boss", false)] [TestCase("boss", true)]
        public void ProducesNextBossTurnWithConfiguredSickness(string first, bool sickness)
        {
            var game = Start(Document(first:first, sickness:sickness));
            if (first == "boss") End(game);
            Arm(game); End(game);
            var id = DeploySpawner(game, new Cell(2, 5));
            var old = game.GetSnapshot();
            Assert.That(old.Units.Count(u => u.Owner == Side.Boss), Is.EqualTo(1));
            End(game); End(game);
            var current = game.GetSnapshot();
            var fighter = current.Units.Single(u => u.Owner == Side.Boss && !u.IsBossSpawner);
            Assert.That(fighter.TypeId, Is.EqualTo("boss")); Assert.That(fighter.Hp, Is.EqualTo(3));
            Assert.That(fighter.Acted, Is.EqualTo(sickness)); Assert.That(fighter.BossSlotId, Is.Null);
            Assert.That(current.Units.Single(u => u.IsBossSpawner).Id, Is.EqualTo(id));
            Assert.That(current.BossSpawnerPending, Is.False);
            Assert.That(old.Units.Count, Is.EqualTo(3));
        }

        /// <summary>固定据点不能移动、攻击、发起或接受互换；条件降低仍生成，摧毁后不影响已经生成的战机。</summary>
        [Test]
        public void ImmobileSpawnerPersistsAfterPressureDropsAndStopsOnDeath()
        {
            var game = Start(); Arm(game); End(game);
            var id = DeploySpawner(game, new Cell(2, 5));
            Do(game, new GameCommand(CommandKind.BossDeploy, Side.Boss, target:new Cell(2, 6)));
            var neighbor = game.GetSnapshot().Units.Single(u => u.Position == new Cell(2, 6));
            Assert.That(game.GetLegalActions(new Selection(SelectionKind.Unit, id)).Swap, Is.Empty);
            foreach (var command in new[] {
                new GameCommand(CommandKind.Move, Side.Boss, id, new Cell(1, 5)),
                new GameCommand(CommandKind.Attack, Side.Boss, id, targetId:1),
                new GameCommand(CommandKind.Swap, Side.Boss, id, targetId:neighbor.Id),
                new GameCommand(CommandKind.Swap, Side.Boss, neighbor.Id, targetId:id) })
                Assert.That(game.Execute(command).Success, Is.False);
            End(game);
            var soldiers = game.GetSnapshot().Units.Where(u => u.Owner == Side.Player).ToArray();
            Do(game, new GameCommand(CommandKind.Withdraw, Side.Player, soldiers[0].Id));
            End(game);
            Assert.That(game.GetSnapshot().Units.Count(u => u.Owner == Side.Boss), Is.EqualTo(3));
            End(game);
            Do(game, new GameCommand(CommandKind.Attack, Side.Player, soldiers[1].Id, targetId:id));
            var count = game.GetSnapshot().Units.Count(u => u.Owner == Side.Boss);
            End(game);
            Assert.That(game.GetSnapshot().Units.Count(u => u.Owner == Side.Boss), Is.EqualTo(count));
            Assert.That(game.GetSnapshot().BossReplacements.All(s => !s.UnitId.HasValue), Is.True);
        }

        /// <summary>摧毁后可重新部署，新据点重新等待一回合，不重复使用旧据点计时。</summary>
        [Test]
        public void DestroyedSpawnerCanBeRedeployed()
        {
            var game = Start(); Arm(game); End(game); var first = DeploySpawner(game, new Cell(2, 5)); End(game);
            var soldier = game.GetSnapshot().Units.First(u => u.Owner == Side.Player);
            Do(game, new GameCommand(CommandKind.Attack, Side.Player, soldier.Id, targetId:first)); End(game);
            Assert.That(game.GetSnapshot().BossSpawnerPending, Is.True);
            var second = DeploySpawner(game, new Cell(2, 5));
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(game.GetSnapshot().Units.Count(u => u.Owner == Side.Boss), Is.EqualTo(1));
        }

        /// <summary>八邻唯一空格可为斜角；存在空格时不会顶替玩家单位。</summary>
        [Test]
        public void DiagonalEmptyCellIsPreferredOverReplacingPlayers()
        {
            var game = Start(); Arm(game);
            var empty = new Cell(1, 4);
            for (var row = 1; row <= 3; row++) for (var col = 4; col <= 6; col++)
                if (new Cell(row,col) != empty && new Cell(row,col) != new Cell(2,5)) Put(game, "miner", new Cell(row,col));
            End(game); DeploySpawner(game, new Cell(2,5)); End(game);
            var old = game.GetSnapshot(); End(game);
            Assert.That(game.GetSnapshot().Units.Single(u => !u.IsBossSpawner && u.Owner == Side.Boss).Position, Is.EqualTo(empty));
            Assert.That(game.GetSnapshot().Units.Count(u => u.Owner == Side.Player), Is.EqualTo(old.Units.Count(u => u.Owner == Side.Player)));
        }

        /// <summary>八邻占满时直接顶替一个高血量玩家单位；来源卡集合守恒且展示分为移除、生成两阶段。</summary>
        [Test]
        public void FullNeighborhoodReplacesOnePlayerWithoutRecyclingCards()
        {
            var game = Start(); Arm(game);
            for (var row = 1; row <= 3; row++) for (var col = 4; col <= 6; col++)
                if (row != 2 || col != 5) Put(game, "miner", new Cell(row,col));
            End(game); DeploySpawner(game, new Cell(2,5)); End(game);
            var old = game.GetSnapshot(); var result = End(game); var current = game.GetSnapshot();
            var removed = old.Units.Where(u => !current.Units.Any(n => n.Id == u.Id)).Single();
            Assert.That(removed.Hp, Is.EqualTo(4)); Assert.That(removed.Owner, Is.EqualTo(Side.Player));
            Assert.That(current.Units.Single(u => u.Position == removed.Position).TypeId, Is.EqualTo("boss"));
            Assert.That(current.Hand.Concat(current.DrawPile).Concat(current.Graveyard).Select(c => c.Id).OrderBy(x => x),
                Is.EqualTo(old.Hand.Concat(old.DrawPile).Concat(old.Graveyard).Select(c => c.Id).OrderBy(x => x)));
            Assert.That(result.Events.Count(e => e.Kind == "spawn-replace"), Is.EqualTo(1));
            Assert.That(result.PresentationFrames.Any(f => f.Snapshot.Units.All(u => u.Position != removed.Position)), Is.True);
        }

        /// <summary>Boss 战机占满八邻则暂停，位置腾出后恢复，不累计；刷怪卡和战机均不登记固定兵位。</summary>
        [Test]
        public void FullBossNeighborhoodPausesAndResumesWithoutBacklog()
        {
            var d = Document(); d.cards.Single(c => c.id == "boss").moveDistance = 10;
            var game = Start(d); Arm(game); End(game); DeploySpawner(game, new Cell(2,5));
            for (var row = 1; row <= 3; row++) for (var col = 4; col <= 6; col++)
                if (row != 2 || col != 5) Do(game, new GameCommand(CommandKind.BossDeploy, Side.Boss, target:new Cell(row,col)));
            End(game); var waiting = End(game);
            Assert.That(waiting.Events.Any(e => e.Kind == "spawner-wait"), Is.True);
            var moving = game.GetSnapshot().Units.Single(u => u.Position == new Cell(1,4));
            Do(game, new GameCommand(CommandKind.Move, Side.Boss, moving.Id, new Cell(0,4)));
            End(game); End(game);
            Assert.That(game.GetSnapshot().Units.Count(u => u.Owner == Side.Boss), Is.EqualTo(10));
            Assert.That(game.GetSnapshot().Units.Single(u => u.Position == new Cell(1,4)).Id, Is.Not.EqualTo(moving.Id));
            Assert.That(game.GetSnapshot().Units.Where(u => u.Owner == Side.Boss).All(u => u.BossSlotId == null), Is.True);
        }

        /// <summary>角落只使用棋盘内邻格；占位的家不可顶替，也不导致错误胜负。</summary>
        [Test]
        public void EdgeNeighborsAndHomesAreProtected()
        {
            var d = Document(); d.rules[0].rows = 1; d.rules[0].bossSpawnerAttackThreshold = 6; d.rules[0].bossSpawnerUnitThreshold = 1;
            var game = Start(d); Put(game,"soldier",new Cell(0,3)); Put(game,"miner",new Cell(0,7));
            End(game); DeploySpawner(game,new Cell(0,8)); End(game); End(game);
            var snapshot = game.GetSnapshot();
            Assert.That(snapshot.Units.Single(u => u.Owner == Side.Boss && !u.IsBossSpawner).Position, Is.EqualTo(new Cell(0,7)));
            Assert.That(snapshot.Homes.Single(h => h.Owner == Side.Boss).Hp, Is.EqualTo(10));
            End(game); var result = End(game);
            Assert.That(result.Events.Any(e => e.Kind == "spawner-wait"), Is.True);
            Assert.That(game.GetSnapshot().Outcome, Is.EqualTo(MatchOutcome.None));
        }

        /// <summary>超宽列范围不钳制；没有候选空格时自动跳过并保留普通行动能力。</summary>
        [TestCase(true)] [TestCase(false)]
        public void OutOfBoardColumnsRemainEmpty(bool countRange)
        {
            var d = Document();
            if (countRange) d.rules[0].bossSpawnerCountFromColumn = 11; else d.rules[0].bossSpawnerDeployFromColumn = 11;
            var game = Start(d); Arm(game); End(game);
            Assert.That(game.GetSnapshot().BossSpawnerPending, Is.False);
            End(game);
        }

        /// <summary>手牌超限必须先完成弃牌才读取技能条件，不能在弃牌中提前进入 Boss 技能窗口。</summary>
        [Test]
        public void DiscardCompletesBeforeOpportunity()
        {
            var d = Document(); d.rules[0].handLimit = 57;
            var game = Start(d); Arm(game); End(game);
            Assert.That(game.GetSnapshot().Phase, Is.EqualTo(MatchPhase.Discard));
            Assert.That(game.GetSnapshot().BossSpawnerPending, Is.False);
            Do(game, new GameCommand(CommandKind.Discard, Side.Player, game.GetSnapshot().Hand[0].Id));
            Assert.That(game.GetSnapshot().BossSpawnerPending, Is.True);
        }

        /// <summary>AI 自动部署和生成使用种子随机源；技能部署先于固定兵位，外部不能冒充 AI 提交技能。</summary>
        [TestCase("player")] [TestCase("boss")]
        public void AiIsDeterministicAndUsesSameOpportunity(string first)
        {
            var a = Start(Document(false,first)); var b = Start(Document(false,first));
            Arm(a); Arm(b); var result = End(a); End(b);
            Assert.That(a.GetSnapshot().Units.Single(u => u.IsBossSpawner).Position, Is.EqualTo(b.GetSnapshot().Units.Single(u => u.IsBossSpawner).Position));
            Assert.That(result.Events.Count(e => e.Kind == "spawner-deploy"), Is.EqualTo(1));
            End(a); End(b);
            Assert.That(a.GetSnapshot().Units.Select(u => u.Position), Is.EqualTo(b.GetSnapshot().Units.Select(u => u.Position)));
            Assert.That(a.GetSnapshot().Units.Count(u => u.IsBossSpawner), Is.EqualTo(1));
            Assert.That(a.Execute(new GameCommand(CommandKind.DeployBossSpawner, Side.Boss, target:new Cell(2,5))).Success, Is.False);
        }
    }
}
