// 对局引导器：唯一负责“加载配置 → 创建对局 → 绑定视图 → 进入回合”的组件。
// 为什么需要单一入口：规则层、UI 与调试面板都需要同一份对局实例，
// 若各处自行创建 GameSession，就会出现“界面显示一局、规则运行另一局”的隐性分叉。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

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
        /// <summary>本次运行会话的携带选择；重开与“再来一局”沿用它，只有启动与菜单“选卡”会重新选。</summary>
        DeckSelectionState _deckSelection;
        /// <summary>携带选择面板是否打开；打开时不再叠加配置面板用的那层遮罩（否则会盖住选卡面板）。</summary>
        bool _deckSelectionOpen;
        /// <summary>当前界面选择上下文。</summary>
        Selection _selection;
        /// <summary>上一次播放过选中表现的卡牌视图；用来在选中变化时把上一张牌恢复原状。</summary>
        Card _selectedCard;
        /// <summary>上一帧快照，用于计算移动动画起点。</summary>
        GameSnapshot _previousSnapshot;
        /// <summary>有序表现队列；只记录已经结算的事件，避免爆炸尚未播完就播放后续普通行动。</summary>
        readonly Queue<IEnumerator> _feedbackQueue = new Queue<IEnumerator>();
        /// <summary>队列播放器是否正在运行。</summary>
        bool _playingFeedback;
        /// <summary>正在展示已结算的炸弹准备或自动 Boss 行动；与调试面板闸门独立，禁止操作未来状态。</summary>
        bool _presentingTurn;
        /// <summary>调试面板是否打开，动画结束不能解除此闸门。</summary>
        bool _modalOpen;
        /// <summary>齿轮菜单是否打开。与模态闸门配套：菜单打开时挡输入，菜单自带遮罩而不显示配置面板用的那层遮罩。</summary>
        bool _menuOpen;
        /// <summary>实际的退出动作（默认关闭播放模式 / 结束应用），可替换以便测试与后续平台适配。</summary>
        Action _quitAction = DefaultQuit;
        /// <summary>当前正在展示的阶段，避免选择或面板刷新提前读取最终快照。</summary>
        GameSnapshot _displaySnapshot;
        /// <summary>唯一活动手牌手势及其指针位置，不与棋盘单位身份混用。</summary>
        HandCardDrag _drag;
        PointerEventData _dragPointer;

        /// <summary>是否正在展示回合阶段，供运行诊断及帧循环测试读取。</summary>
        public bool IsPresentingTurn => _presentingTurn;

        /// <summary>
        /// 仅供 PlayMode 测试：为 true 时启动流程跳过携带选择界面，直接用默认选卡开局。
        /// 为什么需要：大量棋盘/动画用例只关心对局内行为，不应为了跑一帧去点选卡界面；
        /// 选卡链路本身另有专门的 PlayMode 用例覆盖。正式运行包不暴露该开关，也不会被序列化。
        /// </summary>
        public static bool SuppressDeckSelectionOnStart { get; set; }

        /// <summary>本次运行会话的携带选择（供测试与调试读取）。</summary>
        public DeckSelectionState DeckSelection => _deckSelection;

        /// <summary>当前生效的配置资产，供调试面板“恢复默认”使用。</summary>
        public SpotlightConfigAsset CurrentAsset => ConfigLoader.LoadAsset(_configAsset);

        /// <summary>当前对局实例（只读，供调试与测试使用）。</summary>
        public GameSession Session => _session;

        /// <summary>
        /// 退出动作：默认在编辑器里停止播放模式、在独立运行包里结束进程。
        /// 输入/输出：无参数的宿主环境操作。
        /// 为什么抽成可替换属性：退出在 PlayMode 测试里无法真的执行（会直接中断测试运行），
        /// 但又必须验证“菜单按钮 → 退出”这条链路，因此保留一个可替换的执行点。
        /// </summary>
        public Action QuitAction
        {
            get => _quitAction;
            set => _quitAction = value;
        }

        /// <summary>
        /// 默认退出：编辑器下停止播放模式，正式包里结束应用进程。
        /// 输入/输出：无。
        /// 为什么不用单一句 Application.Quit：编辑器里它不生效，开发者会以为按钮没接上。
        /// </summary>
        static void DefaultQuit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

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
            _deckSelection = new DeckSelectionState(configuration);
            if (SuppressDeckSelectionOnStart)
            {
                ConfirmDeckSelection();
                return;
            }
            // 每次启动都必须先选卡：抽牌堆的卡种由玩家决定，不再有“所有卡默认进抽牌堆”的配置。
            OpenDeckSelection(false);
        }

        /// <summary>
        /// 绑定视图事件。
        /// 输入：无；输出：无。
        /// 所有输入都经此进入，形成统一输入入口，便于模态面板一次性阻断。
        /// </summary>
        void BindViewEvents()
        {
            if (_boardView != null) { _boardView.CellClicked -= OnCellClicked; _boardView.CellClicked += OnCellClicked; }
            if (_handView != null)
            {
                _handView.CardClicked -= OnCardClicked; _handView.CardClicked += OnCardClicked;
                _handView.DragStarted -= OnHandDragStarted; _handView.DragStarted += OnHandDragStarted;
                _handView.DragMoved -= OnHandDragMoved; _handView.DragMoved += OnHandDragMoved;
                _handView.DragEnded -= OnHandDragEnded; _handView.DragEnded += OnHandDragEnded;
                _handView.DragCancelled -= OnHandDragCancelled; _handView.DragCancelled += OnHandDragCancelled;
            }
            if (_hudView != null)
            {
                _hudView.Bind();
                _hudView.EndTurnRequested -= OnEndTurnRequested;
                _hudView.EndTurnRequested += OnEndTurnRequested;
                _hudView.SkipDeployRequested -= OnSkipDeployRequested;
                _hudView.SkipDeployRequested += OnSkipDeployRequested;
                _hudView.WithdrawRequested -= OnWithdrawRequested;
                _hudView.WithdrawRequested += OnWithdrawRequested;
                _hudView.BombDeployRequested -= OnBombDeployRequested;
                _hudView.BombDeployRequested += OnBombDeployRequested;
                _hudView.BombSkipRequested -= OnBombSkipRequested;
                _hudView.BombSkipRequested += OnBombSkipRequested;
                _hudView.SpawnerDeployRequested -= OnSpawnerDeployRequested;
                _hudView.SpawnerDeployRequested += OnSpawnerDeployRequested;
                _hudView.SpawnerSkipRequested -= OnSpawnerSkipRequested;
                _hudView.SpawnerSkipRequested += OnSpawnerSkipRequested;
                _hudView.MenuRequested -= OnMenuRequested;
                _hudView.MenuRequested += OnMenuRequested;
                _hudView.MenuResumeRequested -= OnMenuResumeRequested;
                _hudView.MenuResumeRequested += OnMenuResumeRequested;
                _hudView.DeckSelectRequested -= OnMenuDeckSelectRequested;
                _hudView.DeckSelectRequested += OnMenuDeckSelectRequested;
                var selector = _hudView.DeckSelectionPanel;
                if (selector != null)
                {
                    selector.Confirmed -= ConfirmDeckSelection;
                    selector.Confirmed += ConfirmDeckSelection;
                    selector.Cancelled -= OnDeckSelectionCancelled;
                    selector.Cancelled += OnDeckSelectionCancelled;
                }
                _hudView.RestartRequested -= OnRestartRequested;
                _hudView.RestartRequested += OnRestartRequested;
                _hudView.QuitRequested -= OnQuitRequested;
                _hudView.QuitRequested += OnQuitRequested;
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
            CancelHandDrag();
            StopAllCoroutines();
            _feedbackQueue.Clear();
            _playingFeedback = false;
            _presentingTurn = false;
            _displaySnapshot = null;
            _feedback?.KillAll();
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
            UpdateInputGate();
            if (_session.InitialPresentationFrames.Count > 0)
                PresentTurn(_session.InitialPresentationFrames[0].Snapshot, _session.InitialPresentationFrames);
            else Refresh();
        }

        /// <summary>
        /// 用新配置重开对局。
        /// 输入：新配置（通常来自调试面板）；输出：无。
        /// 行为：停止全部动画并重建视图，保证旧对局的卡牌不会残留。
        /// </summary>
        public void RestartWith(GameConfiguration configuration)
        {
            CancelHandDrag();
            _feedback?.KillAll();
            _boardView?.ClearUnits();
            _handView?.Clear();
            // 配置可能来自调试面板：卡池、上级上限或比例都可能变，选卡状态必须先与新配置对齐，
            // 否则菜单里再次打开选卡会看到一组对新配置非法的勾选。
            _deckSelection = ReconcileDeckSelection(configuration);
            StartSession(configuration);
        }

        /// <summary>
        /// 把当前选卡对齐到一份新配置。
        /// 输入：即将生效的配置；输出：可用的选卡状态。
        /// 行为：尽量保留玩家已选卡种（仍存在、属于玩家阶级、不超新上限），
        /// 保留后仍非法则回退为默认选卡——对应“调试面板改配置后不要弹选卡界面”的约定。
        /// </summary>
        DeckSelectionState ReconcileDeckSelection(GameConfiguration configuration)
        {
            if (configuration == null) return null;
            var fresh = new DeckSelectionState(configuration);
            if (_deckSelection == null) return fresh;
            var preserved = new List<string>();
            foreach (var id in _deckSelection.SelectedTypeIds)
            {
                var card = configuration.FindCard(id);
                if (card != null && DeckComposition.IsPlayerTier(card.Tier)) preserved.Add(id);
            }
            return fresh.TryRestore(preserved) ? fresh : new DeckSelectionState(configuration);
        }

        /// <summary>
        /// 打开携带选择面板。
        /// 输入：是否从齿轮菜单进入（菜单入口会先弹二次确认）；
        /// 输出：无。打开期间与配置面板一样暂停对局：遮罩挡住点击，规则层同时拒绝命令。
        /// </summary>
        void OpenDeckSelection(bool fromMenu)
        {
            if (_deckSelection == null) return;
            if (fromMenu) _menuOpen = false;
            _deckSelectionOpen = true;
            SetModalOpen(true);
            _hudView?.DeckSelectionPanel?.Show(_deckSelection, fromMenu);
        }

        /// <summary>关闭携带选择面板并解除暂停。输入：无；输出：无。</summary>
        void CloseDeckSelection()
        {
            _hudView?.DeckSelectionPanel?.Hide();
            if (!_deckSelectionOpen) return;
            _deckSelectionOpen = false;
            SetModalOpen(false);
        }

        /// <summary>
        /// 玩家在选卡面板点“确认”：用当前选卡生成抽牌堆并开局。
        /// 输入：无；输出：无。生成失败时把原因显示在面板里，不关闭面板、不改变现有对局。
        /// </summary>
        void ConfirmDeckSelection()
        {
            if (_deckSelection == null) return;
            if (!_deckSelection.TryBuildConfiguration(out var configuration, out var error))
            {
                _hudView?.DeckSelectionPanel?.ShowError(error);
                return;
            }
            CloseDeckSelection();
            StartSession(configuration);
        }

        /// <summary>
        /// 玩家取消选卡（菜单入口的“继续当前对局”或面板里的“取消”）。
        /// 输入：无；输出：无。开局前不允许取消（面板不显示取消按钮），这里兜底重新打开。
        /// </summary>
        void OnDeckSelectionCancelled()
        {
            _hudView?.DeckSelectionPanel?.Hide();
            _deckSelectionOpen = false;
            if (_session == null)
            {
                OpenDeckSelection(false);
                return;
            }
            SetModalOpen(false);
        }

        /// <summary>
        /// 打开/关闭模态输入闸门。
        /// 输入：是否屏蔽输入；输出：无。
        /// 同时作用于规则层（拒绝命令与高亮）与 HUD 遮罩（视觉与 UGUI 事件拦截）。
        /// </summary>
        public void SetModalOpen(bool blocked)
        {
            if (blocked) CancelHandDrag();
            _modalOpen = blocked;
            // 关闭模态时一并退出菜单视图，避免遮罩残留或界面停在菜单上。
            if (!blocked) _menuOpen = false;
            UpdateInputGate();
            // 菜单自带全屏遮罩，此时不再叠加配置面板用的那层遮罩（否则会盖住「继续 / 重开」并吞掉点击）。
            _hudView?.SetModalVisible(blocked && !_menuOpen && !_deckSelectionOpen);
            _hudView?.SetMenuVisible(_menuOpen);
            if (blocked) _selection = new Selection(SelectionKind.None);
            Refresh();
        }

        /// <summary>玩家点击手牌。输入：被点击的卡牌视图；输出：无。</summary>
        void OnCardClicked(Card card)
        {
            if (_drag != null) return;
            if (_presentingTurn || _modalOpen || _session == null || card == null || card.Record == null) return;
            var snapshot = _session.GetSnapshot();
            if (snapshot.BossBombPending) { Show("请先布置炸弹或跳过炸弹，再开始玩家行动"); return; }
            if (snapshot.BossSpawnerPending) { Show("请先部署刷怪卡或跳过，再开始 Boss 普通行动"); return; }
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

        /// <summary>判断手牌拖动的操作阶段。输入：当前快照；输出：是否允许操作，部署格仍由规则层计算。</summary>
        bool CanDragHand(GameSnapshot snapshot) => isActiveAndEnabled && !_presentingTurn && !_modalOpen &&
            snapshot.ActiveSide == Side.Player && snapshot.Phase == MatchPhase.Action &&
            !snapshot.BossBombPending && !snapshot.BossSpawnerPending;

        /// <summary>接收手牌开始手势。输入：拖动器与指针；输出：选中该卡并许可表现，不复用点击切换逻辑。</summary>
        void OnHandDragStarted(HandCardDrag drag, PointerEventData pointer)
        {
            if (_session == null || drag == null || drag.Card.Record == null) return;
            var snapshot = _session.GetSnapshot();
            if (!CanDragHand(snapshot) || !snapshot.Hand.Any(c => c.Id == drag.Card.Record.Id)) return;
            var grabbedPosition = drag.transform.position;
            CancelHandDrag();
            _selection = new Selection(SelectionKind.Hand, drag.Card.Record.Id);
            Refresh();
            if (!drag.BeginVisual(pointer, _handView.DragLayer, grabbedPosition)) return;
            _drag = drag;
            OnHandDragMoved(drag, pointer);
        }

        /// <summary>更新指针与合法悬停格。输入：活动拖动器与指针；输出：范围及悬停提示，不修改规则。</summary>
        void OnHandDragMoved(HandCardDrag drag, PointerEventData pointer)
        {
            if (_drag != drag) return;
            _dragPointer = pointer;
            UpdateDragHover();
        }

        /// <summary>
        /// 按最新快照重算范围及悬停。输入：缓存的指针；输出：只提示当前合法格。
        /// 每帧更新使资源、回合或占位改变后提示及时失效，鼠标静止时也不会保留过期落点。
        /// </summary>
        void UpdateDragHover()
        {
            if (_drag == null || _session == null) return;
            var snapshot = _session.GetSnapshot();
            if (!CanDragHand(snapshot) || !snapshot.Hand.Any(c => c.Id == _selection.Id))
            {
                CancelHandDrag();
                Refresh();
                return;
            }
            var actions = _session.GetLegalActions(_selection);
            _boardView?.Render(snapshot, actions, _selection);
            if (_boardView != null)
                _boardView.SetDeploymentHover(_boardView.TryGetPointerCell(_dragPointer, out var cell) && actions.Deploy.Contains(cell) ? cell : (Cell?)null);
        }

        /// <summary>帧末重查拖动目标。输入：无；输出：跟随布局和规则变化的悬停提示。</summary>
        void LateUpdate() => UpdateDragHover();

        /// <summary>
        /// 松手时重新命中并提交部署。输入：拖动器与指针；输出：成功则快照移除手牌，失败则回位保留选中。
        /// 在提交前清空活动手势，防止 Render 销毁卡面时的取消事件重复执行命令。
        /// </summary>
        void OnHandDragEnded(HandCardDrag drag, PointerEventData pointer)
        {
            if (_drag != drag) return;
            _drag = null; _dragPointer = null;
            _boardView?.SetDeploymentHover(null);
            var id = drag.Card.Record.Id;
            var success = false;
            if (_session != null && CanDragHand(_session.GetSnapshot()) && _boardView != null &&
                _boardView.TryGetPointerCell(pointer, out var cell) &&
                _session.GetLegalActions(new Selection(SelectionKind.Hand, id)).Deploy.Contains(cell))
            {
                var result = Submit(new GameCommand(CommandKind.Deploy, Side.Player, id, cell));
                success = result != null && result.Success;
            }
            if (success) _selection = new Selection(SelectionKind.None);
            else drag.ReturnToHand(_configuration.Presentation.MoveSeconds);
            Refresh();
        }

        /// <summary>收到视图主动取消。输入：拖动器；输出：撤掉对应手势及悬停，晚到的松手无效。</summary>
        void OnHandDragCancelled(HandCardDrag drag)
        {
            if (_drag != drag) return;
            _drag = null; _dragPointer = null;
            _boardView?.SetDeploymentHover(null);
        }

        /// <summary>统一取消拖拽和回位。输入：是否延后生命周期回调中的排版；输出：复位视图且不提交命令。</summary>
        void CancelHandDrag(bool deferLayout = false)
        {
            _drag = null; _dragPointer = null;
            _boardView?.SetDeploymentHover(null);
            _handView?.CancelDrags(deferLayout);
        }

        /// <summary>失焦时取消手势，避免窗口外松手后卡牌一直跟随。输入：焦点状态；输出：必要时归位。</summary>
        void OnApplicationFocus(bool focused) { if (!focused) CancelHandDrag(); }
        /// <summary>协调器禁用时取消手势。输入：无；输出：释放视图临时状态。</summary>
        void OnDisable() => CancelHandDrag(true);

        /// <summary>玩家点击棋盘格。输入：内部坐标；输出：无。</summary>
        void OnCellClicked(Cell cell)
        {
            if (_presentingTurn || _modalOpen || _session == null || _drag != null) return;
            var snapshot = _session.GetSnapshot();
            var actions = _session.GetLegalActions(_selection);

            if (snapshot.BossSpawnerPending)
            {
                if (_selection.Kind == SelectionKind.BossSpawnerDeployment && actions.Deploy.Contains(cell))
                {
                    Submit(new GameCommand(CommandKind.DeployBossSpawner, Side.Boss, target: cell));
                    _selection = new Selection(SelectionKind.None);
                    Refresh();
                }
                return;
            }
            if (snapshot.BossBombPending)
            {
                if (_selection.Kind == SelectionKind.BossBombDeployment && actions.Deploy.Contains(cell))
                {
                    Submit(new GameCommand(CommandKind.DeployBossBomb, Side.Boss, target: cell));
                    _selection = new Selection(SelectionKind.None);
                    Refresh();
                }
                else Show("请先点击布置炸弹并选择高亮格，或跳过炸弹");
                return;
            }

            // 1) 点击部署只服务手动 Boss；玩家手牌必须通过拖拽松手提交。
            if (actions.Deploy.Contains(cell))
            {
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
            CancelHandDrag();
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
        /// （与部署后清空选中是同一条约定）。Boss 回合演示或配置面板打开时不响应。
        /// </summary>
        void OnWithdrawRequested()
        {
            if (_session == null || _presentingTurn || _modalOpen || _selection.Kind != SelectionKind.Unit) return;
            Submit(new GameCommand(CommandKind.Withdraw, _session.GetSnapshot().ActiveSide, _selection.Id));
            _selection = new Selection(SelectionKind.None);
            Refresh();
        }

        /// <summary>进入炸弹选格。输入：HUD 请求；输出：合法列高亮，实际权限仍由规则层复验。</summary>
        void OnBombDeployRequested()
        {
            if (_presentingTurn || _modalOpen || _session == null) return;
            _selection = new Selection(SelectionKind.BossBombDeployment);
            Refresh();
        }

        /// <summary>放弃当前炸弹机会。输入：HUD 请求；输出：提交跳过命令并恢复普通选择。</summary>
        void OnBombSkipRequested()
        {
            if (_session == null) return;
            Submit(new GameCommand(CommandKind.SkipBossBomb, Side.Boss));
            _selection = new Selection(SelectionKind.None);
            Refresh();
        }

        /// <summary>进入刷怪卡选格。输入：HUD 请求；输出：规则计算的区域空格高亮，提交时仍复验。</summary>
        void OnSpawnerDeployRequested()
        {
            if (_presentingTurn || _modalOpen || _session == null) return;
            _selection = new Selection(SelectionKind.BossSpawnerDeployment);
            Refresh();
        }

        /// <summary>跳过本次刷怪卡机会。输入：HUD 请求；输出：提交命令、撤销选择并刷新普通操作。</summary>
        void OnSpawnerSkipRequested()
        {
            if (_presentingTurn || _modalOpen || _session == null) return;
            Submit(new GameCommand(CommandKind.SkipBossSpawner, Side.Boss));
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
        /// 进入齿轮菜单。
        /// 输入：无（由 HUD 的齿轮按钮触发）；输出：无。
        /// 行为：切到“原有面板”视图（日志、Boss 状态与选中详情），并复用模态闸门挡住棋盘与手牌的点击。
        /// 为什么复用 SetModalOpen：暂停的实质就是“期间不接受对局操作”，与配置面板完全一致，
        /// 界面遮罩加规则层输入闸门已经足够，不需要给规则层增加“暂停”这种新概念。
        /// </summary>
        void OnMenuRequested()
        {
            if (_session == null) return;
            _menuOpen = true;
            SetModalOpen(true);
        }

        /// <summary>关闭齿轮菜单并继续对局。输入：无；输出：无。</summary>
        void OnMenuResumeRequested() => SetModalOpen(false);

        /// <summary>
        /// 菜单里的“选卡”：先弹二次确认，确认后才进入选卡面板（换卡会重开当前对局）。
        /// 输入：无；输出：无。选卡状态与界面都比对局长寿，因此这里直接复用 _deckSelection。
        /// </summary>
        void OnMenuDeckSelectRequested() => OpenDeckSelection(true);

        /// <summary>
        /// HUD 请求退出游戏。
        /// 输入：无（由菜单里的“退出游戏”按钮触发）；输出：无（调用宿主退出动作后进程/播放模式结束）。
        /// 行为：先停掉所有补间，再做退出动作，避免退出过程中仍有 DOTween 回调访问即将销毁的界面对象。
        /// 为什么不做“先结束对局”：本局不保存进度，退出就是终止运行，不存在需要回写的规则状态。
        /// </summary>
        void OnQuitRequested()
        {
            _feedback?.KillAll();
            _quitAction?.Invoke();
        }

        /// <summary>
        /// 提交命令并刷新。
        /// 输入：命令；输出：规则执行结果，输入被屏蔽时为 null，供拖动判断是否需要回位。
        /// 行为：记录渲染前快照 → 同步结算；炸弹准备及自动 Boss 按阶段展示，其他命令直接刷新并播放反馈。
        /// 失败时只提示原因，不改变界面以外的状态。
        /// </summary>
        CommandResult Submit(GameCommand command)
        {
            if (_presentingTurn || _modalOpen || _session == null) return null;
            var before = _session.GetSnapshot();
            var result = _session.Execute(command);
            if (!result.Success)
            {
                Show(result.Error);
                _previousSnapshot = before;
                Refresh();
                return result;
            }
            _hudView?.ShowError(null);
            _previousSnapshot = before;
            if (result.PresentationFrames.Count > 0) PresentTurn(before, result.PresentationFrames);
            else
            {
                Refresh();
                PlayFeedback(before, _session.GetSnapshot(), result.Events);
            }
            return result;
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
            var snapshot = _presentingTurn ? _displaySnapshot : _session.GetSnapshot();
            if (snapshot == null) return;
            var actions = _presentingTurn ? new LegalActions() : _session.GetLegalActions(_selection);
            _boardView?.Render(snapshot, actions, _selection);
            _handView?.Render(snapshot, _selection);
            _hudView?.Render(snapshot, _configuration, _selection, actions, _presentingTurn || _modalOpen);
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

        /// <summary>合并两种输入闸门。输入：面板与展示状态；输出：规则层屏蔽状态，任一闸门存在就不能操作。</summary>
        void UpdateInputGate() => _session?.SetInputBlocked(_modalOpen || _presentingTurn);

        /// <summary>
        /// 将炸弹准备及自动 Boss 阶段加入已有表现队列。输入：命令前快照与阶段列表；输出：按序展示。
        /// 立即屏蔽操作并保留当前画面，避免先刷新下一回合；无视图/反馈时仍能正常走到最终状态。
        /// </summary>
        void PresentTurn(GameSnapshot before, IReadOnlyList<PresentationFrame> frames)
        {
            CancelHandDrag();
            _presentingTurn = true;
            _displaySnapshot = before;
            _selection = new Selection(SelectionKind.None);
            UpdateInputGate();
            Refresh();
            _feedbackQueue.Enqueue(PlayPresentationFrames(before, frames));
            if (!_playingFeedback) StartCoroutine(DrainFeedbackQueue());
        }

        /// <summary>
        /// 展示同步结算留下的阶段。输入：初始快照与有序阶段；输出：协程，绝不推进或重算规则。
        /// 每阶段至少显示一帧；模态面板暂停阶段切换，重开通过 StopAllCoroutines 取消旧流程。
        /// </summary>
        IEnumerator PlayPresentationFrames(GameSnapshot before, IReadOnlyList<PresentationFrame> frames)
        {
            foreach (var frame in frames)
            {
                while (_modalOpen) yield return null;
                _displaySnapshot = frame.Snapshot;
                Refresh();
                yield return PlayFeedbackEvents(before, frame.Snapshot, frame.Events, true);
                yield return null;
                before = frame.Snapshot;
            }
            while (_modalOpen) yield return null;
            _presentingTurn = false;
            _displaySnapshot = null;
            UpdateInputGate();
            Refresh();
        }

        /// <summary>
        /// 按事件播放必要动效。
        /// 输入：命令前快照、命令后快照与事件列表；输出：无。
        /// 只对仍然存在的对象播放动画，避免动画操作已被销毁的视图。
        /// </summary>
        void PlayFeedback(GameSnapshot before, GameSnapshot after, IReadOnlyList<GameEvent> events)
        {
            if (_boardView == null || _feedback == null) return;
            _feedbackQueue.Enqueue(PlayFeedbackEvents(before, after, events));
            if (!_playingFeedback) StartCoroutine(DrainFeedbackQueue());
        }

        /// <summary>
        /// 按提交顺序播放已结算事件。输入：表现队列；输出：协程等待，不修改对局状态。
        /// 阶段展示期间屏蔽操作，重开时统一取消队列，避免旧事件访问新棋盘。
        /// </summary>
        IEnumerator DrainFeedbackQueue()
        {
            _playingFeedback = true;
            while (_feedbackQueue.Count > 0) yield return _feedbackQueue.Dequeue();
            _playingFeedback = false;
        }

        /// <summary>
        /// 播放一次命令的事件序列。输入：前后快照与有序事件；输出：表现协程。
        /// 爆炸逐格结束后才播放后续普通行动，已死亡单位的爆炸使用独立格子效果。
        /// </summary>
        IEnumerator PlayFeedbackEvents(GameSnapshot before, GameSnapshot after, IReadOnlyList<GameEvent> events, bool waitForCompletion = false)
        {
            if (_boardView == null || _feedback == null) yield break;
            foreach (var e in events)
            {
                switch (e.Kind)
                {
                    case "bomb-deploy":
                        _feedback.PlayBombDeploy(_boardView.CellRect(e.Bomb.Position));
                        if (waitForCompletion) yield return _feedback.WaitForBombEffects();
                        break;
                    case "bomb-explode":
                        yield return _feedback.PlayBombExplosion(e.BlastSteps, _boardView.CellRect);
                        break;
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
                if (waitForCompletion && (e.Kind == "deploy" || e.Kind == "upgrade" || e.Kind == "move" || e.Kind == "attack"))
                    yield return _feedback.WaitForCard(_boardView.GetCard(e.SubjectId));
            }
        }

        /// <summary>显示提示。输入：文本；输出：无。</summary>
        void Show(string message)
        {
            _hudView?.ShowError(message);
        }
    }
}
