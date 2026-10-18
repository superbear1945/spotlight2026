// 击杀恢复行动（维京长船-2056 机制）的规则回归测试。
// 输入：独立配置夹具与公开命令；输出：触发条件、连续行动、普通操作消耗与错误来源不恢复的断言。
// 只通过 Execute/GetLegalActions/GetSnapshot 推进对局，不篡改 GameSession 私有状态。
using System.Linq;
using NUnit.Framework;

namespace Spotlight.Tests
{
    /// <summary>
    /// 覆盖“直接攻击（含伤害标记追加）击杀后恢复一次普通行动”“未击杀不恢复”“恢复后可移动/攻击/互换”
    /// “不重开回合、不跨回合累计”“炸弹、其它单位与离场不误恢复”“AI 使用该能力时能连续决策”。
    /// </summary>
    public class CardKillActionTests
    {
        /// <summary>
        /// 构造隔离夹具。输入：无；输出：家区域放大到全棋盘、Boss 单位 1 攻 1 程，
        /// 并追加一张带击杀恢复行动的攻击卡 “raider”（2 攻 1 程 1 移）。
        /// </summary>
        static ConfigDocument Document()
        {
            var d = GameSessionTests.Document();
            d.rules[0].homeRegionWidth = 10;
            d.cards = d.cards.Concat(new[]
            {
                new CardRow
                {
                    tier = "basic", id = "raider", name = "维京长船", category = "attack",
                    hp = 10, atk = 2, range = 1, moveDistance = 1, killActionComponent = true, viewKey = "default"
                }
            }).ToArray();
            return d;
        }

        /// <summary>建立对局。输入：配置与可选牌堆；输出：固定种子的规则内核。</summary>
        static GameSession Start(ConfigDocument d, params DeckEntry[] deck) =>
            new GameSession(new GameConfiguration(d).WithDeck(
                deck.Length > 0 ? deck : new[] { new DeckEntry("raider", 8), new DeckEntry("soldier", 8) }, false), new SeededRandom(31), 1000);

        /// <summary>执行并断言成功。输入：对局与命令；输出：命令结果。</summary>
        static CommandResult Do(GameSession g, GameCommand command)
        {
            var result = g.Execute(command);
            Assert.That(result.Success, Is.True, result.Error);
            return result;
        }

        /// <summary>结束当前回合（跳过手动 Boss 的剩余放牌）。输入：对局；输出：结束结果。</summary>
        static CommandResult End(GameSession g) => Do(g, new GameCommand(CommandKind.EndTurn, g.GetSnapshot().ActiveSide, skipDeployment: true));

        /// <summary>在玩家回合部署手牌中的指定卡种。输入：对局、卡种与坐标；输出：新单位 ID。</summary>
        static long Put(GameSession g, string type, Cell cell)
        {
            var card = g.GetSnapshot().Hand.First(c => c.TypeId == type);
            Do(g, new GameCommand(CommandKind.Deploy, Side.Player, card.Id, cell));
            return g.GetSnapshot().Units.Single(u => u.Position == cell).Id;
        }

        /// <summary>当前行动方发动攻击。输入：对局、攻击者与目标；输出：命令结果。</summary>
        static CommandResult Attack(GameSession g, long attacker, long target) =>
            Do(g, new GameCommand(CommandKind.Attack, g.GetSnapshot().ActiveSide, attacker, targetId: target));

        /// <summary>
        /// 在 Boss 回合依次部署多个 Boss 单位。输入：对局与目标格数组；输出：各单位 ID（按部署顺序）。
        /// 为什么需要：连杀测试要求同一回合内存在多个可击杀目标。
        /// </summary>
        static long[] DeployBoss(GameSession g, params Cell[] cells)
        {
            foreach (var cell in cells) Do(g, new GameCommand(CommandKind.BossDeploy, Side.Boss, target: cell));
            return g.GetSnapshot().Units.Where(u => u.Owner == Side.Boss).OrderBy(u => u.Id).Select(u => u.Id).ToArray();
        }

