// 入场标记能力测试（星棹收集系统）：标记只在进入范围那一刻生效、永久保留、不叠加，阵亡时给标记者阵营发资源。
// 为什么单独成文件：这是纯规则能力（无 Prefab 组件），断言集中在 UnitState.MarkedBy 与资源结算上；
// 全部通过公开命令推进对局，不直接篡改 GameSession 私有状态。
using System.Linq;
using NUnit.Framework;

namespace Spotlight.Tests
{
    /// <summary>覆盖标记产生、永久保留、不叠加、阵亡发放、非死亡不发放与配置校验。</summary>
    public class CardMarkOnEnterTests
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

        /// <summary>星棹式标记来源：资源卡，半径 1、奖励 1。</summary>
        static CardRow Collector() => new CardRow
        {
            tier = "intermediate", id = "collector", name = "收集系统", category = "resource", hp = 1, produce = 1, resourceComponent = true,
            markOnEnterComponent = true, markOnEnterRadius = 1, markRewardResource = 1, viewKey = "default"
        };

        /// <summary>
        /// 准备一个“标记来源 + 待标记目标”的棋盘：目标放在 Boss 区域旁，便于下一回合由 Boss 单位击杀。
        /// 输入：无；输出：已部署收集系统与 1 血矿机的对局。
        /// </summary>
        static GameSession MarkedBoard(out UnitState source, out UnitState target)
        {
            var document = With(GameSessionTests.Document(), Collector());
            document.cards.Single(c => c.id == "miner").hp = 1; // 只为让一次 1 点伤害就能击杀
            var game = Start(document);
            source = Deploy(game, "collector", 0, 8);
            target = Deploy(game, "miner", 0, 9);
            return game;
        }

        [Test]
        public void EnteringRangeMarksUnitAndDeathPaysMarker()
        {
            var game = MarkedBoard(out var source, out var target);
            var marked = game.GetSnapshot().Units.Single(u => u.Id == target.Id);
            Assert.That(marked.MarkedBy, Is.EqualTo(Side.Player), "落入半径内的单位应被标记");
            Assert.That(marked.MarkReward, Is.EqualTo(1));
            Assert.That(marked.MarkedBy, Is.EqualTo(source.Owner));

            // 交出回合，由手动 Boss 在目标旁部署并击杀；玩家资源应在阵亡瞬间增加奖励值。
            var playerSnapshot = game.GetSnapshot();
            Assert.That(game.Execute(new GameCommand(CommandKind.EndTurn, Side.Player, skipDeployment: true)).Success, Is.True);
            Assert.That(game.Execute(new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(1, 9))).Success, Is.True);
            var boss = game.GetSnapshot().Units.Single(u => u.Owner == Side.Boss);
            var resourcesBeforeKill = game.GetSnapshot().Resources;

            var attack = game.Execute(new GameCommand(CommandKind.Attack, Side.Boss, boss.Id, targetId: target.Id));

            Assert.That(attack.Success, Is.True, attack.Error);
            Assert.That(game.GetSnapshot().Units.Any(u => u.Id == target.Id), Is.False, "目标应已阵亡");
            Assert.That(game.GetSnapshot().Resources, Is.EqualTo(resourcesBeforeKill + 1), "标记目标阵亡必须发放奖励资源");
            Assert.That(playerSnapshot.Resources, Is.EqualTo(resourcesBeforeKill), "阵亡前资源不应变化");
        }

        [Test]
        public void MarkStaysAfterSourceWithdraw()
        {
            var game = MarkedBoard(out var source, out var target);

            Assert.That(game.Execute(new GameCommand(CommandKind.Withdraw, Side.Player, source.Id)).Success, Is.True);

            var snapshot = game.GetSnapshot();
            Assert.That(snapshot.Units.Any(u => u.Id == source.Id), Is.False, "来源应已离场");
            Assert.That(snapshot.Units.Single(u => u.Id == target.Id).MarkedBy, Is.EqualTo(Side.Player), "来源离场后标记必须保留");
        }

        [Test]
        public void WithdrawDoesNotPayReward()
        {
            var game = MarkedBoard(out _, out var target);
            var before = game.GetSnapshot().Resources;

            Assert.That(game.Execute(new GameCommand(CommandKind.Withdraw, Side.Player, target.Id)).Success, Is.True);

            Assert.That(game.GetSnapshot().Resources, Is.EqualTo(before), "主动离场不是死亡，不得发放标记奖励");
        }

        [Test]
        public void OverlappingSourcesMarkOnlyOnce()
        {
            var document = With(GameSessionTests.Document(), Collector());
            var game = Start(document);
            Deploy(game, "collector", 0, 1);
            var target = Deploy(game, "miner", 0, 0);
            Deploy(game, "collector", 1, 0);

            var snapshot = game.GetSnapshot();
            var marked = snapshot.Units.Single(u => u.Id == target.Id);
            Assert.That(marked.MarkedBy, Is.EqualTo(Side.Player));
            Assert.That(snapshot.Log.Count(e => e.Kind == "mark" && e.SubjectId == target.Id), Is.EqualTo(1), "多个来源重叠时同一目标只标记一次");
        }

        [Test]
        public void ValidationRejectsInconsistentMarkFields()
        {
            var document = GameSessionTests.Document();
            document.cards = document.cards.Concat(new[]
            {
                new CardRow { tier = "intermediate", id = "badMark1", name = "缺数值", category = "resource", hp = 1, markOnEnterComponent = true, viewKey = "default" },
                new CardRow { tier = "intermediate", id = "badMark2", name = "多数值", category = "resource", hp = 1, markOnEnterRadius = 1, markRewardResource = 1, viewKey = "default" }
            }).ToArray();

            var errors = ConfigValidation.Validate(document);
            var all = string.Join(" / ", errors);

            Assert.That(errors.Any(e => e.Contains("启用 markOnEnterComponent 时标记半径与奖励必须为正整数")), Is.True, all);
            Assert.That(errors.Any(e => e.Contains("未启用 markOnEnterComponent 时标记半径与奖励必须为 0")), Is.True, all);
        }
    }
}
