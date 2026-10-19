using System;
using System.Linq;
using NUnit.Framework;

namespace Spotlight.Tests
{
    /// <summary>Boss 状态机行为回归。通过公开命令建立棋盘，不修改会话内部状态；测试数值与正式默认配置分离。</summary>
    public class BossPhaseTests
    {
        /// <summary>合并回归：二阶段禁止攻击 Boss 时仍可治疗友方资源单位，目标高亮与命令保持一致。</summary>
        [Test]
        public void AlliedHealingRemainsAvailableWhileBossIsLocked()
        {
            var d = Document();
            var soldier = d.cards.Single(c => c.id == "soldier");
            soldier.attacksAlliesAsHeal = true;
            soldier.canAttackResources = false;
            var game = BossSpawnerTests.Start(d);
            Trigger(game);
            var healer = Put(game, "soldier", new Cell(0, 1));
            var ally = Put(game, "miner", new Cell(0, 2));
            var before = game.GetSnapshot();
            var legal = game.GetLegalActions(new Selection(SelectionKind.Unit, healer));
            Assert.That(legal.Attack, Has.No.Member(before.Homes.Single(h => h.Owner == Side.Boss).Position));
            Assert.That(legal.Heal, Does.Contain(new Cell(0, 2)));
            Assert.That(legal.Attack, Does.Contain(new Cell(0, 2)));
            Assert.That(game.Execute(new GameCommand(CommandKind.Attack, Side.Player, healer, targetId: -2)).Success, Is.False);
            Assert.That(game.GetSnapshot().Units.Single(u => u.Id == healer).Acted, Is.False);
            var result = Do(game, new GameCommand(CommandKind.Attack, Side.Player, healer, targetId: ally));
            Assert.That(result.Events.Any(e => e.Kind == "heal"), Is.True);
            Assert.That(game.GetSnapshot().Units.Single(u => u.Id == ally).Hp,
                Is.EqualTo(before.Units.Single(u => u.Id == ally).Hp));
            Assert.That(game.GetSnapshot().BossPhase, Is.EqualTo(BossPhase.PhaseTwo));
        }

        /// <summary>合并回归：指定战舰阵亡同时触发入场标记奖励、死亡加攻和阶段推进，且每种效果只触发一次。</summary>
        [Test]
        public void WarshipDeathsKeepMarkRewardsAndDeathGrowth()
        {
            var d = Document();
            var miner = d.cards.Single(c => c.id == "miner");
            miner.markOnEnterComponent = true; miner.markOnEnterRadius = 10; miner.markRewardResource = 3;
            var soldier = d.cards.Single(c => c.id == "soldier");
            soldier.deathGrowthComponent = true; soldier.deathGrowthPerKill = 1; soldier.deathGrowthLimit = 4;
            var game = BossSpawnerTests.Start(d);
            Put(game, "miner", new Cell(4, 0));
            Trigger(game);
            var ships = game.GetSnapshot().BossWarships.ToArray();
            var attacker = Put(game, "soldier", new Cell(0, 1));
            var second = Put(game, "soldier", new Cell(0, 2));
            var before = game.GetSnapshot();
            Assert.That(before.Units.Single(u => u.Id == ships[0].UnitId).MarkedBy, Is.EqualTo(Side.Player));
            var result = Do(game, new GameCommand(CommandKind.Attack, Side.Player, attacker, targetId: ships[0].UnitId.Value));
            Assert.That(result.Events.Count(e => e.Kind == "mark-reward"), Is.EqualTo(1));
            Assert.That(result.Events.Count(e => e.Kind == "warship-defeated"), Is.EqualTo(1));
            Assert.That(game.GetSnapshot().Resources, Is.EqualTo(before.Resources + 3));
            Assert.That(game.GetSnapshot().Units.Single(u => u.Id == second).Attack,
                Is.EqualTo(before.Units.Single(u => u.Id == second).Attack + 1));
            Assert.That(game.GetSnapshot().BossPhase, Is.EqualTo(BossPhase.PhaseTwo));
            Do(game, new GameCommand(CommandKind.Attack, Side.Player, second, targetId: ships[1].UnitId.Value));
            var after = game.GetSnapshot();
            Assert.That(after.Resources, Is.EqualTo(before.Resources + 6));
            Assert.That(after.Units.Single(u => u.Id == second).DeathGrowthTriggers, Is.EqualTo(2));
            Assert.That(after.BossWarships.All(s => s.Status == BossWarshipStatus.Defeated), Is.True);
            Assert.That(after.BossPhase, Is.EqualTo(BossPhase.PhaseThree));
            Assert.That(after.BossInvulnerable, Is.False);
        }

