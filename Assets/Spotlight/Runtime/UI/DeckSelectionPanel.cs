// 携带选择面板（UI Toolkit）：把 DeckSelectionState 渲染成“阶级分组 + 勾选行 + 实时张数预览”。
// 为什么需要独立类：选卡界面只在开局前与齿轮菜单里出现，逻辑与战斗 HUD 完全不同；
// 放进 HudView 会让那个类同时承担“对局渲染”和“开局表单”两件事。
// 为什么要显示属性与特性：只给卡名时玩家无法判断“带回哪张”，选卡变成盲猜；
// 因此每行直接给出属性行与特性短标，右侧再用详情区展开完整规则（悬停即可看，触屏靠点击）。
// 职责边界：本类只做“读状态 → 画界面 → 把点击转发成状态变化”，
// 不判断任何规则（上下限、每阶级至少 1 种等）——判定全部来自 Spotlight.Core，
// 数值与规则文字全部来自 CardText（与对局内卡面同源，不得在此另拼一套）。
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;

namespace Spotlight
{
    /// <summary>
    /// 携带选择面板控制器。
    /// 输入：界面根元素（与 HUD 共用同一 UXML 文档）与一份 DeckSelectionState；
    /// 输出：勾选变化后的界面重绘，以及“确认 / 取消”事件。
    /// 生命周期：GameBootstrap 持有并在开局、菜单选卡时 Show/Hide；状态与界面都比对局实例长寿，
    /// 因此“重开”“再来一局”可以沿用玩家刚选好的卡组而不必重新选。
    /// </summary>
    public sealed class DeckSelectionPanel
    {
        /// <summary>玩家点击“确认并开局/确认并重开”。</summary>
        public event Action Confirmed;
        /// <summary>玩家点击“取消”或二次确认里的“继续当前对局”。</summary>
        public event Action Cancelled;

        VisualElement _mask;
        VisualElement _confirmMask;
        VisualElement _cardContainer;
        VisualElement _detailPane;
        VisualElement _detailTraits;
        Label _title;
        Label _hint;
        Label _preview;
        Label _error;
        Label _detailTitle;
        Label _detailMeta;
        Label _detailStats;
        Label _detailBody;
        Button _apply;
        Button _cancel;
        DeckSelectionState _state;
        /// <summary>右侧详情区正在展示的卡种 ID；null 表示尚未预览，按“第一张已选卡种”回退。</summary>
        string _detailId;
        bool _fromMenu;
        bool _visible;

        /// <summary>面板当前是否可见。输入：无；输出：可见为 true（含二次确认层）。</summary>
        public bool IsVisible => _visible;

        /// <summary>当前绑定的选卡状态（供测试与调试读取）。</summary>
        public DeckSelectionState State => _state;

        /// <summary>
        /// 右侧详情区当前展示的卡种 ID。输入：无；输出：卡种 ID（尚未预览时为 null）。
        /// 供测试核对“悬停/点击哪一行就展示哪张卡”，避免只断言面板可见却看不出内容是否正确。
        /// </summary>
        public string DetailCardId => _detailId;

