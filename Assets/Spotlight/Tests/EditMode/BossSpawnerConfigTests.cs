using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;
using Spotlight.EditorTools;
using UnityEngine;

namespace Spotlight.Tests
{
    /// <summary>刷怪技能配置管线回归。输入：独立文档及临时工作簿；输出：校验、批注、JSON 与默认值一致性断言。</summary>
    public class BossSpawnerConfigTests
    {
        /// <summary>技能新增中文能由项目字体生成，且两套界面字体引用完整；输入为正式字体，输出为缺字断言。</summary>
        [Test]
        public void SpawnerChineseTextIsSupportedByProjectFont()
        {
            var font = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(CjkFontSetup.FontPath);
            Assert.That(font, Is.Not.Null);
            const string text = "部署刷怪卡跳过生成战机顶替暂停恢复周围八格固定据点不能攻击移动或互换下个回合开始生命数量起始列玩家一侧总攻击力阈值";
            foreach (var character in text.Distinct())
                Assert.That(font.HasCharacter(character), Is.True, "字体缺少：" + character);
            var panel = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>(CjkFontSetup.PanelSettingsPath);
            Assert.That(CjkFontSetup.ValidatePanelFonts(panel), Is.Empty);
            Assert.That(UnityEditor.AssetDatabase.LoadMainAssetAtPath(CjkFontSetup.FontAssetPath), Is.Not.Null);
        }

        /// <summary>全部正整数参数都拒绝零和负数；缺失字段反序列化为零时也不能悄悄开启默认行为。</summary>
        [TestCase("bossSpawnerAttackThreshold")] [TestCase("bossSpawnerCountFromColumn")]
        [TestCase("bossSpawnerUnitThreshold")] [TestCase("bossSpawnerDeployFromColumn")]
        public void PositiveParametersAreRequired(string field)
        {
            foreach (var value in new[] { 0, -1 })
            {
                var d = BossSpawnerTests.Document(); typeof(RulesRow).GetField(field).SetValue(d.rules[0], value);
                Assert.That(ConfigValidation.Validate(d).Any(e => e.Contains(field)), Is.True);
            }
            Assert.That(TableSchema.For("Rules").Find(field).Required, Is.True);
            Assert.That(TableSchema.For("Rules").Find(field).Description, Does.Contain("默认"));
        }

        /// <summary>刷怪卡须为固定且无其他能力的特殊卡，生命正数；生成战机引用须为攻击类，错误一次汇总。</summary>
        [Test]
        public void ReferencesAndSpawnerCapabilitiesAreValidated()
        {
            var d = BossSpawnerTests.Document();
            d.rules[0].bossSpawnerCardType = "missing"; d.cards.Single(c => c.id == "spawner").spawnCardType = "miner";
            var errors = ConfigValidation.Validate(d);
            Assert.That(errors.Any(e => e.Contains("bossSpawnerCardType")), Is.True);
            Assert.That(errors.Any(e => e.Contains("spawnCardType")), Is.True);
            foreach (var field in new[] { "hp", "atk", "range", "moveDistance", "category", "resourceComponent", "teleportComponent" })
            {
                d = BossSpawnerTests.Document(); var card = d.cards.Single(c => c.id == "spawner");
                var info = typeof(CardRow).GetField(field);
                info.SetValue(card, field == "category" ? (object)"attack" : info.FieldType == typeof(bool) ? true : field == "hp" ? 0 : 1);
                Assert.That(ConfigValidation.Validate(d).Any(e => e.Contains("bossSpawnerCardType")), Is.True, field);
            }
        }

        /// <summary>正式参数在 JSON 草稿和不可变规则间无损往返，改草稿不污染已创建规则；旧配置缺列会明确失败。</summary>
        [Test]
        public void JsonRoundTripAndMissingFieldsDoNotUseHiddenDefaults()
        {
            var d = BossSpawnerTests.Document();
            var draft = new DebugConfigDraft(null);
            Assert.That(draft.Import(JsonUtility.ToJson(d), out var error, out _), Is.True, error);
            Assert.That(draft.TryCreateConfiguration(out var config, out error), Is.True, error);
            draft.Document.rules[0].bossSpawnerAttackThreshold = 24;
            Assert.That(config.Rules.BossSpawnerAttackThreshold, Is.EqualTo(12));
            Assert.That(draft.Import(draft.ExportJson(), out error, out _), Is.True, error);
            Assert.That(draft.Document.rules[0].bossSpawnerAttackThreshold, Is.EqualTo(24));
            var old = JsonUtility.ToJson(d).Replace("\"bossSpawnerAttackThreshold\":12,", "");
            Assert.That(draft.Import(old, out error, out _), Is.True, error);
            Assert.That(draft.Validate().Any(e => e.Contains("bossSpawnerAttackThreshold")), Is.True);
        }