        /// <summary>即时卡组合不能绕过二阶段完成条件；文档校验与直接组装配置均拒绝部署即消失的指定战舰。</summary>
        [Test]
        public void PhaseWarshipCannotConsumeItselfOnDeployment()
        {
            var d = Document();
            var valid = new GameConfiguration(d);
            var ship = d.cards.Single(c => c.id == "warship");
            ship.leaveAfterDeploy = true; ship.deployGainResource = 1;
            Assert.That(ConfigValidation.Validate(d).Any(e => e.Contains("战舰不能是即时卡")), Is.True);
            var error = Assert.Throws<System.ArgumentException>(() => new GameConfiguration(valid.Rules,
                d.cards.Select(CardArchetype.FromRow), valid.Deck, valid.BossSlots, 10, 30, 2, valid.Presentation, valid.DeckConfig));
            Assert.That(error.Message, Does.Contain("战舰不能是即时卡"));
        }

        /// <summary>创建可在全盘部署的隔离配置；输入为控制模式、先手和召唤失调，输出为可独立修改的文档。</summary>
        internal static ConfigDocument Document(bool manual = true, string first = "player", bool sickness = false)
        {
            var d = BossSpawnerTests.Document(manual, first, sickness);
            d.homes.Single(h => h.owner == "boss").hp = 30;
            d.homes.Single(h => h.owner == "player").hp = 1000;
            d.rules[0].bossPhaseTwoHp = 20;
            d.rules[0].bossWarshipColumn = 8;
            d.rules[0].bossSpawnerAttackThreshold = 10000;
            var soldier = d.cards.Single(c => c.id == "soldier");
            soldier.atk = 20; soldier.range = 30; soldier.hp = 1000;
            return d;
        }

        /// <summary>执行真实命令并立即报告失败；输入为会话、命令，输出为领域事件和表现快照。</summary>
        internal static CommandResult Do(GameSession game, GameCommand command) => BossSpawnerTests.Do(game, command);

        /// <summary>部署测试手牌，返回对应战斗单位 ID；不将卡牌记录 ID 当成单位 ID。</summary>
        internal static long Put(GameSession game, string type, Cell cell)
        {
            var card = game.GetSnapshot().Hand.First(c => c.TypeId == type);
            Do(game, new GameCommand(CommandKind.Deploy, Side.Player, card.Id, cell));
            return game.GetSnapshot().Units.Single(u => u.SourceCardId == card.Id).Id;
        }

        /// <summary>结束当前行动方回合，允许跳过普通放牌；技能机会必须由各用例显式处理。</summary>
        internal static void End(GameSession game) => Do(game, new GameCommand(CommandKind.EndTurn, game.GetSnapshot().ActiveSide, skipDeployment: true));

        /// <summary>用合法攻击触发二阶段；输入为已处于玩家回合的会话，输出为攻击结果。</summary>
        internal static CommandResult Trigger(GameSession game)
        {
            var attacker = Put(game, "soldier", new Cell(0, 0));
            return Do(game, new GameCommand(CommandKind.Attack, Side.Player, attacker, targetId: -2));
        }

        /// <summary>阈值相等、越过阈值和极高致死伤害都只扣到阈值，立即部署且保留攻击者已行动。</summary>
        [TestCase(10)] [TestCase(11)] [TestCase(1000)]
        public void ThresholdDamageIsClampedAndDeploysExactlyTwoShips(int damage)
        {
            var d = Document(); d.cards.Single(c => c.id == "soldier").atk = damage;
            var game = BossSpawnerTests.Start(d);
            var before = game.GetSnapshot();
            var result = Trigger(game);
            var state = game.GetSnapshot();
            Assert.That(state.BossPhase, Is.EqualTo(BossPhase.PhaseTwo));
            Assert.That(state.BossInvulnerable, Is.True);
            Assert.That(state.Homes.Single(h => h.Owner == Side.Boss).Hp, Is.EqualTo(20));
            Assert.That(state.Outcome, Is.EqualTo(MatchOutcome.None));
            Assert.That(state.BossWarships.Count, Is.EqualTo(2));
            Assert.That(state.BossWarships.All(s => s.Status == BossWarshipStatus.Alive), Is.True);
            Assert.That(state.Units.Where(u => u.TypeId == "warship").Select(u => u.Position),
                Is.EquivalentTo(new[] { new Cell(1, 7), new Cell(3, 7) }));
            Assert.That(state.Units.Where(u => u.TypeId == "warship").All(u => u.Hp == 15 && u.Attack == 2), Is.True);
            Assert.That(result.Events.Any(e => e.Kind == "attack" && e.Message.Contains("造成 10 伤害")), Is.True);
            Assert.That(before.BossPhase, Is.EqualTo(BossPhase.PhaseOne));
            Assert.That(before.BossWarships, Is.Empty);
        }

