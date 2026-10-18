// 伤害标记（SNP-EU 机制）的规则回归测试。
// 输入：独立配置夹具与公开命令；输出：挂层时序、叠层、永久状态、伤害归属与无递归的断言。
// 只通过 Execute/GetSnapshot/GetLegalActions 推进对局，不篡改 GameSession 私有状态。
using System.Linq;
using NUnit.Framework;

namespace Spotlight.Tests
{
    /// <summary>
    /// 覆盖“只有直接攻击存活战斗单位才挂层”“本次受伤按受伤前层数结算”“追加伤害不递归、不挂层”
    /// “家不挂层、治疗不挂层”“旧快照与来源离场不影响已有层”等已确认规则。
    /// </summary>
    public class CardDamageMarkTests
    {
        /// <summary>
        /// 构造隔离夹具。
        /// 输入：是否追加“marksman”卡种（每层 2 点，用于验证不同来源的层保留各自数值）；
        /// 输出：家区域放大到全棋盘、士兵带 1 点/层伤害标记、Boss 单位 30 血的配置。
        /// </summary>
        static ConfigDocument Document(bool withMarksman = false)
        {
            var d = GameSessionTests.Document();
            d.rules[0].homeRegionWidth = 10;
            var soldier = d.cards.Single(c => c.id == "soldier");
            soldier.damageMarkComponent = true;
            soldier.damageMarkDamagePerStack = 1;
            d.cards.Single(c => c.id == "boss").hp = 30;
            if (withMarksman)
                d.cards = d.cards.Concat(new[]
                {
                    new CardRow
                    {
                        tier = "basic", id = "marksman", name = "神射手", category = "attack",
                        hp = 2, atk = 2, range = 2, moveDistance = 1,
                        damageMarkComponent = true, damageMarkDamagePerStack = 2, viewKey = "default"
                    }
                }).ToArray();
            return d;
        }

        /// <summary>建立对局。输入：配置与牌堆；输出：固定种子的规则内核。</summary>
        static GameSession Start(ConfigDocument d, params DeckEntry[] deck) =>
            new GameSession(new GameConfiguration(d).WithDeck(deck.Length > 0 ? deck : new[] { new DeckEntry("soldier", 16) }, false), new SeededRandom(23), 1000);

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

        /// <summary>当前行动方对目标发动一次攻击。输入：对局、攻击者与目标 ID；输出：命令结果。</summary>
        static CommandResult Attack(GameSession g, long attacker, long target) =>
            Do(g, new GameCommand(CommandKind.Attack, g.GetSnapshot().ActiveSide, attacker, targetId: target));

        /// <summary>
        /// 建立“玩家两个士兵已就位、Boss 单位相邻”的局面。
        /// 输入：对局与 Boss 目标格；输出：Boss 单位 ID。士兵固定在 (2,4) 与 (2,3)，均能攻击 (2,5)。
        /// </summary>
        static long FaceOff(GameSession g)
        {
            Put(g, "soldier", new Cell(2, 4));
            Put(g, "soldier", new Cell(2, 3));
            End(g);
            Do(g, new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(2, 5)));
            var boss = g.GetSnapshot().Units.Single(u => u.Owner == Side.Boss).Id;
            End(g);
            return boss;
        }

        /// <summary>取指定结果的攻击/标记事件中的结构化事实。输入：结果与事件类型；输出：事件。</summary>
        static GameEvent Event(GameSnapshot snapshot, string kind) => snapshot.Log.Last(e => e.Kind == kind);

        /// <summary>首次攻击只结算自身伤害，目标存活后获得 1 层；第二击才按已有层追加。</summary>
        [Test]
        public void FirstHitUsesOwnDamageAndMarksOnlyForNextHit()
        {
            var g = Start(Document());
            var target = FaceOff(g);
            var attacker = g.GetSnapshot().Units.First(u => u.TypeId == "soldier" && u.Position == new Cell(2, 4)).Id;

            var first = Attack(g, attacker, target).Events.Single(e => e.Kind == "attack");
            Assert.That(first.Damage.BaseDamage, Is.EqualTo(2));
            Assert.That(first.Damage.BonusDamage, Is.Zero, "首次攻击没有既有标记，不应追加");
            var marked = g.GetSnapshot().Units.Single(u => u.Id == target);
            Assert.That(marked.DamageMarkLayers, Is.EqualTo(1));
            Assert.That(marked.DamageMarkBonus, Is.EqualTo(1));
            Assert.That(marked.Hp, Is.EqualTo(28));

            var second = g.GetSnapshot().Units.First(u => u.TypeId == "soldier" && u.Position == new Cell(2, 3)).Id;
            var hit = Attack(g, second, target).Events.Single(e => e.Kind == "attack");
            Assert.That(hit.Damage.BaseDamage, Is.EqualTo(2));
            Assert.That(hit.Damage.BonusDamage, Is.EqualTo(1), "本次受伤按受伤前 1 层结算");
            Assert.That(hit.Damage.TotalDamage, Is.EqualTo(3));
            var after = g.GetSnapshot().Units.Single(u => u.Id == target);
            Assert.That(after.DamageMarkLayers, Is.EqualTo(2), "存活目标在攻击结束后再增加一层");
            Assert.That(after.Hp, Is.EqualTo(25));
        }

