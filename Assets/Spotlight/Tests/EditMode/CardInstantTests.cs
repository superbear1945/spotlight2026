// 即时卡测试（战时储备）：部署即结算、结算即退场。
// 为什么单独成文件：它引入了新的退场原因 Consumed，并需要证明“在场时间视为 0”不会顺带触发
// 一次性减攻、入场标记、死亡加攻等既有能力。全部通过公开命令推进对局。
using System.Linq;
using NUnit.Framework;

namespace Spotlight.Tests
{
    /// <summary>覆盖资源入账、退场、牌区守恒、不参与入场标记与配置校验。</summary>
    public class CardInstantTests
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

        /// <summary>战时储备式即时卡：特殊卡，部署后获得 3 资源并立即离场。</summary>
        static CardRow Reserve() => new CardRow
        {
            tier = "intermediate", id = "reserve", name = "战时储备", category = "special", hp = 1,
            leaveAfterDeploy = true, deployGainResource = 3, viewKey = "default"
        };

        /// <summary>星棹式标记来源：资源卡，半径 1、奖励 1。</summary>
        static CardRow Collector() => new CardRow
        {
            tier = "intermediate", id = "collector", name = "收集系统", category = "resource", hp = 1, produce = 1, resourceComponent = true,
            markOnEnterComponent = true, markOnEnterRadius = 1, markRewardResource = 1, viewKey = "default"
        };

        [Test]
        public void InstantCardGrantsResourceAndLeavesBoard()
        {
            var game = Start(With(GameSessionTests.Document(), Reserve()));
            var before = game.GetSnapshot();
            var card = before.Hand.First(c => c.TypeId == "reserve");

            var result = game.Execute(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(0, 0)));

            Assert.That(result.Success, Is.True, result.Error);
            var after = game.GetSnapshot();
            Assert.That(after.Resources, Is.EqualTo(before.Resources + 3), "即时卡必须立即入账配置的资源");
            Assert.That(after.Units, Is.Empty, "即时卡不得留在棋盘上");
            Assert.That(after.Hand.Count, Is.EqualTo(before.Hand.Count - 1), "手牌应减少一张");
            Assert.That(after.Graveyard.Count, Is.EqualTo(before.Graveyard.Count + 1), "玩家卡部署成功即进墓地");
            Assert.That(after.Log.Any(e => e.Kind == "instant"), Is.True, "应写入 instant 事件");
            Assert.That(after.Log.Any(e => e.Kind == "consume"), Is.True, "应以“结算后离场”的原因移除");
        }

        [Test]
        public void InstantCardDoesNotGetMarked()
        {
            // 星棹在旁边：即时卡只存在一瞬间，不能留下“被标记”的幽灵状态，也不能多产生一条 mark 事件。
            var document = With(GameSessionTests.Document(), Reserve());
            document.cards = document.cards.Concat(new[] { Collector() }).ToArray();
            document.deck = document.deck.Concat(new[] { new DeckRow { cardType = "collector", count = 4 } }).ToArray();
            var game = Start(document);
            var collectorCard = game.GetSnapshot().Hand.First(c => c.TypeId == "collector");
            Assert.That(game.Execute(new GameCommand(CommandKind.Deploy, Side.Player, collectorCard.Id, new Cell(0, 0))).Success, Is.True);
            var reserveCard = game.GetSnapshot().Hand.First(c => c.TypeId == "reserve");

            Assert.That(game.Execute(new GameCommand(CommandKind.Deploy, Side.Player, reserveCard.Id, new Cell(0, 1))).Success, Is.True);

            var marks = game.GetSnapshot().Log.Where(e => e.Kind == "mark").ToArray();
            Assert.That(marks, Is.Empty, "即时卡不应触发入场标记（它自身也不应被标记）");
            Assert.That(game.GetSnapshot().Units.Count, Is.EqualTo(1), "场上只应剩下收集系统");
        }

        [Test]
        public void InstantCardDeckTotalStaysConstant()
        {
            var game = Start(With(GameSessionTests.Document(), Reserve()));
            var before = game.GetSnapshot();
            var total = before.Hand.Count + before.DrawPile.Count + before.Graveyard.Count;
            var card = before.Hand.First(c => c.TypeId == "reserve");

            Assert.That(game.Execute(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(0, 0))).Success, Is.True);

            var after = game.GetSnapshot();
            Assert.That(after.Hand.Count + after.DrawPile.Count + after.Graveyard.Count, Is.EqualTo(total), "抽牌堆+手牌+墓地总数必须守恒");
        }

        [Test]
        public void ValidationRejectsInconsistentInstantAndBossReference()
        {
            var document = GameSessionTests.Document();
            document.cards = document.cards.Concat(new[]
            {
                new CardRow { tier = "intermediate", id = "badInstant1", name = "缺收益", category = "special", hp = 1, leaveAfterDeploy = true, viewKey = "default" },
                new CardRow { tier = "intermediate", id = "badInstant2", name = "多数益", category = "special", hp = 1, deployGainResource = 3, viewKey = "default" }
            }).ToArray();
            var errors = ConfigValidation.Validate(document);
            var all = string.Join(" / ", errors);
            Assert.That(errors.Any(e => e.Contains("启用 leaveAfterDeploy 时 deployGainResource 必须为正整数")), Is.True, all);
            Assert.That(errors.Any(e => e.Contains("未启用 leaveAfterDeploy 时 deployGainResource 必须为 0")), Is.True, all);

            // Boss 卡种不能是即时卡：否则补兵逻辑会面对“已移除的单位”。
            var bossDocument = GameSessionTests.Document();
            bossDocument.cards.Single(c => c.id == "boss").leaveAfterDeploy = true;
            bossDocument.cards.Single(c => c.id == "boss").deployGainResource = 1;
            var bossErrors = ConfigValidation.Validate(bossDocument);
            Assert.That(bossErrors.Any(e => e.Contains("Boss 卡种不能是即时卡")), Is.True, string.Join(" / ", bossErrors));
        }
    }
}