        /// <summary>锁血目标同时从高亮和命令入口排除；被拒绝攻击不能消耗行动或改变生命。</summary>
        [Test]
        public void InvulnerabilityCannotBeBypassedByForgedAttack()
        {
            var game = BossSpawnerTests.Start(Document()); Trigger(game);
            var attacker = Put(game, "soldier", new Cell(0, 1));
            var before = game.GetSnapshot();
            Assert.That(game.GetLegalActions(new Selection(SelectionKind.Unit, attacker)).Attack,
                Has.No.Member(before.Homes.Single(h => h.Owner == Side.Boss).Position));
            var result = game.Execute(new GameCommand(CommandKind.Attack, Side.Player, attacker, targetId: -2));
            Assert.That(result.Success, Is.False);
            Assert.That(game.GetSnapshot().Units.Single(u => u.Id == attacker).Acted, Is.False);
            Assert.That(game.GetSnapshot().Homes.Single(h => h.Owner == Side.Boss).Hp, Is.EqualTo(20));
        }

        /// <summary>只跟踪两艘指定战舰；最后一艘死亡立即解锁，旧快照保持存活，随后致死攻击不重入二阶段。</summary>
        [Test]
        public void BothShipDeathsEnterPhaseThreeAndAllowVictory()
        {
            var game = BossSpawnerTests.Start(Document()); Trigger(game);
            var before = game.GetSnapshot();
            for (var i = 0; i < 2; i++)
            {
                var attacker = Put(game, "soldier", new Cell(0, i + 1));
                Do(game, new GameCommand(CommandKind.Attack, Side.Player, attacker, targetId: before.BossWarships[i].UnitId.Value));
                Assert.That(game.GetSnapshot().BossPhase, Is.EqualTo(i == 0 ? BossPhase.PhaseTwo : BossPhase.PhaseThree));
            }
            Assert.That(before.BossWarships.All(s => s.Status == BossWarshipStatus.Alive), Is.True);
            Assert.That(game.GetSnapshot().BossInvulnerable, Is.False);
            var finisher = Put(game, "soldier", new Cell(0, 3));
            Do(game, new GameCommand(CommandKind.Attack, Side.Player, finisher, targetId: -2));
            Assert.That(game.GetSnapshot().Outcome, Is.EqualTo(MatchOutcome.PlayerWins));
            Assert.That(game.GetSnapshot().BossPhase, Is.EqualTo(BossPhase.PhaseThree));
        }

        /// <summary>行数变化与左右镜像使用统一坐标算法，实际部署永不重叠。</summary>
        [TestCase(1, "left", 0, 0, 7)] [TestCase(2, "left", 0, 1, 7)]
        [TestCase(5, "left", 1, 3, 7)] [TestCase(5, "right", 1, 3, 2)]
        [TestCase(8, "left", 2, 5, 7)]
        public void ThirdPointsAndColumnMirrorAreDeterministic(int rows, string side, int firstRow, int secondRow, int col)
        {
            var d = Document(); d.rules[0].rows = rows; d.rules[0].playerSide = side;
            var game = BossSpawnerTests.Start(d);
            var attacker = Put(game, "soldier", new Cell(0, 4));
            Do(game, new GameCommand(CommandKind.Attack, Side.Player, attacker, targetId: -2));
            var ships = game.GetSnapshot().BossWarships;
            Assert.That(ships.Select(s => s.PreferredCell), Is.EqualTo(new[] { new Cell(firstRow, col), new Cell(secondRow, col) }));
            Assert.That(game.GetSnapshot().Units.Where(u => u.TypeId == "warship").Select(u => u.Position).Distinct().Count(), Is.EqualTo(2));
        }

