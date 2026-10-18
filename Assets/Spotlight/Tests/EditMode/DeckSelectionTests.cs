// 携带选择与抽牌堆构成的 EditMode 测试。
// 为什么单独成文件：Deck 表废除后，“抽牌堆怎么凑出来”变成一组纯计算规则（张数 × 比例 → 每阶级张数 → 每卡种张数），
// 它既是配置校验的输入，也是选卡界面预览与正式开局的唯一来源，必须在没有场景与资源的情况下逐条固定下来。
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Spotlight.Tests
{
    /// <summary>
    /// 覆盖分配算法、默认选卡、携带上限、配置校验与“选卡 → 配置 → 抽牌堆”的完整链路。
    /// 夹具里的数值属于独立测试数据，不代表正式默认值。
    /// </summary>
    public class DeckSelectionTests
    {
        /// <summary>
        /// 造一行卡种。输入：ID、分类与阶级，以及可选的战斗/资源数值；输出：可直接进入 Cards 表的行。
        /// 默认值刻意保持“合法且平庸”，让每个用例只显式写与断言相关的字段。
        /// </summary>
        static CardRow Card(string id, string category, string tier, int hp = 1, int atk = 0, int range = 0,
            int move = 0, int produce = 0, int upkeep = 0, bool resource = true)
        {
            return new CardRow
            {
                id = id,
                name = "卡" + id,
                category = category,
                tier = tier,
                viewKey = "default",
                hp = hp,
                atk = atk,
                range = range,
                moveDistance = move,
                produce = produce,
                upkeep = upkeep,
                resourceComponent = resource
            };
        }

        /// <summary>
        /// 构造一张完整可校验的配置文档。
        /// 输入：抽牌堆规则与卡种集合（默认 6 初级 / 8 中级 / 1 高级 + Boss 用的两张 none 卡）；
        /// 输出：通过 ConfigValidation 的文档。抽牌堆内容不再由文档提供，改由规则推导。
        /// </summary>
        static ConfigDocument Document(int size = 24, int ratioBasic = 70, int ratioIntermediate = 20, int ratioAdvanced = 10,
            int maxBasic = 5, int maxIntermediate = 3, int maxAdvanced = 2, IEnumerable<CardRow> cards = null)
        {
            var list = (cards ?? Pool()).ToList();
            list.Add(Card("bossCombat", "attack", "none", 3, 1, 1, 1, 0, 0, false));
            var spawner = Card("bossSpawner", "special", "none", 1, 0, 0, 0, 0, 0, false);
            spawner.spawnComponent = true; spawner.spawnCardType = "bossCombat"; spawner.canSwap = false;
            list.Add(spawner);
            var ship = Card("warship", "attack", "none", 15, 2, 1, 1, 0, 0, false);
            ship.spawnComponent = true; ship.spawnCardType = "bossCombat"; ship.canAttackResources = false;
            list.Add(ship);
            return new ConfigDocument
            {
                schemaVersion = 2,
                cards = list.ToArray(),
                rules = new[]
                {
                    new RulesRow
                    {
                        rows = 5, columns = 10, homeRegionWidth = 2, playerPlays = 5, bossPlays = 1,
                        drawCount = 5, handLimit = 5, initialResources = 0, bossRespawnDelay = 5,
                        playerSide = "left", firstSide = "player", bossCardType = "bossCombat",
                        playerControlsBoss = false, accumulateResources = true, summoningSickness = true,
                        adjacentSwap = true, lineBlocking = false, bossBombInterval = 10, bossBombDamage = 3,
                        bossBombColumn = 5, bossSpawnerAttackThreshold = 12, bossSpawnerCountFromColumn = 4,
                        bossSpawnerUnitThreshold = 2, bossSpawnerDeployFromColumn = 5,
                        bossSpawnerCardType = "bossSpawner", bossPhaseTwoHp = 1, bossWarshipColumn = 1, bossWarshipCardType = "warship", drawPileSize = size, tierRatioBasic = ratioBasic, tierRatioIntermediate = ratioIntermediate,
                        tierRatioAdvanced = ratioAdvanced, maxTypesBasic = maxBasic,
                        maxTypesIntermediate = maxIntermediate, maxTypesAdvanced = maxAdvanced
                    }
                },
                homes = new[]
                {
                    new HomeRow { owner = "player", hp = 10, produce = 2 },
                    new HomeRow { owner = "boss", hp = 10, produce = 0 }
                },
                bossSlots = new[]
                {
                    new BossSlotRow { id = "a", rowFraction = 0, edgeOffset = 1 },
                    new BossSlotRow { id = "b", rowFraction = 0.5, edgeOffset = 1 },
                    new BossSlotRow { id = "c", rowFraction = 1, edgeOffset = 1 }
                },
                presentation = new[]
                {
                    new PresentationRow
                    {
                        referenceWidth = 1920, referenceHeight = 1080, fontSize = 18, logLimit = 100,
                        moveSeconds = 0.2f, feedbackSeconds = 0.1f, selectedScale = 1.05f,
                        playerColor = "#3366FF", bossColor = "#FF3333", resourceColor = "#33AA66",
                        attackColor = "#EE8833", specialColor = "#AA55FF", moveColor = "#33AAFF",
                        attackTargetColor = "#FF3333", swapColor = "#AA33FF", deployColor = "#33FF99"
                    }
                }
            };
        }

        /// <summary>
        /// 默认卡种池：6 初级（可携带上限 5）、8 中级（上限 3）、1 高级（上限 2，但池里只有 1 种）。
        /// 输入：无；输出：卡种行序列，顺序即 Cards 表顺序（余数发放顺序依赖它）。
        /// </summary>
        static IEnumerable<CardRow> Pool()
        {
            for (var i = 0; i < 6; i++) yield return Card("b" + i, "resource", "basic");
            for (var i = 0; i < 8; i++) yield return Card("m" + i, "attack", "intermediate", 2, 1, 1, 1, 0, 1);
            yield return Card("a0", "special", "advanced", 2, 0, 0, 1, 0, 4);
        }

        /// <summary>把三阶级张数断言写短一点。输入：规则与期望值；输出：无（失败时指出实际数组）。</summary>
        static void AssertAllocation(DeckConfig config, int basic, int intermediate, int advanced)
        {
            var counts = DeckComposition.AllocateTierCounts(config);
            Assert.That(counts, Is.EqualTo(new[] { basic, intermediate, advanced }),
                $"分配结果应为 {basic}/{intermediate}/{advanced}，实际为 {counts[0]}/{counts[1]}/{counts[2]}");
            Assert.That(counts.Sum(), Is.EqualTo(config.DrawPileSize), "三阶级张数之和必须等于抽牌堆张数");
        }

        [Test]
        public void AllocateFloorThenShortfallGoesToBasic()
        {
            // 24 × 70/20/10 = 16.8 / 4.8 / 2.4 → 向下取整 16/4/2，合计 22，缺额 2 全给初级。
            AssertAllocation(new DeckConfig(24, 70, 20, 10, 5, 3, 2), 18, 4, 2);
            // 30 × 70/20/10 = 21/6/3，正好整除，没有缺额。
            AssertAllocation(new DeckConfig(30, 70, 20, 10, 5, 3, 2), 21, 6, 3);
            // 24 × 33/33/34 = 7/7/8，缺额 2 全给初级（不是按比例追加）。
            AssertAllocation(new DeckConfig(24, 33, 33, 34, 5, 3, 2), 9, 7, 8);
        }

        [Test]
        public void AllocateGuaranteesAtLeastOnePerTier()
        {
            // 张数下限 3：70/20/10 向下取整为 2/0/0，抬升到 2/1/1 后溢出 1，从初级扣回 → 1/1/1。
            AssertAllocation(new DeckConfig(3, 70, 20, 10, 5, 3, 2), 1, 1, 1);
            // 极端的 1/1/98：初级 floor(0.03)=0 抬升到 1，合计 4 溢出 1 → 从张数最多的初级扣 → 1/1/1。
            AssertAllocation(new DeckConfig(3, 1, 1, 98, 5, 3, 2), 1, 1, 1);
            AssertAllocation(new DeckConfig(4, 1, 1, 98, 5, 3, 2), 1, 1, 2);
        }

        [Test]
        public void DefaultSelectionFillsEveryTierToEffectiveCap()
        {
            var configuration = new GameConfiguration(Document());
            var state = new DeckSelectionState(configuration);
            Assert.That(state.SelectedCount(CardTier.Basic), Is.EqualTo(5), "默认应带满 5 种初级");
            Assert.That(state.SelectedCount(CardTier.Intermediate), Is.EqualTo(3), "默认应带满 3 种中级");
            Assert.That(state.SelectedCount(CardTier.Advanced), Is.EqualTo(1), "高级池只有 1 种，默认应只带这 1 种");
            Assert.That(state.ValidationMessage, Is.Null, "默认选卡必须合法");
        }

        [Test]
        public void DefaultDeckSplitsTierCardsEvenlyWithRemainderToEarlierTypes()
        {
            var configuration = new GameConfiguration(Document());
            var deck = configuration.Deck.ToDictionary(e => e.TypeId, e => e.Count);
            // 初级 18 张分给 5 种：4/4/4/3/3（余数 3 张发给靠前的 3 种）。
            Assert.That(new[] { "b0", "b1", "b2", "b3", "b4" }.Select(id => deck[id]), Is.EqualTo(new[] { 4, 4, 4, 3, 3 }),
                "初级应按 Cards 顺序均分，余数发给靠前的卡种");
            // 中级 4 张分给 3 种：2/1/1。
            Assert.That(new[] { "m0", "m1", "m2" }.Select(id => deck[id]), Is.EqualTo(new[] { 2, 1, 1 }));
            // 高级只有 1 种，拿走全部 2 张。
            Assert.That(deck["a0"], Is.EqualTo(2));
            Assert.That(deck.Values.Sum(), Is.EqualTo(24), "抽牌堆总张数必须等于 drawPileSize");
        }

        [Test]
        public void EverySelectedTypeAppearsAtLeastOnce()
        {
            // 默认选卡已经把每阶级都选到有效上限（5/3/1），因此直接断言“每个被选卡种都至少拿到 1 张”。
            var configuration = new GameConfiguration(Document());
            var state = new DeckSelectionState(configuration);
            Assert.That(state.ValidationMessage, Is.Null, state.ValidationMessage);
            state.TryBuildDeck(out var deck, out var error);
            Assert.That(error, Is.Null, error);
            var selected = state.SelectedTypeIds.ToArray();
            Assert.That(selected.Length, Is.EqualTo(9), "默认应选中 5 + 3 + 1 个卡种");
            foreach (var id in selected)
            {
                var entry = deck.SingleOrDefault(e => e.TypeId == id);
                Assert.That(entry, Is.Not.Null, $"被选中的卡种 {id} 没有进入抽牌堆");
                Assert.That(entry.Count, Is.GreaterThanOrEqualTo(1), $"卡种 {id} 在抽牌堆里少于 1 张");
            }
            Assert.That(deck.Sum(e => e.Count), Is.EqualTo(24));
        }

        [Test]
        public void SingleTypeInTierTakesWholeShare()
        {
            var configuration = new GameConfiguration(Document());
            var state = new DeckSelectionState(configuration);
            // 只留一种初级，其他初级全部取消：初级 18 张应全部给这一种。
            foreach (var id in new[] { "b0", "b1", "b2", "b4" }) state.Toggle(id);
            Assert.That(state.SelectedCount(CardTier.Basic), Is.EqualTo(1));
            Assert.That(state.ValidationMessage, Is.Null, state.ValidationMessage);
            state.TryBuildDeck(out var deck, out var error);
            Assert.That(error, Is.Null, error);
            Assert.That(deck.Single(e => e.TypeId == "b3").Count, Is.EqualTo(18), "唯一的初级卡种应拿走 18 张");
        }

        [Test]
        public void MissingTierSelectionIsRejected()
        {
            var configuration = new GameConfiguration(Document());
            var state = new DeckSelectionState(configuration);
            state.Toggle("a0");
            Assert.That(state.IsSelected("a0"), Is.False, "取消后不应仍处于选中状态");
            state.Toggle("a0");
            Assert.That(state.ValidationMessage, Is.Null);
            // 直接构造一个缺高级的选卡（跳过界面）：必须给出可读原因。
            Assert.That(DeckComposition.TryBuild(configuration.DeckConfig, configuration.CardOrder,
                new[] { "b0", "m0" }, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("高级"), error);
        }

        [Test]
        public void CapacityIsCappedByPoolSizeAndConfiguredCap()
        {
            var configuration = new GameConfiguration(Document());
            var state = new DeckSelectionState(configuration);
            Assert.That(state.Capacity(CardTier.Basic), Is.EqualTo(5), "初级配置上限 5，池里 6 种，有效上限应为 5");
            Assert.That(state.Capacity(CardTier.Intermediate), Is.EqualTo(3), "中级配置上限 3");
            Assert.That(state.Capacity(CardTier.Advanced), Is.EqualTo(1), "高级配置上限 2，但池里只有 1 种，有效上限应为 1");
            Assert.That(state.CanSelect("b5"), Is.False, "初级已满 5 种，第 6 种应勾不上");
            Assert.That(state.Toggle("b5"), Is.False, "超上限时 Toggle 应返回 false 且不改变状态");
            Assert.That(state.SelectedCount(CardTier.Basic), Is.EqualTo(5));
        }

        [Test]
        public void NonPlayerTierCardCannotBeCarried()
        {
            var configuration = new GameConfiguration(Document());
            var state = new DeckSelectionState(configuration);
            Assert.That(configuration.FindCard("bossCombat").Tier, Is.EqualTo(CardTier.None));
            Assert.That(state.CanSelect("bossCombat"), Is.False, "none（Boss 专用）卡种不能携带");
            Assert.That(DeckComposition.TryBuild(configuration.DeckConfig, configuration.CardOrder,
                new[] { "b0", "m0", "a0", "bossCombat" }, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("不能携带"), error);
        }

        [Test]
        public void PreviewMatchesBuiltPile()
        {
            var configuration = new GameConfiguration(Document());
            var state = new DeckSelectionState(configuration);
            var preview = state.PreviewTierCounts();
            var copies = state.PreviewTypeCounts();
            state.TryBuildDeck(out var deck, out var error);
            Assert.That(error, Is.Null, error);
            foreach (var tier in DeckComposition.PlayerTiers)
            {
                var actual = deck.Where(e => configuration.FindCard(e.TypeId).Tier == tier).Sum(e => e.Count);
                Assert.That(preview[tier], Is.EqualTo(actual), $"{DeckComposition.TierLabel(tier)}预览张数应与实际抽牌堆一致");
            }
            foreach (var entry in deck)
                Assert.That(copies[entry.TypeId], Is.EqualTo(entry.Count), "每卡种预览张数应与实际一致");
        }

        [Test]
        public void SelectionRoundTripsIntoSessionDrawPile()
        {
            var configuration = new GameConfiguration(Document());
            var state = new DeckSelectionState(configuration);
            Assert.That(state.TryBuildConfiguration(out var built, out var error), Is.True, error);
            Assert.That(built.Deck.Sum(e => e.Count), Is.EqualTo(24), "选卡生成的抽牌堆张数应等于 drawPileSize");
            var session = new GameSession(built, new SeededRandom(7), 50);
            var snapshot = session.GetSnapshot();
            // 开局已抽 drawCount 张，因此抽牌堆剩余 = 24 - 抽到手的张数。
            Assert.That(snapshot.DrawPile.Count + snapshot.Hand.Count, Is.EqualTo(24), "开局后抽牌堆与手牌之和应等于抽牌堆张数");
        }

        [Test]
        public void BadDeckRulesAreRejected()
        {
            // 比例之和不是 100
            var sum = ConfigValidation.Validate(Document(ratioBasic: 60));
            Assert.That(sum.Any(e => e.Contains("之和必须为 100")), Is.True, string.Join(" | ", sum));
            // 比例必须至少 1
            var zero = ConfigValidation.Validate(Document(ratioBasic: 90, ratioIntermediate: 10, ratioAdvanced: 0));
            Assert.That(zero.Any(e => e.Contains("至少为 1")), Is.True, string.Join(" | ", zero));
            // 张数至少 3
            var small = ConfigValidation.Validate(Document(size: 2));
            Assert.That(small.Any(e => e.Contains("至少为 3")), Is.True, string.Join(" | ", small));
            // 携带上限至少 1
            var zeroCap = ConfigValidation.Validate(Document(maxAdvanced: 0));
            Assert.That(zeroCap.Any(e => e.Contains("携带上限")), Is.True, string.Join(" | ", zeroCap));
            // 某阶级没有任何卡种
            var noAdvanced = ConfigValidation.Validate(Document(cards: Pool().Where(c => c.tier != "advanced")));
            Assert.That(noAdvanced.Any(e => e.Contains("没有任何可携带卡种")), Is.True, string.Join(" | ", noAdvanced));
            // 分配张数少于有效携带上限（5 张 @70/20/10 时初级只分到 3 张，上限却是 5）
            var tooFew = ConfigValidation.Validate(Document(size: 5, maxBasic: 5, maxIntermediate: 3, maxAdvanced: 2));
            Assert.That(tooFew.Any(e => e.Contains("少于携带上限")), Is.True, string.Join(" | ", tooFew));
            // 旧 schemaVersion 明确被拒绝
            var old = Document();
            old.schemaVersion = 1;
            Assert.That(ConfigValidation.Validate(old).Any(e => e.Contains("schemaVersion")), Is.True);
        }

        [Test]
        public void WithDeckChecksDrawPileSizeOnlyWhenAsked()
        {
            var configuration = new GameConfiguration(Document());
            var shortDeck = new[] { new DeckEntry("b0", 3), new DeckEntry("m0", 1), new DeckEntry("a0", 1) };
            // 调试强制牌堆允许任意张数。
            var forced = configuration.WithDeck(shortDeck, false);
            Assert.That(forced.Deck.Sum(e => e.Count), Is.EqualTo(5));
            // 正式路径必须与 drawPileSize 一致。
            Assert.Throws<ArgumentException>(() => configuration.WithDeck(shortDeck));
        }

        /// <summary>
        /// 正式工作簿必须提供六种高级卡（总 22 行），且高级上限 2 时的默认选卡与预览/正式抽牌堆一致。
        /// 输入：正式 xlsx；输出：卡池、默认选卡、预览张数与 WithDeck 结果的断言。
        /// 为什么需要：新增五张高级卡会改变默认携带结果（星门 + 后羿-II），必须锁定这份正式契约。
        /// </summary>
        [Test]
        public void FormalWorkbookExposesSixAdvancedTypesAndTwoCardPreview()
        {
            var report = Spotlight.EditorTools.ConfigCompiler.Compile(Spotlight.EditorTools.ConfigCompiler.DefaultWorkbookPath, false);
            Assert.That(report.Success, Is.True, string.Join("\n", report.Issues));
            var configuration = new GameConfiguration(report.Document);
            var advanced = configuration.CardOrder.Where(c => c.Tier == CardTier.Advanced).Select(c => c.Id).ToArray();
            Assert.That(advanced, Is.EqualTo(new[] { "gate", "houyi2", "texasRanger", "viking2056", "snpEu", "napoleon1769" }));
            Assert.That(configuration.CardOrder.Count, Is.EqualTo(23), "正式卡种总数应为 23（含阶段战舰）");
            Assert.That(configuration.DeckConfig.EffectiveMaxTypes(CardTier.Advanced, advanced.Length), Is.EqualTo(2),
                "正式高级上限仍为 2，卡池扩大后有效上限从 1 变为 2");

            // 默认选卡按 Cards 表顺序取满：高级为星门 + 后羿-II，2 张平分各 1 张。
            var state = new DeckSelectionState(configuration);
            Assert.That(state.SelectedTypeIds.Where(id => configuration.FindCard(id).Tier == CardTier.Advanced),
                Is.EqualTo(new[] { "gate", "houyi2" }));
            var preview = state.PreviewTypeCounts();
            Assert.That(preview["gate"], Is.EqualTo(1));
            Assert.That(preview["houyi2"], Is.EqualTo(1));
            Assert.That(state.TryBuildDeck(out var deck, out var error), Is.True, error);
            Assert.That(deck.Where(e => configuration.FindCard(e.TypeId).Tier == CardTier.Advanced).Select(e => e.Count),
                Is.EqualTo(new[] { 1, 1 }), "预览与正式抽牌堆必须一致");

            // 只带一种高级卡时，该卡种拿走全部 2 张。
            var single = new DeckSelectionState(configuration);
            Assert.That(single.Toggle("houyi2"), Is.True, "高级只选一种也必须合法");
            Assert.That(single.PreviewTypeCounts()["gate"], Is.EqualTo(2));
            Assert.That(single.TryBuildDeck(out var singleDeck, out var singleError), Is.True, singleError);
            Assert.That(singleDeck.Single(e => configuration.FindCard(e.TypeId).Tier == CardTier.Advanced).Count, Is.EqualTo(2));
        }
    }
}
