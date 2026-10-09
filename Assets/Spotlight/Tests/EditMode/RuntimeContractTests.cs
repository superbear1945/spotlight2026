// 运行时契约与调试流程的 EditMode 测试。
// 覆盖：配置组装校验、调试草稿的合法性判断、旧 JSON 迁移、卡牌文本格式与语义颜色。
// 这些能力都不依赖场景，因此可以在 EditMode 下直接验证，不需要创建对局对象以外的资源。
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Spotlight;
using UnityEngine;

namespace Spotlight.Tests
{
    /// <summary>运行时层契约测试。输入：内存构造的配置与 JSON 文本；输出：NUnit 断言结果。</summary>
    public class RuntimeContractTests
    {
        /// <summary>构造一个最小合法文档，供各用例按需破坏。</summary>
        static ConfigDocument Document()
        {
            return new ConfigDocument
            {
                cards = new[]
                {
                    new CardRow { id = "miner", name = "矿机", category = "resource", hp = 4, produce = 1, resourceComponent = true, viewKey = "default" },
                    new CardRow { id = "soldier", name = "士兵", category = "attack", hp = 5, atk = 2, range = 2, moveDistance = 3, viewKey = "default" },
                    new CardRow { id = "gate", name = "星门", category = "special", hp = 3, moveDistance = 1, upkeep = 4, resourceComponent = true, teleportComponent = true, permitsAttackDeploy = true, deployHomeOnly = true, avoidAdjacentSameType = true, viewKey = "default" },
                    new CardRow { id = "upgrade", name = "高级矿机", category = "resource", hp = 6, atk = 1, range = 1, requiresUpgrade = true, upgradeFrom = "miner", viewKey = "default" },
                    new CardRow { id = "boss", name = "敌兵", category = "attack", hp = 3, atk = 1, range = 1, moveDistance = 1, viewKey = "default" }
                },
                deck = new[] { new DeckRow { cardType = "miner", count = 2 }, new DeckRow { cardType = "soldier", count = 2 } },
                rules = new[] { new RulesRow { rows = 5, columns = 10, homeRegionWidth = 2, playerPlays = 5, bossPlays = 1, drawCount = 3, handLimit = 5, initialResources = 0, bossRespawnDelay = 3, playerSide = "left", firstSide = "player", bossCardType = "boss", playerControlsBoss = false, accumulateResources = true, summoningSickness = true, adjacentSwap = true } },
                homes = new[] { new HomeRow { owner = "player", hp = 10, produce = 2 }, new HomeRow { owner = "boss", hp = 10, produce = 0 } },
                bossSlots = new[] { new BossSlotRow { id = "a", rowFraction = 0, edgeOffset = 1 }, new BossSlotRow { id = "b", rowFraction = 0.5, edgeOffset = 1 }, new BossSlotRow { id = "c", rowFraction = 1, edgeOffset = 1 } },
                presentation = new[] { new PresentationRow { referenceWidth = 1920, referenceHeight = 1080, fontSize = 18, logLimit = 50, moveSeconds = 0.2f, feedbackSeconds = 0.1f, selectedScale = 1.05f, playerColor = "#3366FF", bossColor = "#FF3333", resourceColor = "#33AA66", attackColor = "#EE8833", specialColor = "#AA55FF", moveColor = "#33AAFF", attackTargetColor = "#FF3333", swapColor = "#AA33FF", deployColor = "#33FF99" } }
            };
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
                new GameConfiguration(configuration.Rules, cards, configuration.Deck, configuration.BossSlots, 0, 10, 2, configuration.Presentation);
            });

            // 兵位数量不足时同样被拒绝。
            Assert.Throws<System.ArgumentException>(() =>
            {
                new GameConfiguration(configuration.Rules, cards, configuration.Deck, configuration.BossSlots.Take(2).ToArray(), 10, 10, 2, configuration.Presentation);
            });

            // 合法组合可以成功建立。
            var ok = new GameConfiguration(configuration.Rules, cards, configuration.Deck, configuration.BossSlots, 10, 10, 2, configuration.Presentation);
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

        [Test]
        public void LegacyJsonMigrationRemovesDeprecatedFieldsAndReportsThem()
        {
            var notices = new List<string>();
            var json = "{\"cost\":12,\"diagonalMove\":true,\"counterAttack\":false,\"chainDeployOnly\":true,\"row\":3,\"col\":4,\"hp\":1}";
            var cleaned = LegacyJsonMigration.Strip(json, notices);
            Assert.That(cleaned, Does.Not.Contain("cost"));
            Assert.That(cleaned, Does.Not.Contain("diagonalMove"));
            Assert.That(cleaned, Does.Not.Contain("counterAttack"));
            Assert.That(cleaned, Does.Not.Contain("chainDeployOnly"));
            Assert.That(notices.Count, Is.EqualTo(5), string.Join("\n", notices));
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
            var resource = new CardRow { id = "ge1", name = "GE-1开采机", category = "resource", hp = 1, produce = 1, resourceComponent = true, viewKey = "default" };
            var line = CardText.StatLine(resource);
            Assert.That(line, Does.Contain("生命 1"));
            Assert.That(line, Does.Contain("产 1"));
            Assert.That(line, Does.Not.Contain("攻"));
            Assert.That(line, Does.Not.Contain("移"));

            var upgrade = new CardRow { id = "ge12", name = "GE-12开采机", category = "resource", hp = 1, atk = 1, range = 1, produce = 1, resourceComponent = true, requiresUpgrade = true, upgradeFrom = "ge1", viewKey = "default" };
            Assert.That(CardText.StatLine(upgrade), Does.Contain("升级自 ge1"));
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
