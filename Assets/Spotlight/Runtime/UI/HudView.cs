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
        /// <summary>进入刷怪卡合法空格选择模式。</summary>
        public event Action SpawnerDeployRequested;
        /// <summary>放弃本次刷怪卡部署机会。</summary>
        public event Action SpawnerSkipRequested;
        /// <summary>点击“重开”。</summary>
        public event Action RestartRequested;
        /// <summary>暂停面板打开状态变化；true 表示需要冻结对局输入与时间，false 表示恢复。</summary>
        public event Action<bool> PauseChanged;
        /// <summary>点击暂停面板中的“退出战斗”。</summary>
        public event Action ExitRequested;
        /// <summary>点击“配置/调试”。</summary>
        public event Action DebugRequested;
        /// <summary>点击“离场”：请求让当前选中的己方单位离开棋盘。</summary>
        public event Action WithdrawRequested;

        /// <summary>界面根元素。</summary>
        VisualElement _root;
        /// <summary>回合与行动方文本。</summary>
        Label _turnLabel;
        /// <summary>资源文本。</summary>
        Label _resourceLabel;
        /// <summary>剩余放牌次数文本。</summary>
        Label _playsLabel;
        /// <summary>回合圆盘中的两位回合数。</summary>
        Label _roundNumberLabel;
        /// <summary>玩家家的实时生命。</summary>
        Label _playerLifeLabel;
        /// <summary>Boss 家的实时生命。</summary>
        Label _bossLifeLabel;
        /// <summary>回合圆盘根节点，用于切换玩家/Boss 回合外观。</summary>
        VisualElement _roundCounter;
        /// <summary>Boss 自动行动时显示在回合圆盘右侧的“对方回合”状态牌。</summary>
        VisualElement _opponentTurnIndicator;
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
        /// <summary>完整日志面板，由记录按钮控制显隐。</summary>
        VisualElement _logPanel;
        /// <summary>选中详情面板；没有选择或阶段提示时保持隐藏，避免遮挡棋盘。</summary>
        VisualElement _sidePanel;
        /// <summary>日志内容容器。</summary>
        VisualElement _logContent;
        /// <summary>战斗记录入口按钮，只控制日志面板显示，不修改规则状态。</summary>
        Button _historyButton;
        /// <summary>右上角暂停入口。</summary>
        Button _pauseButton;
        /// <summary>结束回合按钮。</summary>
        Button _endTurnButton;
        /// <summary>跳过放牌按钮。</summary>
        Button _skipButton;
        /// <summary>“离场”按钮（仅在选中的己方单位可以离场时显示）。</summary>
        Button _withdrawButton;
        /// <summary>炸弹部署与跳过按钮，只有待处理机会时可见。</summary>
        Button _bombDeployButton, _bombSkipButton;
        /// <summary>刷怪卡部署与跳过按钮，仅在 Boss 技能准备窗口显示。</summary>
        Button _spawnerDeployButton, _spawnerSkipButton;
        /// <summary>胜负遮罩。</summary>
        VisualElement _victoryOverlay;
        /// <summary>胜负文本。</summary>
        Label _victoryLabel;
        /// <summary>玩家胜利时显示的美术标题。</summary>
        VisualElement _victoryArt;
        /// <summary>玩家失败时显示的美术标题。</summary>
        VisualElement _defeatArt;
        /// <summary>模态遮罩（配置面板打开时挡住棋盘点击）。</summary>
        VisualElement _modalOverlay;
        /// <summary>暂停面板根节点；显示时位于通用模态遮罩之上。</summary>
        VisualElement _pauseOverlay;
        /// <summary>暂停面板中的关闭、继续、重开与退出按钮。</summary>
        Button _pauseCloseButton, _pauseContinueButton, _pauseRestartButton, _pauseExitButton;
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
            _roundNumberLabel = _root.Q<Label>("round-number-label");
            _playerLifeLabel = _root.Q<Label>("player-life-label");
            _bossLifeLabel = _root.Q<Label>("boss-life-label");
            _roundCounter = _root.Q<VisualElement>("round-counter");
            _opponentTurnIndicator = _root.Q<VisualElement>("opponent-turn-indicator");
            _bossLabel = _root.Q<Label>("boss-info-label");
            _phaseLabel = _root.Q<Label>("phase-label");
            _detailLabel = _root.Q<Label>("unit-detail");
            _errorLabel = _root.Q<Label>("error-label");
            _logScroll = _root.Q<ScrollView>("log-list");
            _logPanel = _root.Q<VisualElement>("log-panel");
            _sidePanel = _root.Q<VisualElement>("side-panel");
            _logContent = _root.Q<VisualElement>("log-content");
            _historyButton = _root.Q<Button>("history-button");
            _pauseButton = _root.Q<Button>("pause-button");
            _endTurnButton = _root.Q<Button>("end-turn-button");
            _skipButton = _root.Q<Button>("skip-deploy-button");
            _withdrawButton = _root.Q<Button>("withdraw-button");
            _bombDeployButton = _root.Q<Button>("bomb-deploy-button");
            _bombSkipButton = _root.Q<Button>("bomb-skip-button");
            _spawnerDeployButton = _root.Q<Button>("spawner-deploy-button");
            _spawnerSkipButton = _root.Q<Button>("spawner-skip-button");
            _victoryOverlay = _root.Q<VisualElement>("victory-overlay");
            _victoryLabel = _root.Q<Label>("victory-label");
            _victoryArt = _root.Q<VisualElement>("victory-art");
            _defeatArt = _root.Q<VisualElement>("defeat-art");
            _modalOverlay = _root.Q<VisualElement>("modal-overlay");
            _pauseOverlay = _root.Q<VisualElement>("pause-overlay");
            _pauseCloseButton = _root.Q<Button>("pause-close-button");
            _pauseContinueButton = _root.Q<Button>("pause-continue-button");
            _pauseRestartButton = _root.Q<Button>("pause-restart-button");
            _pauseExitButton = _root.Q<Button>("pause-exit-button");

            if (_endTurnButton != null) _endTurnButton.clicked += () => EndTurnRequested?.Invoke();
            if (_skipButton != null) _skipButton.clicked += () => SkipDeployRequested?.Invoke();
            if (_withdrawButton != null) _withdrawButton.clicked += () => WithdrawRequested?.Invoke();
            if (_bombDeployButton != null) _bombDeployButton.clicked += () => BombDeployRequested?.Invoke();
            if (_bombSkipButton != null) _bombSkipButton.clicked += () => BombSkipRequested?.Invoke();
            if (_spawnerDeployButton != null) _spawnerDeployButton.clicked += () => SpawnerDeployRequested?.Invoke();
            if (_spawnerSkipButton != null) _spawnerSkipButton.clicked += () => SpawnerSkipRequested?.Invoke();
            if (_historyButton != null) _historyButton.clicked += ToggleLogPanel;
            if (_pauseButton != null) { _pauseButton.clicked -= OpenPausePanel; _pauseButton.clicked += OpenPausePanel; }
            if (_pauseCloseButton != null) { _pauseCloseButton.clicked -= ClosePausePanel; _pauseCloseButton.clicked += ClosePausePanel; }
            if (_pauseContinueButton != null) { _pauseContinueButton.clicked -= ClosePausePanel; _pauseContinueButton.clicked += ClosePausePanel; }
            if (_pauseRestartButton != null) { _pauseRestartButton.clicked -= RestartFromPause; _pauseRestartButton.clicked += RestartFromPause; }
            if (_pauseExitButton != null) { _pauseExitButton.clicked -= ExitFromPause; _pauseExitButton.clicked += ExitFromPause; }
            var restart = _root.Q<Button>("restart-button");
            if (restart != null) restart.clicked += () => RestartRequested?.Invoke();
            var debug = _root.Q<Button>("debug-button");
            if (debug != null) debug.clicked += () => DebugRequested?.Invoke();
            var victoryRestart = _root.Q<Button>("victory-restart-button");
            if (victoryRestart != null) victoryRestart.clicked += () => RestartRequested?.Invoke();
            if (_victoryOverlay != null) _victoryOverlay.style.display = DisplayStyle.None;
            if (_victoryArt != null) _victoryArt.style.display = DisplayStyle.None;
            if (_defeatArt != null) _defeatArt.style.display = DisplayStyle.None;
            if (_modalOverlay != null) _modalOverlay.style.display = DisplayStyle.None;
            if (_pauseOverlay != null) _pauseOverlay.style.display = DisplayStyle.None;
            if (_errorLabel != null) _errorLabel.style.display = DisplayStyle.None;
            if (_withdrawButton != null) _withdrawButton.style.display = DisplayStyle.None;

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
            if (_resourceLabel != null) _resourceLabel.text = snapshot.Resources.ToString();
            if (_playsLabel != null) _playsLabel.text = $"剩余出牌 {snapshot.PlaysRemaining}";
            if (_roundNumberLabel != null) _roundNumberLabel.text = snapshot.Round.ToString("00");
            if (_roundCounter != null) _roundCounter.EnableInClassList("opponent-turn", snapshot.ActiveSide == Side.Boss);
            var showEndTurn = snapshot.ActiveSide == Side.Player || snapshot.PlayerControlsBoss;
            var showOpponentTurn = snapshot.ActiveSide == Side.Boss && !snapshot.PlayerControlsBoss;
            if (_endTurnButton != null) _endTurnButton.style.display = showEndTurn ? DisplayStyle.Flex : DisplayStyle.None;
            if (_opponentTurnIndicator != null) _opponentTurnIndicator.style.display = showOpponentTurn ? DisplayStyle.Flex : DisplayStyle.None;
            var playerHome = snapshot.Homes.FirstOrDefault(h => h.Owner == Side.Player);
            var bossHome = snapshot.Homes.FirstOrDefault(h => h.Owner == Side.Boss);
            if (_playerLifeLabel != null) _playerLifeLabel.text = playerHome != null ? playerHome.Hp.ToString() : "—";
            if (_bossLifeLabel != null) _bossLifeLabel.text = bossHome != null ? bossHome.Hp.ToString() : "—";
            var bossLine = BuildBossLine(snapshot, configuration);
            var phaseLine = BuildPhaseLine(snapshot);
            var detailLine = BuildDetail(selection, snapshot, configuration);
            if (_bossLabel != null) _bossLabel.text = bossLine;
            if (_phaseLabel != null) _phaseLabel.text = phaseLine;
            if (_detailLabel != null) _detailLabel.text = detailLine;
            if (_sidePanel != null)
            {
                var showDetails = selection.Kind != SelectionKind.None || !string.IsNullOrEmpty(phaseLine);
                _sidePanel.style.display = showDetails ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (_endTurnButton != null) _endTurnButton.SetEnabled(showEndTurn && !inputBlocked && snapshot.Phase != MatchPhase.Finished && !snapshot.BossBombPending && !snapshot.BossSpawnerPending);
            var spawnerReady = snapshot.BossSpawnerPending && snapshot.Phase == MatchPhase.Action;
            if (_spawnerDeployButton != null) _spawnerDeployButton.style.display = spawnerReady ? DisplayStyle.Flex : DisplayStyle.None;
            if (_spawnerSkipButton != null) _spawnerSkipButton.style.display = spawnerReady ? DisplayStyle.Flex : DisplayStyle.None;
            _spawnerDeployButton?.SetEnabled(!inputBlocked);
            _spawnerSkipButton?.SetEnabled(!inputBlocked);
            _bombDeployButton?.SetEnabled(!inputBlocked);
            _bombSkipButton?.SetEnabled(!inputBlocked);
            var bombReady = snapshot.BossBombPending && snapshot.Phase == MatchPhase.Action;
            if (_bombDeployButton != null) _bombDeployButton.style.display = bombReady ? DisplayStyle.Flex : DisplayStyle.None;
            if (_bombSkipButton != null) _bombSkipButton.style.display = bombReady ? DisplayStyle.Flex : DisplayStyle.None;
            if (_skipButton != null)
            {
                var showSkip = snapshot.PlayerControlsBoss && snapshot.ActiveSide == Side.Boss && snapshot.PlaysRemaining > 0;
                _skipButton.style.display = showSkip ? DisplayStyle.Flex : DisplayStyle.None;
                _skipButton.SetEnabled(!inputBlocked && !snapshot.BossBombPending && !snapshot.BossSpawnerPending);
            }
            if (_withdrawButton != null)
            {
                // 是否可离场由规则层给出；未选中单位或不该离场时一律隐藏。
                var canWithdraw = actions != null && actions.CanWithdraw;
                _withdrawButton.style.display = canWithdraw ? DisplayStyle.Flex : DisplayStyle.None;
            }
            AppendLog(snapshot.Log);
            if (_victoryOverlay != null)
            {
                var finished = snapshot.Phase == MatchPhase.Finished;
                _victoryOverlay.style.display = finished ? DisplayStyle.Flex : DisplayStyle.None;
                if (_victoryArt != null)
                    _victoryArt.style.display = finished && snapshot.Outcome == MatchOutcome.PlayerWins ? DisplayStyle.Flex : DisplayStyle.None;
                if (_defeatArt != null)
                    _defeatArt.style.display = finished && snapshot.Outcome == MatchOutcome.BossWins ? DisplayStyle.Flex : DisplayStyle.None;
                if (finished && _victoryLabel != null)
                    _victoryLabel.text = snapshot.Outcome == MatchOutcome.Draw ? "平局" : snapshot.Outcome == MatchOutcome.PlayerWins ? "玩家获胜" : "Boss 获胜";
            }
        }

        /// <summary>
        /// 切换对局日志面板的显示状态。
        /// 输入：无；输出：无。
        /// 为什么由 HudView 自己处理：这是纯界面折叠行为，不改变对局状态，也不应该向规则层新增命令。
        /// </summary>
        void ToggleLogPanel()
        {
            if (_logPanel == null) return;
            var hide = _logPanel.resolvedStyle.display != DisplayStyle.None;
            _logPanel.style.display = hide ? DisplayStyle.None : DisplayStyle.Flex;
            _historyButton?.EnableInClassList("log-hidden", hide);
        }

        /// <summary>
        /// 打开暂停面板并通知对局层冻结输入与时间。
        /// 输入：无；输出：无（通过 PauseChanged(true) 输出状态变化）。
        /// 为什么由 HudView 控制显隐：暂停面板属于纯界面状态，但真正的输入与时间冻结仍由 GameBootstrap 统一处理。
        /// </summary>
        void OpenPausePanel()
        {
            if (_pauseOverlay == null) return;
            _pauseOverlay.style.display = DisplayStyle.Flex;
            PauseChanged?.Invoke(true);
        }

        /// <summary>
        /// 关闭暂停面板并通知对局层恢复。
        /// 输入：无；输出：无（通过 PauseChanged(false) 输出状态变化）。
        /// 同时服务“继续战斗”和右上关闭按钮，保证两条入口行为完全一致。
        /// </summary>
        void ClosePausePanel()
        {
            if (_pauseOverlay == null) return;
            _pauseOverlay.style.display = DisplayStyle.None;
            PauseChanged?.Invoke(false);
        }

        /// <summary>
        /// 从暂停面板重新开始本局。
        /// 输入：无；输出：先关闭暂停状态，再通过 RestartRequested 请求对局层重建当前配置的对局。
        /// 为什么先恢复暂停：重开后的表现协程与 DOTween 不能继承 timeScale=0。
        /// </summary>
        void RestartFromPause()
        {
            ClosePausePanel();
            RestartRequested?.Invoke();
        }

        /// <summary>
        /// 请求退出战斗。
        /// 输入：无；输出：触发 ExitRequested；实际退出策略由 GameBootstrap 决定，视图不直接终止应用。
        /// </summary>
        void ExitFromPause() => ExitRequested?.Invoke();

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
            if (snapshot.BossSpawnerPending) sb.AppendLine("刷怪卡机会：请先部署或跳过");
            var spawner = snapshot.Units.FirstOrDefault(u => u.IsBossSpawner);
            if (spawner != null)
                sb.Append("刷怪卡：").Append(spawner.Hp).Append(" 血 · ").AppendLine(snapshot.Round <= spawner.DeployedRound ? "下个 Boss 回合开始生成战机" : "每个 Boss 回合尝试生成一架战机");
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
            if (snapshot.BossSpawnerPending) return "先部署刷怪卡或跳过，再开始 Boss 普通行动";
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
            if (selection.Kind == SelectionKind.BossSpawnerDeployment) return "选择高亮空格部署刷怪卡，列号从玩家一侧起算";
            if (selection.Kind == SelectionKind.Hand)
            {
                var card = snapshot.Hand.FirstOrDefault(c => c.Id == selection.Id);
                return card != null && configuration.Cards.TryGetValue(card.TypeId, out var arch) ? CardText.DetailLine(arch, Side.Player) : string.Empty;
            }
            if (selection.Kind == SelectionKind.Unit)
            {
                var unit = snapshot.Units.FirstOrDefault(u => u.Id == selection.Id);
                if (unit == null) return string.Empty;
                if (unit.IsBossSpawner) return $"{configuration.Cards[unit.TypeId].Name} · 当前生命 {unit.Hp}\n固定据点，不能攻击、移动或互换。下个 Boss 回合起，每回合在周围八格生成一架战机；满格时随机顶替玩家单位，无可用位置则暂停。";
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
