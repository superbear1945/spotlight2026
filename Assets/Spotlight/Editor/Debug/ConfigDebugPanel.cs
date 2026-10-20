// 【位置约定】本文件编进 Spotlight.Editor 程序集（仅编辑器编译）。
// 正式包与 Development Build 都不包含它的代码、界面骨架或类名——这也是本仓库唯一
// 能让调试面板彻底不进包的做法：Unity 会在构建时给运行层程序集写一份类型目录，
// 只靠 #if 只能把代码从 DLL 里去掉，类名仍会留在包内的类型目录里。
// 面板由 ConfigPanelInstaller 在进入 Play 时挂到引导器上（运行层通过 DebugSupport.PanelInstaller 回调）。
// 对局配置面板（表单式）：按分区逐项编辑棋盘、家、卡组、逐卡种属性、Boss 卡、回合规则与开关。
// 设计依据是既有的网页版配置界面：字段分组与文案保持一致，但已确认删除的“反击”“斜移”开关不再出现。
// 编辑直接写回内存草稿；“应用并重开”会先校验并创建候选对局，失败时保留原对局与草稿。
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

using Spotlight;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// 配置面板控制器。
    /// 输入：HUD 根元素与 GameBootstrap；
    /// 输出：写回草稿的字段编辑、实时校验结果，以及在成功“应用并重开”时请求替换对局。
    /// 为什么用表单而不是 JSON 文本框：策划需要快速调整数值，
    /// JSON 只能作为“导入导出通道”保留；面板打开期间由规则层输入闸门屏蔽棋盘操作。
    /// 为什么是普通类而不是 MonoBehaviour：本文件在 Editor 目录下（仅编辑器程序集），
    /// Unity 不允许给 GameObject 添加来自编辑器程序集的脚本（AddComponent 会报
    /// “it is an editor script”）。面板本身也不需要 Unity 的生命周期回调，
    /// 由 ConfigPanelInstaller 直接 `new` 出来并把 HUD 根节点传进来即可。
    /// </summary>
    public sealed class ConfigDebugPanel
    {
        /// <summary>对局引导器，用于取当前配置资产与请求重开。</summary>
        GameBootstrap _bootstrap;
        /// <summary>面板挂载的 HUD 根节点；重新绑定到新的根节点时会重建骨架。</summary>
        VisualElement _root;

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
        /// <summary>JSON 区域状态文本。</summary>
        Label _jsonStatus;

        /// <summary>面板当前是否打开。输入：无；输出：打开状态。</summary>
        public bool IsOpen => _open;

        /// <summary>面板所在的 HUD 根节点（安装器据此判断是否需要重新绑定）。</summary>
        internal VisualElement Root => _root;

        /// <summary>
        /// 绑定界面元素与事件。
        /// 输入：对局引导器与 HUD 根节点；输出：无。
        /// 由 ConfigPanelInstaller 在进入 Play 时调用；正式包不存在这段代码。
        /// </summary>
        public void Bind(GameBootstrap bootstrap, VisualElement root)
        {
            if (bootstrap != null) _bootstrap = bootstrap;
            if (root == null) { Debug.LogWarning("ConfigDebugPanel：界面根元素为空"); return; }
            if (!ReferenceEquals(_root, root)) _panel = null;
            _root = root;
            BindTo(root, bootstrap);
        }

        /// <summary>
        /// 在 HUD 根节点下创建面板骨架（已存在时直接返回）。
        /// 输入：HUD 根元素；输出：无。
        /// 为什么用代码搭骨架而不是 UXML 资产：只要骨架是个资产，就可能被某个序列化引用拉进
        /// 正式包（实测场景里挂着的调试面板组件就会把 UXML 一起打进去，拆包能读到整张表单）。
        /// 本方法连同整个文件都编在仅编辑器编译的 Spotlight.Editor 程序集里，
        /// 因此正式包里既没有资产也没有这些节点与文案。
        /// 幂等：GameBootstrap 重开局时可能再次调用 Bind，不能叠出两套面板。
        /// </summary>
        void EnsurePanelSkeleton(VisualElement root)
        {
            if (root.Q<VisualElement>("debug-panel") != null) return;

            var panel = new VisualElement { name = "debug-panel" };
            panel.AddToClassList("cfg-mask");
            // USS 的 .cfg-mask 已经是 display:none，这里再显式设一次，避免样式表未加载时面板一开局就盖住棋盘。
            panel.style.display = DisplayStyle.None;

            var card = new VisualElement();
            card.AddToClassList("cfg-panel");
            panel.Add(card);

            var head = new VisualElement();
            head.AddToClassList("cfg-head");
            card.Add(head);
            var headTop = new VisualElement();
            headTop.AddToClassList("cfg-head-top");
            head.Add(headTop);
            var title = new Label("⚙ 对局配置");
            title.AddToClassList("cfg-title");
            headTop.Add(title);
            var headActions = new VisualElement();
            headActions.AddToClassList("cfg-head-actions");
            headTop.Add(headActions);
            headActions.Add(new Button { name = "debug-reset-button", text = "恢复默认" });
            headActions.Add(new Button { name = "debug-close-button", text = "关闭" });
            var apply = new Button { name = "debug-apply-button", text = "应用并重开" };
            apply.AddToClassList("primary");
            headActions.Add(apply);
            var error = new Label { name = "debug-error" };
            error.AddToClassList("cfg-error");
            head.Add(error);

            var body = new VisualElement();
            body.AddToClassList("cfg-body");
            card.Add(body);
            var scroll = new ScrollView(ScrollViewMode.Vertical) { name = "debug-scroll" };
            scroll.AddToClassList("cfg-scroll");
            body.Add(scroll);

            scroll.Add(MakeNote("修改仅在“应用并重开”后生效。关闭后保留草稿，重新打开可继续编辑。"));
            // 七个分区容器由 Rebuild 按草稿结构填充；名字必须与 Unbind/Rebuild 里的查询一致。
            foreach (var containerName in new[] { "debug-board", "debug-home", "debug-deck", "debug-cards", "debug-boss", "debug-turn", "debug-toggles" })
                scroll.Add(new VisualElement { name = containerName });

            var jsonSection = new VisualElement();
            jsonSection.AddToClassList("cfg-section");
            var jsonTitle = new Label("JSON 导入 / 导出");
            jsonTitle.AddToClassList("cfg-section-title");
            jsonSection.Add(jsonTitle);
            jsonSection.Add(MakeNote("复制完整配置 JSON 后可粘贴到文本文件保存。导入前请先复制文件中的全部 JSON；导入只修改草稿，应用并重开后生效。"));
            var jsonActions = new VisualElement();
            jsonActions.AddToClassList("cfg-json-actions");
            jsonActions.Add(new Button { name = "debug-export-button", text = "复制 JSON" });
            jsonActions.Add(new Button { name = "debug-import-button", text = "从剪贴板导入" });
            jsonSection.Add(jsonActions);
            var jsonStatus = new Label { name = "debug-json-status" };
            jsonStatus.AddToClassList("cfg-note");
            jsonSection.Add(jsonStatus);
            scroll.Add(jsonSection);

            root.Add(panel);
        }

        /// <summary>
        /// 生成一条说明文本（沿用 UXML 时代的 .cfg-note 样式）。
        /// 输入：说明文案；输出：可直接加入容器的 Label。
        /// </summary>
        static Label MakeNote(string text)
        {
            var note = new Label(text);
            note.AddToClassList("cfg-note");
            return note;
        }

        /// <summary>
        /// 按给定根元素装配面板。
        /// 输入：界面根元素（来自 UIDocument 或测试构造的容器）与 Bootstrap；
        /// 输出：无。
        /// 为什么单独暴露：EditMode 测试可以在只有 UIDocument 根节点的环境下验证表单生成，
        /// 不需要创建场景、PanelSettings 或进入 Play 模式。
        /// </summary>
        internal void BindTo(VisualElement root, GameBootstrap bootstrap)
        {
            if (bootstrap != null) _bootstrap = bootstrap;
            if (root == null) { Debug.LogWarning("ConfigDebugPanel：界面根元素为空"); return; }
            _root = root;
            EnsurePanelSkeleton(root);
            _panel = root.Q<VisualElement>("debug-panel");
            if (_panel == null) { Debug.LogWarning("ConfigDebugPanel：界面缺少 debug-panel"); return; }
            // 双重保险：USS 已默认 display:none，这里再显式设一次。
            _panel.style.display = DisplayStyle.None;
            _open = false;

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
            if (_panel == null) return;
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
            // 走 SetDebugPanelOpen 而不是 SetModalOpen：运行层据此记住“还有面板开着”，
            // 关闭暂停面板时才不会顺带解除这里的输入屏蔽。
            _bootstrap?.SetDebugPanelOpen(true);
            Rebuild();
        }

        /// <summary>关闭面板（保留草稿）。输入：无；输出：无。</summary>
        public void Close()
        {
            _open = false;
            if (_panel != null) _panel.style.display = DisplayStyle.None;
            _bootstrap?.SetDebugPanelOpen(false);
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
                // 让策划仍能通过剪贴板导入恢复一份可用配置，而不是在表单构建时抛空引用。
                if (document == null || document.rules == null || document.rules.Length == 0
                    || document.homes == null || document.homes.Length < 2
                    || document.cards == null || document.cards.Length == 0
                    || document.presentation == null || document.presentation.Length == 0)
                {
                    Clear(_boardContainer, _homeContainer, _deckContainer, _cardsContainer, _bossContainer, _turnContainer, _toggleContainer);
                    if (_deckSummary != null) _deckSummary.text = string.Empty;
                    if (_errorLabel != null)
                    {
                        _errorLabel.text = "没有可用的配置：请先执行 Tools/Spotlight/编译全部配置，或复制配置 JSON 后点击“从剪贴板导入”。";
                        _errorLabel.style.display = DisplayStyle.Flex;
                    }
                    if (_applyButton != null) _applyButton.SetEnabled(false);
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

        /// <summary>
        /// 构建“抽牌堆”分区：张数、三阶级比例、三阶级携带上限与调试用强制牌堆。
        /// 输入：草稿文档；输出：无。
        /// 为什么这里不再逐卡种设置数量：Deck 表已废除，抽牌堆由“张数 + 比例”推导，
        /// 具体带哪些卡由玩家在开局选卡界面决定；逐卡种数量只作为调试压测手段保留。
        /// </summary>
        void BuildDeck(ConfigDocument document)
        {
            var rules = document.rules[0];
            var section = ConfigFieldBuilder.Section("抽牌堆（选卡与阶级比例）", out var content, ConfigFieldBuilder.Grid(
                ConfigFieldBuilder.Number("抽牌堆张数（默认 24，至少 3）", () => rules.drawPileSize, value => { rules.drawPileSize = value; Changed(); }, 3),
                ConfigFieldBuilder.Number("初级比例 %（默认 70）", () => rules.tierRatioBasic, value => { rules.tierRatioBasic = value; Changed(); }, 1, 100),
                ConfigFieldBuilder.Number("中级比例 %（默认 20）", () => rules.tierRatioIntermediate, value => { rules.tierRatioIntermediate = value; Changed(); }, 1, 100),
                ConfigFieldBuilder.Number("高级比例 %（默认 10）", () => rules.tierRatioAdvanced, value => { rules.tierRatioAdvanced = value; Changed(); }, 1, 100),
                ConfigFieldBuilder.Number("初级携带上限（种）", () => rules.maxTypesBasic, value => { rules.maxTypesBasic = value; Changed(); }, 1),
                ConfigFieldBuilder.Number("中级携带上限（种）", () => rules.maxTypesIntermediate, value => { rules.maxTypesIntermediate = value; Changed(); }, 1),
                ConfigFieldBuilder.Number("高级携带上限（种）", () => rules.maxTypesAdvanced, value => { rules.maxTypesAdvanced = value; Changed(); }, 1)));
            _deckSummary = new Label();
            _deckSummary.AddToClassList("cfg-deck-summary");
            content.Add(_deckSummary);
            content.Add(ConfigFieldBuilder.Note("三阶级比例之和必须为 100，且每阶级至少 1%；抽牌堆张数至少 3。开局时按比例算张数（向下取整，缺额全部给初级，每阶级至少 1 张），再把张数均分给玩家选中的卡种。携带上限的实际值 = min(本值, 该阶级卡种数)。"));

            content.Add(ConfigFieldBuilder.Note("调试用强制牌堆：任一卡种填大于 0 的张数即整体覆盖抽牌堆（与选卡结果无关），仅在本次运行会话生效，不写入任何资产；全部为 0 表示按选卡结果生成。"));
            var forcedGrid = new VisualElement();
            forcedGrid.AddToClassList("cfg-grid");
            foreach (var card in document.cards)
            {
                if (card == null) continue;
                var id = card.id;
                var label = string.IsNullOrWhiteSpace(card.name) ? id : card.name.Trim();
                forcedGrid.Add(ConfigFieldBuilder.Number($"{label}（强制）", () => _draft.GetForcedDeckCount(id), value => { _draft.SetForcedDeckCount(id, value); Changed(); }, 0, 999));
            }
            content.Add(forcedGrid);
            var clearForced = new Button(() => { _draft.ClearForcedDeck(); Rebuild(); }) { text = "清空强制牌堆" };
            clearForced.AddToClassList("cfg-add-button");
            content.Add(clearForced);
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
        /// 字段顺序与网页版一致：名字、卡牌阶级、（资源卡）产出、维持费、生命、攻击、射程、移动，
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
                ConfigFieldBuilder.Choice("卡牌阶级", TierOptions(card.tier), () => card.tier, value => { card.tier = value; Changed(); })
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

            // 部署消耗与邻域部署要求：与 Excel 的 deployCost / deployRequireAdjacent* 一一对应。
            // 两者都只参与“能不能部署到这格”的判定、不产生任何组件，因此属于卡牌级字段。
            box.Add(ConfigFieldBuilder.Grid(ConfigFieldBuilder.Number("部署一次性消耗资源（0 = 免费）", () => card.deployCost, value => { card.deployCost = value; Changed(); }, 0)));
            box.Add(ConfigFieldBuilder.Toggle("部署时要求四邻有指定分类单位", () => card.deployRequireAdjacentCount > 0, value =>
            {
                if (value)
                {
                    if (card.deployRequireAdjacentCount < 1) card.deployRequireAdjacentCount = 1;
                    if (string.IsNullOrEmpty(card.deployRequireAdjacentCategory)) card.deployRequireAdjacentCategory = "resource";
                }
                else
                {
                    card.deployRequireAdjacentCount = 0;
                    card.deployRequireAdjacentCategory = string.Empty;
                }
                Changed();
            }));
            if (card.deployRequireAdjacentCount > 0)
            {
                box.Add(ConfigFieldBuilder.Grid(
                    ConfigFieldBuilder.Choice("四邻需要的分类", new[] { ("resource", "资源卡"), ("attack", "攻击卡"), ("special", "特殊卡") },
                        () => string.IsNullOrEmpty(card.deployRequireAdjacentCategory) ? "resource" : card.deployRequireAdjacentCategory,
                        value => { card.deployRequireAdjacentCategory = value; Changed(); }),
                    ConfigFieldBuilder.Number("四邻需要的数量", () => card.deployRequireAdjacentCount, value => { card.deployRequireAdjacentCount = value; Changed(); }, 1)));
                box.Add(ConfigFieldBuilder.Note("只统计四邻（上下左右）的在场单位，不含家，不区分阵营；数量不足时该格不合法也不会高亮。"));
            }

            // 入场标记：与 Excel 的 markOnEnterComponent / markOnEnterRadius / markRewardResource 一一对应。
            // 关闭能力时两个数值必须归零，否则校验会阻止“应用并重开”。
            box.Add(ConfigFieldBuilder.Toggle("入场标记：上场时标记半径内其它单位", () => card.markOnEnterComponent, value =>
            {
                if (value)
                {
                    if (card.markOnEnterRadius < 1) card.markOnEnterRadius = 1;
                    if (card.markRewardResource < 1) card.markRewardResource = 1;
                }
                else
                {
                    card.markOnEnterRadius = 0;
                    card.markRewardResource = 0;
                }
                Changed();
            }));
            if (card.markOnEnterComponent)
            {
                box.Add(ConfigFieldBuilder.Grid(
                    ConfigFieldBuilder.Number("标记半径（1 = 3×3）", () => card.markOnEnterRadius, value => { card.markOnEnterRadius = value; Changed(); }, 1),
                    ConfigFieldBuilder.Number("阵亡奖励资源", () => card.markRewardResource, value => { card.markRewardResource = value; Changed(); }, 1)));
                box.Add(ConfigFieldBuilder.Note("标记永久保留、先到先得不叠加；只有因战斗阵亡才发奖励，Boss 侧暂无资源系统。"));
            }

            // 死亡加攻：与 Excel 的 deathGrowthComponent / deathGrowthPerKill / deathGrowthLimit 一一对应。
            box.Add(ConfigFieldBuilder.Toggle("死亡加攻：任一单位阵亡时提升攻击力", () => card.deathGrowthComponent, value =>
            {
                if (value)
                {
                    if (card.deathGrowthPerKill < 1) card.deathGrowthPerKill = 1;
                    if (card.deathGrowthLimit < 1) card.deathGrowthLimit = 4;
                }
                else
                {
                    card.deathGrowthPerKill = 0;
                    card.deathGrowthLimit = 0;
                }
                Changed();
            }));
            if (card.deathGrowthComponent)
            {
                box.Add(ConfigFieldBuilder.Grid(
                    ConfigFieldBuilder.Number("每次阵亡加攻", () => card.deathGrowthPerKill, value => { card.deathGrowthPerKill = value; Changed(); }, 1),
                    ConfigFieldBuilder.Number("最多触发次数", () => card.deathGrowthLimit, value => { card.deathGrowthLimit = value; Changed(); }, 1)));
                box.Add(ConfigFieldBuilder.Note("全棋盘任一单位因战斗阵亡时触发；次数上限不是攻击力上限；非战斗移除不触发。"));
            }

            // 治疗型攻击：与 Excel 的 attacksAlliesAsHeal 对应，仅攻击卡可启用。
            if (card.category == "attack")
            {
                box.Add(ConfigFieldBuilder.Toggle("可攻击友方，并把友方结算改为治疗", () => card.attacksAlliesAsHeal, value => { card.attacksAlliesAsHeal = value; Changed(); }));
                box.Add(ConfigFieldBuilder.Note("治疗量 = 当前攻击力，受生命上限限流；满血也允许白打并照常消耗行动；家不可治疗。"));
            }

            // 即时卡：与 Excel 的 leaveAfterDeploy / deployGainResource 一一对应。
            // 关闭时收益必须归零，否则校验会阻止“应用并重开”。
            box.Add(ConfigFieldBuilder.Toggle("即时卡：部署后立即结算并离场", () => card.leaveAfterDeploy, value =>
            {
                card.leaveAfterDeploy = value;
                if (!value) card.deployGainResource = 0;
                else if (card.deployGainResource < 1) card.deployGainResource = 1;
                Changed();
            }));
            if (card.leaveAfterDeploy)
            {
                box.Add(ConfigFieldBuilder.Grid(ConfigFieldBuilder.Number("结算时获得资源", () => card.deployGainResource, value => { card.deployGainResource = value; Changed(); }, 1)));
                box.Add(ConfigFieldBuilder.Note("在场时间视为 0：不参与一次性减攻与入场标记，退场原因记为“结算后离场”，不触发补兵、死亡加攻或标记奖励。Boss 卡种与刷怪卡卡种不能是即时卡。"));
            }
            box.Add(ConfigFieldBuilder.Toggle("允许直接攻击资源卡", () => card.canAttackResources, value => { card.canAttackResources = value; Changed(); }));
            box.Add(ConfigFieldBuilder.Toggle("允许互换", () => card.canSwap, value => { card.canSwap = value; Changed(); }));
            box.Add(ConfigFieldBuilder.Toggle("每回合生成战机", () => card.spawnComponent, value =>
            {
                card.spawnComponent = value;
                card.spawnCardType = value ? _draft.Document.rules[0].bossCardType : string.Empty;
                ChangedStructure();
            }));
            if (card.spawnComponent)
                box.Add(ConfigFieldBuilder.Grid(ConfigFieldBuilder.Choice("生成战机卡种", CardOptions(_draft.Document, "attack", card.spawnCardType), () => card.spawnCardType, value => { card.spawnCardType = value; Changed(); })));

            // 四邻空格限制：与 Excel 的 adjacentClearanceComponent / adjacentClearanceMinEmpty 一一对应。
            // 关闭时阈值必须归零，否则集中校验会阻止“应用并重开”。
            box.Add(ConfigFieldBuilder.Toggle("四邻空格限制：部署与移动/互换前后至少 N 格为空", () => card.adjacentClearanceComponent, value =>
            {
                if (!value) card.adjacentClearanceMinEmpty = 0;
                else if (card.adjacentClearanceMinEmpty < 1) card.adjacentClearanceMinEmpty = 2;
                ChangedStructure();
            }));
            if (card.adjacentClearanceComponent)
            {
                box.Add(ConfigFieldBuilder.Grid(ConfigFieldBuilder.Number("四邻最少空格数（1～4）", () => card.adjacentClearanceMinEmpty, value => { card.adjacentClearanceMinEmpty = value; Changed(); }, 1)));
                box.Add(ConfigFieldBuilder.Note("只统计上下左右四邻，斜角不计，棋盘外按空计算；部署检查目标格，移动与互换同时检查起点与终点的最终布局。被围后仍可攻击，只是不能移动与互换。"));
            }

            // 击杀恢复行动与伤害标记都只允许攻击类卡启用（与治疗型攻击相同的分类约束）。
            // 非攻击卡不展示开关；若旧草稿残留非法组合，集中校验会给出错误而不是静默保留。
            if (card.category == "attack")
            {
                box.Add(ConfigFieldBuilder.Toggle("击杀恢复行动：直接攻击击杀后再行动一次", () => card.killActionComponent, value => { card.killActionComponent = value; Changed(); }));
                box.Add(ConfigFieldBuilder.Note("包括伤害标记追加击杀；可连续触发，但不重开回合（不抽牌、不重复扣维持费），未使用机会不跨回合保存。炸弹与其它单位击杀不会触发。"));

                box.Add(ConfigFieldBuilder.Toggle("伤害标记：直接攻击后使目标获得 1 层", () => card.damageMarkComponent, value =>
                {
                    card.damageMarkComponent = value;
                    if (!value) card.damageMarkDamagePerStack = 0;
                    else if (card.damageMarkDamagePerStack < 1) card.damageMarkDamagePerStack = 1;
                    ChangedStructure();
                }));
                if (card.damageMarkComponent)
                {
                    box.Add(ConfigFieldBuilder.Grid(ConfigFieldBuilder.Number("每层追加伤害", () => card.damageMarkDamagePerStack, value => { card.damageMarkDamagePerStack = value; Changed(); }, 1)));
                    box.Add(ConfigFieldBuilder.Note("新层从下一次受伤起生效；普通攻击与炸弹都会触发已有层，追加伤害不递归、不挂新层、不消耗炸弹预算；家不挂层。"));
                }
            }

            // 一次性部署减攻：与 Excel 的 deploymentPenaltyComponent / deploymentAttackReduction 一一对应。
            // 两个字段必须同时生效：启用必须有正整数减攻量，禁用则减攻量归零，否则校验会阻止应用。
            box.Add(ConfigFieldBuilder.Toggle("在场期间一次：减攻随后上场的一个己方攻击类单位", () => card.deploymentPenaltyComponent, value =>
            {
                card.deploymentPenaltyComponent = value;
                if (!value) card.deploymentAttackReduction = 0;
                else if (card.deploymentAttackReduction < 1) card.deploymentAttackReduction = 1;
                ChangedStructure();
            }));
            if (card.deploymentPenaltyComponent)
            {
                box.Add(ConfigFieldBuilder.Grid(ConfigFieldBuilder.Number("一次性减攻量（默认 2）", () => card.deploymentAttackReduction, value => { card.deploymentAttackReduction = value; Changed(); }, 1)));
                box.Add(ConfigFieldBuilder.Note("该单位成功上场后拥有一次未消耗效果：随后同阵营攻击类单位成功上场时按部署顺序取最早的来源，攻击力降低该值，最低为 0，持续到目标单位离场。多个来源依次触发，每个目标最多承受一次；来源提前离场则未触发效果消失。"));
            }

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
            content.Add(ConfigFieldBuilder.Grid(
                ConfigFieldBuilder.Number("刷怪触发总攻击力（默认 12）", () => rules.bossSpawnerAttackThreshold, value => { rules.bossSpawnerAttackThreshold = value; Changed(); }, 1),
                ConfigFieldBuilder.Number("攻击单位统计起始列（默认 4）", () => rules.bossSpawnerCountFromColumn, value => { rules.bossSpawnerCountFromColumn = value; Changed(); }, 1),
                ConfigFieldBuilder.Number("攻击单位数量下限（默认 2）", () => rules.bossSpawnerUnitThreshold, value => { rules.bossSpawnerUnitThreshold = value; Changed(); }, 1),
                ConfigFieldBuilder.Number("刷怪卡部署起始列（默认 5）", () => rules.bossSpawnerDeployFromColumn, value => { rules.bossSpawnerDeployFromColumn = value; Changed(); }, 1),
                ConfigFieldBuilder.Choice("刷怪卡卡种", CardOptions(document, "special", rules.bossSpawnerCardType), () => rules.bossSpawnerCardType, value => { rules.bossSpawnerCardType = value; ChangedStructure(); }),
                ConfigFieldBuilder.Number("二阶段锁血阈值（默认 20）", () => rules.bossPhaseTwoHp, value => { rules.bossPhaseTwoHp = value; Changed(); }, 1),
                ConfigFieldBuilder.Number("战舰部署列（默认 8，玩家一侧起算）", () => rules.bossWarshipColumn, value => { rules.bossWarshipColumn = value; Changed(); }, 1),
                ConfigFieldBuilder.Choice("战舰卡种", CardOptions(document, "attack", rules.bossWarshipCardType), () => rules.bossWarshipCardType, value => { rules.bossWarshipCardType = value; ChangedStructure(); })));
            var spawner = _draft.FindCard(rules.bossSpawnerCardType);
            if (spawner != null)
            {
                content.Add(ConfigFieldBuilder.Number("刷怪卡生命（默认 1）", () => spawner.hp, value => { spawner.hp = value; Changed(); }, 1));
                content.Add(ConfigFieldBuilder.Choice("刷怪卡生成战机卡种", CardOptions(document, "attack", spawner.spawnCardType), () => spawner.spawnCardType, value => { spawner.spawnCardType = value; Changed(); }));
            }
            var warship = _draft.FindCard(rules.bossWarshipCardType);
            if (warship != null) content.Add(BuildCardBox(warship));
            var guards = ConfigFieldBuilder.Section("三阶段护卫舰", out var guardContent);
            guards.name = "boss-phase-three-guards";
            guardContent.Add(ConfigFieldBuilder.Grid(
                ConfigFieldBuilder.Choice("前列护卫舰卡种", CardOptions(document, "attack", rules.bossPhaseThreeFrontCardType), () => rules.bossPhaseThreeFrontCardType, value => { rules.bossPhaseThreeFrontCardType = value; ChangedStructure(); }),
                ConfigFieldBuilder.Choice("当前列护卫舰卡种", CardOptions(document, "attack", rules.bossPhaseThreeRearCardType), () => rules.bossPhaseThreeRearCardType, value => { rules.bossPhaseThreeRearCardType = value; ChangedStructure(); })));
            var frontGuard = _draft.FindCard(rules.bossPhaseThreeFrontCardType);
            var rearGuard = _draft.FindCard(rules.bossPhaseThreeRearCardType);
            if (frontGuard != null) guardContent.Add(BuildCardBox(frontGuard));
            if (rearGuard != null && rearGuard != frontGuard) guardContent.Add(BuildCardBox(rearGuard));
            guardContent.Add(ConfigFieldBuilder.Note("进入三阶段立即在 Boss 家当前列和朝玩家方向的前一列生成一次护卫舰，跳过双方的家。占位单位视作死亡，护卫舰不补回并遵守召唤失调。索敌顺序：攻击单位、玩家家、特殊单位、资源单位；攻击单位先看距 Boss 家的距离，同距离优先当前攻击力更高者。修改仅在应用并重开后生效。"));
            content.Add(guards);
            content.Add(ConfigFieldBuilder.Note("每个 Boss 回合开始仅检查一次。总攻击力包含全部玩家单位，数量只统计攻击类；列号从玩家一侧起算并包含本列，超过棋盘宽度则范围为空。刷怪卡同时最多一张，固定不动，从下个 Boss 回合起持续在八邻生成战机；满格优先顶替玩家单位，无可用位置暂停，下回合重试。技能不消耗普通放牌次数。"));
            content.Add(ConfigFieldBuilder.Grid(
                ConfigFieldBuilder.Number("炸弹部署间隔（回合）", () => rules.bossBombInterval, value => { rules.bossBombInterval = value; Changed(); }, 1),
                ConfigFieldBuilder.Number("炸弹总伤害", () => rules.bossBombDamage, value => { rules.bossBombDamage = value; Changed(); }, 1),
                ConfigFieldBuilder.Number("炸弹部署列号", () => rules.bossBombColumn, value => { rules.bossBombColumn = value; Changed(); }, 1)));
            content.Add(ConfigFieldBuilder.Note("炸弹在玩家行动前部署，下一完整回合玩家行动前爆炸；手动模式必须先布置或跳过炸弹。列号从屏幕左侧第 1 列起算，超过半场时取半场列数向下取整。"));
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

        /// <summary>
        /// 生成技能卡种选项。输入：草稿、所需分类和当前 ID；输出：名称与稳定 ID 对应的选项。
        /// 保留不合法的当前引用供用户看到并修复，避免显示为第一项却仍保存旧值；选择刷怪卡后再重建生命字段。
        /// </summary>
        static List<(string Value, string Label)> CardOptions(ConfigDocument document, string category, string current)
        {
            var options = new List<(string Value, string Label)>();
            foreach (var card in document.cards)
                if (card != null && card.category == category) options.Add((card.id, $"{card.name}（{card.id}）"));
            if (!options.Exists(option => option.Value == current)) options.Insert(0, (current, $"无效引用（{current}）"));
            return options;
        }

        /// <summary>
        /// 生成独立阶级选项。输入：当前配置文本；输出：三种玩家阶级与 Boss 专用不适用状态。
        /// 缺失或非法旧值保留为显式错误选项，防止界面显示初级却仍存储空值，便于修复旧 JSON。
        /// </summary>
        static List<(string Value, string Label)> TierOptions(string current)
        {
            var options = new List<(string Value, string Label)>
            {
                ("basic", "初级"), ("intermediate", "中级"), ("advanced", "高级"), ("none", "不适用（Boss 专用）")
            };
            if (!options.Exists(option => option.Value == current))
                options.Insert(0, (current ?? string.Empty, $"请补齐阶级（{current}）"));
            return options;
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
            var pile = _draft.GetDrawPileSummary();
            if (_deckSummary != null)
            {
                _deckSummary.text = _draft.HasForcedDeck
                    ? $"强制牌堆 {pile.Total} 张 → 初级 {pile.Basic} 张 / 中级 {pile.Intermediate} 张 / 高级 {pile.Advanced} 张（仅本次运行会话）"
                    : $"抽牌堆 {pile.Total} 张 → 初级 {pile.Basic} 张 / 中级 {pile.Intermediate} 张 / 高级 {pile.Advanced} 张（按比例推导，实际卡种由玩家开局时选择）";
            }
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

        /// <summary>
        /// 复制完整草稿 JSON 到系统剪贴板。输入：当前草稿；输出：剪贴板文本与简短成功提示。
        /// 不把 JSON 放入任何界面文本元素，避免长配置超过 UI Toolkit 单元素文本顶点上限。
        /// </summary>
        void ExportJson()
        {
            if (_draft == null) return;
            GUIUtility.systemCopyBuffer = _draft.ExportJson();
            if (_jsonStatus != null) _jsonStatus.text = "已复制完整配置 JSON，可粘贴到文本文件保存。";
        }

        /// <summary>
        /// 从系统剪贴板导入 JSON。输入：剪贴板全文；输出：草稿表单及导入结果提示。
        /// 不渲染原始 JSON；Import 仅在解析成功后替换文档，失败不重新导入旧文档，
        /// 从而保留原草稿的字段引用和强制牌堆设置。成功仍需“应用并重开”才影响对局。
        /// </summary>
        void ImportJson()
        {
            if (_draft == null) return;
            if (!_draft.Import(GUIUtility.systemCopyBuffer, out var error, out var notices))
            {
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
