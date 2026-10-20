// 运行时契约与调试流程的 EditMode 测试。
// 覆盖：配置组装校验、调试草稿的合法性判断、旧 JSON 迁移、卡牌文本格式与语义颜色。
// 这些能力都不依赖场景，因此可以在 EditMode 下直接验证，不需要创建对局对象以外的资源。
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Spotlight;
using Spotlight.EditorTools;
using UnityEngine;

namespace Spotlight.Tests
{
    /// <summary>运行时层契约测试。输入：内存构造的配置与 JSON 文本；输出：NUnit 断言结果。</summary>
    public class RuntimeContractTests
    {
        /// <summary>
        /// 构造一个最小合法文档，供各用例按需破坏。
        /// 说明：Deck 表已废除，抽牌堆改由 Rules 的张数与三阶级比例推导。
        /// </summary>
        static ConfigDocument Document()
        {
            return new ConfigDocument
            {
                cards = new[]
                {
                    new CardRow { tier = "basic", id = "miner", name = "矿机", category = "resource", hp = 4, produce = 1, resourceComponent = true, viewKey = "default" },
                    new CardRow { tier = "basic", id = "soldier", name = "士兵", category = "attack", hp = 5, atk = 2, range = 2, moveDistance = 3, viewKey = "default" },
                    new CardRow { tier = "advanced", id = "gate", name = "星门", category = "special", hp = 3, moveDistance = 1, upkeep = 4, resourceComponent = true, teleportComponent = true, permitsAttackDeploy = true, deployHomeOnly = true, avoidAdjacentSameType = true, viewKey = "default" },
                    new CardRow { tier = "intermediate", id = "upgrade", name = "高级矿机", category = "resource", hp = 6, atk = 1, range = 1, requiresUpgrade = true, upgradeFrom = "miner", viewKey = "default" },
                    new CardRow { tier = "none", id = "boss", name = "敌兵", category = "attack", hp = 3, atk = 1, range = 1, moveDistance = 1, viewKey = "default" },
                    new CardRow { tier = "none", id = "spawner", name="刷怪卡", category="special", hp=1, viewKey="default" , canSwap = false, spawnComponent = true, spawnCardType = "boss" },
                    new CardRow { tier = "none", id = "phaseGuard", name="测试护卫舰", category="attack", hp=7, atk=1, range=2, moveDistance=1, viewKey="default" },
                    new CardRow { tier = "none", id = "warship", name="战舰", category="attack", hp=15, atk=2, range=1, moveDistance=1, canAttackResources=false, spawnComponent=true, spawnCardType="boss", viewKey="default" }
                },
                rules = new[] { new RulesRow { drawPileSize = 24, tierRatioBasic = 70, tierRatioIntermediate = 20, tierRatioAdvanced = 10, maxTypesBasic = 5, maxTypesIntermediate = 3, maxTypesAdvanced = 2, bossSpawnerAttackThreshold = 1000, bossSpawnerCountFromColumn = 4, bossSpawnerUnitThreshold = 2, bossSpawnerDeployFromColumn = 5, bossSpawnerCardType = "spawner", bossPhaseTwoHp = 1, bossWarshipColumn = 1, bossWarshipCardType = "warship", bossPhaseThreeFrontCardType = "phaseGuard", bossPhaseThreeRearCardType = "phaseGuard", bossBombInterval = 10, bossBombDamage = 3, bossBombColumn = 5, rows = 5, columns = 10, homeRegionWidth = 2, playerPlays = 5, bossPlays = 1, drawCount = 3, handLimit = 5, initialResources = 0, bossRespawnDelay = 3, playerSide = "left", firstSide = "player", bossCardType = "boss", playerControlsBoss = false, accumulateResources = true, summoningSickness = true, adjacentSwap = true } },
                homes = new[] { new HomeRow { owner = "player", hp = 10, produce = 2 }, new HomeRow { owner = "boss", hp = 10, produce = 0 } },
                bossSlots = new[] { new BossSlotRow { id = "a", rowFraction = 0, edgeOffset = 1 }, new BossSlotRow { id = "b", rowFraction = 0.5, edgeOffset = 1 }, new BossSlotRow { id = "c", rowFraction = 1, edgeOffset = 1 } },
                presentation = new[] { new PresentationRow { referenceWidth = 1920, referenceHeight = 1080, fontSize = 18, logLimit = 50, moveSeconds = 0.2f, feedbackSeconds = 0.1f, selectedScale = 1.05f, playerColor = "#3366FF", bossColor = "#FF3333", resourceColor = "#33AA66", attackColor = "#EE8833", specialColor = "#AA55FF", moveColor = "#33AAFF", attackTargetColor = "#FF3333", swapColor = "#AA33FF", deployColor = "#33FF99" } }
            };
        }