        /// <summary>攻击力为 3 时只触发一组标记追加，而不是按 3 个基础点重复触发。</summary>
        [Test]
        public void MultiPointAttackTriggersMarksOnlyOnce()
        {
            var d = Document();
            d.cards.Single(c => c.id == "soldier").atk = 3;
            var g = Start(d);
            var a = Put(g, "soldier", new Cell(2, 4));
            var b = Put(g, "soldier", new Cell(2, 3));
            var thirdAttacker = Put(g, "soldier", new Cell(1, 4));
            End(g);
            Do(g, new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(2, 5)));
            var target = g.GetSnapshot().Units.Single(u => u.Owner == Side.Boss).Id;
            End(g);
            Attack(g, a, target);
            Attack(g, b, target);
            // 第三次攻击必须由尚未行动的单位发起，否则会被“本回合已行动”拒绝。
            var third = Attack(g, thirdAttacker, target).Events.Single(e => e.Kind == "attack");
            Assert.That(third.Damage.BaseDamage, Is.EqualTo(3));
            Assert.That(third.Damage.BonusDamage, Is.EqualTo(2), "两层各追加一次，共 2 点");
            Assert.That(third.Damage.TotalDamage, Is.EqualTo(5));
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == target).DamageMarkLayers, Is.EqualTo(3));
        }

        /// <summary>追加伤害不递归：一次受伤只挂一层，标记事件只出现一次。</summary>
        [Test]
        public void BonusDamageNeitherRecursesNorAddsExtraMark()
        {
            var g = Start(Document());
            var target = FaceOff(g);
            var a = g.GetSnapshot().Units.First(u => u.Position == new Cell(2, 4)).Id;
            Attack(g, a, target);
            var before = g.GetSnapshot().Log.Count(e => e.Kind == "damage-mark");
            var b = g.GetSnapshot().Units.First(u => u.Position == new Cell(2, 3)).Id;
            var result = Attack(g, b, target);
            Assert.That(result.Events.Count(e => e.Kind == "damage-mark"), Is.EqualTo(1), "一次攻击只挂一层");
            Assert.That(g.GetSnapshot().Log.Count(e => e.Kind == "damage-mark"), Is.EqualTo(before + 1));
            Assert.That(result.Events.Single(e => e.Kind == "attack").Damage.BonusDamage, Is.EqualTo(1), "追加伤害本身不再次追加");
        }

        /// <summary>家不能被施加伤害标记，攻击家只造成基础伤害。</summary>
        [Test]
        public void HomesNeverReceiveDamageMarks()
        {
            var d = Document();
            d.rules[0].homeRegionWidth = 10;
            var g = Start(d);
            var a = Put(g, "soldier", new Cell(2, 7));
            var result = Attack(g, a, -2);
            Assert.That(result.Events.Any(e => e.Kind == "damage-mark"), Is.False);
            Assert.That(result.Events.Single(e => e.Kind == "attack").Damage.BonusDamage, Is.Zero);
            Assert.That(g.GetSnapshot().Homes.Single(h => h.Owner == Side.Boss).Hp, Is.EqualTo(8));
        }

        /// <summary>治疗不是伤害：不触发标记、不挂层，也不清除目标已有层。</summary>
        [Test]
        public void HealingNeitherTriggersNorClearsMarks()
        {
            var d = Document();
            var soldier = d.cards.Single(c => c.id == "soldier");
            soldier.attacksAlliesAsHeal = true;
            var g = Start(d);
            var healer = Put(g, "soldier", new Cell(2, 4));
            var ally = Put(g, "soldier", new Cell(2, 3));
            var result = Do(g, new GameCommand(CommandKind.Attack, Side.Player, healer, targetId: ally));
            Assert.That(result.Events.Any(e => e.Kind == "heal"), Is.True);
            Assert.That(result.Events.Any(e => e.Kind == "damage-mark"), Is.False);
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == ally).DamageMarkLayers, Is.Zero);
        }

        /// <summary>击杀归属看本次攻击的根来源：后手攻击用基础伤害击杀时不再追加，也不会记到最初施加者头上。</summary>
        [Test]
        public void KillBelongsToTheAttackerThatDealtIt()
        {
            var d = Document();
            d.cards.Single(c => c.id == "boss").hp = 4;
            var g = Start(d);
            var target = FaceOff(g);
            var a = g.GetSnapshot().Units.First(u => u.Position == new Cell(2, 4)).Id;
            var b = g.GetSnapshot().Units.First(u => u.Position == new Cell(2, 3)).Id;
            Attack(g, a, target);
            var result = Attack(g, b, target);
            var damage = result.Events.Single(e => e.Kind == "attack").Damage;
            Assert.That(damage.Killed, Is.True);
            Assert.That(damage.SourceUnitId, Is.EqualTo(b), "结构化来源必须是本次攻击者");
            Assert.That(damage.BonusDamage, Is.Zero, "基础伤害已击杀，不再追加");
            Assert.That(result.Events.Count(e => e.Kind == "death"), Is.EqualTo(1));
        }

        /// <summary>旧快照保持攻击前的层数，后续挂层不会改写已交付的数据。</summary>
        [Test]
        public void OldSnapshotKeepsMarkLayers()
        {
            var g = Start(Document());
            var target = FaceOff(g);
            var a = g.GetSnapshot().Units.First(u => u.Position == new Cell(2, 4)).Id;
            var b = g.GetSnapshot().Units.First(u => u.Position == new Cell(2, 3)).Id;
            Attack(g, a, target);
            var snapshot = g.GetSnapshot();
            Attack(g, b, target);
            var oldUnit = snapshot.Units.Single(u => u.Id == target);
            Assert.That(oldUnit.DamageMarkLayers, Is.EqualTo(1));
            Assert.That(oldUnit.DamageMarkBonus, Is.EqualTo(1));
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == target).DamageMarkLayers, Is.EqualTo(2));
        }

        /// <summary>不同来源的层保留各自施加时的数值；来源离场后仍然生效。</summary>
        [Test]
        public void LayersKeepAppliedValuesAfterSourceLeaves()
        {
            var g = Start(Document(true), new DeckEntry("marksman", 8), new DeckEntry("soldier", 8));
            var marksman = Put(g, "marksman", new Cell(2, 3));
            End(g);
            Do(g, new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(2, 4)));
            var target = g.GetSnapshot().Units.Single(u => u.Owner == Side.Boss).Id;
            End(g);
            Attack(g, marksman, target);
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == target).DamageMarkBonus, Is.EqualTo(2));
            Do(g, new GameCommand(CommandKind.Withdraw, Side.Player, marksman));
            var soldier = Put(g, "soldier", new Cell(2, 5));
            var hit = Attack(g, soldier, target).Events.Single(e => e.Kind == "attack");
            Assert.That(hit.Damage.BonusDamage, Is.EqualTo(2), "层数值来自施加时副本，不随来源离场消失");
        }

        /// <summary>标记随单位消失：同一卡种重新部署的新单位不继承旧层。</summary>
        [Test]
        public void FreshUnitDoesNotInheritMarks()
        {
            var d = Document();
            d.cards.Single(c => c.id == "boss").hp = 4;
            var g = Start(d);
            var target = FaceOff(g);
            var a = g.GetSnapshot().Units.First(u => u.Position == new Cell(2, 4)).Id;
            Attack(g, a, target);
            var b = g.GetSnapshot().Units.First(u => u.Position == new Cell(2, 3)).Id;
            Attack(g, b, target);
            Assert.That(g.GetSnapshot().Units.Any(u => u.Id == target), Is.False);
            End(g);
            Do(g, new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(2, 5)));
            var fresh = g.GetSnapshot().Units.Single(u => u.Owner == Side.Boss);
            Assert.That(fresh.DamageMarkLayers, Is.Zero);
            Assert.That(fresh.DamageMarkBonus, Is.Zero);
        }

        /// <summary>标记跨回合永久保留：结束并重新开始玩家回合后层数不变。</summary>
        [Test]
        public void MarksSurviveAcrossTurns()
        {
            var g = Start(Document());
            var target = FaceOff(g);
            var a = g.GetSnapshot().Units.First(u => u.Position == new Cell(2, 4)).Id;
            Attack(g, a, target);
            End(g);
            End(g);
            var marked = g.GetSnapshot().Units.Single(u => u.Id == target);
            Assert.That(marked.DamageMarkLayers, Is.EqualTo(1));
            Assert.That(Event(g.GetSnapshot(), "turn"), Is.Not.Null);
        }
    }
}
