// 统一伤害结算的回归测试。
// 输入：独立配置夹具与公开命令；输出：结构化伤害事实、炸弹预算语义与既有死亡联动的断言。
// 为什么单独成文件：后面“伤害标记追加伤害”会复用同一入口，必须先锁定“没有标记时行为不变”。
using System.Linq;
using NUnit.Framework;

namespace Spotlight.Tests
{
    /// <summary>
    /// 覆盖普通攻击与炸弹的结构化伤害结果、实际扣血与预算消耗的区别、单次死亡处理与快照隔离。
    /// 只通过 Execute/GetSnapshot 推进对局，不篡改 GameSession 私有状态。
    /// </summary>
    public class DamageResolutionTests
    {
        /// <summary>构造隔离夹具。输入：无；输出：家区域放大到全棋盘，便于让双方单位相邻以验证攻击。</summary>
        static ConfigDocument Document()
        {
            var d = GameSessionTests.Document();
            d.rules[0].homeRegionWidth = 10;
            return d;
        }

        /// <summary>建立对局。输入：配置；输出：固定种子的规则内核，使用夹具牌堆避免受正式 drawPileSize 影响。</summary>
        static GameSession Start(ConfigDocument d) =>
            new GameSession(new GameConfiguration(d).WithDeck(GameSessionTests.DefaultDeck(), false), new SeededRandom(11), 1000);

        /// <summary>执行并断言成功。输入：对局与命令；输出：命令结果，失败时显示规则错误。</summary>
        static CommandResult Do(GameSession g, GameCommand command)
        {
            var result = g.Execute(command);
            Assert.That(result.Success, Is.True, result.Error);
            return result;
        }

        /// <summary>结束当前回合（跳过手动 Boss 的剩余放牌）。输入：对局；输出：结束结果。</summary>
        static CommandResult End(GameSession g) => Do(g, new GameCommand(CommandKind.EndTurn, g.GetSnapshot().ActiveSide, skipDeployment: true));

        /// <summary>在玩家回合部署指定卡种。输入：对局、卡种与坐标；输出：新单位 ID。</summary>
        static long Put(GameSession g, string type, Cell cell)
        {
            var card = g.GetSnapshot().Hand.First(c => c.TypeId == type);
            Do(g, new GameCommand(CommandKind.Deploy, Side.Player, card.Id, cell));
            return g.GetSnapshot().Units.Single(u => u.Position == cell).Id;
        }

        /// <summary>
        /// 建立“玩家单位已就位、Boss 单位相邻”的局面。
        /// 输入：对局、玩家单位坐标与 Boss 单位坐标；输出：双方单位 ID。
        /// </summary>
        static (long player, long boss) FaceOff(GameSession g, Cell playerCell, Cell bossCell)
        {
            var mine = Put(g, "soldier", playerCell);
            End(g);
            Do(g, new GameCommand(CommandKind.BossDeploy, Side.Boss, target: bossCell));
            var boss = g.GetSnapshot().Units.Single(u => u.Owner == Side.Boss);
            End(g);
            return (mine, boss.Id);
        }

