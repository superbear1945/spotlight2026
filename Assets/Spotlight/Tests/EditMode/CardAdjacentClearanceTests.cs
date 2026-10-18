// 四邻空格限制（后羿-II综合舰机制）的规则回归测试。
// 输入：独立配置夹具与公开命令；输出：部署/移动/互换的前后布局、棋盘外计空、被围仍可攻击等断言。
// 只通过 Execute/GetLegalActions/GetSnapshot 推进对局，不篡改 GameSession 私有状态。
using System.Linq;
using NUnit.Framework;

namespace Spotlight.Tests
{
    /// <summary>
    /// 覆盖已确认规则：四邻（上下左右）至少 N 格为空；斜角不计、棋盘外算空；
    /// 部署只看目标格，移动看起点与落点，互换看交换前后且双方都检查；
    /// 路径中间格不检查；被围后仍可攻击；失败命令不改变状态。
    /// </summary>
    public class CardAdjacentClearanceTests
    {
        /// <summary>
        /// 构造隔离夹具。
        /// 输入：最少空邻格数与是否追加一张无能力的攻击卡 "plain"；
        /// 输出：家区域放大到全棋盘（便于摆放实验格）、士兵带四邻空格限制的配置。
        /// </summary>
        static ConfigDocument Document(int minEmpty = 2, bool withPlain = false)
        {
            var d = GameSessionTests.Document();
            d.rules[0].homeRegionWidth = 10;
            var soldier = d.cards.Single(c => c.id == "soldier");
            soldier.adjacentClearanceComponent = true;
            soldier.adjacentClearanceMinEmpty = minEmpty;
            if (withPlain)
                d.cards = d.cards.Concat(new[]
                {
                    new CardRow
                    {
                        tier = "basic", id = "plain", name = "普通兵", category = "attack",
                        hp = 4, atk = 1, range = 1, moveDistance = 1, viewKey = "default"
                    }
                }).ToArray();
            return d;
        }

        /// <summary>建立对局。输入：配置与可选牌堆；输出：固定种子的规则内核。</summary>
        static GameSession Start(ConfigDocument d, params DeckEntry[] deck) =>
            // 默认牌堆恰好 16 张（抽牌数 16 张全进手牌）：10 张矿机用于摆放阻挡，6 张士兵用于能力验证。
            new GameSession(new GameConfiguration(d).WithDeck(deck.Length > 0 ? deck : new[] { new DeckEntry("miner", 10), new DeckEntry("soldier", 6) }, false), new SeededRandom(37), 1000);

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

        /// <summary>取手牌中第一张指定卡种的可用部署格。输入：对局与卡种；输出：可部署格列表。</summary>
        static System.Collections.Generic.IReadOnlyList<Cell> DeployCells(GameSession g, string type)
        {
            var card = g.GetSnapshot().Hand.First(c => c.TypeId == type);
            return g.GetLegalActions(new Selection(SelectionKind.Hand, card.Id)).Deploy;
        }

        /// <summary>内部位置正好 2 空合法、只剩 1 空非法；斜角不计。</summary>
        [Test]
        public void DeploymentCountsOnlyOrthogonalEmptyNeighbors()
        {
            var g = Start(Document());
            Assert.That(DeployCells(g, "soldier"), Does.Contain(new Cell(2, 5)), "四邻全空应合法");
            Put(g, "miner", new Cell(3, 5));
            Put(g, "miner", new Cell(1, 5));
            Assert.That(DeployCells(g, "soldier"), Does.Contain(new Cell(2, 5)), "正好 2 空仍合法");
            // 斜角占用不影响统计：四角放满矿机也不改变 (2,5) 的判定。
            Put(g, "miner", new Cell(3, 4));
            Put(g, "miner", new Cell(1, 6));
            Assert.That(DeployCells(g, "soldier"), Does.Contain(new Cell(2, 5)), "斜角不算四邻");
            Put(g, "miner", new Cell(2, 6));
            Assert.That(DeployCells(g, "soldier"), Has.No.Member(new Cell(2, 5)), "只剩 1 空时不能部署");
            var card = g.GetSnapshot().Hand.First(c => c.TypeId == "soldier");
            Assert.That(g.Execute(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(2, 5))).Success, Is.False);
        }

