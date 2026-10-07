// 对局配置面板的 EditMode 测试。
// 覆盖：导入草稿后的表单生成、字段编辑写回草稿、数值夹紧、卡组摘要统计、
// 校验失败禁用“应用并重开”、空配置的降级表现，以及草稿层的新增卡种与数量设置。
// 测试直接实例化 UXML 并注入草稿，不创建场景、不需要 PanelSettings，也不模拟点击事件。
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Spotlight;
using UnityEngine;
using UnityEngine.UIElements;

namespace Spotlight.Tests
{
    /// <summary>配置面板表单测试。输入：UXML 实例与内存草稿；输出：NUnit 断言结果。</summary>
    public class ConfigPanelTests
    {
        /// <summary>面板界面资产路径。</summary>
        const string UxmlPath = "Assets/Spotlight/UI/Spotlight.uxml";

        /// <summary>测试用界面根元素。</summary>
        VisualElement _root;
        /// <summary>被测试的配置面板。</summary>
        ConfigDebugPanel _panel;
        /// <summary>承载面板组件的临时对象。</summary>
        GameObject _host;

        /// <summary>装配界面实例与面板组件。输入：无；输出：无。</summary>
        [SetUp]
        public void SetUp()
        {
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            Assert.That(asset, Is.Not.Null, "找不到界面文件 " + UxmlPath);
            _root = asset.Instantiate();
            _host = new GameObject("ConfigPanelTestHost");
            _panel = _host.AddComponent<ConfigDebugPanel>();
            _panel.BindTo(_root, null);
        }

        /// <summary>释放临时对象。输入：无；输出：无。</summary>
        [TearDown]
        public void TearDown()
        {
            if (_host != null) Object.DestroyImmediate(_host);
            _root = null;
            _panel = null;
        }

        /// <summary>构造一份最小可用配置文档。输入：无；输出：文档。</summary>
        internal static ConfigDocument Document()
        {
            return new ConfigDocument
            {
                cards = new[]
                {
                    new CardRow { id = "miner", name = "矿机", category = "resource", hp = 2, produce = 1, resourceComponent = true, viewKey = "default" },
                    new CardRow { id = "soldier", name = "士兵", category = "attack", hp = 3, atk = 1, range = 1, moveDistance = 1, upkeep = 1, resourceComponent = true, viewKey = "default" },
                    new CardRow { id = "gate", name = "星门", category = "special", hp = 2, moveDistance = 1, upkeep = 4, resourceComponent = true, teleportComponent = true, permitsAttackDeploy = true, deployHomeOnly = true, avoidAdjacentSameType = true, viewKey = "default" },
                    new CardRow { id = "boss", name = "Boss敌兵", category = "attack", hp = 3, atk = 1, range = 1, moveDistance = 1, viewKey = "default" }
                },
                deck = new[]
                {
                    new DeckRow { cardType = "miner", count = 4 },
                    new DeckRow { cardType = "soldier", count = 4 },
                    new DeckRow { cardType = "gate", count = 4 }
                },
                rules = new[]
                {
                    new RulesRow { rows = 5, columns = 10, homeRegionWidth = 2, playerPlays = 5, bossPlays = 1, drawCount = 5, handLimit = 5, initialResources = 0, bossRespawnDelay = 3, playerSide = "left", firstSide = "player", bossCardType = "boss", playerControlsBoss = false, accumulateResources = true, summoningSickness = true, adjacentSwap = true }
                },
                homes = new[] { new HomeRow { owner = "player", hp = 10, produce = 2 }, new HomeRow { owner = "boss", hp = 10, produce = 0 } },
                bossSlots = new[]
                {
                    new BossSlotRow { id = "a", rowFraction = 0, edgeOffset = 1 },
                    new BossSlotRow { id = "b", rowFraction = 0.5, edgeOffset = 1 },
                    new BossSlotRow { id = "c", rowFraction = 1, edgeOffset = 1 }
                },
                presentation = new[]
                {
                    new PresentationRow { referenceWidth = 1920, referenceHeight = 1080, fontSize = 18, logLimit = 200, moveSeconds = 0.2f, feedbackSeconds = 0.12f, selectedScale = 1.05f, playerColor = "#3366FF", bossColor = "#FF3333", resourceColor = "#33AA66", attackColor = "#EE8833", specialColor = "#AA55FF", moveColor = "#33AAFF", attackTargetColor = "#FF3333", swapColor = "#AA33FF", deployColor = "#33FF99" }
                }
            };
        }