        /// <summary>导出的每张可变表必须独立。输入为内存资产；修改文档后源资产和另一份文档均不变。</summary>
        [Test]
        public void ExportedDocumentDoesNotShareMutableRowsWithAsset()
        {
            var asset = ScriptableObject.CreateInstance<SpotlightConfigAsset>();
            try
            {
                asset.EditorApply(Document(), System.Array.Empty<CardDefinitionSO>());
                var original = JsonUtility.ToJson(asset.ToDocument());
                var copy = asset.ToDocument();
                var other = asset.ToDocument();
                Assert.That(copy.rules[0], Is.Not.SameAs(asset.RulesRow));
                Assert.That(copy.presentation[0], Is.Not.SameAs(asset.PresentationRow));
                Assert.That(copy.homes[0], Is.Not.SameAs(asset.Homes[0]));
                Assert.That(copy.bossSlots[0], Is.Not.SameAs(asset.BossSlots[0]));
                copy.rules[0].homeRegionWidth = 10;
                copy.presentation[0].moveSeconds = .03f;
                copy.homes[0].hp = 999;
                copy.bossSlots[0].edgeOffset = 7;
                Assert.That(JsonUtility.ToJson(asset.ToDocument()), Is.EqualTo(original));
                Assert.That(JsonUtility.ToJson(other), Is.EqualTo(original));
            }
            finally { Object.DestroyImmediate(asset); }
        }

        /// <summary>调试面板的编辑和恢复默认必须只操作草稿；输入为内存资产，输出为源数据与默认值断言。</summary>
        [Test]
        public void EditingDebugDraftCannotChangeAssetOrResetDefaults()
        {
            var asset = ScriptableObject.CreateInstance<SpotlightConfigAsset>();
            try
            {
                asset.EditorApply(Document(), System.Array.Empty<CardDefinitionSO>());
                var original = JsonUtility.ToJson(asset.ToDocument());
                var draft = new DebugConfigDraft(asset);
                draft.Document.rules[0].homeRegionWidth = 10;
                draft.Document.presentation[0].moveSeconds = .03f;
                draft.Document.homes[0].hp = 999;
                draft.Document.bossSlots[0].edgeOffset = 7;
                Assert.That(JsonUtility.ToJson(asset.ToDocument()), Is.EqualTo(original));
                draft.ResetToAsset(asset);
                Assert.That(JsonUtility.ToJson(draft.Document), Is.EqualTo(original));
            }
            finally { Object.DestroyImmediate(asset); }
        }

