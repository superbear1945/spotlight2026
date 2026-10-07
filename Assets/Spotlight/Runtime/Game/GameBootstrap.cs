// 对局引导器：唯一负责“加载配置 → 创建对局 → 绑定视图 → 进入回合”的组件。
// 为什么需要单一入口：规则层、UI 与调试面板都需要同一份对局实例，
// 若各处自行创建 GameSession，就会出现“界面显示一局、规则运行另一局”的隐性分叉。
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 对局引导与命令转发组件（挂在场景根节点）。
    /// 输入：Inspector 上的配置资产与视图引用、玩家的点击与按钮事件；
    /// 输出：视图刷新、动效播放与错误提示。
    /// 职责边界：Bootstrap 只做“选择 → 组装命令 → 提交 → 刷新”，不判断合法性，
    /// 所有规则判断都回到 GameSession，保证玩家与 AI 使用同一套结算。
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        [Header("配置")]
        /// <summary>配置资产；为空时尝试从 Resources/SpotlightConfig 加载。</summary>
        [SerializeField] SpotlightConfigAsset _configAsset;
        /// <summary>随机种子；0 表示每次运行随机。</summary>
        [SerializeField] int _seed;
        /// <summary>历史日志容量；0 表示使用表现配置中的值。</summary>
        [SerializeField] int _logLimit;

        [Header("视图")]
        /// <summary>棋盘视图。</summary>
        [SerializeField] BoardView _boardView;
        /// <summary>手牌视图。</summary>
        [SerializeField] HandView _handView;
        /// <summary>HUD 视图（UI Toolkit）。</summary>
        [SerializeField] HudView _hudView;
        /// <summary>动效播放器。</summary>
        [SerializeField] CardFeedback _feedback;
        /// <summary>配置调试面板。</summary>
        [SerializeField] ConfigDebugPanel _debugPanel;

        /// <summary>当前对局实例；唯一状态所有者。</summary>
        GameSession _session;
        /// <summary>当前生效配置（可能来自调试面板“应用并重开”）。</summary>
        GameConfiguration _configuration;
        /// <summary>当前界面选择上下文。</summary>
        Selection _selection;
        /// <summary>上一帧快照，用于计算移动动画起点。</summary>
        GameSnapshot _previousSnapshot;

        /// <summary>当前生效的配置资产，供调试面板“恢复默认”使用。</summary>
        public SpotlightConfigAsset CurrentAsset => ConfigLoader.LoadAsset(_configAsset);

        /// <summary>当前对局实例（只读，供调试与测试使用）。</summary>
        public GameSession Session => _session;

        /// <summary>
        /// 初始化对局并绑定视图。
        /// 输入：Inspector 配置；输出：无（失败时在界面显示原因并阻止开局）。
        /// </summary>
        void Start()
        {
            _configAsset = ConfigLoader.LoadAsset(_configAsset);
            if (!ConfigLoader.TryCreateConfiguration(out var configuration, out var error, _configAsset))
            {
                Debug.LogError("无法开局：" + error);
                _hudView?.Bind();
                _hudView?.ShowError("无法开局：" + error);
                return;
            }
            BindViewEvents();
            StartSession(configuration);
        }

        /// <summary>
        /// 绑定视图事件。
        /// 输入：无；输出：无。
        /// 所有输入都经此进入，形成统一输入入口，便于模态面板一次性阻断。
        /// </summary>
        void BindViewEvents()
        {
            if (_boardView != null) { _boardView.CellClicked -= OnCellClicked; _boardView.CellClicked += OnCellClicked; }
            if (_handView != null) { _handView.CardClicked -= OnCardClicked; _handView.CardClicked += OnCardClicked; }
            if (_hudView != null)
            {
                _hudView.Bind();
                _hudView.EndTurnRequested -= OnEndTurnRequested;
                _hudView.EndTurnRequested += OnEndTurnRequested;
                _hudView.SkipDeployRequested -= OnSkipDeployRequested;
                _hudView.SkipDeployRequested += OnSkipDeployRequested;
                _hudView.RestartRequested -= OnRestartRequested;
                _hudView.RestartRequested += OnRestartRequested;
                _hudView.DebugRequested -= OnDebugRequested;
                _hudView.DebugRequested += OnDebugRequested;
            }
            _debugPanel?.Bind(this);
        }

        /// <summary>
        /// 用指定配置开始一局。
        /// 输入：已校验配置；输出：无。
        /// 行为：清理旧对局与视图，创建新 GameSession，按配置构建棋盘并渲染首帧。
        /// </summary>
        public void StartSession(GameConfiguration configuration)
        {
            _configuration = configuration;
            var limit = _logLimit > 0 ? _logLimit : configuration.Presentation != null ? configuration.Presentation.LogLimit : 100;
            _session = new GameSession(configuration, new SeededRandom(DebugSupport.ResolveSeed(_seed)), Mathf.Max(1, limit));
            _selection = new Selection(SelectionKind.None);
            _boardView?.Build(configuration, _configAsset);
            _handView?.Build(_configAsset, configuration);
            _hudView?.ResetLog();
            _feedback?.Configure(configuration.Presentation);
            _previousSnapshot = _session.GetSnapshot();
            Refresh();
        }

        /// <summary>
        /// 用新配置重开对局。
        /// 输入：新配置（通常来自调试面板）；输出：无。
        /// 行为：停止全部动画并重建视图，保证旧对局的卡牌不会残留。
        /// </summary>
        public void RestartWith(GameConfiguration configuration)
        {
            _feedback?.KillAll();
            _boardView?.ClearUnits();
            _handView?.Clear();
            StartSession(configuration);
        }

        /// <summary>
        /// 打开/关闭模态输入闸门。
        /// 输入：是否屏蔽输入；输出：无。
        /// 同时作用于规则层（拒绝命令与高亮）与 HUD 遮罩（视觉与 UGUI 事件拦截）。
        /// </summary>
        public void SetModalOpen(bool blocked)
        {
            _session?.SetInputBlocked(blocked);
            _hudView?.SetModalVisible(blocked);
            if (blocked) _selection = new Selection(SelectionKind.None);
            Refresh();
        }

        /// <summary>玩家点击手牌。输入：被点击的卡牌视图；输出：无。</summary>
        void OnCardClicked(Card card)
        {
            if (_session == null || card == null || card.Record == null) return;
            var snapshot = _session.GetSnapshot();
            if (snapshot.Phase == MatchPhase.Discard)
            {
                Submit(new GameCommand(CommandKind.Discard, Side.Player, card.Record.Id));
                return;
            }
            if (snapshot.ActiveSide != Side.Player) { Show("只有玩家回合可以使用手牌"); return; }
            if (snapshot.Phase != MatchPhase.Action) { Show("当前不能出牌"); return; }
            _selection = new Selection(SelectionKind.Hand, card.Record.Id);
            _feedback?.PlaySelect(card, true);
            Refresh();
        }

        /// <summary>玩家点击棋盘格。输入：内部坐标；输出：无。</summary>
        void OnCellClicked(Cell cell)
        {
            if (_session == null) return;
            var snapshot = _session.GetSnapshot();
            var actions = _session.GetLegalActions(_selection);

            // 1) 部署：手牌选择或手动 Boss 未选单位时的部署高亮。
            if (actions.Deploy.Contains(cell))
            {
                if (_selection.Kind == SelectionKind.Hand)
                {
                    Submit(new GameCommand(CommandKind.Deploy, snapshot.ActiveSide, _selection.Id, cell));
                    _selection = new Selection(SelectionKind.None);
                    Refresh();
                    return;
                }
                if (snapshot.ActiveSide == Side.Boss && snapshot.PlayerControlsBoss)
                {
                    Submit(new GameCommand(CommandKind.BossDeploy, Side.Boss, 0, cell));
                    Refresh();
                    return;
                }
            }

            // 2) 已选单位：按 移动 → 攻击 → 互换 的优先级处理。
            if (_selection.Kind == SelectionKind.Unit)
            {
                if (actions.Move.Contains(cell))
                {
                    Submit(new GameCommand(CommandKind.Move, snapshot.ActiveSide, _selection.Id, cell));
                    return;
                }
                if (actions.Attack.Contains(cell))
                {
                    var targetId = FindTargetAt(snapshot, cell);
                    if (targetId != 0) Submit(new GameCommand(CommandKind.Attack, snapshot.ActiveSide, _selection.Id, targetId: targetId));
                    return;
                }
                if (actions.Swap.Contains(cell))
                {
                    var partner = FindUnitAt(snapshot, cell, snapshot.ActiveSide);
                    if (partner != 0) Submit(new GameCommand(CommandKind.Swap, snapshot.ActiveSide, _selection.Id, targetId: partner));
                    return;
                }
            }

            // 3) 已选家：移动一格。
            if (_selection.Kind == SelectionKind.Home && actions.Move.Contains(cell))
            {
                Submit(new GameCommand(CommandKind.MoveHome, snapshot.ActiveSide, _selection.Id, cell));
                return;
            }

            // 4) 否则尝试把点击解释为新的选择。
            SelectAt(cell, snapshot);
        }

        /// <summary>
        /// 把点击解释为选择。
        /// 输入：坐标与当前快照；输出：无。
        /// 行为：优先选择该格的单位（含敌方，用于查看详情）；否则选择己方家；都不是则清除选择。
        /// </summary>
        void SelectAt(Cell cell, GameSnapshot snapshot)
        {
            var unit = snapshot.Units.FirstOrDefault(u => u.Position == cell);
            if (unit != null)
            {
                _selection = new Selection(SelectionKind.Unit, unit.Id);
                _feedback?.PlaySelect(_boardView != null ? _boardView.GetCard(unit.Id) : null, true);
                Refresh();
                return;
            }
            var home = snapshot.Homes.FirstOrDefault(h => h.Position == cell);
            if (home != null && home.Owner == snapshot.ActiveSide)
            {
                _selection = new Selection(SelectionKind.Home, home.Id);
                Refresh();
                return;
            }
            _selection = snapshot.ActiveSide == Side.Boss && snapshot.PlayerControlsBoss && snapshot.PlaysRemaining > 0
                ? new Selection(SelectionKind.BossDeployment)
                : new Selection(SelectionKind.None);
            Refresh();
        }

        /// <summary>查找某格上的敌方目标实体 ID（单位优先，其次家）。输入：快照与坐标；输出：实体 ID（0 表示无）。</summary>
        static long FindTargetAt(GameSnapshot snapshot, Cell cell)
        {
            var unit = snapshot.Units.FirstOrDefault(u => u.Position == cell);
            if (unit != null) return unit.Id;
            var home = snapshot.Homes.FirstOrDefault(h => h.Position == cell);
            return home != null ? home.Id : 0;
        }

        /// <summary>查找某格上属于指定阵营的单位 ID。输入：快照、坐标与阵营；输出：单位 ID（0 表示无）。</summary>
        static long FindUnitAt(GameSnapshot snapshot, Cell cell, Side owner)
        {
            var unit = snapshot.Units.FirstOrDefault(u => u.Position == cell && u.Source.Owner == owner);
            return unit != null ? unit.Id : 0;
        }

        /// <summary>HUD 请求结束回合。输入：无；输出：无。</summary>
        void OnEndTurnRequested()
        {
            if (_session == null) return;
            Submit(new GameCommand(CommandKind.EndTurn, _session.GetSnapshot().ActiveSide));
        }

        /// <summary>HUD 请求“跳过放牌并结束”（仅手动 Boss 模式）。输入：无；输出：无。</summary>
        void OnSkipDeployRequested()
        {
            if (_session == null) return;
            Submit(new GameCommand(CommandKind.EndTurn, Side.Boss, skipDeployment: true));
        }

        /// <summary>HUD 请求重开：使用当前生效配置重新开局。输入：无；输出：无。</summary>
        void OnRestartRequested()
        {
            if (_configuration == null) return;
            RestartWith(_configuration);
        }

        /// <summary>HUD 请求打开配置面板。输入：无；输出：无。</summary>
        void OnDebugRequested() => _debugPanel?.Open();

        /// <summary>
        /// 提交命令并刷新。
        /// 输入：命令；输出：无。
        /// 行为：记录渲染前快照 → 执行 → 刷新视图 → 按事件播放在新快照上仍存在的对象动效。
        /// 失败时只提示原因，不改变界面以外的状态。
        /// </summary>
        void Submit(GameCommand command)
        {
            var before = _session.GetSnapshot();
            var result = _session.Execute(command);
            if (!result.Success)
            {
                Show(result.Error);
                _previousSnapshot = before;
                Refresh();
                return;
            }
            _hudView?.ShowError(null);
            _previousSnapshot = before;
            Refresh();
            PlayFeedback(before, _session.GetSnapshot(), result.Events);
        }

        /// <summary>刷新全部视图。输入：无；输出：无。</summary>
        void Refresh()
        {
            if (_session == null) return;
            var snapshot = _session.GetSnapshot();
            var actions = _session.GetLegalActions(_selection);
            _boardView?.Render(snapshot, actions, _selection);
            _handView?.Render(snapshot, _selection);
            _hudView?.Render(snapshot, _configuration, _selection);
        }

        /// <summary>
        /// 按事件播放必要动效。
        /// 输入：命令前快照、命令后快照与事件列表；输出：无。
        /// 只对仍然存在的对象播放动画，避免动画操作已被销毁的视图。
        /// </summary>
        void PlayFeedback(GameSnapshot before, GameSnapshot after, IReadOnlyList<GameEvent> events)
        {
            if (_boardView == null || _feedback == null) return;
            foreach (var e in events)
            {
                switch (e.Kind)
                {
                    case "deploy":
                    case "upgrade":
                        _feedback.PlayDeploy(_boardView.GetCard(e.SubjectId));
                        break;
                    case "move":
                        {
                            var previous = before.Units.FirstOrDefault(u => u.Id == e.SubjectId);
                            var current = after.Units.FirstOrDefault(u => u.Id == e.SubjectId);
                            if (previous != null && current != null)
                                _feedback.PlayMove(_boardView.GetCard(e.SubjectId), _boardView.CellOffset(previous.Position, current.Position));
                            break;
                        }
                    case "attack":
                        _feedback.PlayHit(_boardView.GetCard(e.SubjectId));
                        break;
                    case "death":
                        break;
                }
            }
        }

        /// <summary>显示提示。输入：文本；输出：无。</summary>
        void Show(string message)
        {
            _hudView?.ShowError(message);
        }
    }
}