        /// <summary>普通攻击必须给出结构化结果：基础伤害=攻击者当前攻击力，没有追加伤害、来源与目标可追溯。</summary>
        [Test]
        public void AttackProducesStructuredResultWithCurrentAttack()
        {
            var g = Start(Document());
            var (mine, boss) = FaceOff(g, new Cell(2, 4), new Cell(2, 5));
            var result = Do(g, new GameCommand(CommandKind.Attack, Side.Player, mine, targetId: boss));
            var attack = result.Events.Single(e => e.Kind == "attack");
            var damage = attack.Damage;
            Assert.That(damage, Is.Not.Null, "攻击事件必须携带结构化伤害结果");
            Assert.That(damage.Kind, Is.EqualTo(DamageKind.DirectAttack));
            Assert.That(damage.RootKind, Is.EqualTo(DamageKind.DirectAttack));
            Assert.That(damage.SourceSide, Is.EqualTo(Side.Player));
            Assert.That(damage.SourceUnitId, Is.EqualTo(mine));
            Assert.That(damage.TargetId, Is.EqualTo(boss));
            Assert.That(damage.BaseDamage, Is.EqualTo(2));
            Assert.That(damage.BonusDamage, Is.Zero);
            Assert.That(damage.TotalDamage, Is.EqualTo(2));
            Assert.That(damage.Killed, Is.False);
            Assert.That(damage.AppliedLayers, Is.Empty);
            Assert.That(damage.Bomb, Is.Null);
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == boss).Hp, Is.EqualTo(1));
        }

        /// <summary>击杀只处理一次死亡：结果标记 Killed，日志只有一条 death，单位从快照移除。</summary>
        [Test]
        public void KillingAttackRemovesUnitOnceAndReportsKilled()
        {
            var d = Document();
            d.cards.Single(c => c.id == "boss").hp = 2;
            var g = Start(d);
            var (mine, boss) = FaceOff(g, new Cell(2, 4), new Cell(2, 5));
            var result = Do(g, new GameCommand(CommandKind.Attack, Side.Player, mine, targetId: boss));
            var damage = result.Events.Single(e => e.Kind == "attack").Damage;
            Assert.That(damage.Killed, Is.True);
            Assert.That(damage.TotalDamage, Is.EqualTo(2));
            Assert.That(result.Events.Count(e => e.Kind == "death"), Is.EqualTo(1));
            Assert.That(g.GetSnapshot().Units.Any(u => u.Id == boss), Is.False);
        }

        /// <summary>攻击家的伤害同样结构化，但家不参与伤害标记且不会阵亡离场。</summary>
        [Test]
        public void AttackOnHomeReportsStructuredDamageWithoutBonus()
        {
            var g = Start(Document());
            var mine = Put(g, "soldier", new Cell(2, 7));
            var result = Do(g, new GameCommand(CommandKind.Attack, Side.Player, mine, targetId: -2));
            var damage = result.Events.Single(e => e.Kind == "attack").Damage;
            Assert.That(damage.TargetId, Is.EqualTo(-2));
            Assert.That(damage.BaseDamage, Is.EqualTo(2));
            Assert.That(damage.BonusDamage, Is.Zero);
            Assert.That(damage.Killed, Is.False);
            Assert.That(g.GetSnapshot().Homes.Single(h => h.Owner == Side.Boss).Hp, Is.EqualTo(8));
        }

        /// <summary>命令前后的快照互不影响：旧快照生命不变，结构化结果不被后续结算改写。</summary>
        [Test]
        public void SnapshotAndDamageResultStayImmutable()
        {
            var g = Start(Document());
            var (mine, boss) = FaceOff(g, new Cell(2, 4), new Cell(2, 5));
            var before = g.GetSnapshot();
            var result = Do(g, new GameCommand(CommandKind.Attack, Side.Player, mine, targetId: boss));
            var damage = result.Events.Single(e => e.Kind == "attack").Damage;
            Assert.That(before.Units.Single(u => u.Id == boss).Hp, Is.EqualTo(3), "旧快照必须保留攻击前生命");
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == boss).Hp, Is.EqualTo(1));
            Do(g, new GameCommand(CommandKind.EndTurn, Side.Player, skipDeployment: true));
            Assert.That(damage.BaseDamage, Is.EqualTo(2), "已交付的结果不能被后续结算改写");
            Assert.That(damage.TotalDamage, Is.EqualTo(2));
        }

        /// <summary>部署炸弹并在下一回合引爆。输入：对局与炸弹格；输出：本次爆炸事件。</summary>
        static GameEvent Explode(GameSession g, Cell position)
        {
            End(g);
            End(g);
            Do(g, new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: position));
            End(g);
            return End(g).Events.Single(e => e.Kind == "bomb-explode");
        }

        /// <summary>没有伤害标记时，炸弹步骤的追加伤害为 0，预算消耗与实际扣血一致。</summary>
        [Test]
        public void BombStepKeepsBudgetSemanticsWithoutMarks()
        {
            var d = Document();
            d.cards.Single(c => c.id == "miner").hp = 10;
            d.rules[0].bossBombInterval = 2;
            d.rules[0].bossBombDamage = 3;
            d.rules[0].bossBombColumn = 5;
            var g = Start(d);
            var victim = Put(g, "miner", new Cell(2, 4));
            var blast = Explode(g, new Cell(2, 4));
            var step = blast.BlastSteps.Single();
            Assert.That(step.UnitId, Is.EqualTo(victim));
            Assert.That(step.Damage, Is.EqualTo(3), "预算消耗保持原语义");
            Assert.That(step.BonusDamage, Is.Zero);
            Assert.That(step.TotalDamage, Is.EqualTo(3));
            Assert.That(step.Killed, Is.False);
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == victim).Hp, Is.EqualTo(7));
            Assert.That(blast.Damage, Is.Null, "炸弹的结构化事实由步骤承载，不重复挂一份");
        }

        /// <summary>炸弹击杀时步骤 Killed 来自真实结算，死亡事件只出现一次。</summary>
        [Test]
        public void BombKillIsReportedOnce()
        {
            var d = Document();
            d.cards.Single(c => c.id == "miner").hp = 2;
            d.rules[0].bossBombInterval = 2;
            d.rules[0].bossBombDamage = 3;
            d.rules[0].bossBombColumn = 5;
            var g = Start(d);
            var victim = Put(g, "miner", new Cell(2, 4));
            var blast = Explode(g, new Cell(2, 4));
            var step = blast.BlastSteps.Single();
            Assert.That(step.Damage, Is.EqualTo(2), "阵亡后不再消耗剩余预算");
            Assert.That(step.Killed, Is.True);
            Assert.That(g.GetSnapshot().Units.Any(u => u.Id == victim), Is.False);
            Assert.That(g.GetSnapshot().Log.Count(e => e.Kind == "death"), Is.EqualTo(1));
        }
    }
}