        /// <summary>用给定文档打开面板。输入：文档；输出：草稿实例。</summary>
        DebugConfigDraft Open(ConfigDocument document)
        {
            var draft = new DebugConfigDraft(null);
            Assert.That(draft.Import(JsonUtility.ToJson(document), out var error, out _), Is.True, error);
            _panel.Open(draft);
            return draft;
        }

        /// <summary>空配置时只展示提示，不构建表单，也不允许应用。输入：无；输出：无。</summary>
        [Test]
        public void EmptyConfigurationShowsHintInsteadOfForm()
        {
            _panel.Open();
            Assert.That(_root.Q<VisualElement>("debug-board").childCount, Is.EqualTo(0));
            Assert.That(_root.Q<Button>("debug-apply-button").enabledSelf, Is.False);
            Assert.That(_root.Q<Label>("debug-error").text, Does.Contain("编译全部配置"));
        }

        /// <summary>打开后各分区都应有内容。输入：无；输出：无。</summary>
        [Test]
        public void OpenBuildsEverySection()
        {
            Open(Document());
            Assert.That(_root.Q<VisualElement>("debug-board").childCount, Is.EqualTo(1));
            Assert.That(_root.Q<VisualElement>("debug-home").childCount, Is.EqualTo(1));
            Assert.That(_root.Q<VisualElement>("debug-deck").childCount, Is.EqualTo(1));
            Assert.That(_root.Q<VisualElement>("debug-boss").childCount, Is.EqualTo(1));
            Assert.That(_root.Q<VisualElement>("debug-turn").childCount, Is.EqualTo(1));
            Assert.That(_root.Q<VisualElement>("debug-toggles").childCount, Is.EqualTo(1));
            // 逐卡种分区：资源卡 + 玩家战斗卡 + 特殊卡 + 末尾一条公共说明。
            Assert.That(_root.Q<VisualElement>("debug-cards").childCount, Is.EqualTo(4));
            Assert.That(_root.Q<Button>("debug-apply-button").enabledSelf, Is.True);
            Assert.That(_root.Q<Label>("debug-error").style.display.value, Is.EqualTo(DisplayStyle.None));
        }

        /// <summary>卡组摘要按分类统计。输入：无；输出：无。</summary>
        [Test]
        public void DeckSummaryCountsByCategory()
        {
            Open(Document());
            var summary = DeckSummary();
            Assert.That(summary.text, Does.Contain("共 12 张"));
            Assert.That(summary.text, Does.Contain("资源卡 4 张"));
            Assert.That(summary.text, Does.Contain("战斗卡 4 张"));
            Assert.That(summary.text, Does.Contain("特殊卡 4 张"));
        }

        /// <summary>编辑数字字段会立刻写回草稿。输入：无；输出：无。</summary>
        [Test]
        public void EditingNumberFieldWritesBackToDraft()
        {
            var draft = Open(Document());
            var rows = FindIntegerField("行数（纵向，默认 5）");
            Assert.That(rows, Is.Not.Null, "找不到“行数”字段");
            rows.value = 7;
            // UI Toolkit 不会向未挂面板的元素派发 ChangeEvent，因此显式执行同一段写回逻辑。
            ConfigFieldBuilder.ApplyCurrentValue(rows);
            Assert.That(draft.Document.rules[0].rows, Is.EqualTo(7));
        }

