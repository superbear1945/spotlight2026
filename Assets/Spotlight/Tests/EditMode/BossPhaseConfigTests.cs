using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Spotlight.EditorTools;
using UnityEditor;
using UnityEngine;

namespace Spotlight.Tests
{
    /// <summary>Boss 阶段及刷怪能力管线回归。独立配置验证错误边界，正式资产只读验证发布结果。</summary>
    public class BossPhaseConfigTests
    {
        /// <summary>合并后的正式 SO 同时保留主分支能力和刷怪配置；通过导出与加载入口检查发布链路没有漏传参数。</summary>
        [Test]
        public void PublishedConfigurationRetainsBothCapabilitySets()
        {
            var asset = Resources.Load<SpotlightConfigAsset>(ConfigLoader.DefaultResourcePath);
            Assert.That(ConfigLoader.TryCreateConfiguration(out var config, out var error, asset), Is.True, error);
            var d = asset.ToDocument();
            Assert.That(d.cards.Length, Is.EqualTo(18));
            Assert.That(config.Cards["kepler1"].DeployCost, Is.EqualTo(10));
            Assert.That(config.Cards["kepler1"].DeployRequireAdjacentCount, Is.EqualTo(2));
            Assert.That(config.Cards["collector1"].MarkOnEnter, Is.Not.Null);
            Assert.That(config.Cards["force1"].DeathGrowth, Is.Not.Null);
            Assert.That(config.Cards["flounder1"].AttacksAlliesAsHeal, Is.True);
            Assert.That(config.Cards["reserve1"].Instant, Is.Not.Null);
            Assert.That(config.Cards["bossWarship"].Tier, Is.EqualTo(CardTier.None));
            Assert.That(config.Cards["bossWarship"].Spawn.SpawnCardType, Is.EqualTo("bossCombat"));
            Assert.That(d.cards.Single(c => c.id == "collector1").markOnEnterComponent, Is.True);
            Assert.That(d.cards.Single(c => c.id == "force1").deathGrowthComponent, Is.True);
            Assert.That(d.cards.Single(c => c.id == "reserve1").leaveAfterDeploy, Is.True);
            Assert.That(d.cards.Single(c => c.id == "bossWarship").spawnComponent, Is.True);
            Assert.That(d.schemaVersion, Is.EqualTo(2));
            Assert.That(config.DeckConfig.DrawPileSize, Is.EqualTo(24));
            Assert.That(config.Deck.Sum(e => e.Count), Is.EqualTo(24));
            Assert.That(config.Deck.All(e => config.Cards[e.TypeId].Tier != CardTier.None), Is.True);
            var game = new GameSession(config, new SeededRandom(42), 200);
            Assert.That(game.GetSnapshot().BossPhase, Is.EqualTo(BossPhase.PhaseOne));
            Assert.That(config.Rules.BossPhaseTwoHp, Is.EqualTo(20));
            Assert.That(config.Rules.HomeRegionWidth, Is.EqualTo(2));
        }

