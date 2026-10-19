using System;
using System.Linq;
using NUnit.Framework;

namespace Spotlight.Tests
{
    /// <summary>三阶段生成、死亡与 AI 的行为回归。所有状态均由独立配置和公开命令建立，不修改会话私有字段。</summary>
    public class BossGuardTests
    {
        /// <summary>创建包含两类威胁及特殊目标的隔离文档；输入控制模式和召唤失调，输出可自由部署的夹具。</summary>
        internal static ConfigDocument Document(bool manual = true, bool sickness = false)
        {
            var d = BossPhaseTests.Document(manual, sickness: sickness);
            d.rules[0].drawCount = 100;
            d.rules[0].handLimit = 200;
            d.cards = d.cards.Concat(new[]
            {
                new CardRow { id = "threatA", name = "测试低攻目标", category = "attack", tier = "basic", hp = 100, atk = 1, range = 1, moveDistance = 1, viewKey = "default" },
                new CardRow { id = "threatB", name = "测试高攻目标", category = "attack", tier = "basic", hp = 100, atk = 5, range = 1, moveDistance = 1, viewKey = "default" },
                new CardRow { id = "specialTarget", name = "测试特殊目标", category = "special", tier = "advanced", hp = 100, moveDistance = 0, viewKey = "default" }
            }).ToArray();
            return d;
        }

        /// <summary>部署三名玩家攻击者并通过攻击进入二阶段、击败第一艘战舰；保留另一艘供用例布置场景后触发三阶段。</summary>
        internal static GameSession Prepare(ConfigDocument d)
        {
            var deck = new[] { "soldier", "miner", "threatA", "threatB", "specialTarget" }.Select(id => new DeckEntry(id, 8));
            var game = new GameSession(new GameConfiguration(d).WithDeck(deck, false), new SeededRandom(42), 5000);
            var first = BossPhaseTests.Put(game, "soldier", new Cell(0, 0));
            var second = BossPhaseTests.Put(game, "soldier", new Cell(0, 1));
            BossPhaseTests.Put(game, "soldier", new Cell(0, 2));
            if (d.rules[0].summoningSickness) { BossPhaseTests.End(game); BossPhaseTests.End(game); }
            BossPhaseTests.Do(game, new GameCommand(CommandKind.Attack, Side.Player, first, targetId: -2));
            var ship = game.GetSnapshot().BossWarships.First().UnitId.Value;
            BossPhaseTests.Do(game, new GameCommand(CommandKind.Attack, Side.Player, second, targetId: ship));
            return game;
        }

        /// <summary>通过真实攻击击败最后一艘指定战舰，输出本次结算和阶段表现帧。</summary>
        internal static CommandResult Finish(GameSession game)
        {
            var state = game.GetSnapshot();
            return BossPhaseTests.Do(game, new GameCommand(CommandKind.Attack, Side.Player,
                state.Units.First(u => u.Owner == Side.Player && u.TypeId == "soldier" && u.CanAct).Id,
                targetId: state.BossWarships.Single(s => s.Status == BossWarshipStatus.Alive).UnitId.Value));
        }

        /// <summary>移除负责推进阶段的测试攻击者，避免它们干扰护卫舰索敌；不回收来源卡也不修改单位字段。</summary>
        internal static void WithdrawStrikers(GameSession game)
        {
            foreach (var unit in game.GetSnapshot().Units.Where(u => u.Owner == Side.Player && u.TypeId == "soldier"))
                BossPhaseTests.Do(game, new GameCommand(CommandKind.Withdraw, Side.Player, unit.Id));
        }