        /// <summary>一个目标被占时不得抢另一个战舰的指定格；距离并列按内部行号排序。</summary>
        [Test]
        public void OccupiedTargetUsesNearestFreeCellAndPreservesOtherPreferredCell()
        {
            var game = BossSpawnerTests.Start(Document()); Put(game, "miner", new Cell(1, 7)); Trigger(game);
            var state = game.GetSnapshot();
            Assert.That(state.Units.Single(u => u.Id == state.BossWarships[0].UnitId).Position, Is.EqualTo(new Cell(0, 7)));
            Assert.That(state.Units.Single(u => u.Id == state.BossWarships[1].UnitId).Position, Is.EqualTo(new Cell(3, 7)));
            Assert.That(state.Units.Single(u => u.Position == new Cell(1, 7)).TypeId, Is.EqualTo("miner"));
        }

        /// <summary>满盘保留待部署状态；下一 Boss 回合空出一个位置时只部署一艘，不误解锁或重复部署。</summary>
        [Test]
        public void FullBoardRetriesPendingShipsWithoutCountingThemAsDefeated()
        {
            var d = Document(); d.rules[0].rows = 1;
            var game = BossSpawnerTests.Start(d);
            var attacker = Put(game, "soldier", new Cell(0, 1));
            for (var col = 2; col <= 8; col++) Put(game, "miner", new Cell(0, col));
            Do(game, new GameCommand(CommandKind.Attack, Side.Player, attacker, targetId: -2));
            Assert.That(game.GetSnapshot().BossWarships.All(s => s.Status == BossWarshipStatus.Pending), Is.True);
            var withdrawn = game.GetSnapshot().Units.Single(u => u.Position == new Cell(0, 7));
            Do(game, new GameCommand(CommandKind.Withdraw, Side.Player, withdrawn.Id)); End(game);
            Assert.That(game.GetSnapshot().BossWarships.Count(s => s.Status == BossWarshipStatus.Alive), Is.EqualTo(1));
            Assert.That(game.GetSnapshot().BossWarships.Count(s => s.Status == BossWarshipStatus.Pending), Is.EqualTo(1));
            Assert.That(game.GetSnapshot().BossInvulnerable, Is.True);
        }

        /// <summary>战舰自动部署不扣放牌次数；手动普通放牌继续有效，战舰不能攻击资源单位但可攻击家。</summary>
        [Test]
        public void ManualShipTargetPolicyAndOrdinaryDeploymentShareRuleChecks()
        {
            var d = Document(); d.rules[0].bossWarshipColumn = 2; d.cards.Single(c => c.id == "warship").range = 20;
            var game = BossSpawnerTests.Start(d);
            var mine = Put(game, "miner", new Cell(1, 2)); Trigger(game); End(game);
            var ship = game.GetSnapshot().Units.Single(u => u.Id == game.GetSnapshot().BossWarships[0].UnitId);
            var before = game.GetSnapshot();
            Assert.That(game.GetLegalActions(new Selection(SelectionKind.Unit, ship.Id)).Attack, Has.No.Member(new Cell(1, 2)));
            Assert.That(game.Execute(new GameCommand(CommandKind.Attack, Side.Boss, ship.Id, targetId: mine)).Success, Is.False);
            Assert.That(game.GetSnapshot().Units.Single(u => u.Id == ship.Id).Acted, Is.False);
            var free = game.GetLegalActions(new Selection(SelectionKind.BossDeployment)).Deploy.First();
            Do(game, new GameCommand(CommandKind.BossDeploy, Side.Boss, target: free));
            Assert.That(game.GetSnapshot().PlaysRemaining, Is.EqualTo(before.PlaysRemaining - 1));
            var home = game.GetSnapshot().Homes.Single(h => h.Owner == Side.Player);
            Assert.That(game.GetLegalActions(new Selection(SelectionKind.Unit, ship.Id)).Attack, Does.Contain(home.Position));
            Do(game, new GameCommand(CommandKind.Attack, Side.Boss, ship.Id, targetId: -1));
        }

