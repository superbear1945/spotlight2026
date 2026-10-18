// 携带选择面板（UI Toolkit）：把 DeckSelectionState 渲染成“阶级分组 + 勾选行 + 实时张数预览”。
// 为什么需要独立类：选卡界面只在开局前与齿轮菜单里出现，逻辑与战斗 HUD 完全不同；
// 放进 HudView 会让那个类同时承担“对局渲染”和“开局表单”两件事。
// 职责边界：本类只做“读状态 → 画界面 → 把点击转发成状态变化”，
// 不判断任何规则（上下限、每阶级至少 1 种等）——判定全部来自 Spotlight.Core。
using System;
using System.Collections.Generic;
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
        VisualElement _tierContainer;
        VisualElement _cardContainer;
        Label _title;
        Label _hint;
        Label _preview;
        Label _error;
        Button _apply;
        Button _cancel;
        DeckSelectionState _state;
        bool _fromMenu;
        bool _visible;

        /// <summary>面板当前是否可见。输入：无；输出：可见为 true（含二次确认层）。</summary>
        public bool IsVisible => _visible;

        /// <summary>当前绑定的选卡状态（供测试与调试读取）。</summary>
        public DeckSelectionState State => _state;

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
            _tierContainer = root.Q<VisualElement>("deck-select-tiers");
            _cardContainer = root.Q<VisualElement>("deck-select-cards");
            _title = root.Q<Label>("deck-select-title");
            _hint = root.Q<Label>("deck-select-hint");
            _preview = root.Q<Label>("deck-select-preview");
            _error = root.Q<Label>("deck-select-error");
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
            if (_state == null || _tierContainer == null || _cardContainer == null) return;
            _tierContainer.Clear();
            _cardContainer.Clear();
            var preview = _state.PreviewTierCounts();
            var copies = _state.PreviewTypeCounts();
            foreach (var tier in DeckComposition.PlayerTiers)
            {
                var name = DeckComposition.TierLabel(tier);
                var selected = _state.SelectedCount(tier);
                var capacity = _state.Capacity(tier);
                var total = preview.TryGetValue(tier, out var value) ? value : 0;
                var head = new Label($"{name}  {selected}/{capacity}  ·  抽牌堆 {total} 张");
                head.AddToClassList("deck-tier-head");
                _tierContainer.Add(head);
                foreach (var card in _state.Pool(tier))
                {
                    var isSelected = _state.IsSelected(card.Id);
                    var row = new VisualElement();
                    row.AddToClassList("deck-card-row");
                    if (isSelected) row.AddToClassList("selected");
                    var id = card.Id;
                    var toggle = new Button(() => OnCardClicked(id)) { text = isSelected ? "取消" : "携带" };
                    // 已达上限时按钮置灰：这本身就是“不能再带”的提示，不需要弹窗打断。
                    toggle.SetEnabled(isSelected || _state.CanSelect(id));
                    var label = new Label($"{card.Name} · {CardText.CategoryLabel(card.Category)}");
                    label.AddToClassList("deck-card-name");
                    var copy = new Label(isSelected ? $"本局 ×{(copies.TryGetValue(id, out var count) ? count : 0)}" : "—");
                    copy.AddToClassList("deck-card-copies");
                    row.Add(toggle);
                    row.Add(label);
                    row.Add(copy);
                    _cardContainer.Add(row);
                }
            }

            var totalCards = 0;
            foreach (var pair in preview) totalCards += pair.Value;
            if (_preview != null)
                _preview.text = $"抽牌堆 {totalCards} 张 → 初级 {Preview(preview, CardTier.Basic)} / 中级 {Preview(preview, CardTier.Intermediate)} / 高级 {Preview(preview, CardTier.Advanced)}";
            if (_hint != null)
                _hint.text = "每阶级至少带 1 种，最多带到括号里的上限；张数由阶级比例推导，取消/勾选会立即重新均分。";
            if (_apply != null) _apply.SetEnabled(_state.ValidationMessage == null);
            var message = _state.ValidationMessage;
            if (_error != null)
            {
                _error.text = message ?? string.Empty;
                _error.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;
            }
        }

        /// <summary>显示一条错误（例如生成配置失败）。输入：文本；输出：无。</summary>
        public void ShowError(string message)
        {
            if (_error == null) return;
            _error.text = message ?? string.Empty;
            _error.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;
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