        /// <summary>默认工作簿明确包含阶段值、战舰基础属性与每卡刷怪引用，且直接通过正式校验。</summary>
        [Test]
        public void DefaultWorkbookContainsPhaseAndSpawnConfiguration()
        {
            var path = Path.Combine(Path.GetTempPath(), "spotlight-phases-" + Guid.NewGuid().ToString("N") + ".xlsx");
            try
            {
                DefaultWorkbookWriter.WriteDefault(path);
                var report = ConfigCompiler.Compile(path, false);
                Assert.That(report.Issues, Is.Empty, string.Join("\n", report.Issues));
                var d = report.Document;
                Assert.That(d.homes.Single(h => h.owner == "boss").hp, Is.EqualTo(30));
                Assert.That(d.rules[0].bossPhaseTwoHp, Is.EqualTo(20));
                Assert.That(d.rules[0].bossWarshipColumn, Is.EqualTo(8));
                var ship = d.cards.Single(c => c.id == d.rules[0].bossWarshipCardType);
                Assert.That(new[] { ship.hp, ship.atk, ship.range, ship.moveDistance }, Is.EqualTo(new[] { 15, 2, 1, 1 }));
                Assert.That(ship.spawnComponent && ship.canSwap && !ship.canAttackResources, Is.True);
                Assert.That(ship.spawnCardType, Is.EqualTo("bossCombat"));
                Assert.That(new GameConfiguration(d).Deck.Any(e => e.TypeId == ship.id), Is.False);
                Assert.That(TableSchema.For("Rules").Find("bossSpawnerSpawnCardType"), Is.Null);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        /// <summary>阈值必须为正且小于初始生命，列必须在棋盘内；一次性返回可读错误。</summary>
        [TestCase(0, 8)] [TestCase(30, 8)] [TestCase(31, 8)] [TestCase(20, 0)] [TestCase(20, 11)]
        public void InvalidPhaseParametersAreRejected(int hp, int column)
        {
            var d = BossPhaseTests.Document(); d.rules[0].bossPhaseTwoHp = hp; d.rules[0].bossWarshipColumn = column;
            Assert.That(ConfigValidation.Validate(d), Is.Not.Empty);
        }

        /// <summary>新增表列必须显式存在，刷怪引用即使允许禁用行留空，也不能缺少整列。</summary>
        [TestCase("spawnComponent")] [TestCase("spawnCardType")]
        [TestCase("canAttackResources")] [TestCase("canSwap")]
        public void WorkbookMissingCapabilityColumnHasMigrationError(string missing)
        {
            var schema = TableSchema.For("Cards");
            var names = schema.Columns.Where(c => c.Name != missing).Select(c => c.Name).ToArray();
            var cells = new string[1, names.Length];
            for (var i = 0; i < names.Length; i++) cells[0, i] = names[i];
            var issues = new System.Collections.Generic.List<ConfigIssue>();
            Assert.That(TableMapper.TryBuildHeader("old.xlsx", schema, cells, issues, out _), Is.False);
            Assert.That(issues.Any(i => i.Reason.Contains(missing) && i.Reason.Contains("迁移")), Is.True);
        }

        /// <summary>显式权限不能留空后被映射成 false，否则旧卡可能无提示地失去攻击资源或互换能力。</summary>
        [TestCase("canAttackResources")] [TestCase("canSwap")] [TestCase("spawnComponent")]
        public void EmptyCapabilityFlagIsRejected(string missing)
        {
            var table = new TableDefinition("Cards", new[] { TableSchema.For("Cards").Find(missing) });
            var issues = new System.Collections.Generic.List<ConfigIssue>();
            var cells = new[,] { { missing, "name" }, { "", "使该行非空" } };
            var headers = new System.Collections.Generic.Dictionary<string, int> { { missing, 0 } };
            Assert.That(TableMapper.MapRows<CardRow>("old.xlsx", table, cells, headers, issues), Is.Empty);
            Assert.That(issues.Any(i => i.Reason.Contains(missing)), Is.True);
        }

        /// <summary>引用和能力组合由配置校验，不按卡名推导；坏引用与不可移动战舰不可发布。</summary>
        [Test]
        public void ReferencesAndCapabilitiesAreValidatedWithoutNameBranches()
        {
            var d = BossPhaseTests.Document(); var ship = d.cards.Single(c => c.id == "warship");
            ship.name = "新的战舰显示名称";
            Assert.That(ConfigValidation.Validate(d), Is.Empty);
            ship.canAttackResources = true;
            Assert.That(ConfigValidation.Validate(d).Any(e => e.Contains("bossWarshipCardType")), Is.True);
            ship.canAttackResources = false; ship.spawnCardType = "miner";
            Assert.That(ConfigValidation.Validate(d).Any(e => e.Contains("spawnCardType")), Is.True);
            ship.spawnCardType = "boss"; ship.moveDistance = 0;
            Assert.That(ConfigValidation.Validate(d).Any(e => e.Contains("bossWarshipCardType")), Is.True);
        }

        /// <summary>新草稿可往返且与配置脱钩；旧全局刷怪字段或缺少显式权限时拒绝导入并保留草稿。</summary>
        [Test]
        public void JsonMigrationIsExplicitAndFailedImportPreservesDraft()
        {
            var d = BossPhaseTests.Document(); var draft = new DebugConfigDraft(null);
            var json = JsonUtility.ToJson(d);
            Assert.That(draft.Import(json, out var error, out _), Is.True, error);
            Assert.That(draft.TryCreateConfiguration(out var config, out error), Is.True, error);
            draft.FindCard("warship").spawnCardType = "soldier";
            Assert.That(config.Cards["warship"].Spawn.SpawnCardType, Is.EqualTo("boss"));
            var previous = draft.Document;
            Assert.That(draft.Import(json.Replace("\"bossPhaseTwoHp\":20", "\"bossSpawnerSpawnCardType\":\"boss\",\"bossPhaseTwoHp\":20"), out error, out _), Is.False);
            Assert.That(error, Does.Contain("迁移"));
            Assert.That(draft.Document, Is.SameAs(previous));
            Assert.That(draft.Import(json.Replace("\"canSwap\":true,", ""), out error, out _), Is.False);
            Assert.That(error, Does.Contain("canSwap"));
            Assert.That(draft.Document, Is.SameAs(previous));
        }

        /// <summary>卡名中的括号与转义引号不影响逐行迁移检查；缺少任何新字段均不能被静默补默认值。</summary>
        [TestCase("spawnCardType")] [TestCase("bossPhaseTwoHp")] [TestCase("bossWarshipColumn")]
        [TestCase("bossWarshipCardType")] [TestCase("spawnComponent")]
        public void JsonRequiresExplicitFieldsEvenWithBracketsInCardName(string field)
        {
            var d = BossPhaseTests.Document(); d.cards[0].name = "特殊 ] { 名称 \" 引号";
            var json = JsonUtility.ToJson(d);
            var draft = new DebugConfigDraft(null);
            Assert.That(draft.Import(json, out var error, out _), Is.True, error);
            var previous = draft.Document;
            json = System.Text.RegularExpressions.Regex.Replace(json,
                "\\\"" + field + "\\\":(?:\\\"[^\\\"]*\\\"|true|false|[0-9]+),?", "");
            Assert.That(draft.Import(json, out error, out _), Is.False);
            Assert.That(error, Does.Contain(field));
            Assert.That(draft.Document, Is.SameAs(previous));
        }

        /// <summary>正式生成卡种同时具备正确组件与子资产，源旧 ID 仍可解析；门禁验证不依赖运行时补挂组件。</summary>
        [Test]
        public void PublishedSpawnAssetsAndPrefabReferencesMatch()
        {
            var asset = Resources.Load<SpotlightConfigAsset>(ConfigLoader.DefaultResourcePath);
            Assert.That(ConfigLoader.TryCreateConfiguration(out var config, out var error, asset), Is.True, error);
            foreach (var id in new[] { config.Rules.BossSpawnerCardType, config.Rules.BossWarshipCardType })
            {
                var definition = asset.Find(id);
                Assert.That(definition.Spawn, Is.Not.Null);
                Assert.That(definition.Spawn.name, Is.EqualTo(id + "_Spawn"));
                // HideInHierarchy 的配置经 Resources 加载后，Unity 2022 的 IsSubAsset 标志可能为 false。
                // 直接验证磁盘归属、持久性及宿主主对象，避免用显示标志代替实际子资产契约。
                var path = AssetDatabase.GetAssetPath(definition);
                Assert.That(EditorUtility.IsPersistent(definition.Spawn), Is.True);
                Assert.That(AssetDatabase.GetAssetPath(definition.Spawn), Is.EqualTo(path));
                Assert.That(AssetDatabase.LoadMainAssetAtPath(path), Is.SameAs(definition));
                Assert.That(AssetDatabase.LoadAllAssetsAtPath(path), Does.Contain(definition.Spawn));
                Assert.That(definition.Prefab.Spawn.Config, Is.SameAs(definition.Spawn));
                Assert.That(definition.Prefab.GetComponent<SpawnComponent>(), Is.SameAs(definition.Prefab.Spawn));
                Assert.That(definition.ToCardRow().spawnComponent, Is.True);
            }
        }

        /// <summary>重新发布正式文档必须保留卡种 GUID 与刷怪子资产本地 ID，防止场景和 Prefab 引用断裂。</summary>
        [Test]
        public void RepublishPreservesSpawnAssetIdentity()
        {
            var asset = Resources.Load<SpotlightConfigAsset>(ConfigLoader.DefaultResourcePath);
            var d = asset.ToDocument();
            var spawn = asset.Find(d.rules[0].bossWarshipCardType).Spawn;
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(spawn, out string guid, out long localId), Is.True);
            var report = ConfigCompiler.Compile("Assets/Spotlight/Config/Spotlight.xlsx", true);
            Assert.That(report.Issues, Is.Empty, string.Join("\n", report.Issues));
            var refreshed = Resources.Load<SpotlightConfigAsset>(ConfigLoader.DefaultResourcePath).Find(d.rules[0].bossWarshipCardType).Spawn;
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(refreshed, out string afterGuid, out long afterLocalId), Is.True);
            Assert.That(afterGuid, Is.EqualTo(guid));
            Assert.That(afterLocalId, Is.EqualTo(localId));
        }