        /// <summary>无论完整回合是否递增，玩家回合部署的两艘战舰都在下一次 Boss 回合各刷一架；两种先手一致。</summary>
        [TestCase("player")] [TestCase("boss")]
        public void SpawnEligibilityUsesOwnerTurnRatherThanFullRound(string first)
        {
            var d = Document(first: first); var game = BossSpawnerTests.Start(d);
            if (first == "boss") End(game);
            Trigger(game);
            Assert.That(game.GetSnapshot().Units.Count(u => u.Owner == Side.Boss), Is.EqualTo(2));
            End(game);
            Assert.That(game.GetSnapshot().Units.Count(u => u.TypeId == "boss"), Is.EqualTo(2));
            End(game); End(game);
            Assert.That(game.GetSnapshot().Units.Count(u => u.TypeId == "boss"), Is.EqualTo(4));
        }

        /// <summary>新战机保留召唤失调，战舰下一 Boss 回合恢复行动；不会在生成后再次全体恢复。</summary>
        [Test]
        public void GeneratedFightersRetainSummoningSickness()
        {
            var d = Document(sickness: true); var game = BossSpawnerTests.Start(d);
            var attacker = Put(game, "soldier", new Cell(0, 0)); End(game); End(game);
            Do(game, new GameCommand(CommandKind.Attack, Side.Player, attacker, targetId: -2)); End(game);
            Assert.That(game.GetSnapshot().Units.Where(u => u.TypeId == "boss").All(u => u.Acted), Is.True);
            Assert.That(game.GetSnapshot().Units.Where(u => u.TypeId == "warship").All(u => !u.Acted), Is.True);
        }

        /// <summary>移动战舰后下一轮刷怪以新位置为中心；来源自身行动不关闭被动刷怪。</summary>
        [Test]
        public void SpawnUsesMovedSourcePosition()
        {
            var d = Document(); d.cards.Single(c => c.id == "warship").moveDistance = 8;
            var game = BossSpawnerTests.Start(d); Trigger(game); End(game);
            var ship = game.GetSnapshot().BossWarships[0].UnitId.Value;
            var destination = new Cell(1, 1);
            Do(game, new GameCommand(CommandKind.Move, Side.Boss, ship, destination));
            var oldIds = game.GetSnapshot().Units.Select(u => u.Id).ToArray();
            End(game); End(game);
            var generated = game.GetSnapshot().Units.Where(u => !oldIds.Contains(u.Id)).ToArray();
            Assert.That(generated.Length, Is.EqualTo(2));
            Assert.That(generated.Any(u => Math.Abs(u.Position.Row - destination.Row) <= 1 && Math.Abs(u.Position.Col - destination.Col) <= 1), Is.True);
        }

        /// <summary>二、三阶段不新增一阶段技能；已部署炸弹仍按期爆炸，普通手动放牌不会被虚假的技能机会阻塞。</summary>
        [Test]
        public void ExistingBombExplodesButNewPhaseOneSkillsStayDisabled()
        {
            var d = Document(); d.rules[0].bossBombInterval = 1;
            d.rules[0].bossSpawnerAttackThreshold = 1; d.rules[0].bossSpawnerUnitThreshold = 1;
            var game = BossSpawnerTests.Start(d);
            Do(game, new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: new Cell(4, 4)));
            Trigger(game); End(game);
            Assert.That(game.GetSnapshot().BossSpawnerPending, Is.False);
            End(game);
            Assert.That(game.GetSnapshot().BossBombs, Is.Empty);
            Assert.That(game.GetSnapshot().BossBombPending, Is.False);
            Assert.That(game.GetSnapshot().Log.Any(e => e.Kind == "bomb-explode"), Is.True);
        }

        /// <summary>自动模式二阶段也不会首次部署固定兵位，战舰可自动行动；重开回到一阶段且没有残留记录。</summary>
        [Test]
        public void AutomaticPhaseTwoSkipsFixedSlotsAndRestartClearsPhase()
        {
            var d = Document(manual: false); var game = BossSpawnerTests.Start(d); Trigger(game); End(game);
            Assert.That(game.GetSnapshot().BossReplacements.All(s => !s.HasSpawned), Is.True);
            Assert.That(game.GetSnapshot().Units.Where(u => u.TypeId == "warship").Any(u => u.Position.Col < 7), Is.True);
            var restarted = BossSpawnerTests.Start(d).GetSnapshot();
            Assert.That(restarted.BossPhase, Is.EqualTo(BossPhase.PhaseOne));
            Assert.That(restarted.BossWarships, Is.Empty);
        }