        /// <summary>未击杀不恢复行动：攻击后该单位本回合不能再移动。</summary>
        [Test]
        public void NonKillingAttackConsumesTheAction()
        {
            var d = Document();
            d.cards.Single(c => c.id == "boss").hp = 3;
            var g = Start(d);
            var raider = Put(g, "raider", new Cell(2, 4));
            End(g);
            var boss = DeployBoss(g, new Cell(2, 5))[0];
            End(g);
            Attack(g, raider, boss);
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == raider).Acted, Is.True);
            Assert.That(g.GetLegalActions(new Selection(SelectionKind.Unit, raider)).Move, Is.Empty);
            Assert.That(g.GetSnapshot().Log.Any(e => e.Kind == "action-restored"), Is.False);
        }

        /// <summary>击杀后恢复行动，可连续击杀两个单位，最后一次攻击把行动消耗掉。</summary>
        [Test]
        public void KillRestoresActionAndAllowsChainedKills()
        {
            var d = Document();
            d.cards.Single(c => c.id == "boss").hp = 2;
            var g = Start(d);
            var raider = Put(g, "raider", new Cell(2, 4));
            End(g);
            var bosses = DeployBoss(g, new Cell(2, 5), new Cell(2, 3));
            End(g);
            var first = Attack(g, raider, bosses[0]);
            Assert.That(first.Events.Count(e => e.Kind == "action-restored"), Is.EqualTo(1));
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == raider).Acted, Is.False, "击杀后应恢复行动");
            var second = Attack(g, raider, bosses[1]);
            Assert.That(second.Events.Count(e => e.Kind == "action-restored"), Is.EqualTo(1));
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == raider).Acted, Is.False, "再次击杀可再次恢复");
            Assert.That(g.GetSnapshot().Units.Any(u => u.Owner == Side.Boss), Is.False);
        }

        /// <summary>由伤害标记追加造成的击杀同样恢复行动，且归属仍是当前攻击者。</summary>
        [Test]
        public void BonusDamageKillAlsoRestoresAction()
        {
            var d = Document();
            d.cards.Single(c => c.id == "raider").damageMarkComponent = true;
            d.cards.Single(c => c.id == "raider").damageMarkDamagePerStack = 1;
            d.cards.Single(c => c.id == "boss").hp = 5;
            var g = Start(d);
            var first = Put(g, "raider", new Cell(2, 3));
            var second = Put(g, "raider", new Cell(2, 5));
            End(g);
            var boss = DeployBoss(g, new Cell(2, 4))[0];
            End(g);
            Attack(g, first, boss); // 基础 2 → 3 血，挂 1 层
            var result = Attack(g, second, boss);
            var damage = result.Events.Single(e => e.Kind == "attack").Damage;
            Assert.That(damage.Killed, Is.True);
            Assert.That(damage.BonusDamage, Is.EqualTo(1), "本次由标记追加补完最后一击");
            Assert.That(damage.SourceUnitId, Is.EqualTo(second));
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == second).Acted, Is.False);
        }

        /// <summary>恢复后可以换成移动或互换：正常消耗机会，另一方不白得恢复。</summary>
        [Test]
        public void RestoredActionCanBeSpentOnMoveOrSwap()
        {
            var d = Document();
            d.cards.Single(c => c.id == "boss").hp = 2;
            var g = Start(d);
            var raider = Put(g, "raider", new Cell(2, 4));
            var ally = Put(g, "soldier", new Cell(3, 4));
            End(g);
            var boss = DeployBoss(g, new Cell(2, 5))[0];
            End(g);
            Attack(g, raider, boss);
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == raider).Acted, Is.False);
            Do(g, new GameCommand(CommandKind.Move, Side.Player, raider, new Cell(2, 6)));
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == raider).Acted, Is.True, "移动照常消耗恢复的机会");
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == ally).Acted, Is.False, "互换/移动不会给其它单位额外机会");
        }

        /// <summary>恢复不是重开回合：不抽牌、不产资源、不重复扣维持费、不改变出牌次数。</summary>
        [Test]
        public void RestoreDoesNotRestartTurn()
        {
            var d = Document();
            d.cards.Single(c => c.id == "boss").hp = 2;
            var g = Start(d);
            var raider = Put(g, "raider", new Cell(2, 4));
            End(g);
            var boss = DeployBoss(g, new Cell(2, 5))[0];
            End(g);
            var before = g.GetSnapshot();
            Attack(g, raider, boss);
            var after = g.GetSnapshot();
            Assert.That(after.Round, Is.EqualTo(before.Round));
            Assert.That(after.Resources, Is.EqualTo(before.Resources));
            Assert.That(after.Hand.Count, Is.EqualTo(before.Hand.Count));
            Assert.That(after.PlaysRemaining, Is.EqualTo(before.PlaysRemaining));
            Assert.That(after.ActiveSide, Is.EqualTo(before.ActiveSide));
            Assert.That(after.Units.Single(u => u.Id == raider).Hp, Is.EqualTo(before.Units.Single(u => u.Id == raider).Hp));
        }

        /// <summary>其它单位击杀、炸弹击杀与主动离场都不会让带该能力的单位恢复行动。</summary>
        [Test]
        public void OtherKillsAndWithdrawDoNotRestoreAction()
        {
            var d = Document();
            d.cards.Single(c => c.id == "boss").hp = 1;
            d.rules[0].bossBombInterval = 10;
            var g = Start(d);
            var raider = Put(g, "raider", new Cell(2, 4));
            var soldier = Put(g, "soldier", new Cell(2, 6));
            End(g);
            var boss = DeployBoss(g, new Cell(2, 7))[0];
            End(g);
            // 先让维京长船行动一次（移动到相邻格），再用士兵击杀目标。
            Do(g, new GameCommand(CommandKind.Move, Side.Player, raider, new Cell(2, 5)));
            var result = Attack(g, soldier, boss);
            Assert.That(result.Events.Any(e => e.Kind == "death"), Is.True);
            Assert.That(result.Events.Any(e => e.Kind == "action-restored"), Is.False);
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == raider).Acted, Is.True, "其它单位击杀不能恢复维京长船");
            // 主动离场不是死亡，也不产生恢复事件。
            var withdraw = Do(g, new GameCommand(CommandKind.Withdraw, Side.Player, raider));
            Assert.That(withdraw.Events.Any(e => e.Kind == "action-restored"), Is.False);
        }

        /// <summary>AI 使用该能力时会按最新状态连续决策，直到没有可击杀目标才结束该单位回合。</summary>
        [Test]
        public void AiWithKillActionKeepsDeciding()
        {
            var d = Document();
            d.rules[0].playerControlsBoss = false;
            d.rules[0].homeRegionWidth = 10;
            d.cards.Single(c => c.id == "boss").atk = 2;
            d.cards.Single(c => c.id == "boss").range = 2;
            d.cards.Single(c => c.id == "boss").moveDistance = 0;
            d.cards.Single(c => c.id == "boss").killActionComponent = true;
            d.cards.Single(c => c.id == "miner").hp = 1;
            // 三个兵位指向同一首选格：只会有一个 Boss 单位成功部署，使 AI 决策可确定。
            foreach (var slot in d.bossSlots) { slot.rowFraction = 0.5; slot.edgeOffset = 1; }
            var g = Start(d, new DeckEntry("miner", 8));
            Put(g, "miner", new Cell(2, 7));
            Put(g, "miner", new Cell(2, 6));
            End(g);
            var log = g.GetSnapshot().Log;
            Assert.That(g.GetSnapshot().Units.Any(u => u.Owner == Side.Player), Is.False, "AI 应连续击杀两个低血单位");
            Assert.That(log.Count(e => e.Kind == "action-restored"), Is.EqualTo(2));
            Assert.That(log.Count(e => e.Kind == "death"), Is.EqualTo(2));
        }
    }
}