        /// <summary>数字字段超出下限时被夹紧。输入：无；输出：无。</summary>
        [Test]
        public void NumberFieldClampsToValidRange()
        {
            var draft = Open(Document());
            var rows = FindIntegerField("行数（纵向，默认 5）");
            rows.value = 0;
            ConfigFieldBuilder.ApplyCurrentValue(rows);
            Assert.That(rows.value, Is.EqualTo(1), "行数应被夹紧到最小值 1");
            Assert.That(draft.Document.rules[0].rows, Is.EqualTo(1));
        }

        /// <summary>切换分类下拉会写回草稿。输入：无；输出：无。</summary>
        [Test]
        public void DropdownWritesBackToDraft()
        {
            var draft = Open(Document());
            var side = FindDropdownField("玩家所在边缘");
            Assert.That(side, Is.Not.Null, "找不到“玩家所在边缘”字段");
            side.index = 1;
            ConfigFieldBuilder.ApplyCurrentValue(side);
            Assert.That(draft.Document.rules[0].playerSide, Is.EqualTo("right"));
        }

        /// <summary>把 Boss 卡种改成资源卡后校验失败并禁用应用。输入：无；输出：无。</summary>
        [Test]
        public void InvalidBossCardTypeDisablesApply()
        {
            var document = Document();
            document.rules[0].bossCardType = "miner";
            Open(document);
            Assert.That(_root.Q<Button>("debug-apply-button").enabledSelf, Is.False);
            Assert.That(_root.Q<Label>("debug-error").text, Does.Contain("bossCardType"));
        }