        /// <summary>默认生成器写出全部中文表头批注与生命批注，不增加数据行，且新工作簿可直接通过正式编译校验。</summary>
        [Test]
        public void DefaultWorkbookHasNativeNotesAndCompiles()
        {
            var path = Path.Combine(Path.GetTempPath(), "spotlight-spawner-" + Guid.NewGuid().ToString("N") + ".xlsx");
            try
            {
                DefaultWorkbookWriter.WriteDefault(path);
                var report = ConfigCompiler.Compile(path, false);
                Assert.That(report.Issues, Is.Empty, string.Join("\n", report.Issues));
                var r = report.Document.rules[0];
                Assert.That(new[] { r.bossSpawnerAttackThreshold, r.bossSpawnerCountFromColumn, r.bossSpawnerUnitThreshold, r.bossSpawnerDeployFromColumn }, Is.EqualTo(new[] { 12,4,2,5 }));
                Assert.That(report.Document.cards.Single(c => c.id == r.bossSpawnerCardType).spawnCardType, Is.EqualTo("bossCombat"));
                Assert.That(report.Document.cards.Single(c => c.id == r.bossSpawnerCardType).hp, Is.EqualTo(1));
                Assert.That(report.Document.cards.Single(c => c.id == r.bossSpawnerCardType).tier, Is.EqualTo("none"),
                    "刷怪卡为不适用阶级，不可能被选进玩家抽牌堆");
                using (var zip = ZipFile.OpenRead(path))
                {
                    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                    var count = 0;
                    foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/comments") && e.FullName.EndsWith(".xml")))
                        using (var stream = entry.Open())
                        {
                            var notes = XDocument.Load(stream).Descendants(ns + "comment").ToArray();
                            count += notes.Length;
                            Assert.That(notes.All(n => n.Value.Contains("默认") || n.Value.Contains("卡牌阶级")), Is.True);
                        }
                    // 原有 40 条说明加上三阶段前列、当前列护卫舰的两项 Rules 引用说明，共 42 条；
                    // 再加上 Cards.cardArt 的卡图说明及三项音乐计时说明，共 46 条。
                    // 保留精确数量检查，避免遗漏批注；额外检查新字段内容，防止错误批注恰好凑足数量。
                    Assert.That(count, Is.EqualTo(46));
                    using (var stream = zip.GetEntry("xl/comments2.xml").Open())
                    {
                        var notes = XDocument.Load(stream).Descendants(ns + "comment").Select(n => n.Value).ToArray();
                        Assert.That(notes.Any(n => n.Contains("bossGuardType1")), Is.True, "前列护卫舰引用缺少默认卡种说明");
                        Assert.That(notes.Any(n => n.Contains("bossGuardType2")), Is.True, "当前列护卫舰引用缺少默认卡种说明");
                    }
                    using (var stream = zip.GetEntry("xl/comments5.xml").Open())
                    {
                        var notes = XDocument.Load(stream).Descendants(ns + "comment").ToArray();
                        var fields = new[] { "musicLoopGapSeconds", "musicFadeOutSeconds", "musicPhaseGapSeconds" };
                        var cells = new[] { "Q1", "R1", "S1" };
                        var descriptions = new[] { "音乐自然结束后等待", "切阶段或结算淡出", "淡出完成后等待新阶段音乐" };
                        var defaults = new[] { "默认 10", "默认 5", "默认 5" };
                        for (var i = 0; i < fields.Length; i++)
                        {
                            var note = notes.Single(n => (string)n.Attribute("ref") == cells[i]);
                            Assert.That(note.Value, Does.Contain(descriptions[i]), fields[i] + " 批注内容不匹配");
                            Assert.That(note.Value, Does.Contain(defaults[i]), fields[i] + " 缺少默认秒数说明");
                        }
                    }
                    // Deck 表已废除，工作表顺序为 Cards、Rules、Homes、BossSlots、Presentation，
                    // 因此带批注的 Rules 是第 2 张表。
                    Assert.That(zip.GetEntry("xl/worksheets/_rels/sheet2.xml.rels"), Is.Not.Null);
                    Assert.That(zip.GetEntry("xl/drawings/notes2.vml"), Is.Not.Null);
                }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        /// <summary>展示名称不参与技能识别；列号大于棋盘宽度合法且运行时范围为空。</summary>
        [Test]
        public void RenamingDoesNotChangeReferencesAndWideColumnsAreLegal()
        {
            var d = BossSpawnerTests.Document(); d.cards.Single(c => c.id == "spawner").name = "生产据点";
            d.rules[0].bossSpawnerDeployFromColumn = 100;
            Assert.That(ConfigValidation.Validate(d), Is.Empty);
            var game = BossSpawnerTests.Start(d); BossSpawnerTests.Arm(game); BossSpawnerTests.End(game);
            Assert.That(game.GetSnapshot().BossSpawnerPending, Is.False);
        }
    }
}
