// 高级卡新增能力的配置链路测试。
// 输入：内存文档与内存 SO；输出：字段校验、文档/SO 往返与 JSON 兼容性的断言。
// 为什么需要：三种能力同时存在“Excel 文档路径”和“只读 SO 路径”，必须证明两条路径给出同一份可用配置，
// 而且旧 JSON 缺字段时不会意外启用能力。
using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Spotlight.Tests
{
    /// <summary>
    /// 覆盖四邻空格限制、击杀恢复行动与伤害标记的配置契约：
    /// 合法值往返一致、非法值一次性报错、直接组装路径同样拦截、旧 JSON 缺字段按关闭处理。
    /// </summary>
    public class AdvancedCardConfigTests
    {
        /// <summary>取得夹具中的攻击卡行。输入：启用哪几种能力；输出：已写入能力的 CardRow。</summary>
        static CardRow Row(bool clearance = true, bool killAction = true, bool damageMark = true)
        {
            var row = GameSessionTests.Document().cards.Single(c => c.id == "soldier");
            row.adjacentClearanceComponent = clearance;
            row.adjacentClearanceMinEmpty = clearance ? 2 : 0;
            row.killActionComponent = killAction;
            row.damageMarkComponent = damageMark;
            row.damageMarkDamagePerStack = damageMark ? 1 : 0;
            return row;
        }

        /// <summary>把某个卡行换进文档。输入：待替换的行；输出：可校验的完整文档。</summary>
        static ConfigDocument Document(CardRow card)
        {
            var d = GameSessionTests.Document();
            var index = Array.FindIndex(d.cards, c => c.id == card.id);
            d.cards[index] = card;
            return d;
        }

        /// <summary>收集某次改动的全部校验错误。输入：改动委托；输出：以换行连接的错误文本。</summary>
        static string Errors(Action<CardRow> mutate)
        {
            var row = Row();
            mutate(row);
            return string.Join("\n", ConfigValidation.Validate(Document(row)));
        }

        /// <summary>三种能力的合法值在文档路径上全部生效，且未启用的能力为 null。</summary>
        [Test]
        public void DocumentPathBuildsAllThreeAbilities()
        {
            var document = Document(Row());
            Assert.That(ConfigValidation.Validate(document), Is.Empty);
            var configuration = new GameConfiguration(document);
            var soldier = configuration.Cards["soldier"];
            Assert.That(soldier.AdjacentClearance, Is.Not.Null);
            Assert.That(soldier.AdjacentClearance.MinEmptyNeighbors, Is.EqualTo(2));
            Assert.That(soldier.KillAction, Is.Not.Null);
            Assert.That(soldier.DamageMark, Is.Not.Null);
            Assert.That(soldier.DamageMark.DamagePerStack, Is.EqualTo(1));

            var plain = new GameConfiguration(Document(Row(false, false, false)));
            var basic = plain.Cards["soldier"];
            Assert.That(basic.AdjacentClearance, Is.Null);
            Assert.That(basic.KillAction, Is.Null);
            Assert.That(basic.DamageMark, Is.Null);
        }

        /// <summary>SO → CardRow → CardArchetype 与纯文档路径完全一致。</summary>
        [Test]
        public void DefinitionAssetRoundTripsNewAbilities()
        {
            var row = Row();
            var definition = ScriptableObject.CreateInstance<CardDefinitionSO>();
            var clearance = ScriptableObject.CreateInstance<AdjacentClearanceConfigSO>();
            var killAction = ScriptableObject.CreateInstance<KillActionConfigSO>();
            var damageMark = ScriptableObject.CreateInstance<DamageMarkConfigSO>();
            try
            {
                clearance.EditorApply(2);
                damageMark.EditorApply(1);
                definition.EditorApply(row, null, null, null, null, null, "test.xlsx", "Cards", null, null, null, clearance, killAction, damageMark);

                var restoredRow = definition.ToCardRow();
                Assert.That(restoredRow.adjacentClearanceComponent, Is.True);
                Assert.That(restoredRow.adjacentClearanceMinEmpty, Is.EqualTo(2));
                Assert.That(restoredRow.killActionComponent, Is.True);
                Assert.That(restoredRow.damageMarkComponent, Is.True);
                Assert.That(restoredRow.damageMarkDamagePerStack, Is.EqualTo(1));

                var fromSo = definition.ToArchetype();
                var fromRow = CardArchetype.FromRow(row);
                Assert.That(fromSo.AdjacentClearance.MinEmptyNeighbors, Is.EqualTo(fromRow.AdjacentClearance.MinEmptyNeighbors));
                Assert.That(fromSo.KillAction, Is.Not.Null);
                Assert.That(fromSo.DamageMark.DamagePerStack, Is.EqualTo(fromRow.DamageMark.DamagePerStack));

                // 关闭能力后 SO 必须清空引用，避免旧资产残留“仍在启用”的假象。
                definition.EditorApply(Row(false, false, false), null, null, null, null, null, "test.xlsx", "Cards");
                Assert.That(definition.ToCardRow().adjacentClearanceComponent, Is.False);
                Assert.That(definition.ToCardRow().killActionComponent, Is.False);
                Assert.That(definition.ToCardRow().damageMarkComponent, Is.False);
                Assert.That(definition.ToArchetype().AdjacentClearance, Is.Null);
                Assert.That(definition.ToArchetype().KillAction, Is.Null);
                Assert.That(definition.ToArchetype().DamageMark, Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(definition);
                UnityEngine.Object.DestroyImmediate(clearance);
                UnityEngine.Object.DestroyImmediate(killAction);
                UnityEngine.Object.DestroyImmediate(damageMark);
            }
        }

        /// <summary>非法数值与非法分类组合都要给出可读错误，而不是静默接受。</summary>
        [Test]
        public void InvalidAbilityValuesAreReported()
        {
            Assert.That(Errors(r => r.adjacentClearanceMinEmpty = 0), Does.Contain("adjacentClearanceMinEmpty"));
            Assert.That(Errors(r => r.adjacentClearanceMinEmpty = 5), Does.Contain("1～4"));
            Assert.That(Errors(r => r.adjacentClearanceComponent = false), Does.Contain("adjacentClearanceMinEmpty 必须为 0"));
            Assert.That(Errors(r => r.damageMarkDamagePerStack = 0), Does.Contain("damageMarkDamagePerStack 必须为正整数"));
            Assert.That(Errors(r => r.damageMarkDamagePerStack = -1), Does.Contain("不得为负"));
            Assert.That(Errors(r => r.damageMarkComponent = false), Does.Contain("damageMarkDamagePerStack 必须为 0"));
            Assert.That(Errors(r => r.category = "resource"), Does.Contain("killActionComponent 仅攻击卡可启用"));
            Assert.That(Errors(r => r.category = "special"), Does.Contain("damageMarkComponent 仅攻击卡可启用"));
        }

        /// <summary>直接组装路径（跳过 ConfigDocument）同样拒绝非法能力配置。</summary>
        [Test]
        public void DirectCompositionRejectsIllegalClearance()
        {
            var configuration = new GameConfiguration(GameSessionTests.Document());
            var bad = Row();
            bad.adjacentClearanceMinEmpty = 5;
            var cards = configuration.Cards.Values
                .Select(c => c.Id == bad.id ? CardArchetype.FromRow(bad) : c)
                .ToArray();
            var error = Assert.Throws<ArgumentException>(() =>
                new GameConfiguration(configuration.Rules, cards, configuration.Deck, configuration.BossSlots, 10, 10, 2, configuration.Presentation, configuration.DeckConfig));
            Assert.That(error.Message, Does.Contain("四邻空格限制"));
        }

        /// <summary>
        /// 正式工作簿必须包含五张高级新卡，数值与能力同计划一致；星门与旧卡保持原值。
        /// 输入：正式 xlsx 编译结果；输出：逐卡数值、能力开关与资源组件断言。
        /// 为什么需要：新卡是“正式数据”而非测试夹具，任何一项填错都会直接进入玩家可选池。
        /// </summary>
        [Test]
        public void FormalWorkbookContainsFiveAdvancedCards()
        {
            var report = Spotlight.EditorTools.ConfigCompiler.Compile(Spotlight.EditorTools.ConfigCompiler.DefaultWorkbookPath, false);
            Assert.That(report.Success, Is.True, string.Join("\n", report.Issues));
            var cards = report.Document.cards.ToDictionary(c => c.id);

            void Expect(string id, string name, int hp, int atk, int range, int move, int upkeep,
                bool clearance, int minEmpty, bool killAction, bool damageMark, int perStack)
            {
                var row = cards[id];
                Assert.That(row.name, Is.EqualTo(name));
                Assert.That(row.category, Is.EqualTo("attack"));
                Assert.That(row.tier, Is.EqualTo("advanced"));
                Assert.That(new[] { row.hp, row.atk, row.range, row.moveDistance, row.upkeep },
                    Is.EqualTo(new[] { hp, atk, range, move, upkeep }), id + " 数值不符");
                Assert.That(row.produce, Is.Zero);
                Assert.That(row.resourceComponent, Is.True, id + " 需要资源配置来支付维持费");
                Assert.That(row.deployCost, Is.Zero, id + " 本次新卡不新增部署费用");
                Assert.That(row.requiresUpgrade, Is.False);
                Assert.That(row.adjacentClearanceComponent, Is.EqualTo(clearance), id + " 空格限制开关不符");
                Assert.That(row.adjacentClearanceMinEmpty, Is.EqualTo(minEmpty));
                Assert.That(row.killActionComponent, Is.EqualTo(killAction), id + " 击杀恢复开关不符");
                Assert.That(row.damageMarkComponent, Is.EqualTo(damageMark), id + " 伤害标记开关不符");
                Assert.That(row.damageMarkDamagePerStack, Is.EqualTo(perStack));
            }

            Expect("houyi2", "后羿-II综合舰", 6, 2, 2, 1, 4, true, 2, false, false, 0);
            Expect("texasRanger", "德克萨斯游骑兵", 3, 3, 2, 1, 3, false, 0, false, false, 0);
            Expect("viking2056", "维京长船-2056", 2, 3, 2, 1, 4, false, 0, true, false, 0);
            Expect("snpEu", "SNP-EU", 1, 1, 3, 1, 3, false, 0, false, true, 1);
            Expect("napoleon1769", "拿破仑-1769", 8, 3, 2, 1, 6, false, 0, false, false, 0);

            // 星门保持正式配置现状：不因为新增高级卡而被覆盖成参考表数值。
            var gate = cards["gate"];
            Assert.That(gate.category, Is.EqualTo("special"));
            Assert.That(new[] { gate.hp, gate.atk, gate.range, gate.moveDistance, gate.upkeep },
                Is.EqualTo(new[] { 2, 0, 0, 1, 4 }));
            Assert.That(gate.teleportComponent, Is.True);
            Assert.That(gate.permitsAttackDeploy, Is.True);
            Assert.That(gate.deployHomeOnly, Is.True);
            Assert.That(gate.adjacentClearanceComponent, Is.False);

            // 新卡全部能通过配置构造进入正式卡池，并且未启用的能力为 null。
            var configuration = new GameConfiguration(report.Document);
            Assert.That(configuration.Cards["houyi2"].AdjacentClearance.MinEmptyNeighbors, Is.EqualTo(2));
            Assert.That(configuration.Cards["viking2056"].KillAction, Is.Not.Null);
            Assert.That(configuration.Cards["snpEu"].DamageMark.DamagePerStack, Is.EqualTo(1));
            Assert.That(configuration.Cards["texasRanger"].AdjacentClearance, Is.Null);
            Assert.That(configuration.Cards["napoleon1769"].KillAction, Is.Null);
        }

        /// <summary>旧 JSON 缺少新字段时按关闭处理；新字段类型错误会被拦截；导出导入不丢值。</summary>
        [Test]
        public void JsonImportKeepsCompatibilityAndRejectsWrongTypes()
        {
            var draft = new DebugConfigDraft(null);
            var json = JsonUtility.ToJson(Document(Row()), true);
            Assert.That(draft.Import(json, out var error, out _), Is.True, error);
            var imported = draft.Document.cards.Single(c => c.id == "soldier");
            Assert.That(imported.adjacentClearanceMinEmpty, Is.EqualTo(2));
            Assert.That(imported.killActionComponent, Is.True);
            Assert.That(imported.damageMarkDamagePerStack, Is.EqualTo(1));

            // 导出的文本必须包含新字段，才能在同一次会话外保存能力；再次导入保持一致。
            var exported = draft.ExportJson();
            Assert.That(exported, Does.Contain("adjacentClearanceMinEmpty"));
            Assert.That(exported, Does.Contain("damageMarkDamagePerStack"));
            Assert.That(draft.Import(exported, out var again, out _), Is.True, again);

            Assert.That(draft.Import("{\"schemaVersion\":2,\"cards\":[{\"id\":\"soldier\",\"damageMarkDamagePerStack\":\"abc\"}]}", out var typeError, out _), Is.False);
            Assert.That(typeError, Does.Contain("damageMarkDamagePerStack"));

            // 旧 JSON：移除三个新字段的文本后必须全部按关闭处理。
            var legacy = System.Text.RegularExpressions.Regex.Replace(json,
                "\"(adjacentClearanceComponent|adjacentClearanceMinEmpty|killActionComponent|damageMarkComponent|damageMarkDamagePerStack)\"\\s*:\\s*[^,}\\]]+,?",
                string.Empty);
            legacy = System.Text.RegularExpressions.Regex.Replace(legacy, @",\s*([}\]])", "$1");
            var legacyDraft = new DebugConfigDraft(null);
            Assert.That(legacyDraft.Import(legacy, out var legacyError, out _), Is.True, legacyError);
            var legacyRow = legacyDraft.Document.cards.Single(c => c.id == "soldier");
            Assert.That(legacyRow.adjacentClearanceComponent, Is.False);
            Assert.That(legacyRow.adjacentClearanceMinEmpty, Is.Zero);
            Assert.That(legacyRow.killActionComponent, Is.False);
            Assert.That(legacyRow.damageMarkComponent, Is.False);
            Assert.That(legacyRow.damageMarkDamagePerStack, Is.Zero);
        }
    }
}