        /// <summary>面板默认隐藏，打开后显示，关闭后再次隐藏。输入：无；输出：无。</summary>
        [Test]
        public void PanelIsHiddenUntilOpened()
        {
            var panel = _root.Q<VisualElement>("debug-panel");
            Assert.That(panel.style.display.value, Is.EqualTo(DisplayStyle.None));
            _panel.Open(new DebugConfigDraft(null));
            Assert.That(panel.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            _panel.Close();
            Assert.That(panel.style.display.value, Is.EqualTo(DisplayStyle.None));
        }

        /// <summary>新增卡种使用 custom_分类_序号 命名并默认 0 张。输入：无；输出：无。</summary>
        [Test]
        public void AddCardTypeUsesStableNamingAndZeroCount()
        {
            var draft = new DebugConfigDraft(null);
            Assert.That(draft.Import(JsonUtility.ToJson(Document()), out _, out _), Is.True);
            var added = draft.AddCardType("resource");
            Assert.That(added.id, Is.EqualTo("custom_resource_1"));
            Assert.That(added.name, Is.EqualTo("新资源卡1"));
            Assert.That(draft.GetDeckCount(added.id), Is.EqualTo(0));

            var second = draft.AddCardType("resource");
            Assert.That(second.id, Is.EqualTo("custom_resource_2"));
            Assert.That(second.name, Is.EqualTo("新资源卡2"));

            var attack = draft.AddCardType("attack");
            Assert.That(attack.id, Is.EqualTo("custom_attack_1"));
            Assert.That(attack.category, Is.EqualTo("attack"));
            Assert.That(draft.FindCard("custom_attack_1"), Is.SameAs(attack));
        }

        /// <summary>设置卡组数量会补建缺失条目。输入：无；输出：无。</summary>
        [Test]
        public void SetDeckCountCreatesMissingEntry()
        {
            var draft = new DebugConfigDraft(null);
            Assert.That(draft.Import(JsonUtility.ToJson(Document()), out _, out _), Is.True);
            draft.SetDeckCountValue("soldier", 9);
            Assert.That(draft.GetDeckCount("soldier"), Is.EqualTo(9));
            draft.SetDeckCountValue("gate", -3);
            Assert.That(draft.GetDeckCount("gate"), Is.EqualTo(0));
            var summary = draft.GetDeckSummary();
            Assert.That(summary.Total, Is.EqualTo(4 + 9 + 0));
        }

        /// <summary>草稿可直接开局，说明面板产出的是可用配置。输入：无；输出：无。</summary>
        [Test]
        public void DraftProducesPlayableConfiguration()
        {
            var draft = new DebugConfigDraft(null);
            Assert.That(draft.Import(JsonUtility.ToJson(Document()), out _, out _), Is.True);
            Assert.That(draft.TryCreateConfiguration(out var configuration, out var error), Is.True, error);
            var session = new GameSession(configuration, new SeededRandom(3), 32);
            Assert.That(session.GetSnapshot().Round, Is.EqualTo(1));
        }

        /// <summary>
        /// 下半部分分区（Boss、回合规则、开关、JSON）的文案与开关集合。
        /// 输入：无；输出：无。
        /// 为什么逐条断言：这些分区在 1080p 下需要滚动才能看到，
        /// 用断言固定“有哪些字段、开关叫什么”比截图更容易在回归时发现问题。
        /// </summary>
        [Test]
        public void LowerSectionsExposeExpectedFieldsAndSwitches()
        {
            Open(Document());

            var bossTexts = CollectLabels(_root.Q<VisualElement>("debug-boss"));
            Assert.That(bossTexts, Does.Contain("AI 阵亡补卡间隔（回合）"));
            Assert.That(bossTexts, Does.Contain("玩家控制 Boss"));
            Assert.That(bossTexts, Does.Contain("攻击距离"));

            var turnTexts = CollectLabels(_root.Q<VisualElement>("debug-turn"));
            Assert.That(turnTexts, Does.Contain("每回合抽牌数"));
            Assert.That(turnTexts, Does.Contain("手牌上限"));
            Assert.That(turnTexts, Does.Contain("每回合出牌上限"));
            Assert.That(turnTexts, Does.Contain("Boss 每回合放牌数（仅手动模式）"));
            Assert.That(turnTexts, Does.Contain("先手"));

            var toggleTexts = CollectLabels(_root.Q<VisualElement>("debug-toggles"));
            Assert.That(toggleTexts, Does.Contain("资源跨回合累积"));
            Assert.That(toggleTexts, Does.Contain("刚上场的单位当回合不能行动"));
            Assert.That(toggleTexts, Does.Contain("同列/同行单位遮挡攻击"));
            Assert.That(toggleTexts, Does.Contain("相邻己方卡牌可互换位置"));
            // 已确认删除的规则不得以任何形式回到界面上。
            Assert.That(toggleTexts, Has.None.Contains("反击"));
            Assert.That(toggleTexts, Has.None.Contains("斜向"));

            Assert.That(_root.Q<Button>("debug-export-button").text, Is.EqualTo("导出到下方"));
            Assert.That(_root.Q<Button>("debug-import-button").text, Is.EqualTo("从下方导入"));
        }

        /// <summary>收集容器内全部标签文本。输入：容器；输出：文本列表。</summary>
        static System.Collections.Generic.List<string> CollectLabels(VisualElement container)
            => container.Query<Label>().ToList().Select(l => l.text).ToList();

        /// <summary>取卡组摘要文本。输入：无；输出：标签。</summary>
        Label DeckSummary()
            => _root.Query<Label>().ToList().First(l => l.ClassListContains("cfg-deck-summary"));

        /// <summary>按字段标签查找整数输入控件。输入：标签文本；输出：控件（未找到为 null）。</summary>
        IntegerField FindIntegerField(string label)
        {
            foreach (var field in _root.Query<IntegerField>().ToList())
                if (HasLabel(field, label)) return field;
            return null;
        }

        /// <summary>按字段标签查找下拉控件。输入：标签文本；输出：控件（未找到为 null）。</summary>
        DropdownField FindDropdownField(string label)
        {
            foreach (var field in _root.Query<DropdownField>().ToList())
                if (HasLabel(field, label)) return field;
            return null;
        }

        /// <summary>判断控件是否带有指定文本的字段标签。输入：控件与标签文本；输出：是否匹配。</summary>
        static bool HasLabel(VisualElement field, string label)
        {
            var parent = field.parent;
            if (parent == null) return false;
            foreach (var child in parent.Children())
                if (child is Label text && text.ClassListContains("cfg-field-label") && text.text == label) return true;
            return false;
        }
    }
}