        /// <summary>
        /// 绑定界面元素与按钮事件。
        /// 输入：与 HUD 共用的根元素；输出：无（元素缺失时给出警告并保持不可用）。
        /// 为什么容忍缺失：界面文件由策划维护，缺一个容器不应该让整个场景无法运行。
        /// </summary>
        public void Bind(VisualElement root)
        {
            if (root == null) return;
            _mask = root.Q<VisualElement>("deck-select-mask");
            _confirmMask = root.Q<VisualElement>("deck-select-confirm-overlay");
            _cardContainer = root.Q<VisualElement>("deck-select-cards");
            _detailPane = root.Q<VisualElement>("deck-select-detail");
            _detailTraits = root.Q<VisualElement>("deck-select-detail-traits");
            _title = root.Q<Label>("deck-select-title");
            _hint = root.Q<Label>("deck-select-hint");
            _preview = root.Q<Label>("deck-select-preview");
            _error = root.Q<Label>("deck-select-error");
            _detailTitle = root.Q<Label>("deck-select-detail-title");
            _detailMeta = root.Q<Label>("deck-select-detail-meta");
            _detailStats = root.Q<Label>("deck-select-detail-stats");
            _detailBody = root.Q<Label>("deck-select-detail-body");
            _apply = root.Q<Button>("deck-select-apply-button");
            _cancel = root.Q<Button>("deck-select-cancel-button");
            if (_apply != null) _apply.clicked += OnApplyClicked;
            if (_cancel != null) _cancel.clicked += OnCancelClicked;
            var defaults = root.Q<Button>("deck-select-default-button");
            if (defaults != null) defaults.clicked += OnDefaultsClicked;
            var confirmYes = root.Q<Button>("deck-select-confirm-yes");
            if (confirmYes != null) confirmYes.clicked += OnConfirmYesClicked;
            var confirmNo = root.Q<Button>("deck-select-confirm-no");
            if (confirmNo != null) confirmNo.clicked += OnConfirmNoClicked;
            Hide();
        }

        /// <summary>
        /// 打开面板。
        /// 输入：选卡状态与“是否从齿轮菜单进入”；
        /// 输出：无。从菜单进入时先显示二次确认（换卡会重开当前对局），从开局进入时直接显示选卡。
        /// </summary>
        public void Show(DeckSelectionState state, bool fromMenu)
        {
            _state = state;
            _fromMenu = fromMenu;
            // 换了一份选卡状态就丢掉旧的预览目标：菜单选卡可能换了配置，旧 ID 未必还存在。
            _detailId = null;
            ShowError(null);
            _visible = true;
            if (_cancel != null) _cancel.style.display = fromMenu ? DisplayStyle.Flex : DisplayStyle.None;
            if (fromMenu)
            {
                SetShown(_mask, false);
                SetShown(_confirmMask, true);
                return;
            }
            SetShown(_confirmMask, false);
            SetShown(_mask, true);
            if (_title != null) _title.text = "携带选择";
            Render();
        }

        /// <summary>关闭面板（含二次确认层）。输入：无；输出：无。</summary>
        public void Hide()
        {
            _visible = false;
            SetShown(_mask, false);
            SetShown(_confirmMask, false);
        }

