// 死亡加攻能力测试（力场-I）：全棋盘任一单位“因战斗阵亡”时增加攻击力，最多触发配置的次数。
// 为什么单独成文件：这是纯规则能力（无 Prefab 组件），断言集中在 UnitState 的攻击力与触发次数上；
// 全部通过公开命令推进对局，不直接篡改 GameSession 私有状态。
using System.Linq;
using NUnit.Framework;

namespace Spotlight.Tests
{
    /// <summary>覆盖阵亡加攻、次数上限、非死亡移除不触发与配置校验。</summary>
    public class CardDeathGrowthTests
    {
        /// <summary>在标准夹具上追加一个卡种并加入卡组。</summary>
        static ConfigDocument With(ConfigDocument document, CardRow card, int count = 4)
        {
            document.cards = document.cards.Concat(new[] { card }).ToArray();
            document.deck = document.deck.Concat(new[] { new DeckRow { cardType = card.id, count = count } }).ToArray();
            return document;
        }

        /// <summary>用固定种子创建对局，保证抽牌顺序可复现。</summary>
        static GameSession Start(ConfigDocument document) => new GameSession(new GameConfiguration(document), new SeededRandom(42), 100);

        /// <summary>部署指定卡种并断言成功。</summary>
        static UnitState Deploy(GameSession game, string typeId, int row, int column)
        {
            var card = game.GetSnapshot().Hand.First(c => c.TypeId == typeId);
            var result = game.Execute(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(row, column)));
            Assert.That(result.Success, Is.True, result.Error);
            return game.GetSnapshot().Units.Single(u => u.SourceCardId == card.Id);
        }

        /// <summary>力场式单位：攻击 0、射程 1，每次阵亡 +1，最多 2 次。</summary>
        static CardRow Force() => new CardRow
        {
            tier = "intermediate", id = "force", name = "力场", category = "attack", hp = 3, atk = 0, range = 1, moveDistance = 1,
            deathGrowthComponent = true, deathGrowthPerKill = 1, deathGrowthLimit = 2, viewKey = "default"
        };

        /// <summary>
        /// 玩家在 Boss 区域旁摆好力场与若干个 1 血矿机，Boss 回合可以一一点杀。
        /// 输入：无；输出：已布好阵的对局与力场单位。
        /// </summary>
        static GameSession PreparedForBossKills(out UnitState force, out int victims)
        {
            var document = With(GameSessionTests.Document(), Force());
            document.cards.Single(c => c.id == "miner").hp = 1;
            var game = Start(document);
            force = Deploy(game, "force", 0, 0);
            Deploy(game, "miner", 0, 8);
            Deploy(game, "miner", 1, 8);
            Deploy(game, "miner", 3, 8);
            victims = 3;
            Assert.That(game.Execute(new GameCommand(CommandKind.EndTurn, Side.Player, skipDeployment: true)).Success, Is.True);
            return game;
        }

        [Test]
        public void DeathsRaiseAttackUpToConfiguredLimit()
        {
            var game = PreparedForBossKills(out var force, out var victims);
            Assert.That(force.Attack, Is.Zero, "力场初始攻击为 0");

            // 每回合放一个 Boss 单位并击杀一个矿机：3 次阵亡，但上限是 2 次。
            var rows = new[] { 0, 1, 3 };
            foreach (var row in rows)
            {
                Assert.That(game.Execute(new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(row, 9))).Success, Is.True);
                var boss = game.GetSnapshot().Units.Single(u => u.Owner == Side.Boss && u.Position == new Cell(row, 9));
                var victim = game.GetSnapshot().Units.Single(u => u.Owner == Side.Player && u.Position == new Cell(row, 8));
                Assert.That(game.Execute(new GameCommand(CommandKind.Attack, Side.Boss, boss.Id, targetId: victim.Id)).Success, Is.True);
            }

            var after = game.GetSnapshot().Units.Single(u => u.Id == force.Id);
            Assert.That(after.DeathGrowthTriggers, Is.EqualTo(2), "达到次数上限后不再累加");
            Assert.That(after.Attack, Is.EqualTo(2), "每次阵亡 +1，上限 2 次");
            Assert.That(game.GetSnapshot().Units.Count(u => u.Owner == Side.Player && u.TypeId == "miner"), Is.Zero, "三个矿机都应阵亡");
        }

        [Test]
        public void NonDeathRemovalDoesNotRaiseAttack()
        {
            var document = With(GameSessionTests.Document(), Force());
            var game = Start(document);
            var force = Deploy(game, "force", 0, 0);
            var miner = Deploy(game, "miner", 0, 1);

            Assert.That(game.Execute(new GameCommand(CommandKind.Withdraw, Side.Player, miner.Id)).Success, Is.True);

            var after = game.GetSnapshot().Units.Single(u => u.Id == force.Id);
            Assert.That(after.DeathGrowthTriggers, Is.Zero, "主动离场不是阵亡，不得触发加成");
            Assert.That(after.Attack, Is.Zero);
        }

        [Test]
        public void ValidationRejectsInconsistentGrowthFields()
        {
            var document = GameSessionTests.Document();
            document.cards = document.cards.Concat(new[]
            {
                new CardRow { tier = "intermediate", id = "badGrowth1", name = "缺数值", category = "attack", hp = 1, deathGrowthComponent = true, viewKey = "default" },
                new CardRow { tier = "intermediate", id = "badGrowth2", name = "多数值", category = "attack", hp = 1, deathGrowthPerKill = 1, deathGrowthLimit = 4, viewKey = "default" }
            }).ToArray();

            var errors = ConfigValidation.Validate(document);
            var all = string.Join(" / ", errors);

            Assert.That(errors.Any(e => e.Contains("启用 deathGrowthComponent 时加成与次数上限必须为正整数")), Is.True, all);
            Assert.That(errors.Any(e => e.Contains("未启用 deathGrowthComponent 时加成与次数上限必须为 0")), Is.True, all);
        }
    }
}
