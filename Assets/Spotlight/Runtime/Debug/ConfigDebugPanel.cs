// 对局配置面板（表单式）：按分区逐项编辑棋盘、家、卡组、逐卡种属性、Boss 卡、回合规则与开关。
// 设计依据是既有的网页版配置界面：字段分组与文案保持一致，但已确认删除的“反击”“斜移”开关不再出现。
// 编辑直接写回内存草稿；“应用并重开”会先校验并创建候选对局，失败时保留原对局与草稿。
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace Spotlight
{
    /// <summary>
    /// 配置面板控制器。
    /// 输入：UXML 中的面板骨架（由 UIDocument 提供）与 GameBootstrap；
    /// 输出：写回草稿的字段编辑、实时校验结果，以及在成功“应用并重开”时请求替换对局。
    /// 为什么用表单而不是 JSON 文本框：策划需要快速调整数值，
    /// JSON 只能作为“导入导出通道”保留；面板打开期间由规则层输入闸门屏蔽棋盘操作。
    /// </summary>
    public sealed class ConfigDebugPanel : MonoBehaviour
    {
        /// <summary>界面文档，提供面板骨架。</summary>
        [SerializeField] UIDocument _document;
        /// <summary>对局引导器，用于取当前配置资产与请求重开。</summary>
        [SerializeField] GameBootstrap _bootstrap;

        /// <summary>本次运行会话内保留的草稿；关闭面板不会清空。</summary>
        DebugConfigDraft _draft;
        /// <summary>面板是否已打开。</summary>
        bool _open;
        /// <summary>正在重建界面，用于抑制重建期间的重复重入。</summary>
        bool _rebuilding;

        /// <summary>面板根元素（遮罩）。</summary>
        VisualElement _panel;
        /// <summary>棋盘分区容器。</summary>
        VisualElement _boardContainer;
        /// <summary>家分区容器。</summary>
        VisualElement _homeContainer;
        /// <summary>卡组摘要容器。</summary>
        VisualElement _deckContainer;
        /// <summary>逐卡种分区容器。</summary>
        VisualElement _cardsContainer;
        /// <summary>Boss 分区容器。</summary>
        VisualElement _bossContainer;
        /// <summary>回合规则分区容器。</summary>
        VisualElement _turnContainer;
        /// <summary>开关分区容器。</summary>
        VisualElement _toggleContainer;
        /// <summary>卡组摘要文本。</summary>
        Label _deckSummary;
        /// <summary>校验错误文本。</summary>
        Label _errorLabel;
        /// <summary>“应用并重开”按钮（校验失败时禁用）。</summary>
        Button _applyButton;
        /// <summary>JSON 编辑框。</summary>
        TextField _json;
        /// <summary>JSON 区域状态文本。</summary>
        Label _jsonStatus;

        /// <summary>面板当前是否打开。输入：无；输出：打开状态。</summary>
        public bool IsOpen => _open;

        /// <summary>
        /// 绑定界面元素与事件。
        /// 输入：Bootstrap；输出：无。
        /// 非编辑器/非 Development Build 时整个面板会被移除，保证正式运行包没有调试入口。
        /// </summary>
        public void Bind(GameBootstrap bootstrap)
        {
            _bootstrap = bootstrap != null ? bootstrap : _bootstrap;
            if (_document == null) _document = GetComponent<UIDocument>();
            if (_document == null) { Debug.LogWarning("ConfigDebugPanel：未找到 UIDocument"); return; }
            BindTo(_document.rootVisualElement, bootstrap);
        }

        /// <summary>
        /// 按给定根元素装配面板。
        /// 输入：界面根元素（来自 UIDocument 或 UXML 实例）与 Bootstrap；
        /// 输出：无。
        /// 为什么单独暴露：EditMode 测试可以直接实例化 UXML 验证表单生成，
        /// 不需要创建场景、PanelSettings 或进入 Play 模式。
        /// </summary>
        internal void BindTo(VisualElement root, GameBootstrap bootstrap)
        {
            if (bootstrap != null) _bootstrap = bootstrap;
            if (root == null) { Debug.LogWarning("ConfigDebugPanel：界面根元素为空"); return; }
            _panel = root.Q<VisualElement>("debug-panel");
            if (_panel == null) { Debug.LogWarning("ConfigDebugPanel：界面缺少 debug-panel"); return; }
            // 双重保险：USS 已默认 display:none，这里再显式设一次。
            _panel.style.display = DisplayStyle.None;
            _open = false;
            if (!DebugSupport.IsDebugAvailable) { _panel.RemoveFromHierarchy(); return; }

            _boardContainer = root.Q<VisualElement>("debug-board");
            _homeContainer = root.Q<VisualElement>("debug-home");
            _deckContainer = root.Q<VisualElement>("debug-deck");
            _cardsContainer = root.Q<VisualElement>("debug-cards");
            _bossContainer = root.Q<VisualElement>("debug-boss");
            _turnContainer = root.Q<VisualElement>("debug-turn");
            _toggleContainer = root.Q<VisualElement>("debug-toggles");
            _deckSummary = null;
            _errorLabel = root.Q<Label>("debug-error");
            _applyButton = root.Q<Button>("debug-apply-button");
            _json = root.Q<TextField>("debug-json");
            _jsonStatus = root.Q<Label>("debug-json-status");

            Hook("debug-close-button", Close);
            Hook("debug-reset-button", ResetToAsset);
            Hook("debug-apply-button", ApplyAndRestart);
            Hook("debug-export-button", ExportJson);
            Hook("debug-import-button", ImportJson);
        }

        /// <summary>给按钮挂接回调。输入：元素名与动作；输出：无（元素缺失时静默跳过）。</summary>
        void Hook(string name, Action action)
        {
            var button = _panel?.Q<Button>(name);
            if (button != null) button.clicked += action;
        }

        /// <summary>
        /// 打开面板。
        /// 输入：无；输出：无。
        /// 首次使用时从正式配置创建草稿，此后保留本次运行内的编辑结果。
        /// </summary>
        public void Open()
        {
            if (!DebugSupport.IsDebugAvailable || _panel == null) return;
            if (_draft == null) _draft = new DebugConfigDraft(_bootstrap != null ? _bootstrap.CurrentAsset : null);
            ShowPanel();
        }

        /// <summary>
        /// 用指定草稿打开面板。
        /// 输入：草稿；输出：无。
        /// 为什么单独暴露：EditMode 测试可以在不依赖配置资产与场景的情况下验证表单生成与字段绑定。
        /// </summary>
        internal void Open(DebugConfigDraft draft)
        {
            if (draft != null) _draft = draft;
            ShowPanel();
        }

        /// <summary>显示面板并重建表单。输入：无；输出：无。</summary>
        void ShowPanel()
        {
            _open = true;
            if (_panel != null) _panel.style.display = DisplayStyle.Flex;
            _bootstrap?.SetModalOpen(true);
            Rebuild();
        }

        /// <summary>关闭面板（保留草稿）。输入：无；输出：无。</summary>
        public void Close()
        {
            _open = false;
            if (_panel != null) _panel.style.display = DisplayStyle.None;
            _bootstrap?.SetModalOpen(false);
        }

        /// <summary>
        /// 重建整个表单。
        /// 输入：无；输出：无。
        /// 为什么整体重建而不是局部刷新：新增卡种、启用覆盖升级等会改变分区结构，
        /// 逐字段比对结构远比重新生成几十个控件复杂，而重建成本在点击频率下可以忽略。
        /// </summary>
        void Rebuild()
        {
            if (_draft == null || _rebuilding) return;
            _rebuilding = true;
            // 表单会被整体重建，旧字段元素即将丢弃，先清空写回动作登记表避免其持有失效引用。
            ConfigFieldBuilder.ClearRegistry();
            try
            {
                var document = _draft.Document;
                // 配置资产缺失时草稿可能只有空文档；此时只保留 JSON 区域，
                // 让策划仍能通过“从下方导入”恢复一份可用配置，而不是在表单构建时抛空引用。
                if (document == null || document.rules == null || document.rules.Length == 0
                    || document.homes == null || document.homes.Length < 2
                    || document.cards == null || document.cards.Length == 0
                    || document.presentation == null || document.presentation.Length == 0)
                {
                    Clear(_boardContainer, _homeContainer, _deckContainer, _cardsContainer, _bossContainer, _turnContainer, _toggleContainer);
                    if (_deckSummary != null) _deckSummary.text = string.Empty;
                    if (_errorLabel != null)
                    {
                        _errorLabel.text = "没有可用的配置：请先执行 Tools/Spotlight/编译全部配置，或在下方粘贴 JSON 后点击“从下方导入”。";
                        _errorLabel.style.display = DisplayStyle.Flex;
                    }
                    if (_applyButton != null) _applyButton.SetEnabled(false);
                    if (_json != null) _json.value = _draft.ExportJson();
                    return;
                }
                Clear(_boardContainer, _homeContainer, _deckContainer, _cardsContainer, _bossContainer, _turnContainer);
                BuildBoard(document);
                BuildHome(document);
                BuildDeck(document);
                BuildCards(document);
                BuildBoss(document);
                BuildTurn(document);
                BuildToggles(document);
                if (_json != null) _json.value = _draft.ExportJson();
                RefreshSummaryAndValidation();
            }
            finally
            {
                _rebuilding = false;
            }
        }

        /// <summary>清空若干容器。输入：容器列表；输出：无。</summary>
        static void Clear(params VisualElement[] containers)
        {
            foreach (var container in containers) container?.Clear();
        }

        /// <summary>字段变更回调：刷新卡组摘要与校验状态。输入：无；输出：无。</summary>
        void Changed()
        {
            if (_rebuilding) return;
            RefreshSummaryAndValidation();
        }

        /// <summary>结构性变更回调：字段数量或可见性发生变化时整体重建。输入：无；输出：无。</summary>
        void ChangedStructure()
        {
            if (_rebuilding) return;
            Rebuild();
        }

        /// <summary>构建“棋盘”分区。输入：草稿文档；输出：无。</summary>
        void BuildBoard(ConfigDocument document)
        {
            var rules = document.rules[0];
            var section = ConfigFieldBuilder.Section("棋盘", out var content, ConfigFieldBuilder.Grid(
                ConfigFieldBuilder.Number("行数（纵向，默认 5）", () => rules.rows, value => { rules.rows = value; Changed(); }, 1, 32),
                ConfigFieldBuilder.Number("列数（横向，默认 10）", () => rules.columns, value => { rules.columns = value; Changed(); }, 2, 32)));
            content.Add(ConfigFieldBuilder.Note("默认 5 行 × 10 列。双方家位于左右边缘中间行；偶数行取中间靠下的一行。家占 1 格。"));
            _boardContainer.Add(section);
        }

        /// <summary>构建“家”分区。输入：草稿文档；输出：无。</summary>
        void BuildHome(ConfigDocument document)
        {
            var rules = document.rules[0];
            var player = document.homes[0].owner == "player" ? document.homes[0] : document.homes[1];
            var boss = document.homes[0].owner == "boss" ? document.homes[0] : document.homes[1];
            var section = ConfigFieldBuilder.Section("家（基地，核心与胜负条件）", out var content, ConfigFieldBuilder.Grid(
                ConfigFieldBuilder.Choice("玩家所在边缘",
                    new[] { ("left", "左侧（Boss 在右侧）"), ("right", "右侧（Boss 在左侧）") },
                    () => rules.playerSide, value => { rules.playerSide = value; Changed(); }),
                ConfigFieldBuilder.Number("玩家家 血量", () => player.hp, value => { player.hp = value; Changed(); }, 1),
                ConfigFieldBuilder.Number("Boss 家 血量", () => boss.hp, value => { boss.hp = value; Changed(); }, 1),
                ConfigFieldBuilder.Number("玩家家每回合产出", () => player.produce, value => { player.produce = value; Changed(); }, 0)));
            content.Add(ConfigFieldBuilder.Note("家没有攻击力，每个己方回合可在所属边缘前两列内上下左右移动一格，不占出牌次数。不能斜移或进入占位格；家移动不会改变部署区域。打空对方家即获胜。"));
            _homeContainer.Add(section);
        }

        /// <summary>构建“卡组”摘要分区。输入：草稿文档；输出：无。</summary>
        void BuildDeck(ConfigDocument document)
        {
            var section = new VisualElement();
            section.AddToClassList("cfg-section");
            var head = new VisualElement();
            head.AddToClassList("cfg-section-head");
            var title = new Label("卡组");
            title.AddToClassList("cfg-section-title");
            head.Add(title);
            section.Add(head);
            _deckSummary = new Label();
            _deckSummary.AddToClassList("cfg-deck-summary");
            section.Add(_deckSummary);
            section.Add(ConfigFieldBuilder.Note("在下方逐种设置卡组数量，0 表示不加入卡组。新增卡默认 0 张。"));
            _deckContainer.Add(section);
        }

        /// <summary>
        /// 构建三个逐卡种分区（资源卡 / 玩家战斗卡 / 特殊卡）。
        /// 输入：草稿文档；输出：无。
        /// </summary>
        void BuildCards(ConfigDocument document)
        {
            BuildCardCategory(document, "resource", "资源卡", "新增资源卡");
            BuildCardCategory(document, "attack", "玩家战斗卡", "新增战斗卡");
            BuildCardCategory(document, "special", "特殊卡", "新增特殊卡");
            _cardsContainer.Add(ConfigFieldBuilder.Note("移动距离 0 表示不能移动，无数值上限。一次移动可转弯，不能穿过单位，移动后不能再攻击。"));
        }

        /// <summary>
        /// 构建一个卡种分类分区。
        /// 输入：草稿文档、分类、标题与新增按钮文案；输出：无。
        /// </summary>
        void BuildCardCategory(ConfigDocument document, string category, string title, string addLabel)
        {
            var add = new Button(() => { _draft.AddCardType(category); Rebuild(); }) { text = addLabel };
            add.AddToClassList("cfg-add-button");
            var section = ConfigFieldBuilder.Section(title, out var content, add);
            foreach (var card in document.cards)
            {
                if (card == null || card.category != category) continue;
                content.Add(BuildCardBox(card));
            }
            _cardsContainer.Add(section);
        }

        /// <summary>
        /// 构建单个卡种编辑框。
        /// 输入：卡行；输出：卡种编辑框元素。
        /// 字段顺序与网页版一致：名字、卡组数量、（资源卡）产出、维持费、生命、攻击、射程、移动，
        /// 之后按分类追加覆盖升级或四邻部署扩展等专属开关。
        /// </summary>
        VisualElement BuildCardBox(CardRow card)
        {
            var box = new VisualElement();
            box.AddToClassList("cfg-card");
            var title = new Label(string.IsNullOrWhiteSpace(card.name) ? "未命名卡牌" : card.name.Trim());
            title.AddToClassList("cfg-card-title");
            box.Add(title);

            var fields = new List<VisualElement>
            {
                ConfigFieldBuilder.Text("名字", () => card.name, value =>
                {
                    card.name = value;
                    title.text = string.IsNullOrWhiteSpace(value) ? "未命名卡牌" : value.Trim();
                    Changed();
                }),
                ConfigFieldBuilder.Number("卡组数量", () => _draft.GetDeckCount(card.id), value => { _draft.SetDeckCountValue(card.id, value); Changed(); }, 0, 999)
            };
            if (card.category == "resource")
            {
                fields.Add(ConfigFieldBuilder.Number("每回合产出资源", () => card.produce, value => { card.produce = value; SyncDerivedFlags(card); Changed(); }, 0));
            }
            fields.Add(ConfigFieldBuilder.Number("每回合消耗资源（维持费）", () => card.upkeep, value => { card.upkeep = value; SyncDerivedFlags(card); Changed(); }, 0));
            fields.Add(ConfigFieldBuilder.Number("生命", () => card.hp, value => { card.hp = value; Changed(); }, 1));
            fields.Add(ConfigFieldBuilder.Number("攻击力", () => card.atk, value => { card.atk = value; Changed(); }, 0));
            fields.Add(ConfigFieldBuilder.Number("攻击距离（曼哈顿）", () => card.range, value => { card.range = value; Changed(); }, 0));
            fields.Add(ConfigFieldBuilder.Number("移动距离", () => card.moveDistance, value => { card.moveDistance = value; Changed(); }, 0));
            box.Add(ConfigFieldBuilder.Grid(fields.ToArray()));

            if (card.category == "special")
            {
                box.Add(ConfigFieldBuilder.Toggle("仅可部署在己方前两列", () => card.deployHomeOnly, value => { card.deployHomeOnly = value; Changed(); }));
                box.Add(ConfigFieldBuilder.Toggle("部署时上下左右不能有同卡种单位", () => card.avoidAdjacentSameType, value => { card.avoidAdjacentSameType = value; Changed(); }));
                box.Add(ConfigFieldBuilder.Note("部署时上下左右不能有同卡种单位（含敌方）；不限制斜角和部署后的移动。"));
                box.Add(ConfigFieldBuilder.Toggle("允许己方战斗卡部署在上下左右空格", () => card.permitsAttackDeploy, value =>
                {
                    card.permitsAttackDeploy = value;
                    if (value) card.teleportComponent = true;
                    Changed();
                }));
            }
            else if (card.category == "resource")
            {
                box.Add(ConfigFieldBuilder.Toggle("需要覆盖升级其它资源卡", () => card.requiresUpgrade, value =>
                {
                    card.requiresUpgrade = value;
                    if (!value) card.upgradeFrom = string.Empty;
                    ChangedStructure();
                }));
                if (card.requiresUpgrade)
                {
                    var options = new List<(string Value, string Label)>();
                    foreach (var other in _draft.Document.cards)
                    {
                        if (other == null || other == card || other.category != "resource") continue;
                        options.Add((other.id, string.IsNullOrWhiteSpace(other.name) ? other.id : other.name.Trim()));
                    }
                    if (options.Count == 0) options.Add((string.Empty, "（没有其它资源卡）"));
                    box.Add(ConfigFieldBuilder.Grid(ConfigFieldBuilder.Choice("要覆盖升级的卡", options, () => card.upgradeFrom, value => { card.upgradeFrom = value; Changed(); })));
                    box.Add(ConfigFieldBuilder.Note("只能覆盖己方指定资源卡；原卡在部署时已进入墓地，被覆盖只移除场上单位，新单位满血并遵守刚上场不能行动的开关。"));
                }
            }
            return box;
        }

        /// <summary>
        /// 按当前数值修正派生开关。
        /// 输入：卡行；输出：无。
        /// 为什么需要：工作簿校验要求“资源字段存在才允许非零产出/维持费”，
        /// 面板自动同步 resourceComponent，避免策划需要理解组件开关。
        /// </summary>
        static void SyncDerivedFlags(CardRow card)
        {
            card.resourceComponent = card.produce != 0 || card.upkeep != 0;
            if (card.category == "special" && card.permitsAttackDeploy) card.teleportComponent = true;
        }

        /// <summary>构建“Boss 战斗卡”分区。输入：草稿文档；输出：无。</summary>
        void BuildBoss(ConfigDocument document)
        {
            var rules = document.rules[0];
            var boss = _draft.GetBossCard();
            var section = ConfigFieldBuilder.Section("Boss 战斗卡（Boss 无资源限制，维持费默认忽略）", out var content);
            content.Add(ConfigFieldBuilder.Toggle("玩家控制 Boss", () => rules.playerControlsBoss, value => { rules.playerControlsBoss = value; ChangedStructure(); }));
            if (boss != null)
            {
                content.Add(ConfigFieldBuilder.Grid(
                    ConfigFieldBuilder.Number("AI 阵亡补卡间隔（回合）", () => rules.bossRespawnDelay, value => { rules.bossRespawnDelay = value; Changed(); }, 1),
                    ConfigFieldBuilder.Text("名字", () => boss.name, value => { boss.name = value; Changed(); }),
                    ConfigFieldBuilder.Number("攻击", () => boss.atk, value => { boss.atk = value; Changed(); }, 0),
                    ConfigFieldBuilder.Number("生命", () => boss.hp, value => { boss.hp = value; Changed(); }, 1),
                    ConfigFieldBuilder.Number("攻击距离", () => boss.range, value => { boss.range = value; Changed(); }, 0),
                    ConfigFieldBuilder.Number("移动距离", () => boss.moveDistance, value => { boss.moveDistance = value; Changed(); }, 0)));
            }
            content.Add(ConfigFieldBuilder.Note("取消勾选启用 AI：首次 Boss 回合均匀部署三卡，玩家先手时须等玩家结束回合并完成弃牌后才部署。阵亡后按间隔补卡，自动移动、攻击并结束回合。第 N 回合阵亡，在第 N+间隔 回合的 Boss 行动开始时补回。AI 仅支持攻击类 Boss 卡种。"));
            _bossContainer.Add(section);
        }

        /// <summary>构建“回合规则”分区。输入：草稿文档；输出：无。</summary>
        void BuildTurn(ConfigDocument document)
        {
            var rules = document.rules[0];
            var section = ConfigFieldBuilder.Section("回合规则", out var content, ConfigFieldBuilder.Grid(
                ConfigFieldBuilder.Number("每回合抽牌数", () => rules.drawCount, value => { rules.drawCount = value; Changed(); }, 0),
                ConfigFieldBuilder.Number("手牌上限", () => rules.handLimit, value => { rules.handLimit = value; Changed(); }, 0),
                ConfigFieldBuilder.Number("每回合出牌上限", () => rules.playerPlays, value => { rules.playerPlays = value; Changed(); }, 0),
                ConfigFieldBuilder.Number("Boss 每回合放牌数（仅手动模式）", () => rules.bossPlays, value => { rules.bossPlays = value; Changed(); }, 0),
                ConfigFieldBuilder.Choice("先手", new[] { ("player", "玩家"), ("boss", "Boss") }, () => rules.firstSide, value => { rules.firstSide = value; Changed(); })));
            _turnContainer.Add(section);
        }

        /// <summary>
        /// 构建“开关”分区。
        /// 输入：草稿文档；输出：无。
        /// 只保留已确认存在的四个开关；反击与斜移已经彻底移除，不再出现在界面上。
        /// </summary>
        void BuildToggles(ConfigDocument document)
        {
            var rules = document.rules[0];
            var section = ConfigFieldBuilder.Section("开关", out var content);
            var toggles = new VisualElement();
            toggles.AddToClassList("cfg-toggles");
            toggles.Add(ConfigFieldBuilder.Toggle("资源跨回合累积", () => rules.accumulateResources, value => { rules.accumulateResources = value; Changed(); }));
            toggles.Add(ConfigFieldBuilder.Toggle("刚上场的单位当回合不能行动", () => rules.summoningSickness, value => { rules.summoningSickness = value; Changed(); }));
            toggles.Add(ConfigFieldBuilder.Toggle("同列/同行单位遮挡攻击", () => rules.lineBlocking, value => { rules.lineBlocking = value; Changed(); }));
            toggles.Add(ConfigFieldBuilder.Toggle("相邻己方卡牌可互换位置", () => rules.adjacentSwap, value => { rules.adjacentSwap = value; Changed(); }));
            content.Add(toggles);
            content.Add(ConfigFieldBuilder.Note("互换默认开启：选中卡牌后点击紫色高亮的己方卡牌，双方都消耗本回合行动，不消耗资源或出牌次数。家不参与；移动距离为 0 的卡牌也可互换。"));
            _toggleContainer.Add(section);
        }

        /// <summary>
        /// 刷新卡组摘要与校验状态。
        /// 输入：无；输出：无。
        /// 摘要让规划者一眼看到卡组结构；校验失败时禁用“应用并重开”并显示原因。
        /// </summary>
        void RefreshSummaryAndValidation()
        {
            if (_draft == null) return;
            var summary = _draft.GetDeckSummary();
            if (_deckSummary != null)
                _deckSummary.text = $"共 {summary.Total} 张 → 资源卡 {summary.Resource} 张 / 战斗卡 {summary.Combat} 张 / 特殊卡 {summary.Special} 张";
            var errors = _draft.Validate();
            if (_errorLabel != null)
            {
                var text = errors.Count == 0 ? string.Empty : string.Join("\n", errors);
                _errorLabel.text = text;
                _errorLabel.style.display = errors.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            }
            if (_applyButton != null) _applyButton.SetEnabled(errors.Count == 0);
        }

        /// <summary>恢复正常配置内容。输入：无；输出：无。</summary>
        void ResetToAsset()
        {
            _draft?.ResetToAsset(_bootstrap != null ? _bootstrap.CurrentAsset : null);
            if (_jsonStatus != null) _jsonStatus.text = "已恢复为 Excel 编译产物。";
            Rebuild();
        }

        /// <summary>导出草稿 JSON 到编辑框。输入：无；输出：无。</summary>
        void ExportJson()
        {
            if (_draft == null) return;
            if (_json != null) _json.value = _draft.ExportJson();
            if (_jsonStatus != null) _jsonStatus.text = "已导出当前草稿 JSON。";
        }

        /// <summary>
        /// 从编辑框导入 JSON。
        /// 输入：无（读取界面文本）；输出：无。
        /// 成功时提示被移除的废弃字段，失败时回滚到导入前的草稿并显示原因。
        /// </summary>
        void ImportJson()
        {
            if (_draft == null || _json == null) return;
            var previous = _draft.ExportJson();
            if (!_draft.Import(_json.value, out var error, out var notices))
            {
                _draft.Import(previous, out _, out _);
                if (_jsonStatus != null) _jsonStatus.text = "导入失败：" + error;
                RefreshSummaryAndValidation();
                return;
            }
            var text = new StringBuilder("导入成功。");
            foreach (var notice in notices) text.Append('\n').Append("提示：").Append(notice);
            if (_jsonStatus != null) _jsonStatus.text = text.ToString();
            Rebuild();
        }

        /// <summary>
        /// 应用草稿并重开。
        /// 输入：无；输出：无。
        /// 行为：先校验并创建候选对局，成功才替换当前对局并关闭面板；失败保留原对局与草稿。
        /// </summary>
        void ApplyAndRestart()
        {
            if (_draft == null) return;
            if (!_draft.TryCreateConfiguration(out var configuration, out var error))
            {
                if (_errorLabel != null)
                {
                    _errorLabel.text = "应用失败（原对局与草稿保留）：" + error;
                    _errorLabel.style.display = DisplayStyle.Flex;
                }
                return;
            }
            _bootstrap?.RestartWith(configuration);
            Close();
        }
    }
}