        /// <summary>
        /// 按当前选卡状态重绘面板。
        /// 输入：无；输出：无。每次勾选、恢复默认都调用它，
        /// 因此每行的“本局 ×N”与底部预览永远来自同一套分配算法。
        /// </summary>
        public void Render()
        {
            if (_state == null || _cardContainer == null) return;
            _cardContainer.Clear();
            var preview = _state.PreviewTierCounts();
            var copies = _state.PreviewTypeCounts();
            foreach (var tier in DeckComposition.PlayerTiers)
            {
                // 每个阶级自成一组：左侧彩色竖条 + 阶级徽章 + 计数，组内只放该阶级的卡种行，
                // 避免出现“标题全在上、卡种全在下”导致玩家看不出哪张卡属于哪个阶级。
                var section = new VisualElement();
                section.AddToClassList("deck-tier");
                section.AddToClassList(TierClass(tier));

                var head = new VisualElement();
                head.AddToClassList("deck-tier-head");
                var badge = new Label(CardText.TierLabel(tier));
                badge.AddToClassList("deck-tier-badge");
                var typeCount = new Label($"{_state.SelectedCount(tier)}/{_state.Capacity(tier)} 种");
                typeCount.AddToClassList("deck-tier-count");
                var pile = new Label($"本阶级抽牌堆 {(preview.TryGetValue(tier, out var value) ? value : 0)} 张");
                pile.AddToClassList("deck-tier-pile");
                head.Add(badge);
                head.Add(typeCount);
                head.Add(pile);
                section.Add(head);

                foreach (var card in _state.Pool(tier))
                {
                    var isSelected = _state.IsSelected(card.Id);
                    var id = card.Id;
                    var row = new VisualElement { userData = id };
                    row.AddToClassList("deck-card-row");
                    if (isSelected) row.AddToClassList("selected");
                    if (id == _detailId) row.AddToClassList("inspected");
                    var toggle = new Button(() => OnCardClicked(id)) { text = isSelected ? "取消" : "携带" };
                    // 已达上限时按钮置灰：这本身就是“不能再带”的提示，不需要弹窗打断。
                    toggle.SetEnabled(isSelected || _state.CanSelect(id));
                    // 行内再带一个阶级短标（初/中/高），方便逐行扫读时确认阶级而不必回看分组标题。
                    var tierTag = new Label(TierShortLabel(tier));
                    tierTag.AddToClassList("deck-card-tier-tag");

                    // 卡名与“携带”按钮拆成两个可点区域：点按钮改变选卡，点信息块只看详情，
                    // 这样“想看看这张卡是什么”不会顺手把它加进／移出本局抽牌堆。
                    var info = new VisualElement();
                    info.AddToClassList("deck-card-info");
                    var cardHead = new VisualElement();
                    cardHead.AddToClassList("deck-card-head");
                    var name = new Label(card.Name);
                    name.AddToClassList("deck-card-name");
                    var category = new Label(CardText.CategoryLabel(card.Category));
                    category.AddToClassList("deck-card-category");
                    category.AddToClassList(CategoryClass(card.Category));
                    var copy = new Label(isSelected ? $"本局 ×{(copies.TryGetValue(id, out var count) ? count : 0)}" : "—");
                    copy.AddToClassList("deck-card-copies");
                    cardHead.Add(name);
                    cardHead.Add(category);
                    cardHead.Add(copy);

                    // 属性行与对局中卡面同源（CardText.StatLine），资源卡的产/耗、付费部署都在这里直接可见。
                    var stats = new Label(CardText.StatLine(card));
                    stats.AddToClassList("deck-card-stats");

                    // 特性短标：用一句话看完整张卡的关键能力；“四邻 1 空 / 击杀回复 / 伤害标记 +1”等均来自配置。
                    var traits = new VisualElement();
                    traits.AddToClassList("deck-card-traits");
                    foreach (var tag in CardText.TraitTags(card)) traits.Add(TraitChip(tag));

                    info.Add(cardHead);
                    info.Add(stats);
                    // 没有特性的卡不生成空容器，避免每行都多出一点无意义的留白。
                    if (traits.childCount > 0) info.Add(traits);
                    // 悬停即预览满足鼠标玩家；点击固定预览是因为触屏（Android 包）没有 hover。
                    info.RegisterCallback<PointerEnterEvent>(_ => ShowDetail(id));
                    info.RegisterCallback<ClickEvent>(_ => ShowDetail(id));

                    row.Add(toggle);
                    row.Add(tierTag);
                    row.Add(info);
                    section.Add(row);
                }
                _cardContainer.Add(section);
            }

            var totalCards = 0;
            foreach (var pair in preview) totalCards += pair.Value;
            if (_preview != null)
                _preview.text = $"抽牌堆 {totalCards} 张 → 初级 {Preview(preview, CardTier.Basic)} / 中级 {Preview(preview, CardTier.Intermediate)} / 高级 {Preview(preview, CardTier.Advanced)}";
            if (_hint != null)
                _hint.text = "每阶级至少带 1 种，最多带到括号里的上限；张数由阶级比例推导，取消/勾选会立即重新均分。鼠标移到卡种上（或点卡种名字）可在右侧查看完整效果与消耗。";
            if (_apply != null) _apply.SetEnabled(_state.ValidationMessage == null);
            var message = _state.ValidationMessage;
            if (_error != null)
            {
                _error.text = message ?? string.Empty;
                _error.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;
            }
            RenderDetail(_detailId);
        }

