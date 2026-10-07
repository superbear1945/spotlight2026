// 配置调试面板（UI Toolkit）：草稿编辑、JSON 导入导出、临时卡种与“应用并重开”。
// 仅在编辑器或 Development Build 可见；草稿只存在于本次运行会话，不写回 Excel 与生成资产。
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace Spotlight
{
    /// <summary>
    /// 调试面板控制器。
    /// 输入：UIDocument（与 HUD 共用同一份界面标记）与 GameBootstrap；
    /// 输出：草稿编辑结果、状态提示，以及在成功“应用并重开”时请求 Bootstrap 替换对局。
    /// 为什么必须由规则层配合：面板打开期间必须屏蔽棋盘输入，
    /// 并且“应用”失败时要保留原对局，所以替换动作只能经 Bootstrap 执行。
    /// </summary>
    public sealed class ConfigDebugPanel : MonoBehaviour
    {
        /// <summary>界面文档，用于查询调试面板元素。</summary>
        [SerializeField] UIDocument _document;
        /// <summary>对局引导器，用于取当前配置资产与请求重开。</summary>
        [SerializeField] GameBootstrap _bootstrap;

        /// <summary>本次运行会话内保留的草稿；关闭面板不会清空，退出 Play 才会丢失。</summary>
        DebugConfigDraft _draft;
        /// <summary>面板根元素。</summary>
        VisualElement _panel;
        /// <summary>JSON 编辑框。</summary>
        TextField _json;
        /// <summary>状态/错误提示文本。</summary>
        Label _status;
        /// <summary>卡种与卡组摘要文本。</summary>
        Label _summary;
        /// <summary>新增卡种的输入控件。</summary>
        TextField _newId, _newName, _newViewKey;
        /// <summary>新增卡种的分类下拉。</summary>
        DropdownField _newCategory;
        /// <summary>新增卡种的数值输入。</summary>
        IntegerField _newHp, _newAttack, _newRange, _newMove, _newProduce, _newUpkeep;
        /// <summary>卡组数量设置输入。</summary>
        TextField _deckId;
        /// <summary>卡组数量输入。</summary>
        IntegerField _deckCount;
        /// <summary>面板是否已打开。</summary>
        bool _open;

        /// <summary>面板当前是否打开。输入：无；输出：打开状态。</summary>
        public bool IsOpen => _open;

        /// <summary>
        /// 绑定界面元素与事件。
        /// 输入：Bootstrap；输出：无。
        /// 调试能力不可用时整体禁用并隐藏面板。
        /// </summary>
        public void Bind(GameBootstrap bootstrap)
        {
            _bootstrap = bootstrap != null ? bootstrap : _bootstrap;
            if (_document == null) _document = GetComponent<UIDocument>();
            if (_document == null) { Debug.LogWarning("ConfigDebugPanel：未找到 UIDocument"); return; }
            var root = _document.rootVisualElement;
            _panel = root.Q<VisualElement>("debug-panel");
            if (_panel == null) { Debug.LogWarning("ConfigDebugPanel：界面缺少 debug-panel"); return; }
            // 双重保险：USS 已默认 display:none，这里再显式设一次，避免样式被后续覆盖。
            _panel.style.display = DisplayStyle.None;
            _open = false;
            if (!DebugSupport.IsDebugAvailable) { _panel.RemoveFromHierarchy(); return; }

            _json = root.Q<TextField>("debug-json");
            _status = root.Q<Label>("debug-status");
            _summary = root.Q<Label>("debug-card-summary");
            _newId = root.Q<TextField>("debug-new-id");
            _newName = root.Q<TextField>("debug-new-name");
            _newViewKey = root.Q<TextField>("debug-new-viewkey");
            _newCategory = root.Q<DropdownField>("debug-new-category");
            _newHp = root.Q<IntegerField>("debug-new-hp");
            _newAttack = root.Q<IntegerField>("debug-new-atk");
            _newRange = root.Q<IntegerField>("debug-new-range");
            _newMove = root.Q<IntegerField>("debug-new-move");
            _newProduce = root.Q<IntegerField>("debug-new-produce");
            _newUpkeep = root.Q<IntegerField>("debug-new-upkeep");
            _deckId = root.Q<TextField>("debug-deck-id");
            _deckCount = root.Q<IntegerField>("debug-deck-count");

            if (_newCategory != null)
            {
                _newCategory.choices = new List<string> { "resource", "attack", "special" };
                _newCategory.value = "resource";
            }
            Hook("debug-close-button", Close);
            Hook("debug-export-button", Export);
            Hook("debug-import-button", Import);
            Hook("debug-reset-button", ResetToAsset);
            Hook("debug-apply-button", ApplyAndRestart);
            Hook("debug-add-card-button", AddCardType);
            Hook("debug-deck-set-button", SetDeckCount);
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
        /// 行为：首次使用时从当前正式配置创建草稿，此后保留本次运行内的编辑结果；
        /// 打开期间通过 Bootstrap 打开规则层输入闸门并显示模态遮罩。
        /// </summary>
        public void Open()
        {
            if (!DebugSupport.IsDebugAvailable || _panel == null) return;
            if (_draft == null) _draft = new DebugConfigDraft(_bootstrap != null ? _bootstrap.CurrentAsset : null);
            _open = true;
            _panel.style.display = DisplayStyle.Flex;
            _bootstrap?.SetModalOpen(true);
            if (_json != null) _json.value = _draft.ExportJson();
            SetStatus("编辑草稿不会影响当前对局；只有“应用并重开”成功才会替换配置。");
            RefreshSummary();
        }

        /// <summary>关闭面板（保留草稿）。输入：无；输出：无。</summary>
        public void Close()
        {
            _open = false;
            if (_panel != null) _panel.style.display = DisplayStyle.None;
            _bootstrap?.SetModalOpen(false);
        }

        /// <summary>导出草稿 JSON 到编辑框。输入：无；输出：无。</summary>
        void Export()
        {
            if (_draft == null) return;
            if (_json != null) _json.value = _draft.ExportJson();
            SetStatus("已导出当前草稿 JSON。");
        }

        /// <summary>
        /// 从编辑框导入 JSON。
        /// 输入：无（读取界面文本）；输出：无。
        /// 成功时提示被移除的废弃字段；失败时保留原草稿并显示原因。
        /// </summary>
        void Import()
        {
            if (_draft == null || _json == null) return;
            var previous = _draft.ExportJson();
            if (!_draft.Import(_json.value, out var error, out var notices))
            {
                // 导入失败时回滚到导入前的草稿，避免半成品覆盖可用配置。
                _draft.Import(previous, out _, out _);
                SetStatus("导入失败：" + error);
                return;
            }
            var sb = new StringBuilder("导入成功。");
            foreach (var notice in notices) sb.Append("\n提示：").Append(notice);
            SetStatus(sb.ToString());
            RefreshSummary();
        }

        /// <summary>恢复为正式配置内容。输入：无；输出：无。</summary>
        void ResetToAsset()
        {
            _draft?.ResetToAsset(_bootstrap != null ? _bootstrap.CurrentAsset : null);
            if (_json != null && _draft != null) _json.value = _draft.ExportJson();
            SetStatus("已恢复为 Excel 编译产物。");
            RefreshSummary();
        }

        /// <summary>
        /// 应用草稿并重开。
        /// 输入：无（读取草稿）；输出：无。
        /// 行为：先校验并创建候选对局，成功才替换当前对局并关闭面板；失败保留原对局与草稿。
        /// </summary>
        void ApplyAndRestart()
        {
            if (_draft == null) return;
            if (!_draft.TryCreateConfiguration(out var configuration, out var error))
            {
                SetStatus("应用失败（原对局与草稿保留）：" + error);
                return;
            }
            _bootstrap?.RestartWith(configuration);
            SetStatus("应用成功，已重开对局。");
            Close();
        }

        /// <summary>
        /// 新增临时卡种。
        /// 输入：无（读取界面输入）；输出：无。
        /// 说明：卡种进入草稿，卡组数量默认 0，需要在下方设置数量后才会进入牌库。
        /// </summary>
        void AddCardType()
        {
            if (_draft == null) return;
            var row = new CardRow
            {
                id = _newId != null ? _newId.value?.Trim() : null,
                name = _newName != null ? _newName.value?.Trim() : null,
                category = _newCategory != null ? _newCategory.value : "resource",
                viewKey = string.IsNullOrWhiteSpace(_newViewKey != null ? _newViewKey.value : null) ? "default" : _newViewKey.value.Trim(),
                hp = _newHp != null ? Mathf.Max(1, _newHp.value) : 1,
                atk = _newAttack != null ? Mathf.Max(0, _newAttack.value) : 0,
                range = _newRange != null ? Mathf.Max(0, _newRange.value) : 0,
                moveDistance = _newMove != null ? Mathf.Max(0, _newMove.value) : 0,
                produce = _newProduce != null ? Mathf.Max(0, _newProduce.value) : 0,
                upkeep = _newUpkeep != null ? Mathf.Max(0, _newUpkeep.value) : 0
            };
            row.resourceComponent = row.produce != 0 || row.upkeep != 0;
            if (!_draft.AddCardType(row, out var error)) { SetStatus("新增卡种失败：" + error); return; }
            SetStatus($"已新增临时卡种 {row.id}；请在卡组中设置数量后再应用。");
            RefreshSummary();
        }

        /// <summary>设置某卡种的卡组数量。输入：无（读取界面输入）；输出：无。</summary>
        void SetDeckCount()
        {
            if (_draft == null) return;
            var id = _deckId != null ? _deckId.value?.Trim() : null;
            var count = _deckCount != null ? _deckCount.value : 0;
            if (string.IsNullOrEmpty(id)) { SetStatus("请填写卡种 ID。"); return; }
            if (!_draft.SetDeckCount(id, count, out var error)) { SetStatus("设置数量失败：" + error); return; }
            SetStatus($"已把 {id} 的卡组数量设为 {count}。");
            RefreshSummary();
        }

        /// <summary>刷新卡种与卡组摘要。输入：无；输出：无。</summary>
        void RefreshSummary()
        {
            if (_summary == null || _draft == null) return;
            var sb = new StringBuilder("卡种与卡组：\n");
            foreach (var row in _draft.Document.cards ?? Array.Empty<CardRow>())
            {
                if (row == null) continue;
                var count = 0;
                foreach (var entry in _draft.Document.deck ?? Array.Empty<DeckRow>())
                    if (entry != null && entry.cardType == row.id) count = entry.count;
                sb.Append(row.id).Append(' ').Append(row.name).Append("（").Append(row.category).Append("）×").Append(count).Append('\n');
            }
            _summary.text = sb.ToString();
        }

        /// <summary>显示状态文本。输入：文本；输出：无。</summary>
        void SetStatus(string message)
        {
            if (_status != null) _status.text = message;
        }
    }
}
