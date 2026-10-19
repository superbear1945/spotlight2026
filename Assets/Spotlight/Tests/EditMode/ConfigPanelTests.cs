// 对局配置面板的 EditMode 测试。
// 覆盖：导入草稿后的表单生成、字段编辑写回草稿、数值夹紧、抽牌堆摘要统计、
// 校验失败禁用“应用并重开”、空配置的降级表现，以及草稿层的新增卡种与强制牌堆设置。
// 测试直接实例化 UXML 并注入草稿，不创建场景、不需要 PanelSettings，也不模拟点击事件。
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Spotlight;
using UnityEngine;
using UnityEngine.UIElements;

namespace Spotlight.Tests
{
    /// <summary>配置面板表单测试。输入：UXML 实例与内存草稿；输出：NUnit 断言结果。</summary>
    public class ConfigPanelTests
    {
        /// <summary>面板界面资产路径（调试面板已从 HUD 的 Spotlight.uxml 拆出，仅编辑器加载）。</summary>
        const string UxmlPath = ConfigDebugPanel.PanelUxmlPath;

        /// <summary>测试用界面根元素。</summary>
        VisualElement _root;
        /// <summary>被测试的配置面板。</summary>
        ConfigDebugPanel _panel;
        /// <summary>承载面板组件的临时对象。</summary>
        GameObject _host;
        /// <summary>保存测试前的剪贴板文本，避免导入导出测试覆盖用户内容。</summary>
        string _previousClipboard;

        /// <summary>装配界面实例与面板组件。输入：无；输出：无。</summary>
        [SetUp]
        public void SetUp()
        {
            _previousClipboard = GUIUtility.systemCopyBuffer;
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
            GUIUtility.systemCopyBuffer = _previousClipboard;
            if (_host != null) Object.DestroyImmediate(_host);
            _root = null;
            _panel = null;
        }

