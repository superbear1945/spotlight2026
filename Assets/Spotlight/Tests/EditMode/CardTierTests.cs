// 卡牌阶级回归：分类独立、配置严格校验、资产/JSON 往返和战斗行为不变。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Spotlight.EditorTools;
using UnityEngine;

namespace Spotlight.Tests
{
    /// <summary>通过公开配置与命令验证阶级只影响组卡信息。输入：隔离夹具或只读工作簿；输出：NUnit 断言。</summary>
    public class CardTierTests
    {
        /// <summary>新增中文在项目字体中有字形，两套字体资产已生成。输入：正式字体；输出：缺字及字体引用断言。</summary>
        [Test]
        public void TierTextIsSupportedByProjectFont()
        {
            var font = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(CjkFontSetup.FontPath);
            Assert.That(font, Is.Not.Null);
            foreach (var character in "卡牌阶级初级中级高级不适用请补齐待修正专用".Distinct())
                Assert.That(font.HasCharacter(character), Is.True, "字体缺少：" + character);
            var panel = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>(CjkFontSetup.PanelSettingsPath);
            Assert.That(CjkFontSetup.ValidatePanelFonts(panel), Is.Empty);
            Assert.That(UnityEditor.AssetDatabase.LoadMainAssetAtPath(CjkFontSetup.FontAssetPath), Is.Not.Null);
        }

        /// <summary>三种阶级可与三种类型任意组合，不根据名称或攻击数值推断。输入：类型、阶级；输出：只读属性断言。</summary>
        [Test]
        public void EveryPlayerTierIsIndependentOfCategory()
        {
            foreach (var category in new[] { "resource", "attack", "special" })
            foreach (var tier in new[] { "basic", "intermediate", "advanced" })
            {
                var document = GameSessionTests.Document();
                var row = document.cards.Single(c => c.id == "soldier");
                row.category = category;
                row.tier = tier;
                row.name = "重命名后仍保留阶级";
                var config = new GameConfiguration(document);
                Assert.That(config.Cards[row.id].Tier, Is.EqualTo(CardTiers.Parse(tier)));
                Assert.That(config.Cards[row.id].Category.ToString().ToLowerInvariant(), Is.EqualTo(category));
                row.tier = "basic";
                Assert.That(config.Cards[row.id].Tier, Is.EqualTo(CardTiers.Parse(tier)), "运行配置与可变草稿脱钩");
            }
        }

        /// <summary>缺失及未知阶级都必须明确报错，禁止旧值或数字混入。输入：非法文本；输出：校验与直接组装均拒绝。</summary>
        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("late")]
        [TestCase("高级")]
        [TestCase("1")]
        [TestCase("Basic")]
        public void InvalidTierIsRejectedByBothConfigurationPaths(string tier)
        {
            var document = GameSessionTests.Document();
            var baseline = new GameConfiguration(document);
            document.cards[0].tier = tier;
            Assert.That(ConfigValidation.Validate(document).Any(e => e.Contains("tier")), Is.True);
            // requireDrawPileSize: false：基准牌堆不凑满 drawPileSize，失败原因必须仍只是阶级非法。
            var error = Assert.Throws<ArgumentException>(() => new GameConfiguration(baseline.Rules,
                document.cards.Select(CardArchetype.FromRow), baseline.Deck, baseline.BossSlots,
                baseline.PlayerHomeHp, baseline.BossHomeHp, baseline.PlayerHomeProduce, baseline.Presentation,
                baseline.DeckConfig, requireDrawPileSize: false));
            Assert.That(error.Message, Does.Contain("tier"));
        }

        /// <summary>
        /// 不适用卡可注册且数量可为零，但不能携带；文档不再包含 Deck 表，因此该规则只在组合校验路径上把关。
        /// 输入：无；输出：零张合法、正数拒绝的断言。
        /// </summary>
        [Test]
        public void NotApplicableCardCannotEnterPlayerDeck()
        {
            var baseline = new GameConfiguration(GameSessionTests.Document());
            // 数量 0 表示“只注册不携带”，属于合法组合。
            var zero = baseline.WithDeck(baseline.Deck.Concat(new[] { new DeckEntry("boss", 0) }), false);
            Assert.That(zero.Deck.Any(e => e.TypeId == "boss"), Is.True);
            // 数量大于 0 时，tier=none 的卡种不得进入抽牌堆。
            var error = Assert.Throws<ArgumentException>(() =>
                baseline.WithDeck(baseline.Deck.Concat(new[] { new DeckEntry("boss", 1) }), false));
            Assert.That(error.Message, Does.Contain("tier=none"));
        }

