using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Spotlight.EditorTools;
using UnityEngine;

namespace Spotlight.Tests
{
    /// <summary>护卫舰配置的 Excel、SO、调试 JSON 三条入口回归；所有修改只发生于隔离文档。</summary>
    public class BossGuardConfigTests
    {
        /// <summary>默认工作簿显式写出两个引用和两种属性，并通过同一业务校验；临时文件用后清理。</summary>
        [Test]
        public void DefaultWorkbookIncludesGuardRowsAndReferences()
        {
            var path = Path.Combine(Path.GetTempPath(), "spotlight-guards-" + Guid.NewGuid().ToString("N") + ".xlsx");
            try
            {
                DefaultWorkbookWriter.WriteDefault(path);
                var report = ConfigCompiler.Compile(path, false);
                Assert.That(report.Issues, Is.Empty, string.Join("\n", report.Issues));
                var config = new GameConfiguration(report.Document);
                Assert.That(config.Rules.BossPhaseThreeFrontCardType, Is.EqualTo("bossGuardType1"));
                Assert.That(config.Rules.BossPhaseThreeRearCardType, Is.EqualTo("bossGuardType2"));
                AssertGuard(config, config.Rules.BossPhaseThreeFrontCardType, 7, 1);
                AssertGuard(config, config.Rules.BossPhaseThreeRearCardType, 3, 3);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        /// <summary>输入运行配置与卡种引用，验证属性、阶级和抽牌堆排除规则；避免只检查源表而遗漏组装链路。</summary>
        static void AssertGuard(GameConfiguration config, string id, int hp, int attack)
        {
            var guard = config.Cards[id];
            Assert.That(new[] { guard.Combat.Hp, guard.Combat.Attack, guard.Combat.Range, guard.Combat.MoveDistance }, Is.EqualTo(new[] { hp, attack, 2, 1 }));
            Assert.That(guard.Tier, Is.EqualTo(CardTier.None));
            Assert.That(guard.Combat.CanSwap && guard.Combat.CanAttackResources, Is.True);
            Assert.That(config.Deck.Any(e => e.TypeId == id), Is.False);
        }

        /// <summary>正式发布后 SO、Prefab 和调试草稿一致；临时改数值不污染源资产或此前创建的运行配置。</summary>
        [Test]
        public void PublishedGuardsAndDebugDraftRoundTripWithoutMutatingAssets()
        {
            var asset = Resources.Load<SpotlightConfigAsset>(ConfigLoader.DefaultResourcePath);
            var source = JsonUtility.ToJson(asset.ToDocument());
            var config = asset.CreateConfiguration();
            AssertGuard(config, config.Rules.BossPhaseThreeFrontCardType, 7, 1);
            AssertGuard(config, config.Rules.BossPhaseThreeRearCardType, 3, 3);
            foreach (var id in new[] { config.Rules.BossPhaseThreeFrontCardType, config.Rules.BossPhaseThreeRearCardType })
                Assert.That(asset.Find(id).Prefab, Is.Not.Null);
            var draft = new DebugConfigDraft(null);
            Assert.That(draft.Import(source, out var error, out _), Is.True, error);
            var front = draft.FindCard(config.Rules.BossPhaseThreeFrontCardType);
            front.hp = 13; front.atk = 6; front.range = 4;
            Assert.That(draft.TryCreateConfiguration(out var changed, out error), Is.True, error);
            Assert.That(changed.Cards[front.id].Combat.Hp, Is.EqualTo(13));
            Assert.That(changed.Cards[front.id].Combat.Attack, Is.EqualTo(6));
            Assert.That(changed.Cards[front.id].Combat.Range, Is.EqualTo(4));
            var imported = new DebugConfigDraft(null);
            Assert.That(imported.Import(JsonUtility.ToJson(draft.Document), out error, out _), Is.True, error);
            Assert.That(imported.FindCard(front.id).hp, Is.EqualTo(13));
            Assert.That(config.Cards[front.id].Combat.Hp, Is.EqualTo(7));
            Assert.That(JsonUtility.ToJson(asset.ToDocument()), Is.EqualTo(source));
        }

        /// <summary>引用缺失和不支持的能力组合必须同时被文档校验及直接 SO 式组装拒绝，错误带具体字段名。</summary>
        [TestCase("missing")] [TestCase("tier")] [TestCase("category")] [TestCase("heal")]
        [TestCase("instant")] [TestCase("resources")] [TestCase("hp")] [TestCase("attack")] [TestCase("range")]
        public void InvalidGuardReferencesFailBothConfigurationPaths(string invalid)
        {
            var d = BossGuardTests.Document(); var good = new GameConfiguration(d);
            var guard = d.cards.Single(c => c.id == "guard1");
            switch (invalid)
            {
                case "missing": d.rules[0].bossPhaseThreeFrontCardType = "not-found"; break;
                case "tier": guard.tier = "basic"; break;
                case "category": guard.category = "resource"; break;
                case "heal": guard.attacksAlliesAsHeal = true; break;
                case "instant": guard.leaveAfterDeploy = true; guard.deployGainResource = 1; break;
                case "resources": guard.canAttackResources = false; break;
                case "hp": guard.hp = 0; break;
                case "attack": guard.atk = 0; break;
                case "range": guard.range = 0; break;
            }
            Assert.That(ConfigValidation.Validate(d).Any(e => e.Contains("bossPhaseThreeFrontCardType")), Is.True);
            var error = Assert.Throws<ArgumentException>(() => new GameConfiguration(new MatchConfig(d.rules[0]),
                d.cards.Select(CardArchetype.FromRow), good.Deck, good.BossSlots, good.PlayerHomeHp, good.BossHomeHp,
                good.PlayerHomeProduce, good.Presentation, good.DeckConfig));
            Assert.That(error.Message, Does.Contain("bossPhaseThreeFrontCardType"));
        }

        /// <summary>旧 JSON 缺少任一引用时明确拒绝，原草稿保留；表结构也必须把对应列标为必填。</summary>
        [TestCase("bossPhaseThreeFrontCardType")] [TestCase("bossPhaseThreeRearCardType")]
        public void MissingGuardReferenceRequiresExplicitMigration(string field)
        {
            var d = BossGuardTests.Document(); var json = JsonUtility.ToJson(d);
            var draft = new DebugConfigDraft(null);
            Assert.That(draft.Import(json, out var error, out _), Is.True, error);
            var previous = draft.Document;
            json = System.Text.RegularExpressions.Regex.Replace(json, "\"" + field + "\":\"[^\"]*\",?", "");
            Assert.That(draft.Import(json, out error, out _), Is.False);
            Assert.That(error, Does.Contain(field));
            Assert.That(draft.Document, Is.SameAs(previous));
            Assert.That(TableSchema.For("Rules").Find(field).Required, Is.True);
            var schema = TableSchema.For("Rules");
            var names = schema.Columns.Where(c => c.Name != field).Select(c => c.Name).ToArray();
            var cells = new string[1, names.Length];
            for (var i = 0; i < names.Length; i++) cells[0, i] = names[i];
            var issues = new System.Collections.Generic.List<ConfigIssue>();
            Assert.That(TableMapper.TryBuildHeader("old.xlsx", schema, cells, issues, out _), Is.False);
            Assert.That(issues.Any(i => i.Reason.Contains(field)), Is.True);
        }
    }
}
