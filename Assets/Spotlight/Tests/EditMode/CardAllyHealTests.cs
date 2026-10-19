// “攻击友方即治疗”能力测试（比目鱼）：可攻击己方单位，对友方的结算改为治疗并受生命上限限流。
// 为什么单独成文件：它改变了攻击目标的集合与结算分支，断言集中在 LegalActions.Heal、生命值与行动消耗上。
using System.Linq;
using NUnit.Framework;

namespace Spotlight.Tests
{
    /// <summary>覆盖治疗结算、上限限流、高亮集合、家不可治疗与配置校验。</summary>
    public class CardAllyHealTests
    {
        /// <summary>
        /// 在标准夹具上追加一个卡种（只改 Cards 表）。
        /// 输入：待改文档与卡行；输出：同一个文档（便于链式调整 Rules）。
        /// 为什么不再在这里登记张数：Deck 表已废除，追加的卡种通过 Start 的 DeckEntry 参数进入抽牌堆。
        /// </summary>
        static ConfigDocument With(ConfigDocument document, CardRow card)
        {
            document.cards = document.cards.Concat(new[] { card }).ToArray();
            return document;
        }

        /// <summary>
        /// 用固定种子创建对局，并把本用例追加的卡种写进抽牌堆。
        /// 输入：文档与追加的抽牌堆条目；输出：可执行公开命令的对局。
        /// 为什么需要：抽牌堆已不能写在文档里，必须在建局时用 WithDeck 显式覆盖；
        /// 默认牌堆来自 GameSessionTests.DefaultDeck()（与原 4/4/4/4 夹具一致）。
        /// </summary>
        static GameSession Start(ConfigDocument document, params DeckEntry[] extra) => new GameSession(
            new GameConfiguration(document).WithDeck(GameSessionTests.DefaultDeck().Concat(extra), false),
            new SeededRandom(42), 100);

        /// <summary>部署指定卡种并断言成功。</summary>
        static UnitState Deploy(GameSession game, string typeId, int row, int column)
        {
            var card = game.GetSnapshot().Hand.First(c => c.TypeId == typeId);
            var result = game.Execute(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(row, column)));
            Assert.That(result.Success, Is.True, result.Error);
            return game.GetSnapshot().Units.Single(u => u.SourceCardId == card.Id);
        }

        /// <summary>比目鱼式单位：攻击 1、射程 1，可攻击友方并改为治疗。</summary>
        static CardRow Flounder() => new CardRow
        {
            tier = "intermediate", id = "flounder", name = "比目鱼", category = "attack", hp = 2, atk = 1, range = 1, moveDistance = 1,
            attacksAlliesAsHeal = true, viewKey = "default"
        };

        /// <summary>
        /// 把玩家区域扩到 5 列，使 Boss 单位能与玩家单位相邻（默认 2 列时只能靠远程，测试无法构造治疗场景）。
        /// </summary>
        static ConfigDocument WideHomeDocument(CardRow extra)
        {
            var document = With(GameSessionTests.Document(), extra);
            document.rules[0].homeRegionWidth = 5;
            return document;
        }

        [Test]
        public void AllyAttackHealsInsteadOfDamaging()
        {
            var game = Start(WideHomeDocument(Flounder()), new DeckEntry("flounder", 4));
            var flounder = Deploy(game, "flounder", 0, 3);
            var miner = Deploy(game, "miner", 0, 4);
            var fullHp = miner.MaxHp;

            // Boss 先打掉 1 点生命：Boss 区域是第 6..10 列，因此 (0,5) 与目标相邻。
            Assert.That(game.Execute(new GameCommand(CommandKind.EndTurn, Side.Player, skipDeployment: true)).Success, Is.True);
            Assert.That(game.Execute(new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(0, 5))).Success, Is.True);
            var boss = game.GetSnapshot().Units.Single(u => u.Owner == Side.Boss);
            Assert.That(game.Execute(new GameCommand(CommandKind.Attack, Side.Boss, boss.Id, targetId: miner.Id)).Success, Is.True);
            Assert.That(game.GetSnapshot().Units.Single(u => u.Id == miner.Id).Hp, Is.EqualTo(fullHp - 1));

            // 回到玩家回合，用比目鱼“攻击”友方：生命 +1，且消耗该单位本回合行动。
            Assert.That(game.Execute(new GameCommand(CommandKind.EndTurn, Side.Boss, skipDeployment: true)).Success, Is.True);
            var heal = game.Execute(new GameCommand(CommandKind.Attack, Side.Player, flounder.Id, targetId: miner.Id));

            Assert.That(heal.Success, Is.True, heal.Error);
            var after = game.GetSnapshot().Units.Single(u => u.Id == miner.Id);
            Assert.That(after.Hp, Is.EqualTo(fullHp), "治疗量应等于攻击力");
            Assert.That(game.GetSnapshot().Units.Single(u => u.Id == flounder.Id).Acted, Is.True, "治疗同样消耗行动");
            Assert.That(game.GetSnapshot().Log.Any(e => e.Kind == "heal"), Is.True, "治疗必须写入 heal 事件");
        }

        [Test]
        public void HealIsCappedByMaxHp()
        {
            var game = Start(WideHomeDocument(Flounder()), new DeckEntry("flounder", 4));
            var flounder = Deploy(game, "flounder", 0, 3);
            var miner = Deploy(game, "miner", 0, 4);

            var result = game.Execute(new GameCommand(CommandKind.Attack, Side.Player, flounder.Id, targetId: miner.Id));

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(game.GetSnapshot().Units.Single(u => u.Id == miner.Id).Hp, Is.EqualTo(miner.MaxHp), "满血治疗不得溢出上限");
        }

        [Test]
        public void HealTargetsAreListedSeparatelyForHighlighting()
        {
            var game = Start(WideHomeDocument(Flounder()), new DeckEntry("flounder", 4));
            var flounder = Deploy(game, "flounder", 0, 3);
            var miner = Deploy(game, "miner", 0, 4);
            var soldier = Deploy(game, "soldier", 1, 3);

            var actions = game.GetLegalActions(new Selection(SelectionKind.Unit, flounder.Id));
            Assert.That(actions.Heal, Does.Contain(miner.Position), "己方单位应出现在治疗高亮里");
            Assert.That(actions.Attack, Does.Contain(miner.Position), "治疗格同时属于合法攻击目标");
            Assert.That(actions.Heal.Any(c => c == new Cell(2, 0)), Is.False, "家不是单位，不能作为治疗目标");

            // 普通攻击卡没有治疗目标，Heal 必须为空，避免界面把敌方目标染成治疗色。
            var plain = game.GetLegalActions(new Selection(SelectionKind.Unit, soldier.Id));
            Assert.That(plain.Heal, Is.Empty);
        }

        [Test]
        public void ValidationRejectsAllyHealOnNonAttackCard()
        {
            var document = GameSessionTests.Document();
            document.cards = document.cards.Concat(new[]
            {
                new CardRow { tier = "intermediate", id = "badHeal", name = "错误治疗", category = "resource", hp = 1, attacksAlliesAsHeal = true, viewKey = "default" }
            }).ToArray();

            var errors = ConfigValidation.Validate(document);

            Assert.That(errors.Any(e => e.Contains("attacksAlliesAsHeal 仅攻击卡可启用")), Is.True, string.Join(" / ", errors));
        }
    }
}