        /// <summary>JSON 保留阶级，旧 JSON 可导入修复但不可应用。输入：完整和缺字段文档；输出：往返与错误断言。</summary>
        [Test]
        public void JsonRoundTripPreservesTierAndMissingTierRequiresRepair()
        {
            var draft = new DebugConfigDraft(null);
            var json = JsonUtility.ToJson(GameSessionTests.Document());
            Assert.That(draft.Import(json, out var error, out _), Is.True, error);
            Assert.That(draft.Import(draft.ExportJson(), out error, out _), Is.True, error);
            Assert.That(draft.FindCard("gate").tier, Is.EqualTo("advanced"));
            Assert.That(draft.TryCreateConfiguration(out _, out error), Is.True, error);
            var legacy = Regex.Replace(json, "\"tier\":\"[^\"]*\",", string.Empty);
            Assert.That(legacy, Does.Not.Contain("\"tier\""));
            Assert.That(draft.Import(legacy, out error, out _), Is.True, error);
            Assert.That(draft.TryCreateConfiguration(out _, out error), Is.False);
            Assert.That(error, Does.Contain("tier"));
        }

        /// <summary>
        /// 抽牌堆摘要改由“张数 + 阶级比例”推导；强制牌堆按张数统计，零张不计入。
        /// 输入：调整后的规则与强制牌堆；输出：张数按阶级归属的断言。
        /// </summary>
        [Test]
        public void DrawPileSummaryCountsCopiesByTier()
        {
            var document = GameSessionTests.Document();
            document.rules[0].drawPileSize = 20;
            document.rules[0].tierRatioBasic = 50;
            document.rules[0].tierRatioIntermediate = 30;
            document.rules[0].tierRatioAdvanced = 20;
            var draft = new DebugConfigDraft(null);
            Assert.That(draft.Import(JsonUtility.ToJson(document), out var error, out _), Is.True, error);
            var summary = draft.GetDrawPileSummary();
            Assert.That(summary.Total, Is.EqualTo(20));
            Assert.That(summary.Basic, Is.EqualTo(10));
            Assert.That(summary.Intermediate, Is.EqualTo(6));
            Assert.That(summary.Advanced, Is.EqualTo(4));
            // 强制牌堆整体覆盖抽牌堆：同种卡带多张逐张累计，数量 0 不贡献统计。
            draft.SetForcedDeckCount("miner", 3);
            draft.SetForcedDeckCount("gate", 2);
            draft.SetForcedDeckCount("upgrade", 0);
            var forced = draft.GetDrawPileSummary();
            Assert.That(forced.Total, Is.EqualTo(5));
            Assert.That(forced.Basic, Is.EqualTo(3));
            Assert.That(forced.Intermediate, Is.Zero);
            Assert.That(forced.Advanced, Is.EqualTo(2));
            var added = draft.AddCardType("special");
            Assert.That(added.tier, Is.EqualTo("basic"));
            Assert.That(draft.GetForcedDeckCount(added.id), Is.Zero);
        }

