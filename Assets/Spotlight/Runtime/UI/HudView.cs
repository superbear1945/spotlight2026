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
        /// <summary>进入炸弹选格模式。</summary>
        public event Action BombDeployRequested;
        /// <summary>跳过本回合炸弹。</summary>
        public event Action BombSkipRequested;
        /// <summary>点击“重开”。</summary>
        public event Action RestartRequested;
        /// <summary>点击“配置/调试”。</summary>
        public event Action DebugRequested;
        /// <summary>点击“离场”：请求让当前选中的己方单位离开棋盘。</summary>
        public event Action WithdrawRequested;
        /// <summary>点击齿轮：请求进入菜单（隐藏战斗界面，显示原有面板与「继续 / 重开」）。</summary>
        public event Action MenuRequested;
        /// <summary>点击菜单里的“继续”：请求关闭菜单并恢复对局。</summary>
        public event Action MenuResumeRequested;

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
        /// <summary>“离场”按钮（仅在选中的己方单位可以离场时显示）。</summary>
        Button _withdrawButton;
        /// <summary>炸弹部署与跳过按钮，只有待处理机会时可见。</summary>
        Button _bombDeployButton, _bombSkipButton;
        /// <summary>齿轮按钮（战斗界面显示，菜单打开时隐藏）。</summary>
        Button _menuButton;
        /// <summary>菜单遮罩（含「继续 / 重开」两个按钮）。</summary>
        VisualElement _menuOverlay;
        /// <summary>战斗界面的左右详情区：左为我方选中，右为敌方选中。</summary>
        VisualElement _detailLeft, _detailRight;
        /// <summary>左右详情区的标题与正文。</summary>
        Label _detailLeftTitle, _detailLeftBody, _detailRightTitle, _detailRightBody;
        /// <summary>手牌同一行两侧的计数区与墓地 / 抽牌堆数字。</summary>
        VisualElement _pileLeft, _pileRight;
        Label _graveCount, _drawCount;
        /// <summary>原有的日志面板与 Boss 状态面板（默认隐藏，只在菜单中显示）。</summary>
        VisualElement _logPanel, _sidePanel;
        /// <summary>胜负遮罩。</summary>
        VisualElement _victoryOverlay;
        /// <summary>胜负文本。</summary>
        Label _victoryLabel;
        /// <summary>模态遮罩（配置面板打开时挡住棋盘点击）。</summary>
        VisualElement _modalOverlay;
        /// <summary>已渲染的日志条数，用于增量追加。</summary>
        int _renderedLogCount;
        /// <summary>已展示的末条事件，用于识别日志容量满后的滚动更新及阶段切换。</summary>
        GameEvent _lastRenderedEvent;

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
            _withdrawButton = _root.Q<Button>("withdraw-button");
            _bombDeployButton = _root.Q<Button>("bomb-deploy-button");
            _bombSkipButton = _root.Q<Button>("bomb-skip-button");
            _menuButton = _root.Q<Button>("menu-button");
            _menuOverlay = _root.Q<VisualElement>("menu-overlay");
            _detailLeft = _root.Q<VisualElement>("detail-left");
            _detailRight = _root.Q<VisualElement>("detail-right");
            _detailLeftTitle = _root.Q<Label>("detail-left-title");
            _detailLeftBody = _root.Q<Label>("detail-left-body");
            _detailRightTitle = _root.Q<Label>("detail-right-title");
            _detailRightBody = _root.Q<Label>("detail-right-body");
            _pileLeft = _root.Q<VisualElement>("pile-left");
            _pileRight = _root.Q<VisualElement>("pile-right");
            _graveCount = _root.Q<Label>("grave-count");
            _drawCount = _root.Q<Label>("draw-count");
            _logPanel = _root.Q<VisualElement>("log-panel");
            _sidePanel = _root.Q<VisualElement>("side-panel");
            _victoryOverlay = _root.Q<VisualElement>("victory-overlay");
            _victoryLabel = _root.Q<Label>("victory-label");
            _modalOverlay = _root.Q<VisualElement>("modal-overlay");

            if (_endTurnButton != null) _endTurnButton.clicked += () => EndTurnRequested?.Invoke();
            if (_skipButton != null) _skipButton.clicked += () => SkipDeployRequested?.Invoke();
            if (_withdrawButton != null) _withdrawButton.clicked += () => WithdrawRequested?.Invoke();
            if (_bombDeployButton != null) _bombDeployButton.clicked += () => BombDeployRequested?.Invoke();
            if (_bombSkipButton != null) _bombSkipButton.clicked += () => BombSkipRequested?.Invoke();
            if (_menuButton != null) _menuButton.clicked += () => MenuRequested?.Invoke();
            var menuResume = _root.Q<Button>("menu-resume-button");
            if (menuResume != null) menuResume.clicked += () => MenuResumeRequested?.Invoke();
            var menuRestart = _root.Q<Button>("menu-restart-button");
            if (menuRestart != null) menuRestart.clicked += () => RestartRequested?.Invoke();
            var debug = _root.Q<Button>("debug-button");
            if (debug != null) debug.clicked += () => DebugRequested?.Invoke();
            var victoryRestart = _root.Q<Button>("victory-restart-button");
            if (victoryRestart != null) victoryRestart.clicked += () => RestartRequested?.Invoke();
            if (_victoryOverlay != null) _victoryOverlay.style.display = DisplayStyle.None;
            if (_modalOverlay != null) _modalOverlay.style.display = DisplayStyle.None;
            if (_errorLabel != null) _errorLabel.style.display = DisplayStyle.None;
            if (_withdrawButton != null) _withdrawButton.style.display = DisplayStyle.None;
            // 战斗界面是默认视图：原有面板（日志、Boss 状态与选中详情）先隐藏，只在齿轮菜单里显示。
            SetMenuVisible(false);

            // 调试入口只在编辑器或 Development Build 提供。
            if (debug != null && !DebugSupport.IsDebugAvailable) debug.style.display = DisplayStyle.None;
        }

        /// <summary>
        /// 渲染一帧界面。
        /// 输入：快照、本局配置、当前选择、规则层算出的合法动作，以及是否处于输入屏蔽期；输出：无。
        /// 行为：刷新文本、按钮的可用性与显隐、日志增量与胜负遮罩。
        /// 为什么需要合法动作：按钮的显隐必须来自规则层（界面不推导合法性），
        /// 例如“离场”按钮只在选中的己方单位确实可以离场时出现。
        /// 为什么需要输入屏蔽：Boss 回合演示或配置面板打开时，按钮要禁用而不只是遮罩，
        /// 避免 UI Toolkit 与 UGUI 之间的点击穿透。
        /// </summary>
        public void Render(GameSnapshot snapshot, GameConfiguration configuration, Selection selection, LegalActions actions, bool inputBlocked = false)
        {
            if (_root == null) return;
            var sideName = snapshot.ActiveSide == Side.Player ? "玩家" : "Boss";
            if (_turnLabel != null) _turnLabel.text = $"第 {snapshot.Round} 回合 · {sideName}行动";
            if (_resourceLabel != null) _resourceLabel.text = $"资源 {snapshot.Resources}";
            if (_playsLabel != null) _playsLabel.text = $"剩余出牌 {snapshot.PlaysRemaining}";
            if (_bossLabel != null) _bossLabel.text = BuildBossLine(snapshot, configuration);
            if (_phaseLabel != null) _phaseLabel.text = BuildPhaseLine(snapshot);
            if (_detailLabel != null) _detailLabel.text = BuildDetail(selection, snapshot, configuration);
            if (_endTurnButton != null) _endTurnButton.SetEnabled(!inputBlocked && snapshot.Phase != MatchPhase.Finished && !snapshot.BossBombPending);
            _bombDeployButton?.SetEnabled(!inputBlocked);
            _bombSkipButton?.SetEnabled(!inputBlocked);
            var bombReady = snapshot.BossBombPending && snapshot.Phase == MatchPhase.Action;
            if (_bombDeployButton != null) _bombDeployButton.style.display = bombReady ? DisplayStyle.Flex : DisplayStyle.None;
            if (_bombSkipButton != null) _bombSkipButton.style.display = bombReady ? DisplayStyle.Flex : DisplayStyle.None;
            if (_skipButton != null)
            {
                var showSkip = snapshot.PlayerControlsBoss && snapshot.ActiveSide == Side.Boss && snapshot.PlaysRemaining > 0;
                _skipButton.style.display = showSkip ? DisplayStyle.Flex : DisplayStyle.None;
                _skipButton.SetEnabled(!inputBlocked && !snapshot.BossBombPending);
            }
            if (_withdrawButton != null)
            {
                // 是否可离场由规则层给出；未选中单位或不该离场时一律隐藏。
                var canWithdraw = actions != null && actions.CanWithdraw;
                _withdrawButton.style.display = canWithdraw ? DisplayStyle.Flex : DisplayStyle.None;
            }
            FillDetailFaces(selection, snapshot, configuration);
            if (_graveCount != null) _graveCount.text = snapshot.Graveyard.Count.ToString();
            if (_drawCount != null) _drawCount.text = snapshot.DrawPile.Count.ToString();
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
            foreach (var bomb in snapshot.BossBombs)
                sb.Append("炸弹 ").Append(bomb.Position).Append(" · 第 ").Append(bomb.DueRound).Append(" 回合玩家行动前爆炸 · 伤害 ").Append(bomb.Damage).AppendLine();
            if (snapshot.BossBombPending) sb.AppendLine("炸弹机会：请先布置或跳过");
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
            if (snapshot.BossBombPending) return "先布置炸弹或跳过炸弹，再开始玩家行动";
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
            if (selection.Kind == SelectionKind.BossBombDeployment) return "选择高亮列中的任意格布置炸弹，可与单位或家同格";
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

        /// <summary>同步阶段日志。输入：当前展示快照的日志；输出：有界列表，滚动截断时仍更新且不重复追加。</summary>
        void AppendLog(IReadOnlyList<GameEvent> log)
        {
            if (_logContent == null) return;
            var last = log.Count > 0 ? log[log.Count - 1] : null;
            if (_renderedLogCount == log.Count && ReferenceEquals(last, _lastRenderedEvent)) return;
            _logContent.Clear();
            for (var i = 0; i < log.Count; i++)
            {
                var line = new Label(log[i].Message);
                line.AddToClassList("log-line");
                _logContent.Add(line);
            }
            _renderedLogCount = log.Count;
            _lastRenderedEvent = last;
            if (_logScroll != null) _logScroll.scrollOffset = new Vector2(0f, float.MaxValue);
        }

        /// <summary>
        /// 切换“战斗界面”与“原有面板”两套视图。
        /// 输入：true 表示进入齿轮菜单；输出：无。
        /// 行为：进入菜单时隐藏战斗界面（左右详情区、卡牌计数、齿轮），显示原有面板（日志、Boss 状态与选中详情）
        /// 以及中间卡片上的「继续 / 重开」；退出时反向恢复。
        /// 为什么保留原有面板：日志与兵位计时是排查问题的主要视图，做成菜单里的第二套界面后，
        /// 战斗界面可以保持干净，同时这些信息不会丢失。
        /// 棋盘与手牌不受影响：它们是对局本体，两套视图下都可见。
        /// </summary>
        public void SetMenuVisible(bool visible)
        {
            ShowElement(_detailLeft, !visible);
            ShowElement(_detailRight, !visible);
            ShowElement(_pileLeft, !visible);
            ShowElement(_pileRight, !visible);
            ShowElement(_menuButton, !visible);
            ShowElement(_logPanel, visible);
            ShowElement(_sidePanel, visible);
            ShowElement(_menuOverlay, visible);
        }

        /// <summary>按显示标记设置元素显隐。输入：元素与是否显示；输出：无。</summary>
        static void ShowElement(VisualElement element, bool visible)
        {
            if (element != null) element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// 填充左右详情区（单选中槽）。
        /// 输入：当前选择、快照与配置；输出：无。
        /// 行为：我方的手牌或单位显示在左侧，敌方单位显示在右侧；未选中、选中家或 Boss 部署阶段时两侧都清空。
        /// 为什么保持单槽：界面同一时刻只有一个选择，另一侧留空可以避免“两侧都被选中”的误导；
        /// 区域本身仍然占位，后续要改成对比或其它用途时不必改布局。
        /// 为什么属性文本不在这里拼：卡面文本统一由 CardText 提供，界面只负责摆放。
        /// </summary>
        void FillDetailFaces(Selection selection, GameSnapshot snapshot, GameConfiguration configuration)
        {
            var leftTitle = string.Empty;
            var leftBody = string.Empty;
            var rightTitle = string.Empty;
            var rightBody = string.Empty;
            if (snapshot != null && configuration != null)
            {
                if (selection.Kind == SelectionKind.Hand)
                {
                    var card = snapshot.Hand.FirstOrDefault(c => c.Id == selection.Id);
                    if (card != null && configuration.Cards.TryGetValue(card.TypeId, out var handArch))
                    {
                        leftTitle = CardText.CategoryLabel(handArch.Category) + " · 玩家";
                        leftBody = CardText.DetailLine(handArch, Side.Player);
                    }
                }
                else if (selection.Kind == SelectionKind.Unit)
                {
                    var unit = snapshot.Units.FirstOrDefault(u => u.Id == selection.Id);
                    if (unit != null && configuration.Cards.TryGetValue(unit.TypeId, out var unitArch))
                    {
                        var title = CardText.CategoryLabel(unitArch.Category) + (unit.Owner == Side.Player ? " · 玩家" : " · Boss");
                        var body = CardText.DetailLine(unitArch, unit.Owner) + Environment.NewLine
                            + $"当前生命 {unit.Hp} · 行动机会 {(unit.CanAct ? "可用" : unit.Acted ? "已用完" : "维持费不足")}";
                        if (unit.Owner == Side.Player) { leftTitle = title; leftBody = body; }
                        else { rightTitle = title; rightBody = body; }
                    }
                }
            }
            if (_detailLeftTitle != null) _detailLeftTitle.text = leftTitle;
            if (_detailLeftBody != null) _detailLeftBody.text = leftBody;
            if (_detailRightTitle != null) _detailRightTitle.text = rightTitle;
            if (_detailRightBody != null) _detailRightBody.text = rightBody;
        }

        /// <summary>重置日志渲染计数。输入：无；输出：无。用途：重开后清空日志列表。</summary>
        public void ResetLog() { if (_logContent != null) _logContent.Clear(); _renderedLogCount = 0; _lastRenderedEvent = null; }

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
