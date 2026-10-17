// 新增卡牌机制测试：部署消耗（deployCost）与邻域部署要求（deployRequireAdjacent*）。
// 为什么单独成文件：这两项是卡牌级部署限制，和既有能力组件（战斗/资源/传送/减攻）分开演进，
// 用例集中在一处便于后续加卡时对照；全部通过公开命令推进对局，不直接篡改 GameSession 私有状态。
// 夹具数值只用于测试断言，不代表正式默认值（正式数值只来自 Excel 编译产物）。
using System.Linq;
using NUnit.Framework;

namespace Spotlight.Tests
{
    /// <summary>覆盖部署资源扣减、资源不足拒绝、合法目标过滤、Boss 豁免与邻域部署要求。</summary>
    public class CardDeployCostTests
    {
        /// <summary>
        /// 在标准夹具上追加一个卡种并加入卡组。
        /// 输入：待改文档、卡行与初始张数；输出：同一个文档（便于链式调整 Rules）。
        /// 为什么复用 GameSessionTests.Document()：确保新用例与既有回归共用同一套规则基线。
        /// </summary>
        static ConfigDocument With(ConfigDocument document, CardRow card, int count = 4)
        {
            document.cards = document.cards.Concat(new[] { card }).ToArray();
            document.deck = document.deck.Concat(new[] { new DeckRow { cardType = card.id, count = count } }).ToArray();
            return document;
        }

        /// <summary>用固定种子创建对局，保证抽牌顺序可复现。</summary>
        static GameSession Start(ConfigDocument document) => new GameSession(new GameConfiguration(document), new SeededRandom(42), 100);

        /// <summary>部署指定卡种到指定格并断言成功，返回新单位。</summary>
        static UnitState Deploy(GameSession game, string typeId, int row, int column)
        {
            var card = game.GetSnapshot().Hand.First(c => c.TypeId == typeId);
            var result = game.Execute(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(row, column)));
            Assert.That(result.Success, Is.True, result.Error);
            return game.GetSnapshot().Units.Single(u => u.SourceCardId == card.Id);
        }

        /// <summary>攻击卡夹具：可指定部署消耗。</summary>
        static CardRow Costly(int cost) => new CardRow
        {
            tier = "basic", id = "costly", name = "昂贵卡", category = "attack", hp = 2, atk = 1, range = 1, moveDistance = 1,
            deployCost = cost, viewKey = "default"
        };

        /// <summary>资源卡夹具：要求四邻至少有 count 个资源卡单位。</summary>
        static CardRow Kepler(int count) => new CardRow
        {
            tier = "intermediate", id = "kepler", name = "开普勒", category = "resource", hp = 2, produce = 3, resourceComponent = true,
            deployRequireAdjacentCategory = "resource", deployRequireAdjacentCount = count, viewKey = "default"
        };

        [Test]
        public void DeployCostIsDeductedFromResources()
        {
            // 首回合资源 = initialResources + 玩家家产出 2，因此先记录快照再比较差额。
            var document = With(GameSessionTests.Document(), Costly(5));
            document.rules[0].initialResources = 10;
            var game = Start(document);
            var before = game.GetSnapshot().Resources;

            Deploy(game, "costly", 0, 0);

            Assert.That(game.GetSnapshot().Resources, Is.EqualTo(before - 5), "部署成功后必须只扣一次配置消耗");
        }