        /// <summary>编译写入、资产反向导出和只读卡种都保留阶级。输入：内存 SO；输出：各阶级往返断言，无正式资产写入。</summary>
        [Test]
        public void DefinitionAssetRoundTripsEveryTier()
        {
            var definition = ScriptableObject.CreateInstance<CardDefinitionSO>();
            try
            {
                var row = GameSessionTests.Document().cards[0];
                foreach (var tier in new[] { "basic", "intermediate", "advanced", "none" })
                {
                    row.tier = tier;
                    definition.EditorApply(row, null, null, null, null, null, "test.xlsx", "Cards");
                    Assert.That(definition.TierText, Is.EqualTo(tier));
                    Assert.That(definition.ToCardRow().tier, Is.EqualTo(tier));
                    Assert.That(definition.ToArchetype().Tier, Is.EqualTo(CardTiers.Parse(tier)));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(definition); }
        }

        /// <summary>阶级必填列缺失时给出明确位置，空单元格也不可通过映射。输入：内存表格；输出：编译错误断言。</summary>
        [Test]
        public void WorkbookRequiresTierColumnAndCell()
        {
            var schema = TableSchema.For("Cards");
            var headers = schema.Columns.Where(c => c.Name != "tier").Select(c => c.Name).ToArray();
            var cells = new string[1, headers.Length];
            for (var i = 0; i < headers.Length; i++) cells[0, i] = headers[i];
            var issues = new List<ConfigIssue>();
            Assert.That(TableMapper.TryBuildHeader("legacy.xlsx", schema, cells, issues, out _), Is.False);
            Assert.That(issues.Any(i => i.Reason.Contains("缺失必需列：tier")), Is.True);
            issues.Clear();
            TableMapper.MapRows<CardRow>("empty.xlsx", schema, new[,] { { "tier", "id" }, { "", "test" } },
                new Dictionary<string, int> { ["tier"] = 0, ["id"] = 1 }, issues);
            Assert.That(issues.Any(i => i.Row == 2 && i.Column == 1 && i.Reason.Contains("tier")), Is.True);
        }

        /// <summary>正式表和默认生成器都只增加已确认的阶级，不引入卡组包；初级六种、中级八种、高级星门、Boss 两种不适用。</summary>
        [Test]
        public void AuthoredAndDefaultWorkbooksHaveConfirmedTiers()
        {
            var path = Path.Combine(Path.GetTempPath(), "spotlight-tier-" + Guid.NewGuid().ToString("N") + ".xlsx");
            try
            {
                DefaultWorkbookWriter.WriteDefault(path);
                foreach (var workbook in new[] { ConfigCompiler.DefaultWorkbookPath, path })
                {
                    var report = ConfigCompiler.Compile(workbook, false);
                    Assert.That(report.Success, Is.True, string.Join("\n", report.Issues));
                    var cards = report.Document.cards;
                    Assert.That(cards.Where(c => c.tier == "basic").Select(c => c.id),
                        Is.EquivalentTo(new[] { "ge1", "ge12", "blade1", "rabbit2", "odysseus1", "kyoto_01" }));
                    Assert.That(cards.Where(c => c.tier == "intermediate").Select(c => c.id),
                        Is.EquivalentTo(new[] { "kepler1", "kepler2", "landing1", "houyi1", "collector1", "force1", "flounder1", "reserve1" }));
                    Assert.That(cards.Single(c => c.id == "gate").tier, Is.EqualTo("advanced"));
                    Assert.That(cards.Where(c => c.tier == "none").Select(c => c.id), Is.EquivalentTo(new[] { "bossCombat", "bossSpawner" }));
                    Assert.That(cards.Length, Is.EqualTo(17));
                }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        /// <summary>高级卡第一回合照常部署并保持原有属性与合法动作；卡种、单位详情均显示阶级，颜色仍按分类。</summary>
        [TestCase("basic", "初级")]
        [TestCase("intermediate", "中级")]
        [TestCase("advanced", "高级")]
        public void TierChangesDetailsButNotDeploymentOrCombat(string tier, string label)
        {
            var document = GameSessionTests.Document();
            var row = document.cards.Single(c => c.id == "soldier");
            row.tier = tier;
            // 显式覆盖抽牌堆：保持与原 Deck 表夹具（4/4/4/4）完全一致的抽牌顺序，避免抽不到 soldier。
            var config = new GameConfiguration(document).WithDeck(GameSessionTests.DefaultDeck(), false);
            var session = new GameSession(config, new SeededRandom(42), 100);
            var card = session.GetSnapshot().Hand.First(c => c.TypeId == row.id);
            var targets = session.GetLegalActions(new Selection(SelectionKind.Hand, card.Id)).Deploy;
            Assert.That(targets, Does.Contain(new Cell(0, 0)));
            var result = session.Execute(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(0, 0)));
            Assert.That(result.Success, Is.True, result.Error);
            var unit = session.GetSnapshot().Units.Single();
            Assert.That(unit.Attack, Is.EqualTo(row.atk));
            Assert.That(unit.Hp, Is.EqualTo(row.hp));
            Assert.That(unit.Range, Is.EqualTo(row.range));
            Assert.That(unit.MoveDistance, Is.EqualTo(row.moveDistance));
            Assert.That(session.GetLegalActions(new Selection(SelectionKind.Unit, unit.Id)).Move, Is.Not.Empty);
            Assert.That(CardText.DetailLine(config.Cards[row.id], Side.Player), Does.Contain(label + " · 攻击"));
            Assert.That(CardText.UnitDetail(config.Cards[row.id], unit), Does.Contain(label + " · 攻击"));
        }
    }
}