        /// <summary>两种朝向和召唤失调下只生成一次，属性来自配置、旧快照不变，后续回合不会补回阵亡护卫舰。</summary>
        [TestCase("left", false)] [TestCase("right", false)] [TestCase("left", true)] [TestCase("right", true)]
        public void FormationUsesConfigurationAndDoesNotRespawn(string side, bool sickness)
        {
            var d = Document(sickness: sickness); d.rules[0].playerSide = side;
            var front = d.cards.Single(c => c.id == "guard1"); front.hp = 11; front.atk = 4; front.range = 3;
            var game = Prepare(d); var before = game.GetSnapshot(); var result = Finish(game);
            var state = game.GetSnapshot(); var guards = state.Units.Where(u => u.AiTargetPolicy == AiTargetPolicy.BossGuard).ToArray();
            Assert.That(state.BossPhase, Is.EqualTo(BossPhase.PhaseThree));
            Assert.That(state.BossInvulnerable, Is.False);
            Assert.That(guards.Length, Is.EqualTo(9));
            Assert.That(guards.Count(u => u.TypeId == "guard1"), Is.EqualTo(5));
            Assert.That(guards.Where(u => u.TypeId == "guard1").All(u => u.Hp == 11 && u.MaxHp == 11 && u.Attack == 4 && u.Range == 3), Is.True);
            Assert.That(guards.Where(u => u.TypeId == "guard1").All(u => u.Position.Col == (side == "left" ? 8 : 1)), Is.True);
            Assert.That(guards.All(u => u.Acted == sickness && u.BossSlotId == null), Is.True);
            Assert.That(state.PlaysRemaining, Is.EqualTo(before.PlaysRemaining));
            Assert.That(state.Hand.Count + state.DrawPile.Count + state.Graveyard.Count, Is.EqualTo(40));
            Assert.That(state.Homes.All(h => state.Units.All(u => u.Position != h.Position)), Is.True);
            Assert.That(before.BossPhase, Is.EqualTo(BossPhase.PhaseTwo));
            Assert.That(before.Units.Any(u => u.AiTargetPolicy == AiTargetPolicy.BossGuard), Is.False);
            Assert.That(result.Events.Count(e => e.Kind == "guard-deploy"), Is.EqualTo(9));
            Assert.That(result.PresentationFrames.Count, Is.GreaterThanOrEqualTo(9));
            BossPhaseTests.End(game); BossPhaseTests.End(game);
            var attacker = game.GetSnapshot().Units.First(u => u.Owner == Side.Player && u.TypeId == "soldier");
            BossPhaseTests.Do(game, new GameCommand(CommandKind.Attack, Side.Player, attacker.Id, targetId: guards[0].Id));
            BossPhaseTests.End(game); BossPhaseTests.End(game);
            Assert.That(game.GetSnapshot().Units.Count(u => u.AiTargetPolicy == AiTargetPolicy.BossGuard), Is.EqualTo(8));
            Assert.That(guards[0].Hp, Is.EqualTo(11), "已交付的快照不能被后续阵亡修改");
        }

        /// <summary>手动移动 Boss 家后，生成使用当前列；护卫舰随后可手动移动，普通放牌次数不被阶段技能消耗。</summary>
        [Test]
        public void FormationFollowsMovedHomeAndManualCommandsRemainAvailable()
        {
            var d = Document(); var game = Prepare(d);
            BossPhaseTests.End(game);
            BossPhaseTests.Do(game, new GameCommand(CommandKind.MoveHome, Side.Boss, -2, new Cell(2, 8)));
            BossPhaseTests.End(game); Finish(game);
            var state = game.GetSnapshot();
            Assert.That(state.Units.Where(u => u.TypeId == "guard1").All(u => u.Position.Col == 7), Is.True);
            Assert.That(state.Units.Where(u => u.TypeId == "guard2").All(u => u.Position.Col == 8), Is.True);
            Assert.That(state.Units.Any(u => u.Position == new Cell(2, 8)), Is.False);
            BossPhaseTests.End(game);
            var guard = game.GetSnapshot().Units.First(u => u.TypeId == "guard1");
            var moves = game.GetLegalActions(new Selection(SelectionKind.Unit, guard.Id)).Move;
            BossPhaseTests.Do(game, new GameCommand(CommandKind.Move, Side.Boss, guard.Id, moves.First()));
            Assert.That(game.GetSnapshot().PlaysRemaining, Is.EqualTo(d.rules[0].bossPlays));
        }

