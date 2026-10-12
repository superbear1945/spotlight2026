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
        /// <summary>上一次播放过选中表现的卡牌视图；用来在选中变化时把上一张牌恢复原状。</summary>
        Card _selectedCard;
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
                _hudView.WithdrawRequested -= OnWithdrawRequested;
                _hudView.WithdrawRequested += OnWithdrawRequested;
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
            // 重开时旧卡牌会被重建，之前记录的选中视图属于已销毁对象，必须一并清空。
            _selectedCard = null;
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
            // 再次点击同一张手牌表示取消选中：规则层用 SelectionKind.None 表示“没有选择”，
            // 而玩家的 None 不会产生部署高亮（那条分支只服务手动 Boss 的未选单位状态），
            // 因此部署范围会一并撤掉；放大表现由 Refresh → SyncSelectionFeedback 自动恢复。
            // 弃牌阶段在上面已经分流，取消选中只作用于正常行动阶段。
            if (IsSameSelection(SelectionKind.Hand, card.Record.Id)) { ClearSelection(); return; }
            _selection = new Selection(SelectionKind.Hand, card.Record.Id);
            // 选中表现（放大新卡、恢复上一张卡）统一交给 Refresh → SyncSelectionFeedback，
            // 这里不再单独播放，否则会绕开“旧卡一定会恢复”的唯一路径。
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
                // 再次点击同一个单位（也包含用于查看详情的敌方单位）表示取消选中。
                if (IsSameSelection(SelectionKind.Unit, unit.Id)) { ClearSelection(); return; }
                _selection = new Selection(SelectionKind.Unit, unit.Id);
                Refresh();
                return;
            }
            var home = snapshot.Homes.FirstOrDefault(h => h.Position == cell);
            if (home != null && home.Owner == snapshot.ActiveSide)
            {
                // 再次点击自己的家同样取消选中，可移动格高亮随之消失。
                if (IsSameSelection(SelectionKind.Home, home.Id)) { ClearSelection(); return; }
                _selection = new Selection(SelectionKind.Home, home.Id);
                Refresh();
                return;
            }
            _selection = snapshot.ActiveSide == Side.Boss && snapshot.PlayerControlsBoss && snapshot.PlaysRemaining > 0
                ? new Selection(SelectionKind.BossDeployment)
                : new Selection(SelectionKind.None);
            Refresh();
        }

        /// <summary>
        /// 判断这次点击是否落在“已经选中的同一个对象”上。
        /// 输入：选择种类与实体 ID；输出：true 表示应视为“再次点击”，即取消选中。
        /// 为什么需要：手牌、棋盘单位与家都支持“点第二次取消选中”，
        /// 统一成一个判断可以保证三处行为一致，也避免以后新增可选对象时漏掉其中一处。
        /// </summary>
        bool IsSameSelection(SelectionKind kind, long id) => _selection.Kind == kind && _selection.Id == id;

        /// <summary>
        /// 取消当前选中。
        /// 输入：无；输出：无。
        /// 行为：把选择置为 None 后刷新；规则层对玩家的 None 不给出任何高亮（部署高亮分支只服务手动 Boss 的未选单位状态），
        /// 选中表现（放大补间与选中标记）由 Refresh → SyncSelectionFeedback 与各视图 Render 自动复位。
        /// </summary>
        void ClearSelection()
        {
            _selection = new Selection(SelectionKind.None);
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
            var unit = snapshot.Units.FirstOrDefault(u => u.Position == cell && u.Owner == owner);
            return unit != null ? unit.Id : 0;
        }

        /// <summary>HUD 请求结束回合。输入：无；输出：无。</summary>
        void OnEndTurnRequested()
        {
            if (_session == null) return;
            // 结束回合前先清空选中：Submit 内部的 Refresh 会以“未选中”状态渲染，
            // 于是棋盘与手牌的选中标记、放大补间以及 HUD 详情都会随这次回合切换一起复位，
            // 不会把本回合的选择残留到对手回合或下一回合。
            _selection = new Selection(SelectionKind.None);
            Submit(new GameCommand(CommandKind.EndTurn, _session.GetSnapshot().ActiveSide));
        }

        /// <summary>HUD 请求“跳过放牌并结束”（仅手动 Boss 模式）。输入：无；输出：无。</summary>
        void OnSkipDeployRequested()
        {
            if (_session == null) return;
            // 与“结束回合”同义：同样在结束前清空选中，避免手动 Boss 的选择残留到玩家回合。
            _selection = new Selection(SelectionKind.None);
            Submit(new GameCommand(CommandKind.EndTurn, Side.Boss, skipDeployment: true));
        }

        /// <summary>
        /// HUD 请求让当前选中的己方单位离场。
        /// 输入：无（读取 _selection）；输出：无。
        /// 行为：用选中的单位 ID 组装 Withdraw 命令交给规则层裁决（能否离场由规则层决定，界面不判断）；
        /// 提交后清空选中，因为该单位已经不在场，残留的选择会让 HUD 详情停留在已移除的单位上
        /// （与部署后清空选中是同一条约定）。
        /// </summary>
        void OnWithdrawRequested()
        {
            if (_session == null || _selection.Kind != SelectionKind.Unit) return;
            Submit(new GameCommand(CommandKind.Withdraw, _session.GetSnapshot().ActiveSide, _selection.Id));
            _selection = new Selection(SelectionKind.None);
            Refresh();
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

        /// <summary>
        /// 刷新全部视图与选中表现。
        /// 输入：无；输出：无。
        /// 顺序：先渲染三个视图（保证新选中的卡牌视图已存在、被移除的卡牌视图已销毁），
        /// 最后统一同步选中表现，因此“上一张牌恢复原状”只在这一处发生。
        /// </summary>
        void Refresh()
        {
            if (_session == null) return;
            var snapshot = _session.GetSnapshot();
            var actions = _session.GetLegalActions(_selection);
            _boardView?.Render(snapshot, actions, _selection);
            _handView?.Render(snapshot, _selection);
            _hudView?.Render(snapshot, _configuration, _selection, actions);
            SyncSelectionFeedback();
        }

        /// <summary>
        /// 同步选中表现：给新选中的卡牌播放放大，并把上一张已选中的卡牌恢复原状。
        /// 输入：无（读取 _selection 与两个视图的卡牌映射）；输出：无。
        /// 为什么必须有这一层：选中表现由两部分组成——Card.SetSelected 的选中标记（每次 Render 全量重算）
        /// 和 CardFeedback.PlaySelect 的放大补间（只在被点击的瞬间播放过）。
        /// 过去放大补间只在点击处调用，于是点第二张牌时第一张牌只关掉了标记、缩放永远停在放大值，
        /// 表现上就是“第一张牌没有回到手牌里”。收敛到 Refresh 之后统一处理后，
        /// 任何改变 _selection 的路径（点手牌、点棋盘、部署后清空、打开模态面板、重开）都会自动复位，
        /// 不需要每条分支各自记得取消选中。
        /// </summary>
        void SyncSelectionFeedback()
        {
            var current = ResolveSelectedCard();
            var previous = _selectedCard;
            _selectedCard = current;
            // 使用 UnityEngine.Object 的相等语义：已销毁的卡牌与 null 视为相同，避免操作已销毁的对象。
            if (current == previous) return;
            if (previous != null) _feedback?.PlaySelect(previous, false);
            if (current != null) _feedback?.PlaySelect(current, true);
        }

        /// <summary>
        /// 取得当前选择对应的卡牌视图。
        /// 输入：无；输出：手牌或棋盘单位的卡牌视图；未选中实体、选中家、或视图已不存在时返回 null。
        /// 说明：家沿用既有表现（只有选中标记、没有放大补间），所以不参与这里的放大切换；
        /// 视图不存在（卡牌刚被部署、弃置或阵亡而销毁）同样返回 null，表示本次没有需要放大的对象。
        /// </summary>
        Card ResolveSelectedCard()
        {
            switch (_selection.Kind)
            {
                case SelectionKind.Hand: return _handView != null ? _handView.GetCard(_selection.Id) : null;
                case SelectionKind.Unit: return _boardView != null ? _boardView.GetCard(_selection.Id) : null;
                default: return null;
            }
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