        [Test]
        public void DirectCompositionRejectsInvalidCombinations()
        {
            // 直接组装路径（正式运行包走这条）必须与文档路径给出同样的拒绝结果。
            var document = Document();
            var configuration = new GameConfiguration(document);
            var cards = configuration.Cards.Values.ToArray();
            Assert.Throws<System.ArgumentException>(() =>
            {
                // 家生命与兵位数量非法时，直接组装路径必须拒绝。
                // requireDrawPileSize: false：夹具牌堆不凑满 drawPileSize，失败原因仍必须是家生命非法。
                new GameConfiguration(configuration.Rules, cards, configuration.Deck, configuration.BossSlots, 0, 10, 2, configuration.Presentation,
                    configuration.DeckConfig, requireDrawPileSize: false);
            });

            // 兵位数量不足时同样被拒绝。
            Assert.Throws<System.ArgumentException>(() =>
            {
                new GameConfiguration(configuration.Rules, cards, configuration.Deck, configuration.BossSlots.Take(2).ToArray(), 10, 10, 2, configuration.Presentation,
                    configuration.DeckConfig, requireDrawPileSize: false);
            });

            // 合法组合可以成功建立（默认牌堆恰好等于 drawPileSize，因此张数校验也一并通过）。
            var ok = new GameConfiguration(configuration.Rules, cards, configuration.Deck, configuration.BossSlots, 10, 10, 2, configuration.Presentation,
                configuration.DeckConfig);
            Assert.That(ok.Cards.Count, Is.EqualTo(document.cards.Length));
        }

        [Test]
        public void DebugDraftValidatesAndCanBuildCandidateSession()
        {
            var draft = new DebugConfigDraft(null);
            Assert.That(draft.Import(JsonUtility.ToJson(Document()), out var error, out _), Is.True, error);
            Assert.That(draft.Validate(), Is.Empty);
            Assert.That(draft.TryCreateConfiguration(out var configuration, out var buildError), Is.True, buildError);
            Assert.That(configuration, Is.Not.Null);
        }

        /// <summary>旧 JSON 缺少炸弹字段时必须报告校验错误，避免正式数值在运行时代码中被默默填入。</summary>
        [Test]
        public void LegacyDraftWithoutBombFieldsCannotBeApplied()
        {
            var draft = new DebugConfigDraft(null);
            var json = JsonUtility.ToJson(Document())
                .Replace("\"bossBombInterval\":10,", "")
                .Replace("\"bossBombDamage\":3,", "")
                .Replace(",\"bossBombColumn\":5", "");
            Assert.That(json, Does.Not.Contain("bossBombInterval"));
            Assert.That(draft.Import(json, out var error, out _), Is.True, error);
            Assert.That(draft.TryCreateConfiguration(out _, out var validation), Is.False);
            Assert.That(validation, Does.Contain("bossBomb"));
        }