        /// <summary>边缘贡献 1 空、角落贡献 2 空；家算占用、炸弹不占格。</summary>
        [Test]
        public void BoardEdgesCountAsEmptyAndBombsDoNotOccupy()
        {
            var d = Document();
            d.rules[0].bossBombInterval = 1; // 第 1 回合即可布置炸弹
            var g = Start(d);
            // 炸弹只能布置在配置列的换算位置（默认第 5 显示列 → 内部第 4 列）。
            Do(g, new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: new Cell(1, 4)));
            Put(g, "miner", new Cell(0, 3));
            Put(g, "miner", new Cell(0, 5));
            Assert.That(DeployCells(g, "soldier"), Does.Contain(new Cell(0, 4)), "边缘 1 空 + 炸弹格不占用 = 2 空");
            Put(g, "miner", new Cell(0, 1));
            Put(g, "miner", new Cell(1, 0));
            Assert.That(DeployCells(g, "soldier"), Does.Contain(new Cell(0, 0)), "角落自带 2 空");
            // 家算占用：目标 (2,1) 的两侧被矿机占满后，只剩 (2,2) 为空。
            Put(g, "miner", new Cell(3, 1));
            Put(g, "miner", new Cell(1, 1));
            Assert.That(DeployCells(g, "soldier"), Has.No.Member(new Cell(2, 1)), "玩家家占用了 (2,0)");
        }

        /// <summary>星门扩展部署仍需满足空格条件：被占满时不可部署，撤走一个阻挡即恢复。</summary>
        [Test]
        public void StarGateExtensionStillRequiresClearance()
        {
            var d = Document();
            d.rules[0].homeRegionWidth = 2;
            // 本用例需要星门与足够的阻挡单位，显式给出 16 张夹具牌堆（抽牌数 16 张全进手牌）。
            var g = Start(d, new DeckEntry("gate", 4), new DeckEntry("miner", 8), new DeckEntry("soldier", 4));
            Put(g, "gate", new Cell(2, 1));
            var blockerA = Put(g, "miner", new Cell(3, 2));
            Put(g, "miner", new Cell(1, 2));
            Assert.That(DeployCells(g, "soldier"), Has.No.Member(new Cell(2, 2)), "四邻只剩 1 空");
            var card = g.GetSnapshot().Hand.First(c => c.TypeId == "soldier");
            Assert.That(g.Execute(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(2, 2))).Success, Is.False);
            Do(g, new GameCommand(CommandKind.Withdraw, Side.Player, blockerA));
            Assert.That(DeployCells(g, "soldier"), Does.Contain(new Cell(2, 2)), "星门四邻 + 空格条件同时满足才合法");
        }

        /// <summary>敌方星门不提供部署扩展：己方攻击卡不能借敌方星门四邻部署。</summary>
        [Test]
        public void EnemyStarGateCannotGrantDeployment()
        {
            var d = Document();
            d.rules[0].homeRegionWidth = 2;
            d.rules[0].bossCardType = "gate";
            var g = Start(d);
            End(g);
            Do(g, new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(2, 8)));
            Assert.That(g.GetSnapshot().Units.Single(u => u.Owner == Side.Boss).TypeId, Is.EqualTo("gate"));
            Assert.That(DeployCells(g, "soldier"), Has.No.Member(new Cell(2, 7)), "敌方星门不能扩展己方部署");
            var card = g.GetSnapshot().Hand.First(c => c.TypeId == "soldier");
            Assert.That(g.Execute(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(2, 7))).Success, Is.False);
        }

        /// <summary>移动检查起点与落点：落点按“旧格已腾空”的最终布局计算，不把原位置当成仍被占用。</summary>
        [Test]
        public void MoveChecksOriginAndDestinationWithFinalLayout()
        {
            var g = Start(Document());
            var soldier = Put(g, "soldier", new Cell(2, 4));
            Put(g, "miner", new Cell(3, 5));
            Put(g, "miner", new Cell(1, 5));
            var actions = g.GetLegalActions(new Selection(SelectionKind.Unit, soldier));
            Assert.That(actions.Move, Does.Contain(new Cell(2, 5)), "落点的四邻要算上移动者腾出的旧格");
            Do(g, new GameCommand(CommandKind.Move, Side.Player, soldier, new Cell(2, 5)));
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == soldier).Position, Is.EqualTo(new Cell(2, 5)));
        }

        /// <summary>起点或落点任意一侧不满足都不能移动，且失败命令不改变状态。</summary>
        [Test]
        public void MoveFailsWhenOriginOrDestinationIsBlocked()
        {
            var destination = Start(Document());
            var mover = Put(destination, "soldier", new Cell(2, 4));
            Put(destination, "miner", new Cell(3, 5));
            Put(destination, "miner", new Cell(1, 5));
            Put(destination, "miner", new Cell(2, 6));
            Assert.That(destination.GetLegalActions(new Selection(SelectionKind.Unit, mover)).Move, Has.No.Member(new Cell(2, 5)));
            var before = destination.GetSnapshot();
            var failed = destination.Execute(new GameCommand(CommandKind.Move, Side.Player, mover, new Cell(2, 5)));
            Assert.That(failed.Success, Is.False);
            Assert.That(failed.Events, Is.Empty, "失败命令不产生事件");
            var after = destination.GetSnapshot();
            Assert.That(after.Units.Single(u => u.Id == mover).Position, Is.EqualTo(new Cell(2, 4)));
            Assert.That(after.Units.Single(u => u.Id == mover).Acted, Is.False);
            Assert.That(after.Resources, Is.EqualTo(before.Resources));

            var origin = Start(Document());
            var blocked = Put(origin, "soldier", new Cell(2, 4));
            Put(origin, "miner", new Cell(3, 4));
            Put(origin, "miner", new Cell(1, 4));
            Put(origin, "miner", new Cell(2, 3));
            Assert.That(origin.GetLegalActions(new Selection(SelectionKind.Unit, blocked)).Move, Is.Empty, "起点被围时无法移动");
        }

        /// <summary>多格移动只检查起终点：中间格不满足也不影响，占位仍然阻断通行。</summary>
        [Test]
        public void PathMiddleIsNotCheckedButStillBlocksPassage()
        {
            var g = Start(Document(3));
            var soldier = Put(g, "soldier", new Cell(2, 4));
            Put(g, "miner", new Cell(3, 5));
            Put(g, "miner", new Cell(1, 5));
            Assert.That(g.GetLegalActions(new Selection(SelectionKind.Unit, soldier)).Move, Does.Contain(new Cell(2, 7)),
                "中间格 (2,5) 只有 2 空不满足 3 空，但起终点合法");
            Do(g, new GameCommand(CommandKind.Move, Side.Player, soldier, new Cell(2, 7)));
        }

        /// <summary>互换检查双方与前后布局：被围的一侧不能互换，解除阻挡后双方都可换。</summary>
        [Test]
        public void SwapChecksBothSidesAndBothLayouts()
        {
            var d = Document(withPlain: true);
            var g = Start(d, new DeckEntry("miner", 8), new DeckEntry("soldier", 4), new DeckEntry("plain", 4));
            var soldier = Put(g, "soldier", new Cell(2, 4));
            var plain = Put(g, "plain", new Cell(2, 5));
            var blockA = Put(g, "miner", new Cell(3, 5));
            var blockB = Put(g, "miner", new Cell(1, 5));
            Put(g, "miner", new Cell(2, 6));
            Assert.That(g.Execute(new GameCommand(CommandKind.Swap, Side.Player, soldier, targetId: plain)).Success, Is.False,
                "能力单位换到被围的格子上不合法");
            Assert.That(g.GetLegalActions(new Selection(SelectionKind.Unit, soldier)).Swap, Has.No.Member(new Cell(2, 5)));
            Do(g, new GameCommand(CommandKind.Withdraw, Side.Player, blockA));
            Do(g, new GameCommand(CommandKind.Withdraw, Side.Player, blockB));
            Assert.That(g.GetLegalActions(new Selection(SelectionKind.Unit, soldier)).Swap, Does.Contain(new Cell(2, 5)),
                "撤走两个阻挡后交换后的位置满足 2 空");
            Do(g, new GameCommand(CommandKind.Swap, Side.Player, soldier, targetId: plain));
            var units = g.GetSnapshot().Units;
            Assert.That(units.Single(u => u.Id == soldier).Position, Is.EqualTo(new Cell(2, 5)));
            Assert.That(units.Single(u => u.Id == plain).Position, Is.EqualTo(new Cell(2, 4)));
            Assert.That(units.Where(u => u.Id == soldier || u.Id == plain).All(u => u.Acted), Is.True);
        }

        /// <summary>被围后仍可攻击；撤走阻挡后重新可以移动。</summary>
        [Test]
        public void SurroundedUnitCanStillAttack()
        {
            var g = Start(Document());
            var soldier = Put(g, "soldier", new Cell(2, 4));
            var minerA = Put(g, "miner", new Cell(3, 4));
            var minerB = Put(g, "miner", new Cell(1, 4));
            Put(g, "miner", new Cell(2, 3));
            End(g);
            Do(g, new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(2, 5)));
            var boss = g.GetSnapshot().Units.Single(u => u.Owner == Side.Boss);
            End(g);
            var actions = g.GetLegalActions(new Selection(SelectionKind.Unit, soldier));
            Assert.That(actions.Move, Is.Empty, "四邻全被占用时不能移动");
            Assert.That(actions.Swap, Is.Empty);
            Assert.That(actions.Attack, Does.Contain(new Cell(2, 5)), "被围不影响攻击");
            // 撤走两个阻挡（保留另一个和一个 Boss 单位）后只剩 2 空，移动资格恢复。
            Do(g, new GameCommand(CommandKind.Withdraw, Side.Player, minerA));
            Do(g, new GameCommand(CommandKind.Withdraw, Side.Player, minerB));
            Assert.That(g.GetLegalActions(new Selection(SelectionKind.Unit, soldier)).Move, Is.Not.Empty, "撤走阻挡后恢复移动资格");
            var attack = Do(g, new GameCommand(CommandKind.Attack, Side.Player, soldier, targetId: boss.Id));
            Assert.That(attack.Events.Any(e => e.Kind == "attack"), Is.True);
        }

        /// <summary>AI 的实际落点必须合法：最远可达落点被围时退回到合法格。</summary>
        [Test]
        public void AiPicksLegalLandingCell()
        {
            var d = Document(3);
            d.rules[0].playerControlsBoss = false;
            var boss = d.cards.Single(c => c.id == "boss");
            boss.hp = 10;
            boss.atk = 1;
            boss.range = 1;
            boss.moveDistance = 3;
            boss.adjacentClearanceComponent = true;
            boss.adjacentClearanceMinEmpty = 3;
            var g = Start(d);
            Put(g, "miner", new Cell(3, 5));
            Put(g, "miner", new Cell(1, 5));
            End(g);
            var units = g.GetSnapshot().Units.Where(u => u.Owner == Side.Boss).ToArray();
            // (2,5) 是“最远可达但四邻不足”的落点：AI 必须退回到合法格 (2,6) 而不是停在这里。
            Assert.That(units.Any(u => u.Position == new Cell(2, 6)), Is.True,
                "AI 应从最远落点 (2,5) 退回满足 3 空的 (2,6)：" + string.Join(",", units.Select(u => u.Position.ToString())));
            Assert.That(units.Any(u => u.Position == new Cell(2, 5)), Is.False);
            // 注意：其它单位随后可以合法靠近，把先行动的单位重新围到不足阈值（玩法明确允许），
            // 因此不能断言“最终快照里所有单位都满足阈值”；退回落点本身由上面的断言锁定。
        }
    }
}