        /// <summary>
        /// 构造一份最小可用配置文档。
        /// 输入：无；输出：文档。
        /// 说明：Deck 表已废除，抽牌堆改由 Rules 的张数与阶级比例推导；
        /// 夹具额外放一张初级填充卡，使“把矿机改成中级”后初级阶级仍有可携带卡种（否则新校验会拒绝该改动）。
        /// </summary>
        internal static ConfigDocument Document()
        {
            return new ConfigDocument
            {
                cards = new[]
                {
                    new CardRow { tier = "basic", id = "miner", name = "矿机", category = "resource", hp = 2, produce = 1, resourceComponent = true, viewKey = "default" },
                    new CardRow { tier = "intermediate", id = "soldier", name = "士兵", category = "attack", hp = 3, atk = 1, range = 1, moveDistance = 1, upkeep = 1, resourceComponent = true, viewKey = "default" },
                    new CardRow { tier = "advanced", id = "gate", name = "星门", category = "special", hp = 2, moveDistance = 1, upkeep = 4, resourceComponent = true, teleportComponent = true, permitsAttackDeploy = true, deployHomeOnly = true, avoidAdjacentSameType = true, viewKey = "default" },
                    new CardRow { tier = "none", id = "boss", name = "Boss敌兵", category = "attack", hp = 3, atk = 1, range = 1, moveDistance = 1, viewKey = "default" },
                    new CardRow { tier = "none", id = "spawner", name="刷怪卡", category="special", hp=1, viewKey="default" , canSwap = false, spawnComponent = true, spawnCardType = "boss" },
                    new CardRow { tier = "none", id = "phaseGuard", name="测试护卫舰", category="attack", hp=7, atk=1, range=2, moveDistance=1, viewKey="default" },
                    new CardRow { tier = "none", id = "warship", name="战舰", category="attack", hp=15, atk=2, range=1, moveDistance=1, canAttackResources=false, spawnComponent=true, spawnCardType="boss", viewKey="default" },
                    // 仅用于满足“初级至少一种可携带卡种”的校验，不参与任何断言。
                    new CardRow { tier = "basic", id = "miner2", name = "备用矿机", category = "resource", hp = 1, produce = 0, resourceComponent = true, viewKey = "default" }
                },
                rules = new[]
                {
                    new RulesRow { drawPileSize = 24, tierRatioBasic = 70, tierRatioIntermediate = 20, tierRatioAdvanced = 10, maxTypesBasic = 5, maxTypesIntermediate = 3, maxTypesAdvanced = 2, bossSpawnerAttackThreshold = 1000, bossSpawnerCountFromColumn = 4, bossSpawnerUnitThreshold = 2, bossSpawnerDeployFromColumn = 5, bossSpawnerCardType = "spawner", bossPhaseTwoHp = 1, bossWarshipColumn = 1, bossWarshipCardType = "warship", bossPhaseThreeFrontCardType = "phaseGuard", bossPhaseThreeRearCardType = "phaseGuard", bossBombInterval = 10, bossBombDamage = 3, bossBombColumn = 5, rows = 5, columns = 10, homeRegionWidth = 2, playerPlays = 5, bossPlays = 1, drawCount = 5, handLimit = 5, initialResources = 0, bossRespawnDelay = 3, playerSide = "left", firstSide = "player", bossCardType = "boss", playerControlsBoss = false, accumulateResources = true, summoningSickness = true, adjacentSwap = true }
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

        /// <summary>
        /// 调用按钮绑定的面板动作。输入：动作方法名；输出：无。
        /// 未挂载面板没有实际事件循环，沿用反射调用约定以验证生产回调而不开放额外运行时 API。
        /// </summary>
        void InvokePanelAction(string method)
        {
            var info = typeof(ConfigDebugPanel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(info, Is.Not.Null);
            info.Invoke(_panel, null);
        }

        /// <summary>
        /// 大配置经剪贴板完整往返且不写入界面文本。输入：多卡种隔离文档；输出：全文一致、
        /// 末尾字段保留及界面无 JSON 文本框的断言，防止用截断配置掩盖顶点上限问题。
        /// </summary>
        [Test]
        public void LargeJsonClipboardRoundTripPreservesAllDataWithoutRenderingJson()
        {
            var document = Document();
            var cards = document.cards.ToList();
            for (var i = 0; i < 32; i++)
            {
                var card = JsonUtility.FromJson<CardRow>(JsonUtility.ToJson(document.cards[1]));
                card.id = "clipboard_" + i;
                card.name = "剪贴板测试卡" + i;
                card.tier = "none";
                card.hp = 100 + i;
                cards.Add(card);
            }
            document.cards = cards.ToArray();
            GUIUtility.systemCopyBuffer = "opening must not replace clipboard";
            var draft = Open(document);
            Assert.That(GUIUtility.systemCopyBuffer, Is.EqualTo("opening must not replace clipboard"));
            var expected = draft.ExportJson();
            Assert.That(expected.Count(c => !char.IsWhiteSpace(c)), Is.GreaterThan(20000));
            Assert.That(_root.Q<TextField>("debug-json"), Is.Null);

            InvokePanelAction("ExportJson");
            Assert.That(GUIUtility.systemCopyBuffer, Is.EqualTo(expected));
            draft.Document.cards.Last().hp = 999;
            InvokePanelAction("ImportJson");
            Assert.That(draft.ExportJson(), Is.EqualTo(expected));
            Assert.That(draft.Document.cards.Last().hp, Is.EqualTo(131));
            Assert.That(_root.Q<Label>("debug-json-status").text, Does.Contain("导入成功"));
            Assert.That(_root.Q<TextField>("debug-json"), Is.Null);
            _panel.Close();
            _panel.Open(draft);
            Assert.That(GUIUtility.systemCopyBuffer, Is.EqualTo(expected));
        }

        /// <summary>
        /// 空文本、错误类型和旧配置缺字段导入失败时保留草稿与强制牌堆。
        /// 输入：三类无效剪贴板内容；输出：原文档身份、数值、强制牌堆及错误提示断言。
        /// </summary>
        [TestCase("")]
        [TestCase("{\"rules\":[{\"adjacentSwap\":\"yes\"}]}")]
        [TestCase("{\"rules\":[{}]}")]
        public void FailedClipboardImportPreservesDraftAndForcedDeck(string clipboard)
        {
            var draft = Open(Document());
            draft.SetForcedDeckCount("soldier", 8);
            var before = draft.ExportJson();
            var document = draft.Document;
            GUIUtility.systemCopyBuffer = clipboard;
            InvokePanelAction("ImportJson");
            Assert.That(draft.Document, Is.SameAs(document));
            Assert.That(draft.ExportJson(), Is.EqualTo(before));
            Assert.That(draft.GetForcedDeckCount("soldier"), Is.EqualTo(8));
            Assert.That(GUIUtility.systemCopyBuffer, Is.EqualTo(clipboard));
            Assert.That(_root.Q<Label>("debug-json-status").text, Does.StartWith("导入失败："));
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

        /// <summary>
        /// “抽牌堆”分区固定展示张数、三阶级比例与三阶级携带上限，并对每个卡种给一个调试用强制牌堆输入框；
        /// 逐卡种“卡组数量”字段已随 Deck 表一起废除。
        /// 输入：无；输出：无。
        /// 为什么需要：这些字段是策划唯一能改构牌规则的地方，字段名或数量变动必须在回归里被发现。
        /// </summary>
        [Test]
        public void DeckSectionExposesDrawPileFieldsInsteadOfCardDeckCounts()
        {
            Open(Document());
            var labels = CollectLabels(_root.Q<VisualElement>("debug-deck"));
            Assert.That(labels, Does.Contain("抽牌堆张数（默认 24，至少 3）"));
            Assert.That(labels, Does.Contain("初级比例 %（默认 70）"));
            Assert.That(labels, Does.Contain("中级比例 %（默认 20）"));
            Assert.That(labels, Does.Contain("高级比例 %（默认 10）"));
            Assert.That(labels, Does.Contain("初级携带上限（种）"));
            Assert.That(labels, Does.Contain("中级携带上限（种）"));
            Assert.That(labels, Does.Contain("高级携带上限（种）"));
            Assert.That(labels, Does.Contain("矿机（强制）"), "每个卡种应有一个调试用强制牌堆输入框");
            Assert.That(_root.Query<Label>().ToList().Any(l => l.text == "卡组数量"), Is.False, "逐卡种卡组数量字段已废除");
        }

        /// <summary>
        /// 抽牌堆摘要按“张数 + 三阶级比例”统计（向下取整，缺额全部给初级）。
        /// 输入：无；输出：无。
        /// 为什么断言这三个数字：24 张、70/20/10 的默认配置下，向下取整得 16/4/2，缺的 2 张全部给初级，最终 18/4/2。
        /// </summary>
        [Test]
        public void DrawPileSummaryFollowsTierRatios()
        {
            Open(Document());
            var summary = DeckSummary();
            Assert.That(summary.text, Does.Contain("抽牌堆 24 张"));
            Assert.That(summary.text, Does.Contain("初级 18 张"));
            Assert.That(summary.text, Does.Contain("中级 4 张"));
            Assert.That(summary.text, Does.Contain("高级 2 张"));
        }

        /// <summary>
        /// 阶级下拉写回草稿并进入新配置，验证编辑、持久化和分类独立性。
        /// 输入：无；输出：无。
        /// 为何不再断言摘要跟着变：抽牌堆摘要由张数与比例推导，不再按逐卡种张数统计，
        /// 所以改单个卡种的阶级不应改变摘要（这一点在此显式断言）。
        /// </summary>
        [Test]
        public void EditingTierWritesBackAndReachesConfiguration()
        {
            var draft = Open(Document());
            var box = _root.Query<VisualElement>(className: "cfg-card").ToList()
                .Single(e => e.Q<Label>(className: "cfg-card-title").text == "矿机");
            var field = box.Q<DropdownField>();
            Assert.That(field.value, Is.EqualTo("初级"));
            field.value = "中级";
            ConfigFieldBuilder.ApplyCurrentValue(field);
            Assert.That(draft.FindCard("miner").tier, Is.EqualTo("intermediate"));
            Assert.That(draft.FindCard("miner").category, Is.EqualTo("resource"));
            Assert.That(DeckSummary().text, Does.Contain("抽牌堆 24 张"), "抽牌堆摘要由阶级比例推导，不应随单个卡种改阶级而变化");
            Assert.That(draft.TryCreateConfiguration(out var config, out var error), Is.True, error);
            Assert.That(config.Cards["miner"].Tier, Is.EqualTo(CardTier.Intermediate));
        }

        /// <summary>
        /// 旧 JSON 缺阶级时保留待修正提示，禁止应用；用户选择后可恢复，不静默显示初级。
        /// 输入：缺 tier 的文档；输出：下拉值、应用按钮与校验错误断言。
        /// 说明：原摘要里的“阶级待修正 N 张”已随逐卡种张数统计一起废除，
        /// 待修正状态改由下拉文案与校验错误（含 tier）同时反映。
        /// </summary>
        [Test]
        public void MissingTierStaysVisibleUntilExplicitlyRepaired()
        {
            var document = Document();
            document.cards[0].tier = null;
            var draft = Open(document);
            var field = _root.Query<DropdownField>().ToList().Single(f => f.value.StartsWith("请补齐阶级"));
            Assert.That(_root.Q<Button>("debug-apply-button").enabledSelf, Is.False);
            Assert.That(draft.Validate().Any(e => e.Contains("tier")), Is.True, "阶级待修正必须体现在校验错误里");
            field.value = "初级";
            ConfigFieldBuilder.ApplyCurrentValue(field);
            Assert.That(draft.Validate(), Is.Empty);
            Assert.That(_root.Q<Button>("debug-apply-button").enabledSelf, Is.True);
            Assert.That(DeckSummary().text, Does.Contain("抽牌堆 24 张"));
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

        /// <summary>三阶段分区提供引用和实际卡种参数编辑；控件写回独立草稿并进入新配置，不改变原文档。</summary>
        [Test]
        public void PhaseThreeGuardFieldsEditTheReferencedCard()
        {
            var document = BossGuardTests.Document();
            var draft = Open(document);
            var section = _root.Q<VisualElement>("boss-phase-three-guards");
            Assert.That(section, Is.Not.Null);
            Assert.That(FindDropdownField("前列护卫舰卡种"), Is.Not.Null);
            Assert.That(FindDropdownField("当前列护卫舰卡种"), Is.Not.Null);
            var box = section.Query<VisualElement>(className: "cfg-card").ToList().First();
            var labels = new[] { "生命", "攻击力", "攻击距离（曼哈顿）", "移动距离" };
            var values = new[] { 14, 5, 4, 0 };
            for (var i = 0; i < labels.Length; i++)
            {
                var field = box.Query<IntegerField>().ToList().Single(f => HasLabel(f, labels[i]));
                field.value = values[i];
                ConfigFieldBuilder.ApplyCurrentValue(field);
            }
            Assert.That(draft.TryCreateConfiguration(out var config, out var error), Is.True, error);
            var combat = config.Cards[config.Rules.BossPhaseThreeFrontCardType].Combat;
            Assert.That(new[] { combat.Hp, combat.Attack, combat.Range, combat.MoveDistance }, Is.EqualTo(values));
            Assert.That(document.cards.Single(c => c.id == "guard1").hp, Is.EqualTo(7));
        }

        /// <summary>炸弹三个字段可编辑并进入新配置；输入为独立调试草稿，不修改正式 Excel。</summary>
        [Test]
        public void BombFieldsWriteBackAndReachNewConfiguration()
        {
            var draft = Open(Document());
            var labels = new[] { "炸弹部署间隔（回合）", "炸弹总伤害", "炸弹部署列号" };
            var values = new[] { 4, 7, 2 };
            for (var i = 0; i < labels.Length; i++)
            {
                var field = FindIntegerField(labels[i]);
                Assert.That(field, Is.Not.Null, labels[i]);
                field.value = values[i];
                ConfigFieldBuilder.ApplyCurrentValue(field);
            }
            Assert.That(draft.TryCreateConfiguration(out var config, out var error), Is.True, error);
            Assert.That(config.Rules.BossBombInterval, Is.EqualTo(4));
            Assert.That(config.Rules.BossBombDamage, Is.EqualTo(7));
            Assert.That(config.Rules.BossBombColumn, Is.EqualTo(2));
        }

        /// <summary>刷怪技能的数值、卡种引用和生命编辑都进入只读配置；输入为隔离草稿，输出为配置断言。</summary>
        [Test]
        public void SpawnerFieldsWriteBackAndReachNewConfiguration()
        {
            var draft = Open(Document());
            var labels = new[] { "刷怪触发总攻击力（默认 12）", "攻击单位统计起始列（默认 4）", "攻击单位数量下限（默认 2）", "刷怪卡部署起始列（默认 5）", "刷怪卡生命（默认 1）" };
            var values = new[] { 15, 3, 4, 20, 6 };
            for (var i = 0; i < labels.Length; i++)
            {
                var field = FindIntegerField(labels[i]);
                Assert.That(field, Is.Not.Null, labels[i]);
                field.value = values[i];
                ConfigFieldBuilder.ApplyCurrentValue(field);
            }
            var fighter = FindDropdownField("刷怪卡生成战机卡种");
            fighter.index = fighter.choices.FindIndex(label => label.Contains("（soldier）"));
            ConfigFieldBuilder.ApplyCurrentValue(fighter);
            Assert.That(draft.TryCreateConfiguration(out var config, out var error), Is.True, error);
            Assert.That(config.Rules.BossSpawnerAttackThreshold, Is.EqualTo(15));
            Assert.That(config.Rules.BossSpawnerCountFromColumn, Is.EqualTo(3));
            Assert.That(config.Rules.BossSpawnerUnitThreshold, Is.EqualTo(4));
            Assert.That(config.Rules.BossSpawnerDeployFromColumn, Is.EqualTo(20));
            Assert.That(config.Cards[config.Rules.BossSpawnerCardType].Spawn.SpawnCardType, Is.EqualTo("soldier"));
            Assert.That(draft.FindCard(config.Rules.BossSpawnerCardType).hp, Is.EqualTo(6));
        }

        /// <summary>换选刷怪卡后生命字段跟随新引用；输入为新合法特殊卡，输出为字段写回及引用断言。</summary>
        [Test]
        public void SpawnerChoiceRebindsHealthWithoutEditingAnotherCard()
        {
            var document = Document();
            document.cards = document.cards.Concat(new[] { new CardRow { tier = "none", id = "nest", name = "新刷怪卡", category = "special", hp = 2, viewKey = "default", canSwap = false, spawnComponent = true, spawnCardType = "boss" } }).ToArray();
            var draft = Open(document);
            var choice = FindDropdownField("刷怪卡卡种");
            choice.index = choice.choices.FindIndex(label => label.Contains("（nest）"));
            ConfigFieldBuilder.ApplyCurrentValue(choice);
            var hp = FindIntegerField("刷怪卡生命（默认 1）");
            Assert.That(hp.value, Is.EqualTo(2));
            hp.value = 7;
            ConfigFieldBuilder.ApplyCurrentValue(hp);
            Assert.That(draft.Document.rules[0].bossSpawnerCardType, Is.EqualTo("nest"));
            Assert.That(draft.FindCard("nest").hp, Is.EqualTo(7));
            Assert.That(draft.FindCard("spawner").hp, Is.EqualTo(1));
            Assert.That(draft.TryCreateConfiguration(out _, out var error), Is.True, error);
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

        /// <summary>新增卡种使用 custom_分类_序号 命名、默认初级且不在强制牌堆里。输入：无；输出：无。</summary>
        [Test]
        public void AddCardTypeUsesStableNamingAndZeroCount()
        {
            var draft = new DebugConfigDraft(null);
            Assert.That(draft.Import(JsonUtility.ToJson(Document()), out _, out _), Is.True);
            var added = draft.AddCardType("resource");
            Assert.That(added.id, Is.EqualTo("custom_resource_1"));
            Assert.That(added.name, Is.EqualTo("新资源卡1"));
            Assert.That(added.tier, Is.EqualTo("basic"));
            Assert.That(draft.GetForcedDeckCount(added.id), Is.EqualTo(0));

            var second = draft.AddCardType("resource");
            Assert.That(second.id, Is.EqualTo("custom_resource_2"));
            Assert.That(second.name, Is.EqualTo("新资源卡2"));

            var attack = draft.AddCardType("attack");
            Assert.That(attack.id, Is.EqualTo("custom_attack_1"));
            Assert.That(attack.category, Is.EqualTo("attack"));
            Assert.That(draft.FindCard("custom_attack_1"), Is.SameAs(attack));
        }

        /// <summary>
        /// 强制牌堆逐键写回：负数夹紧到 0（表示不进牌堆），摘要改按强制牌堆统计。
        /// 输入：无；输出：无。
        /// 为什么需要：选卡界面只能勾卡种、不能自定张数，压测用的“30 张同一种”只能走这条调试通道。
        /// </summary>
        [Test]
        public void ForcedDeckCountOverridesDrawPileSummary()
        {
            var draft = new DebugConfigDraft(null);
            Assert.That(draft.Import(JsonUtility.ToJson(Document()), out _, out _), Is.True);
            draft.SetForcedDeckCount("soldier", 9);
            Assert.That(draft.GetForcedDeckCount("soldier"), Is.EqualTo(9));
            draft.SetForcedDeckCount("gate", -3);
            Assert.That(draft.GetForcedDeckCount("gate"), Is.EqualTo(0));
            Assert.That(draft.HasForcedDeck, Is.True);
            var pile = draft.GetDrawPileSummary();
            Assert.That(pile.Total, Is.EqualTo(9), "强制牌堆只有士兵 9 张");
            Assert.That(pile.Basic, Is.Zero);
            Assert.That(pile.Intermediate, Is.EqualTo(9));
            Assert.That(pile.Advanced, Is.Zero);
            draft.ClearForcedDeck();
            Assert.That(draft.HasForcedDeck, Is.False);
            Assert.That(draft.GetDrawPileSummary().Total, Is.EqualTo(24), "清空后恢复按比例推导");
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
            Assert.That(bossTexts, Does.Contain("炸弹部署间隔（回合）"));
            Assert.That(bossTexts, Does.Contain("炸弹总伤害"));
            Assert.That(bossTexts, Does.Contain("炸弹部署列号"));

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

            Assert.That(_root.Q<Button>("debug-export-button").text, Is.EqualTo("复制 JSON"));
            Assert.That(_root.Q<Button>("debug-import-button").text, Is.EqualTo("从剪贴板导入"));
        }

        /// <summary>
        /// 攻击类卡展示三种新能力开关；打开带数值的能力后出现数值字段并写入草稿；关闭后清零可通过校验。
        /// 输入：隔离草稿；输出：字段文案、配置值与非攻击卡不展示开关的断言。
        /// 为什么需要：这些开关直接决定正式卡是否获得能力，字段缺失或分类限制失效都会让策划静默改错。
        /// </summary>
        [Test]
        public void AttackCardsExposeNewAbilityTogglesAndClosingClearsValues()
        {
            var draft = Open(Document());
            var texts = CollectLabels(_root.Q<VisualElement>("debug-cards"));
            Assert.That(texts, Does.Contain("四邻空格限制：部署与移动/互换前后至少 N 格为空"));
            Assert.That(texts, Does.Contain("击杀恢复行动：直接攻击击杀后再行动一次"));
            Assert.That(texts, Does.Contain("伤害标记：直接攻击后使目标获得 1 层"));

            // 打开两个带数值的能力后，面板出现对应数值字段。
            var soldier = draft.FindCard("soldier");
            soldier.adjacentClearanceComponent = true;
            soldier.adjacentClearanceMinEmpty = 2;
            soldier.damageMarkComponent = true;
            soldier.damageMarkDamagePerStack = 1;
            _panel.Open(draft);
            var withValues = CollectLabels(_root.Q<VisualElement>("debug-cards"));
            Assert.That(withValues, Does.Contain("四邻最少空格数（1～4）"));
            Assert.That(withValues, Does.Contain("每层追加伤害"));
            Assert.That(draft.TryCreateConfiguration(out var configuration, out var error), Is.True, error);
            Assert.That(configuration.Cards["soldier"].AdjacentClearance.MinEmptyNeighbors, Is.EqualTo(2));
            Assert.That(configuration.Cards["soldier"].DamageMark.DamagePerStack, Is.EqualTo(1));

            // 关闭开关必须同时清零数值，否则集中校验会阻止“应用并重开”。
            soldier.adjacentClearanceComponent = false;
            soldier.adjacentClearanceMinEmpty = 0;
            soldier.damageMarkComponent = false;
            soldier.damageMarkDamagePerStack = 0;
            Assert.That(draft.Validate(), Is.Empty, string.Join(";", draft.Validate()));

            // 非攻击卡不展示“击杀恢复行动 / 伤害标记”开关（分类约束由集中校验兜底）。
            var gate = _root.Query<VisualElement>(className: "cfg-card").ToList()
                .First(c => c.Q<Label>(className: "cfg-card-title")?.text == "星门");
            var gateTexts = CollectLabels(gate);
            Assert.That(gateTexts, Has.None.Contains("击杀恢复行动：直接攻击击杀后再行动一次"));
            Assert.That(gateTexts, Has.None.Contains("伤害标记：直接攻击后使目标获得 1 层"));
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