        /// <summary>
        /// 发布中途异常必须恢复全部旧文件并删除本次新资产，不能只恢复主配置引用。
        /// 直接给发布器不满足前置契约的名称制造写入阶段异常；不篡改对局或反射注入失败钩子。
        /// </summary>
        [Test]
        public void FailedPublishRestoresCardsPrefabsManifestAndGuids()
        {
            var asset = Resources.Load<SpotlightConfigAsset>(ConfigLoader.DefaultResourcePath);
            var d = asset.ToDocument();
            var originalHp = d.cards[0].hp;
            var originalId = d.cards[0].id;
            var before = Directory.GetFiles(ConfigAssetPublisher.GeneratedRoot, "*", SearchOption.AllDirectories)
                .ToDictionary(p => p, File.ReadAllBytes);
            d.cards[0].hp += 7;
            d.cards[1].name = null; // EditorApply.Trim 将在先前资产已发布后抛出异常。
            d.cards = new[] { new CardRow { tier = "none", id = "rollbackSpawnProbe", name = "回滚刷怪测试", category = "attack", hp = 1,
                spawnComponent = true, spawnCardType = d.rules[0].bossCardType, viewKey = "default" } }.Concat(d.cards).ToArray();
            var result = ConfigAssetPublisher.Publish(d, "rollback-test.xlsx", "invalid-publication");
            Assert.That(result.Issues.Any(i => i.Reason.Contains("已回滚全部")), Is.True, string.Join("\n", result.Issues));
            Assert.That(result.Paths, Is.Empty);
            Assert.That(Directory.GetFiles(ConfigAssetPublisher.GeneratedRoot, "*", SearchOption.AllDirectories), Is.EquivalentTo(before.Keys));
            foreach (var file in before) Assert.That(File.ReadAllBytes(file.Key), Is.EqualTo(file.Value), file.Key);
            var restored = AssetDatabase.LoadAssetAtPath<CardDefinitionSO>(ConfigAssetPublisher.CardAssetPath(originalId));
            Assert.That(restored.Combat.Hp, Is.EqualTo(originalHp), "回滚必须同步恢复已加载对象的数值");
        }

        /// <summary>新增 UI 文案所需汉字可由现有字体生成，PanelSettings 仍引用有效字体。</summary>
        [Test]
        public void PhaseAndWarshipTextHasFontCoverage()
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(CjkFontSetup.FontPath);
            foreach (var character in "战舰阶段锁血解除阈值待部署存活已击败刷怪组件暂停三等分".Distinct())
                Assert.That(font.HasCharacter(character), Is.True, "字体缺少：" + character);
            var panel = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>(CjkFontSetup.PanelSettingsPath);
            Assert.That(CjkFontSetup.ValidatePanelFonts(panel), Is.Empty);
        }
    }
}
