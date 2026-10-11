// HUD 视图（UI Toolkit / UXML）：回合信息、资源、日志、单位详情、按钮与胜负/模态遮罩。
// 为什么用 UI Toolkit 做静态 UI：面板结构固定、由策划在 UXML 中维护，不需要每帧重建；
// 动态的手牌与棋盘使用 UGUI，避免为频繁增删的元素编写复杂的 UXML 同步代码。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace Spotlight
{
    /// <summary>
    /// HUD 控制器：查询 UXML 中的元素、按快照刷新文本、向外抛出按钮请求。
    /// 输入：Bind 时传入 UIDocument 与表现配置；Render 时传入快照与配置。
    /// 输出：按钮事件与界面文本。
    /// 为什么把按钮做成事件而不是直接调用规则层：界面只表达“玩家想做什么”，
    /// 具体能否执行由 GameBootstrap 提交命令后由规则层裁决，UI 不复制任何合法性判断。
    /// </summary>
    public sealed class HudView : MonoBehaviour
    {
        /// <summary>承载界面标记的 UIDocument（面板与游戏界面共用一个文档）。</summary>
        [SerializeField] UIDocument _document;

        /// <summary>点击“结束回合”。</summary>
        public event Action EndTurnRequested;
        /// <summary>点击“跳过放牌并结束”（仅手动 Boss 模式可用）。</summary>
        public event Action SkipDeployRequested;
        /// <summary>点击“重开”。</summary>
        public event Action RestartRequested;
        /// <summary>点击“配置/调试”。</summary>
        public event Action DebugRequested;

        /// <summary>界面根元素。</summary>
        VisualElement _root;
        /// <summary>回合与行动方文本。</summary>
        Label _turnLabel;
        /// <summary>资源文本。</summary>
        Label _resourceLabel;
        /// <summary>剩余放牌次数文本。</summary>
        Label _playsLabel;
        /// <summary>Boss 兵位与手动模式提示文本。</summary>
        Label _bossLabel;
        /// <summary>阶段提示（弃牌、模态等）。</summary>
        Label _phaseLabel;
        /// <summary>选中对象详情文本。</summary>
        Label _detailLabel;
        /// <summary>错误/提示文本。</summary>
        Label _errorLabel;
        /// <summary>日志滚动容器。</summary>
        ScrollView _logScroll;
        /// <summary>日志内容容器。</summary>
        VisualElement _logContent;
        /// <summary>结束回合按钮。</summary>
        Button _endTurnButton;
        /// <summary>跳过放牌按钮。</summary>
        Button _skipButton;
        /// <summary>胜负遮罩。</summary>
        VisualElement _victoryOverlay;
        /// <summary>胜负文本。</summary>
        Label _victoryLabel;
        /// <summary>模态遮罩（配置面板打开时挡住棋盘点击）。</summary>
        VisualElement _modalOverlay;
        /// <summary>已渲染的日志条数，用于增量追加。</summary>
        int _renderedLogCount;

        /// <summary>界面根元素（供调试面板查询同一文档中的其它元素）。</summary>
        public VisualElement Root => _root;

        /// <summary>
        /// 绑定并查询界面元素。
        /// 输入：可选的 UIDocument 覆盖（默认使用 Inspector 引用）；输出：无。
        /// 行为：缺失的元素会被容忍（只记录警告），保证界面文件尚未完成时场景仍可运行。
        /// </summary>
        public void Bind(UIDocument document = null)
        {
            if (document != null) _document = document;
            if (_document == null) _document = GetComponent<UIDocument>();
            if (_document == null) { Debug.LogWarning("HudView：未找到 UIDocument，HUD 不会显示"); return; }
            _root = _document.rootVisualElement;
            _turnLabel = _root.Q<Label>("turn-label");
            _resourceLabel = _root.Q<Label>("resource-label");
            _playsLabel = _root.Q<Label>("plays-label");
            _bossLabel = _root.Q<Label>("boss-info-label");
            _phaseLabel = _root.Q<Label>("phase-label");
            _detailLabel = _root.Q<Label>("unit-detail");
            _errorLabel = _root.Q<Label>("error-label");
            _logScroll = _root.Q<ScrollView>("log-list");
            _logContent = _root.Q<VisualElement>("log-content");
            _endTurnButton = _root.Q<Button>("end-turn-button");
            _skipButton = _root.Q<Button>("skip-deploy-button");
            _victoryOverlay = _root.Q<VisualElement>("victory-overlay");
            _victoryLabel = _root.Q<Label>("victory-label");
            _modalOverlay = _root.Q<VisualElement>("modal-overlay");

            if (_endTurnButton != null) _endTurnButton.clicked += () => EndTurnRequested?.Invoke();
            if (_skipButton != null) _skipButton.clicked += () => SkipDeployRequested?.Invoke();
            var restart = _root.Q<Button>("restart-button");
            if (restart != null) restart.clicked += () => RestartRequested?.Invoke();
            var debug = _root.Q<Button>("debug-button");
            if (debug != null) debug.clicked += () => DebugRequested?.Invoke();
            var victoryRestart = _root.Q<Button>("victory-restart-button");
            if (victoryRestart != null) victoryRestart.clicked += () => RestartRequested?.Invoke();
            if (_victoryOverlay != null) _victoryOverlay.style.display = DisplayStyle.None;
            if (_modalOverlay != null) _modalOverlay.style.display = DisplayStyle.None;
            if (_errorLabel != null) _errorLabel.style.display = DisplayStyle.None;

            // 调试入口只在编辑器或 Development Build 提供。
            if (debug != null && !DebugSupport.IsDebugAvailable) debug.style.display = DisplayStyle.None;
        }

        /// <summary>
        /// 渲染一帧界面。
        /// 输入：快照、本局配置与当前选择；输出：无。
        /// 行为：刷新文本、按钮可用性、日志增量与胜负遮罩。
        /// </summary>
        public void Render(GameSnapshot snapshot, GameConfiguration configuration, Selection selection)
        {
            if (_root == null) return;
            var sideName = snapshot.ActiveSide == Side.Player ? "玩家" : "Boss";
            if (_turnLabel != null) _turnLabel.text = $"第 {snapshot.Round} 回合 · {sideName}行动";
            if (_resourceLabel != null) _resourceLabel.text = $"资源 {snapshot.Resources}";
            if (_playsLabel != null) _playsLabel.text = $"剩余出牌 {snapshot.PlaysRemaining}";
            if (_bossLabel != null) _bossLabel.text = BuildBossLine(snapshot, configuration);
            if (_phaseLabel != null) _phaseLabel.text = BuildPhaseLine(snapshot);
            if (_detailLabel != null) _detailLabel.text = BuildDetail(selection, snapshot, configuration);
            if (_endTurnButton != null) _endTurnButton.SetEnabled(snapshot.Phase != MatchPhase.Finished);
            if (_skipButton != null)
            {
                var showSkip = snapshot.PlayerControlsBoss && snapshot.ActiveSide == Side.Boss && snapshot.PlaysRemaining > 0;
                _skipButton.style.display = showSkip ? DisplayStyle.Flex : DisplayStyle.None;
            }
            AppendLog(snapshot.Log);
            if (_victoryOverlay != null)
            {
                var finished = snapshot.Phase == MatchPhase.Finished;
                _victoryOverlay.style.display = finished ? DisplayStyle.Flex : DisplayStyle.None;
                if (finished && _victoryLabel != null)
                    _victoryLabel.text = snapshot.Outcome == MatchOutcome.Draw ? "平局" : snapshot.Outcome == MatchOutcome.PlayerWins ? "玩家获胜" : "Boss 获胜";
            }
        }

        /// <summary>
        /// 组装 Boss 区域的提示文本：AI 兵位剩余回合、等待空位或手动模式放牌提示。
        /// 输入：快照与配置；输出：单行或多行文本。
        /// </summary>
        string BuildBossLine(GameSnapshot snapshot, GameConfiguration configuration)
        {
            var sb = new StringBuilder();
            if (snapshot.PlayerControlsBoss)
            {
                var bossName = configuration != null && configuration.Cards.TryGetValue(configuration.Rules.BossCardType, out var boss) ? boss.Name : configuration?.Rules.BossCardType;
                sb.Append("手动 Boss 模式：放牌 ").Append(configuration?.Rules.BossPlays ?? 0).Append(" 张（").Append(bossName).Append("）");
                return sb.ToString();
            }
            sb.Append("AI 兵位：");
            foreach (var slot in snapshot.BossReplacements)
            {
                sb.Append(slot.SlotId).Append("=");
                if (slot.UnitId.HasValue) sb.Append("在场");
                else if (slot.WaitingForSpace) sb.Append("等待空位");
                else sb.Append("补兵 ").Append(slot.RemainingRounds).Append(" 回合");
                sb.Append("  ");
            }
            return sb.ToString();
        }

        /// <summary>组装阶段提示文本。输入：快照；输出：提示行（无提示时为空串）。</summary>
        string BuildPhaseLine(GameSnapshot snapshot)
        {
            if (snapshot.Phase == MatchPhase.Discard) return $"请弃牌至上限（剩余 {snapshot.Hand.Count} 张）";
            if (snapshot.ActiveSide == Side.Boss && !snapshot.PlayerControlsBoss) return "Boss 回合（AI 自动结算）";
            return string.Empty;
        }

        /// <summary>
        /// 组装选中对象详情。
        /// 输入：选择上下文、快照与配置；输出：详情文本。
        /// 行为：手牌显示配置属性与部署限制，单位显示实时生命与属性，家显示生命。
        /// </summary>
        string BuildDetail(Selection selection, GameSnapshot snapshot, GameConfiguration configuration)
        {
            if (selection.Kind == SelectionKind.Hand)
            {
                var card = snapshot.Hand.FirstOrDefault(c => c.Id == selection.Id);
                return card != null && configuration.Cards.TryGetValue(card.TypeId, out var arch) ? CardText.DetailLine(arch, Side.Player) : string.Empty;
            }
            if (selection.Kind == SelectionKind.Unit)
            {
                var unit = snapshot.Units.FirstOrDefault(u => u.Id == selection.Id);
                if (unit == null) return string.Empty;
                var text = configuration.Cards.TryGetValue(unit.TypeId, out var arch) ? CardText.DetailLine(arch, unit.Owner) : string.Empty;
                return text + Environment.NewLine + $"当前生命 {unit.Hp} · 行动机会 {(unit.CanAct ? "可用" : unit.Acted ? "已用完" : "维持费不足")}";
            }
            if (selection.Kind == SelectionKind.Home)
            {
                var home = snapshot.Homes.FirstOrDefault(h => h.Id == selection.Id);
                if (home == null) return string.Empty;
                return $"{(home.Owner == Side.Player ? "玩家" : "Boss")}的家 · 生命 {home.Hp} · {(home.Acted ? "本回合已移动" : "本回合可移动一格")}";
            }
            if (selection.Kind == SelectionKind.BossDeployment) return "选择一个高亮格放置 Boss 卡";
            return "点击手牌、己方单位或己方家进行操作";
        }

        /// <summary>增量追加日志。输入：快照日志列表；输出：无。</summary>
        void AppendLog(IReadOnlyList<GameEvent> log)
        {
            if (_logContent == null) return;
            for (var i = _renderedLogCount; i < log.Count; i++)
            {
                var line = new Label(log[i].Message);
                line.AddToClassList("log-line");
                _logContent.Add(line);
            }
            _renderedLogCount = log.Count;
            if (_logScroll != null) _logScroll.scrollOffset = new Vector2(0f, float.MaxValue);
        }

        /// <summary>重置日志渲染计数。输入：无；输出：无。用途：重开后清空日志列表。</summary>
        public void ResetLog() { if (_logContent != null) _logContent.Clear(); _renderedLogCount = 0; }

        /// <summary>显示一条错误或提示。输入：文本；输出：无。</summary>
        public void ShowError(string message)
        {
            if (_errorLabel == null) return;
            _errorLabel.text = message ?? string.Empty;
            _errorLabel.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>
        /// 显示/隐藏模态遮罩。
        /// 输入：是否显示；输出：无。
        /// 为什么需要：UI Toolkit 面板位于 UGUI 之上时仍可能出现点击穿透，
        /// 规则层另有输入闸门，这里只负责视觉与 UGUI 事件的拦截。
        /// </summary>
        public void SetModalVisible(bool visible)
        {
            if (_modalOverlay != null) _modalOverlay.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}