        /// <summary>
        /// 在右侧详情区展示指定卡种（供悬停/点击与测试调用）。
        /// 输入：卡种 ID（不存在时静默忽略）；输出：无。
        /// 行为：只重绘详情区与“正在查看”高亮，不重建整个卡种列表，
        /// 否则每次鼠标移进一行都会丢掉滚动位置并重新触发悬停事件。
        /// </summary>
        public void ShowDetail(string id)
        {
            if (_state == null || string.IsNullOrEmpty(id)) return;
            if (_state.Configuration.FindCard(id) == null) return;
            _detailId = id;
            RenderDetail(id);
            UpdateInspectedRows();
        }

        /// <summary>
        /// 重绘右侧详情区。
        /// 输入：要展示的卡种 ID（null 或已失效时回退到第一张已选卡种，再回退到卡池第一张）；输出：无。
        /// 内容：名称 / 阶级与分类 / 属性行 / 特性短标 / 完整规则正文；
        /// 文字全部来自 CardText，因此与对局内手牌详情逐字一致，玩家不会对不上。
        /// </summary>
        void RenderDetail(string id)
        {
            if (_state == null) return;
            var card = _state.Configuration.FindCard(id) ?? FirstPreviewableCard();
            _detailId = card != null ? card.Id : null;
            var hasCard = card != null;

            if (_detailTitle != null) _detailTitle.text = hasCard ? card.Name : "卡牌详情";
            if (_detailPane != null)
            {
                // 空状态降亮度：没有可选卡种时（配置异常）也能看出面板本身是好的。
                if (hasCard) _detailPane.RemoveFromClassList("empty");
                else _detailPane.AddToClassList("empty");
            }
            if (_detailMeta != null)
            {
                _detailMeta.text = hasCard
                    ? $"{CardText.TierLabel(card.Tier)} · {CardText.CategoryLabel(card.Category)}"
                    : "把鼠标移到左侧卡种上（或点卡种名字）查看完整效果";
            }
            if (_detailStats != null)
            {
                _detailStats.text = hasCard ? CardText.StatLine(card) : string.Empty;
                SetShown(_detailStats, hasCard);
            }
            if (_detailTraits != null)
            {
                _detailTraits.Clear();
                if (hasCard)
                    foreach (var tag in CardText.TraitTags(card)) _detailTraits.Add(TraitChip(tag));
                SetShown(_detailTraits, hasCard && _detailTraits.childCount > 0);
            }
            if (_detailBody != null)
            {
                var body = hasCard ? CardText.DetailBody(card) : string.Empty;
                // 没有额外规则也要给一句明确文案：空白详情会让玩家以为面板坏了。
                _detailBody.text = !hasCard ? string.Empty : body.Length == 0 ? "无额外部署限制或能力，按普通规则上场。" : body;
                SetShown(_detailBody, hasCard);
            }
        }

        /// <summary>
        /// 详情区的默认目标（还没有悬停过任何卡种时）。
        /// 输入：无；输出：第一张已选中的玩家卡种；一张都没选时返回卡池第一张；卡池为空时返回 null。
        /// 为什么优先已选卡种：默认选卡已填满每阶级上限，面板一打开就能看到“我现在带的是什么”。
        /// </summary>
        CardArchetype FirstPreviewableCard()
        {
            if (_state == null) return null;
            foreach (var tier in DeckComposition.PlayerTiers)
                foreach (var card in _state.Pool(tier))
                    if (_state.IsSelected(card.Id)) return card;
            foreach (var tier in DeckComposition.PlayerTiers)
                foreach (var card in _state.Pool(tier))
                    return card;
            return null;
        }