        [Test]
        public void DeployCostRejectsWhenResourcesAreInsufficient()
        {
            // 默认夹具初始资源 0、家产出 2，凑不出 50：失败必须完全原子，不扣资源、不移出手牌、不产生单位。
            var document = With(GameSessionTests.Document(), Costly(50));
            var game = Start(document);
            var before = game.GetSnapshot();
            var card = before.Hand.First(c => c.TypeId == "costly");

            var result = game.Execute(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(0, 0)));

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("资源不足"));
            var after = game.GetSnapshot();
            Assert.That(after.Resources, Is.EqualTo(before.Resources), "失败不得扣资源");
            Assert.That(after.Hand.Count, Is.EqualTo(before.Hand.Count), "失败不得移除手牌");
            Assert.That(after.Units, Is.Empty, "失败不得部署单位");
        }

        [Test]
        public void DeployCostHidesLegalDeployTargets()
        {
            // 资源不足时规则层不下发任何部署目标（高亮为空），但同一局里放得起的卡不受影响。
            var document = With(GameSessionTests.Document(), Costly(50));
            var game = Start(document);
            var expensive = game.GetSnapshot().Hand.First(c => c.TypeId == "costly");
            var cheap = game.GetSnapshot().Hand.First(c => c.TypeId == "miner");

            Assert.That(game.GetLegalActions(new Selection(SelectionKind.Hand, expensive.Id)).Deploy, Is.Empty);
            Assert.That(game.GetLegalActions(new Selection(SelectionKind.Hand, cheap.Id)).Deploy, Is.Not.Empty);
        }

        [Test]
        public void BossDeployIgnoresDeployCost()
        {
            // Boss 没有资源系统：即使配置了部署消耗也照常放牌，维持“Boss 不受资源限制”的既有规则。
            var document = GameSessionTests.Document();
            document.cards = document.cards.Concat(new[]
            {
                new CardRow { tier = "none", id = "bossCostly", name = "Boss贵兵", category = "attack", hp = 3, atk = 1, range = 1, moveDistance = 1, deployCost = 999, viewKey = "default" }
            }).ToArray();
            document.rules[0].bossCardType = "bossCostly";
            document.rules[0].playerControlsBoss = true;
            var game = Start(document);

            var snapshot = game.GetSnapshot();
            Assert.That(game.Execute(new GameCommand(CommandKind.EndTurn, snapshot.ActiveSide, skipDeployment: true)).Success, Is.True);
            var result = game.Execute(new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(0, 8)));

            Assert.That(result.Success, Is.True, result.Error);
        }

        [Test]
        public void AdjacencyRequirementNeedsEnoughUnits()
        {
            // (0,1) 的四邻中只有一个资源卡单位，少于配置的 2 个：拒绝部署并说明当前数量。
            var document = With(GameSessionTests.Document(), Kepler(2));
            var game = Start(document);
            Deploy(game, "miner", 0, 0);
            var card = game.GetSnapshot().Hand.First(c => c.TypeId == "kepler");

            var result = game.Execute(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(0, 1)));

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("四邻需要至少 2 个资源卡单位"));
        }

        [Test]
        public void AdjacencyRequirementAllowsDeployWhenMet()
        {
            // (0,0) 与 (0,2) 各一个资源卡单位，正好构成 (0,1) 的两个四邻。
            var document = With(GameSessionTests.Document(), Kepler(2));
            var game = Start(document);
            Deploy(game, "miner", 0, 0);
            Deploy(game, "miner", 0, 2);

            Deploy(game, "kepler", 0, 1);

            Assert.That(game.GetSnapshot().Units.Any(u => u.TypeId == "kepler"), Is.True);
        }

        [Test]
        public void AdjacencyRequirementDoesNotCountHomes()
        {
            // (2,1) 只与玩家家 (2,0) 相邻：家不是单位，因此计数为 0，必须拒绝部署。
            var document = With(GameSessionTests.Document(), Kepler(1));
            var game = Start(document);
            var card = game.GetSnapshot().Hand.First(c => c.TypeId == "kepler");

            var result = game.Execute(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(2, 1)));

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("当前 0"));
        }

        [Test]
        public void ValidationRejectsInconsistentDeployFields()
        {
            var document = GameSessionTests.Document();
            document.cards = document.cards.Concat(new[]
            {
                new CardRow { tier = "intermediate", id = "badCost", name = "负消耗", category = "attack", hp = 1, deployCost = -1, viewKey = "default" },
                new CardRow { tier = "intermediate", id = "badAdj", name = "缺分类", category = "resource", hp = 1, deployRequireAdjacentCount = 2, viewKey = "default" },
                new CardRow { tier = "intermediate", id = "badAdj2", name = "多余分类", category = "resource", hp = 1, deployRequireAdjacentCategory = "resource", viewKey = "default" }
            }).ToArray();

            var errors = ConfigValidation.Validate(document);
            var all = string.Join(" / ", errors);

            Assert.That(errors.Any(e => e.Contains("deployCost 不得为负")), Is.True, all);
            Assert.That(errors.Any(e => e.Contains("deployRequireAdjacentCategory 必须为 resource/attack/special")), Is.True, all);
            Assert.That(errors.Any(e => e.Contains("deployRequireAdjacentCount 为 0 时 deployRequireAdjacentCategory 必须留空")), Is.True, all);
        }
    }
}