        /// <summary>普通放牌产生的刷怪源无技能身份仍能工作；三阶段不恢复新技能，来源及卡牌记录身份保持独立。</summary>
        [Test]
        public void OrdinarySpawnSourceSurvivesIntoPhaseThreeWithoutRestoringSkills()
        {
            var d = Document(); d.rules[0].bossCardType = "spawner";
            d.rules[0].bossSpawnerAttackThreshold = 1; d.rules[0].bossSpawnerUnitThreshold = 1;
            d.rules[0].bossSpawnerCountFromColumn = 1;
            var game = BossSpawnerTests.Start(d); End(game);
            Do(game, new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(2, 5)));
            var cage = game.GetSnapshot().Units.Single();
            Assert.That(cage.IsBossSpawner, Is.False);
            End(game); Trigger(game);
            var ships = game.GetSnapshot().BossWarships;
            for (var i = 0; i < 2; i++)
            {
                var attacker = Put(game, "soldier", new Cell(0, i + 1));
                Do(game, new GameCommand(CommandKind.Attack, Side.Player, attacker, targetId: ships[i].UnitId.Value));
            }
            End(game);
            var state = game.GetSnapshot();
            Assert.That(state.BossPhase, Is.EqualTo(BossPhase.PhaseThree));
            Assert.That(state.Units.Count(u => u.TypeId == "boss"), Is.EqualTo(1));
            Assert.That(state.Units.Any(u => u.Id == cage.Id), Is.True);
            Assert.That(state.BossSpawnerPending || state.BossBombPending, Is.False);
            Assert.That(state.BossReplacements.All(s => !s.HasSpawned), Is.True);
            Assert.That(game.Execute(new GameCommand(CommandKind.DeployBossSpawner, Side.Boss, target: new Cell(4, 5))).Success, Is.False);
            End(game); End(game);
            Assert.That(game.GetSnapshot().Units.Count(u => u.TypeId == "boss"), Is.EqualTo(2));
            Assert.That(game.GetSnapshot().BossWarships.All(s => s.Status == BossWarshipStatus.Defeated), Is.True);
        }

        /// <summary>战舰与邻接普通战机可手动互换，双方消耗行动；通用权限不误用固定刷怪笼限制。</summary>
        [Test]
        public void WarshipCanSwapWithFighterAndRetainsTrackedIdentity()
        {
            var game = BossSpawnerTests.Start(Document()); Trigger(game); End(game);
            var state = game.GetSnapshot();
            var ship = state.Units.Single(u => u.Id == state.BossWarships[0].UnitId);
            // 合法位置由规则查询，避免随机战机生成在斜邻时假定可直接互换。
            var free = game.GetLegalActions(new Selection(SelectionKind.BossDeployment)).Deploy
                .First(p => p.Distance(ship.Position) == 1);
            Do(game, new GameCommand(CommandKind.BossDeploy, Side.Boss, target: free));
            var partner = game.GetSnapshot().Units.Single(u => u.Position == free);
            Do(game, new GameCommand(CommandKind.Swap, Side.Boss, ship.Id, targetId: partner.Id));
            var after = game.GetSnapshot();
            Assert.That(after.Units.Single(u => u.Id == ship.Id).Position, Is.EqualTo(free));
            Assert.That(after.Units.Single(u => u.Id == partner.Id).Position, Is.EqualTo(ship.Position));
            Assert.That(after.Units.Where(u => u.Id == ship.Id || u.Id == partner.Id).All(u => u.Acted), Is.True);
            Assert.That(after.BossWarships[0].UnitId, Is.EqualTo(ship.Id));
        }

        /// <summary>AI 与手动目标权限一致；邻接的资源单位不会吸引战舰攻击，普通零攻战机也不造成干扰。</summary>
        [Test]
        public void AutomaticWarshipsNeverAttackAdjacentResources()
        {
            var game = BossSpawnerTests.Start(Document(manual: false));
            var mineA = Put(game, "miner", new Cell(1, 6));
            var mineB = Put(game, "miner", new Cell(3, 6));
            Trigger(game);
            var before = game.GetSnapshot(); End(game);
            var after = game.GetSnapshot();
            foreach (var id in new[] { mineA, mineB })
            {
                Assert.That(after.Units.Single(u => u.Id == id).Hp, Is.EqualTo(before.Units.Single(u => u.Id == id).Hp));
                Assert.That(after.Log.Any(e => e.Kind == "attack" && e.SubjectId == id), Is.False);
            }
        }
    }
}