        /// <summary>
        /// 同步“正在查看”行高亮。输入：无；输出：无。
        /// 用行上缓存的卡种 ID 比对，不依赖界面顺序或索引。
        /// </summary>
        void UpdateInspectedRows()
        {
            if (_cardContainer == null) return;
            foreach (var row in _cardContainer.Query<VisualElement>(className: "deck-card-row").ToList())
            {
                var isInspected = row.userData is string id && id == _detailId;
                if (isInspected) row.AddToClassList("inspected");
                else row.RemoveFromClassList("inspected");
            }
        }

        /// <summary>特性短标控件（行内与详情区共用）。输入：标签文本；输出：带统一样式的 Label。</summary>
        static Label TraitChip(string text)
        {
            var chip = new Label(text);
            chip.AddToClassList("deck-card-trait");
            return chip;
        }

        /// <summary>
        /// 分类对应的 USS 类名。输入：卡牌分类；输出：cat-resource / cat-attack / cat-special。
        /// 为什么用类名而不是内联颜色：配色集中在 Spotlight.uss，改色不用改代码。
        /// </summary>
        static string CategoryClass(CardCategory category) =>
            category == CardCategory.Resource ? "cat-resource" : category == CardCategory.Attack ? "cat-attack" : "cat-special";

        /// <summary>显示一条错误（例如生成配置失败）。输入：文本；输出：无。</summary>
        public void ShowError(string message)
        {
            if (_error == null) return;
            _error.text = message ?? string.Empty;
            _error.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>
        /// 阶级对应的 USS 类名。输入：玩家阶级；输出：tier-basic / tier-intermediate / tier-advanced。
        /// 为什么用类名而不是内联颜色：配色集中在 Spotlight.uss 里，改色不需要改代码。
        /// </summary>
        static string TierClass(CardTier tier)
        {
            switch (tier)
            {
                case CardTier.Basic: return "tier-basic";
                case CardTier.Intermediate: return "tier-intermediate";
                default: return "tier-advanced";
            }
        }

        /// <summary>阶级短名（行内标签用）。输入：玩家阶级；输出：初 / 中 / 高。</summary>
        static string TierShortLabel(CardTier tier)
        {
            switch (tier)
            {
                case CardTier.Basic: return "初";
                case CardTier.Intermediate: return "中";
                default: return "高";
            }
        }

        /// <summary>读取某阶级预览张数。输入：预览表与阶级；输出：张数（缺失为 0）。</summary>
        static int Preview(IReadOnlyDictionary<CardTier, int> preview, CardTier tier) =>
            preview != null && preview.TryGetValue(tier, out var value) ? value : 0;

        /// <summary>设置元素显隐。输入：元素与是否显示；输出：无。</summary>
        static void SetShown(VisualElement element, bool shown)
        {
            if (element != null) element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>点某卡种的“携带/取消”。输入：卡种 ID；输出：无。</summary>
        void OnCardClicked(string id)
        {
            if (_state == null) return;
            _state.Toggle(id);
            Render();
        }

        /// <summary>“恢复默认”：每阶级按有效上限取满。输入：无；输出：无。</summary>
        void OnDefaultsClicked()
        {
            _state?.SelectDefaults();
            Render();
        }

        /// <summary>“确认并开局/确认并重开”。输入：无；输出：无（由 GameBootstrap 真正生成配置）。</summary>
        void OnApplyClicked() => Confirmed?.Invoke();

        /// <summary>“取消”。输入：无；输出：无。</summary>
        void OnCancelClicked() => Cancelled?.Invoke();

        /// <summary>二次确认里的“进入选卡”。输入：无；输出：无。</summary>
        void OnConfirmYesClicked()
        {
            SetShown(_confirmMask, false);
            SetShown(_mask, true);
            if (_title != null) _title.text = _fromMenu ? "携带选择（确认后将重开对局）" : "携带选择";
            Render();
        }

        /// <summary>二次确认里的“继续当前对局”。输入：无；输出：无。</summary>
        void OnConfirmNoClicked()
        {
            Hide();
            Cancelled?.Invoke();
        }
    }
}