        [Test]
        public void DebugDraftRejectsInvalidDraftAndKeepsPreviousContent()
        {
            var draft = new DebugConfigDraft(null);
            Assert.That(draft.Import(JsonUtility.ToJson(Document()), out _, out _), Is.True);
            var broken = Document();
            broken.rules[0].bossCardType = "不存在";
            draft.Import(JsonUtility.ToJson(broken), out _, out _);
            Assert.That(draft.TryCreateConfiguration(out _, out var error), Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        /// <summary>
        /// 旧 JSON 里的废弃字段逐项移除并报告；Deck 表字段也必须作为废弃字段报出。
        /// 输入：带废弃字段的 JSON；输出：清理后的文本与提示条数断言。
        /// </summary>
        [Test]
        public void LegacyJsonMigrationRemovesDeprecatedFieldsAndReportsThem()
        {
            var notices = new List<string>();
            var json = "{\"cost\":12,\"diagonalMove\":true,\"counterAttack\":false,\"chainDeployOnly\":true,\"row\":3,\"col\":4,\"hp\":1,\"deck\":[{\"cardType\":\"ge1\",\"count\":4}]}";
            var cleaned = LegacyJsonMigration.Strip(json, notices);
            Assert.That(cleaned, Does.Not.Contain("cost"));
            Assert.That(cleaned, Does.Not.Contain("diagonalMove"));
            Assert.That(cleaned, Does.Not.Contain("counterAttack"));
            Assert.That(cleaned, Does.Not.Contain("chainDeployOnly"));
            Assert.That(cleaned, Does.Not.Contain("deck"));
            Assert.That(notices.Count, Is.EqualTo(6), string.Join("\n", notices));
            // 清理后的文本仍是合法 JSON。
            Assert.That(JsonUtility.FromJson<CardRow>(cleaned), Is.Not.Null);
        }

        [Test]
        public void ImportRejectsTypeMismatch()
        {
            // JsonUtility 本身会把 "yes" 静默当成 false，所以必须由 JsonShapeValidator 把关，
            // 否则策划会以为非法输入已经生效。
            var draft = new DebugConfigDraft(null);
            var json = JsonUtility.ToJson(Document()).Replace("\"adjacentSwap\":true", "\"adjacentSwap\":\"yes\"");
            Assert.That(json, Does.Contain("\"adjacentSwap\":\"yes\""), "测试夹具未能把布尔字段改成字符串");
            Assert.That(draft.Import(json, out var error, out _), Is.False);
            Assert.That(error, Does.Contain("adjacentSwap"));
            Assert.That(error, Does.Contain("布尔值"));
        }

        [Test]
        public void CardStatLineOnlyShowsAbilitiesTheCardActuallyHas()
        {
            var resource = new CardRow { tier = "basic", id = "ge1", name = "GE-1开采机", category = "resource", hp = 1, produce = 1, resourceComponent = true, viewKey = "default" };
            var line = CardText.StatLine(resource);
            Assert.That(line, Does.Contain("生命 1"));
            Assert.That(line, Does.Contain("产 1"));
            Assert.That(line, Does.Not.Contain("攻"));
            Assert.That(line, Does.Not.Contain("移"));

            var upgrade = new CardRow { tier = "basic", id = "ge12", name = "GE-12开采机", category = "resource", hp = 1, atk = 1, range = 1, produce = 1, resourceComponent = true, requiresUpgrade = true, upgradeFrom = "ge1", viewKey = "default" };
            Assert.That(CardText.StatLine(upgrade), Does.Contain("升级自 ge1"));
        }

        /// <summary>
        /// 选卡面板的属性行必须与对局内卡面同源：同一份数据经 CardRow 与 CardArchetype 两条路径
        /// 拼出的文本要逐字相同，否则玩家选卡时看到的数值会和开局后的卡面不一致。
        /// 输入：一张带资源与部署消耗的卡行；输出：两条路径的 StatLine 相等且包含资源账信息。
        /// </summary>
        [Test]
        public void CardArchetypeStatLineMatchesRowSource()
        {
            var row = new CardRow
            {
                tier = "advanced", id = "houyi2", name = "后羿-II综合舰", category = "attack", viewKey = "default",
                hp = 6, atk = 3, range = 2, moveDistance = 1, produce = 0, upkeep = 2, resourceComponent = true,
                deployCost = 2, adjacentClearanceComponent = true, adjacentClearanceMinEmpty = 2
            };
            var archetype = CardArchetype.FromRow(row);
            Assert.That(CardText.StatLine(archetype), Is.EqualTo(CardText.StatLine(row)),
                "选卡面板与手牌必须使用同一套属性文本");
            Assert.That(CardText.StatLine(archetype), Does.Contain("产 0").And.Contain("耗 2"), "付费维持的卡必须显示消耗");
            Assert.That(CardText.StatLine(archetype), Does.Contain("部署耗 2"), "部署消耗必须可见");
        }

        /// <summary>
        /// 特性短标要覆盖配置里的每一种能力，并且不把“部署消耗”重复成短标（它已经在属性行里）。
        /// 输入：带四邻限制 / 击杀恢复 / 伤害标记的攻击卡；输出：短标与详情正文的分工断言。
        /// </summary>
        [Test]
        public void CardTraitTagsSummarizeConfiguredAbilities()
        {
            var row = new CardRow
            {
                tier = "advanced", id = "viking2056", name = "维京长船-2056", category = "attack", viewKey = "default",
                hp = 5, atk = 2, range = 1, moveDistance = 1, produce = 0, upkeep = 1, resourceComponent = true,
                adjacentClearanceComponent = true, adjacentClearanceMinEmpty = 1,
                killActionComponent = true,
                damageMarkComponent = true, damageMarkDamagePerStack = 1
            };
            var archetype = CardArchetype.FromRow(row);
            var tags = CardText.TraitTags(archetype);
            Assert.That(tags, Is.EqualTo(new[] { "四邻 1 空", "击杀回复", "伤害标记 +1" }), string.Join(" / ", tags));

            var body = CardText.DetailBody(archetype);
            Assert.That(body, Does.Contain("四邻空格限制").And.Contain("击杀恢复行动").And.Contain("伤害标记："),
                "详情正文必须把短标展开成可读规则");
            Assert.That(body, Does.Not.Contain("生命 "), "详情正文不再重复属性行，避免同一数值在面板里出现两次");
            Assert.That(body, Does.Not.Contain(row.name), "详情正文不重复卡名，卡名由详情标题承担");

            // 手牌详情 = 标题 + 属性行 + 正文；选卡详情复用同一份正文，两处不会各写一套措辞。
            var detail = CardText.DetailLine(archetype, Side.Player);
            Assert.That(detail, Does.Contain(row.name));
            Assert.That(detail, Does.EndWith(body), "DetailLine 的规则部分应与 DetailBody 完全一致");
        }

        /// <summary>
        /// 资源卡不应凭空出现特性短标：没有战斗组件的卡也不能提示“不能攻击资源/互换”。
        /// 输入：一张普通资源卡；输出：短标与详情正文都为空的断言。
        /// </summary>
        [Test]
        public void ResourceCardHasNoTraitTags()
        {
            var row = new CardRow
            {
                tier = "basic", id = "ge1", name = "GE-1开采机", category = "resource", viewKey = "default",
                hp = 1, produce = 2, upkeep = 0, resourceComponent = true
            };
            var archetype = CardArchetype.FromRow(row);
            Assert.That(CardText.TraitTags(archetype), Is.Empty, "资源卡没有特性时不应凭空生成短标");
            Assert.That(CardText.DetailBody(archetype), Is.Empty, "没有额外规则时正文应为空，由界面给出“按普通规则上场”的提示");
            Assert.That(CardText.StatLine(archetype), Does.Contain("产 2").And.Not.Contain("攻"));
        }

        [Test]
        public void HighlightColorsFollowPresentationConfig()
        {
            var presentation = new PresentationConfig(Document().presentation[0]);
            Assert.That(CardText.HighlightColor("deploy", presentation).HasValue, Is.True);
            Assert.That(CardText.HighlightColor("move", presentation).HasValue, Is.True);
            Assert.That(CardText.HighlightColor("attack", presentation).HasValue, Is.True);
            Assert.That(CardText.HighlightColor("swap", presentation).HasValue, Is.True);
            Assert.That(CardText.HighlightColor("未知语义", presentation).HasValue, Is.False);
            Assert.That(CardText.OwnerColor(Side.Player, presentation), Is.EqualTo(CardText.Parse("#3366FF", Color.white)));
        }

        [Test]
        public void ManifestAndSourceHashDetectOutOfDateConfiguration()
        {
            var manifest = ScriptableObject.CreateInstance<ConfigManifest>();
            manifest.EditorApply("Assets/Spotlight/Config/Spotlight.xlsx", "hash-a", "2026-01-01T00:00:00Z", new[] { "Assets/Spotlight/Generated/Resources/SpotlightConfig.asset" });
            Assert.That(manifest.SourceHash, Is.EqualTo("hash-a"));
            Assert.That(manifest.GeneratedPaths.Count, Is.EqualTo(1));
            Object.DestroyImmediate(manifest);
        }
    }
}