        /// <summary>同一卡种的普通 Boss 放牌仍用普通策略，只有三阶段技能生成才赋予护卫策略；策略不由 ID 或名字推导。</summary>
        [Test]
        public void OrdinaryDeploymentOfTheSameCardKeepsStandardPolicy()
        {
            var d = Document(); d.rules[0].bossCardType = "guard1";
            d.cards.Single(c => c.id == "guard1").name = "已改名的前列单位";
            var game = Prepare(d); Finish(game); BossPhaseTests.End(game);
            BossPhaseTests.Do(game, new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(1, 5)));
            var state = game.GetSnapshot();
            Assert.That(state.Units.Single(u => u.Position == new Cell(1, 5)).AiTargetPolicy, Is.EqualTo(AiTargetPolicy.Standard));
            Assert.That(state.Units.Count(u => u.TypeId == "guard1" && u.AiTargetPolicy == AiTargetPolicy.BossGuard), Is.EqualTo(5));
        }

        /// <summary>玩家及 Boss 占位都按死亡处理，标记奖励和死亡加攻只触发一次；来源牌不重复回收且新舰不继承标记。</summary>
        [Test]
        public void ReplacementUsesDeathHooksForBothSidesWithoutDuplicatingCards()
        {
            var d = Document();
            var miner = d.cards.Single(c => c.id == "miner"); miner.markOnEnterComponent = true; miner.markOnEnterRadius = 10; miner.markRewardResource = 3;
            var grower = d.cards.Single(c => c.id == "threatA"); grower.deathGrowthComponent = true; grower.deathGrowthPerKill = 2; grower.deathGrowthLimit = 100;
            var game = Prepare(d);
            var observer = BossPhaseTests.Put(game, "threatA", new Cell(1, 3));
            BossPhaseTests.Put(game, "miner", new Cell(1, 4));
            var victim = BossPhaseTests.Put(game, "threatB", new Cell(4, 8));
            BossPhaseTests.End(game);
            BossPhaseTests.Do(game, new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(0, 9)));
            var enemy = game.GetSnapshot().Units.Single(u => u.Position == new Cell(0, 9)).Id;
            BossPhaseTests.End(game);
            var before = game.GetSnapshot(); var result = Finish(game); var after = game.GetSnapshot();
            foreach (var id in new[] { victim, enemy })
            {
                Assert.That(after.Units.Any(u => u.Id == id), Is.False);
                Assert.That(result.Events.Count(e => e.Kind == "death" && e.SubjectId == id), Is.EqualTo(1));
                Assert.That(result.Events.Count(e => e.Kind == "mark-reward" && e.SubjectId == id), Is.EqualTo(1));
            }
            var deaths = result.Events.Count(e => e.Kind == "death");
            Assert.That(after.Units.Single(u => u.Id == observer).Attack, Is.EqualTo(before.Units.Single(u => u.Id == observer).Attack + deaths * 2));
            Assert.That(after.Resources, Is.EqualTo(before.Resources + deaths * 3));
            Assert.That(after.Graveyard.Count, Is.EqualTo(before.Graveyard.Count));
            Assert.That(after.BossGraveyard.Count(c => c.Id == before.Units.Single(u => u.Id == enemy).SourceCardId), Is.EqualTo(1));
            Assert.That(result.Events.Any(e => e.Kind == "action-restored"), Is.False);
            Assert.That(result.Events.Where(e => e.Kind == "guard-deploy").All(e => after.Units.Any(u => u.Id == e.SubjectId && u.DamageMarkLayers == 0)), Is.True);
        }

        /// <summary>AI 用例只留下合法目标；输入为护卫舰射程，输出为已生成护卫舰但尚未交给 Boss 的对局。</summary>
        static GameSession AiGame(int range = 2)
        {
            var d = Document(manual: false);
            foreach (var card in d.cards.Where(c => c.id == "guard1" || c.id == "guard2")) card.range = range;
            var game = Prepare(d); Finish(game); WithdrawStrikers(game);
            return game;
        }

        /// <summary>两个攻击目标同距时优先当前攻击更高者，距离更近则覆盖攻击力排序；以回合首个攻击事件核对。</summary>
        [TestCase(false)] [TestCase(true)]
        public void GuardTargetsDistanceBeforeAttack(bool sameDistance)
        {
            var game = AiGame(20);
            var near = BossPhaseTests.Put(game, "threatA", new Cell(2, 7));
            var strong = BossPhaseTests.Put(game, "threatB", sameDistance ? new Cell(1, 7) : new Cell(2, 5));
            // 使用 (3,7) 与 (1,7) 构造同距目标，避开已被护卫舰占用的第 8 列。
            if (sameDistance)
            {
                BossPhaseTests.Do(game, new GameCommand(CommandKind.Move, Side.Player, near, new Cell(3, 7)));
            }
            var result = BossSpawnerTests.End(game);
            var firstAttack = result.Events.First(e => e.Kind == "attack");
            Assert.That(firstAttack.SubjectId, Is.EqualTo(sameDistance ? strong : near));
        }

        /// <summary>射程内有低优先级目标时先攻击，不为远处攻击类目标放弃攻击；特殊目标优先于资源目标。</summary>
        [Test]
        public void InRangeSpecialBeatsResourceAndOutOfRangeThreat()
        {
            var game = AiGame();
            var special = BossPhaseTests.Put(game, "specialTarget", new Cell(0, 7));
            BossPhaseTests.Put(game, "miner", new Cell(1, 7));
            BossPhaseTests.Put(game, "threatB", new Cell(4, 3));
            var result = BossSpawnerTests.End(game);
            Assert.That(result.Events.First(e => e.Kind == "attack").SubjectId, Is.EqualTo(special));
        }

        /// <summary>玩家家在同一射程内优先于特殊目标，而攻击类目标又优先于可直接摧毁的玩家家。</summary>
        [TestCase(false)] [TestCase(true)]
        public void HomePriorityIsBetweenAttackAndSpecial(bool addThreat)
        {
            var game = AiGame(20);
            BossPhaseTests.Put(game, "specialTarget", new Cell(0, 7));
            var target = addThreat ? BossPhaseTests.Put(game, "threatA", new Cell(2, 6)) : -1;
            var result = BossSpawnerTests.End(game);
            Assert.That(result.Events.First(e => e.Kind == "attack").SubjectId, Is.EqualTo(target));
        }

        /// <summary>没有射程内目标时优先追击攻击类目标；前排向左前进，后排不穿过占位单位。</summary>
        [Test]
        public void GuardsPursueReachableThreatsUsingOrdinaryMovement()
        {
            var game = AiGame();
            BossPhaseTests.Put(game, "threatA", new Cell(0, 4));
            var guard = game.GetSnapshot().Units.First(u => u.AiTargetPolicy == AiTargetPolicy.BossGuard);
            BossSpawnerTests.End(game);
            var after = game.GetSnapshot().Units.Single(u => u.Id == guard.Id);
            Assert.That(after.Position, Is.EqualTo(new Cell(0, 7)));
            Assert.That(after.Acted, Is.True);
        }

        /// <summary>目标攻击力在死亡加攻后高于基础值时，索敌必须使用当前值；低血量其他目标不能覆盖威胁顺序。</summary>
        [Test]
        public void TargetRankingUsesCurrentAttackInsteadOfArchetypeOrLethality()
        {
            var d = Document(manual: false);
            foreach (var guard in d.cards.Where(c => c.id == "guard1" || c.id == "guard2")) guard.range = 20;
            var growing = d.cards.Single(c => c.id == "threatA");
            growing.deathGrowthComponent = true; growing.deathGrowthPerKill = 10; growing.deathGrowthLimit = 10;
            d.cards.Single(c => c.id == "threatB").hp = 1;
            var game = Prepare(d);
            var currentThreat = BossPhaseTests.Put(game, "threatA", new Cell(1, 7));
            Finish(game); WithdrawStrikers(game);
            BossPhaseTests.Put(game, "threatB", new Cell(3, 7));
            Assert.That(game.GetSnapshot().Units.Single(u => u.Id == currentThreat).Attack, Is.EqualTo(11));
            var result = BossSpawnerTests.End(game);
            Assert.That(result.Events.First(e => e.Kind == "attack").SubjectId, Is.EqualTo(currentThreat));
        }

        /// <summary>整列资源单位封路时，无法到达攻击类目标的射程位置；护卫舰必须回退到可达资源目标而非永久追击。</summary>
        [Test]
        public void UnreachableThreatFallsBackToReachableResource()
        {
            var d = Document(manual: false);
            foreach (var guard in d.cards.Where(c => c.id == "guard1" || c.id == "guard2")) guard.range = 1;
            var game = Prepare(d); Finish(game); WithdrawStrikers(game);
            BossPhaseTests.Put(game, "threatA", new Cell(4, 3));
            for (var row = 0; row < 5; row++) BossPhaseTests.Put(game, "miner", new Cell(row, 5));
            var guardId = game.GetSnapshot().Units.First(u => u.AiTargetPolicy == AiTargetPolicy.BossGuard).Id;
            var result = BossSpawnerTests.End(game);
            Assert.That(game.GetSnapshot().Units.Single(u => u.Id == guardId).Position, Is.EqualTo(new Cell(0, 7)));
            Assert.That(result.Events.Any(e => e.Kind == "move" && e.SubjectId == guardId), Is.True);
        }

        /// <summary>小棋盘两列覆盖双方家时仍保护双方家；Boss 家移到玩家侧边缘后，越界前列直接跳过。</summary>
        [TestCase(false)] [TestCase(true)]
        public void SmallBoardProtectsHomesAndSkipsOutOfBoundsFront(bool moveBossToEdge)
        {
            var d = Document(); var r = d.rules[0];
            r.columns = 2; r.homeRegionWidth = 2; r.bossWarshipColumn = 1;
            var game = new GameSession(new GameConfiguration(d).WithDeck(new[] { new DeckEntry("soldier", 8) }, false), new SeededRandom(42), 1000);
            var attackers = new[] { 0, 3, 4 }.Select(row => BossPhaseTests.Put(game, "soldier", new Cell(row, 0))).ToArray();
            if (moveBossToEdge)
            {
                BossPhaseTests.End(game);
                BossPhaseTests.Do(game, new GameCommand(CommandKind.MoveHome, Side.Boss, -2, new Cell(1, 1)));
                BossPhaseTests.End(game); BossPhaseTests.End(game);
                BossPhaseTests.Do(game, new GameCommand(CommandKind.MoveHome, Side.Boss, -2, new Cell(1, 0)));
                BossPhaseTests.End(game);
            }
            BossPhaseTests.Do(game, new GameCommand(CommandKind.Attack, Side.Player, attackers[0], targetId: -2));
            var ships = game.GetSnapshot().BossWarships.ToArray();
            for (var i = 0; i < 2; i++)
                BossPhaseTests.Do(game, new GameCommand(CommandKind.Attack, Side.Player, attackers[i + 1], targetId: ships[i].UnitId.Value));
            var state = game.GetSnapshot();
            Assert.That(state.BossPhase, Is.EqualTo(BossPhase.PhaseThree));
            Assert.That(state.Units.Count(u => u.AiTargetPolicy == AiTargetPolicy.BossGuard), Is.EqualTo(moveBossToEdge ? 3 : 8));
            Assert.That(state.Homes.All(h => state.Units.All(u => u.Position != h.Position)), Is.True);
            Assert.That(state.Homes.Single(h => h.Owner == Side.Player).Hp, Is.EqualTo(1000));
        }
    }
}
