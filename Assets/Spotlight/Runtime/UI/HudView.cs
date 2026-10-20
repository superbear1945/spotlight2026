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
        /// <summary>主音量在 PlayerPrefs 中的持久化键；值域为 0 到 1。</summary>
        const string _masterVolumePreferenceKey = "Spotlight.MasterVolume";
        /// <summary>音乐音量在 PlayerPrefs 中的持久化键；为后续音乐总线保留独立值域。</summary>
        const string _musicVolumePreferenceKey = "Spotlight.MusicVolume";
        /// <summary>音效音量在 PlayerPrefs 中的持久化键；为后续音效总线保留独立值域。</summary>
        const string _soundVolumePreferenceKey = "Spotlight.SoundVolume";
        /// <summary>全屏开关在 PlayerPrefs 中的持久化键；1 表示全屏，0 表示窗口。</summary>
        const string _fullscreenPreferenceKey = "Spotlight.Fullscreen";

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
#if UNITY_EDITOR
        /// <summary>点击调试入口（只有编辑器里才会创建这个按钮；正式包不含该事件）。</summary>
        public event Action DebugRequested;
#endif
        /// <summary>点击“离场”：请求让当前选中的己方单位离开棋盘。</summary>
        public event Action WithdrawRequested;
        /// <summary>点击齿轮：请求进入菜单（隐藏战斗界面，显示原有面板与「继续 / 重开」）。</summary>
        public event Action MenuRequested;
        /// <summary>点击菜单里的“继续”：请求关闭菜单并恢复对局。</summary>
        public event Action MenuResumeRequested;
        /// <summary>点击菜单里的“选卡”：请求进入携带选择（确认后会重开对局）。</summary>
        public event Action DeckSelectRequested;
        /// <summary>点击菜单里的“退出游戏”：请求结束本次运行（对局不保存，也不会返回棋盘）。</summary>
        public event Action QuitRequested;

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
        /// <summary>顶部常驻 Boss 阶段摘要，战斗中无需打开暂停菜单即可查看锁血和战舰数量。</summary>
        Label _bossPhaseLabel;
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
        /// <summary>右上角设置与规则帮助入口；两者都打开会冻结对局的模态面板。</summary>
        Button _settingsButton, _helpButton;
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
        /// <summary>齿轮按钮（战斗界面显示，菜单打开时隐藏）。</summary>
        Button _menuButton;
        /// <summary>菜单遮罩（含「继续 / 重开 / 退出游戏」三个按钮）。</summary>
        VisualElement _menuOverlay;
        /// <summary>当前是否显示齿轮菜单；Render 用它避免把菜单中的 Boss 状态面板按普通战斗界面规则再次隐藏。</summary>
        bool _menuVisible;
        /// <summary>战斗界面的左右详情区：左为我方选中，右为敌方选中。</summary>
        VisualElement _detailLeft, _detailRight;
        /// <summary>左右详情信息区的卡名、卡面式英文阶级、特性标签与规则说明。</summary>
        Label _detailLeftTitle, _detailLeftMeta, _detailLeftTrait, _detailLeftBody;
        Label _detailRightTitle, _detailRightMeta, _detailRightTrait, _detailRightBody;
        /// <summary>左右详情信息区的三项卡面属性值：攻击、生命、移动。</summary>
        Label _detailLeftAttack, _detailLeftHealth, _detailLeftMove;
        Label _detailRightAttack, _detailRightHealth, _detailRightMove;
        /// <summary>手牌同一行两侧的计数区与墓地 / 抽牌堆数字。</summary>
        VisualElement _pileLeft, _pileRight;
        Label _graveCount;
        /// <summary>牌堆正中显示的剩余总张数。</summary>
        Label _drawCount;
        /// <summary>牌堆底部的阶级构成小字（高 / 中 / 初）。</summary>
        Label _drawTiers;
        /// <summary>携带选择面板（开局前与菜单里共用）。</summary>
        DeckSelectionPanel _deckSelectionPanel;
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
        /// <summary>暂停面板中的关闭、继续、重新选卡、重开与退出按钮。</summary>
        Button _pauseCloseButton, _pauseContinueButton, _pauseRestartButton, _pauseExitButton;
        /// <summary>玩家设置面板及其中可实际修改的声音、全屏控件。</summary>
        VisualElement _settingsOverlay;
        Slider _masterVolumeSlider, _musicVolumeSlider, _soundVolumeSlider;
        VisualElement _masterVolumeFill, _musicVolumeFill, _soundVolumeFill;
        Label _masterVolumeValue, _musicVolumeValue, _soundVolumeValue;
        Toggle _fullscreenToggle;
        Button _settingsResetButton, _settingsCloseButton, _settingsXButton;
        /// <summary>规则帮助面板及其关闭按钮。</summary>
        VisualElement _helpOverlay;
        Button _helpCloseButton;
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
            _bossPhaseLabel = _root.Q<Label>("boss-phase-label");
            _phaseLabel = _root.Q<Label>("phase-label");
            _detailLabel = _root.Q<Label>("unit-detail");
            _errorLabel = _root.Q<Label>("error-label");
            _logScroll = _root.Q<ScrollView>("log-list");
            _logPanel = _root.Q<VisualElement>("log-panel");
            _sidePanel = _root.Q<VisualElement>("side-panel");
            _logContent = _root.Q<VisualElement>("log-content");
            _historyButton = _root.Q<Button>("history-button");
            _pauseButton = _root.Q<Button>("pause-button");
            _settingsButton = _root.Q<Button>("settings-button");
            _helpButton = _root.Q<Button>("help-button");
            _endTurnButton = _root.Q<Button>("end-turn-button");
            _skipButton = _root.Q<Button>("skip-deploy-button");
            _withdrawButton = _root.Q<Button>("withdraw-button");
            _bombDeployButton = _root.Q<Button>("bomb-deploy-button");
            _bombSkipButton = _root.Q<Button>("bomb-skip-button");
            _spawnerDeployButton = _root.Q<Button>("spawner-deploy-button");
            _spawnerSkipButton = _root.Q<Button>("spawner-skip-button");
            _menuButton = _root.Q<Button>("menu-button");
            _menuOverlay = _root.Q<VisualElement>("menu-overlay");
            _detailLeft = _root.Q<VisualElement>("detail-left");
            _detailRight = _root.Q<VisualElement>("detail-right");
            _detailLeftTitle = _root.Q<Label>("detail-left-title");
            _detailLeftMeta = _root.Q<Label>("detail-left-meta");
            _detailLeftTrait = _root.Q<Label>("detail-left-trait");
            _detailLeftBody = _root.Q<Label>("detail-left-body");
            _detailLeftAttack = _root.Q<Label>("detail-left-attack");
            _detailLeftHealth = _root.Q<Label>("detail-left-health");
            _detailLeftMove = _root.Q<Label>("detail-left-move");
            _detailRightTitle = _root.Q<Label>("detail-right-title");
            _detailRightMeta = _root.Q<Label>("detail-right-meta");
            _detailRightTrait = _root.Q<Label>("detail-right-trait");
            _detailRightBody = _root.Q<Label>("detail-right-body");
            _detailRightAttack = _root.Q<Label>("detail-right-attack");
            _detailRightHealth = _root.Q<Label>("detail-right-health");
            _detailRightMove = _root.Q<Label>("detail-right-move");
            _pileLeft = _root.Q<VisualElement>("pile-left");
            _pileRight = _root.Q<VisualElement>("pile-right");
            _graveCount = _root.Q<Label>("grave-count");
            _drawCount = _root.Q<Label>("draw-count");
            _drawTiers = _root.Q<Label>("draw-count-tiers");
            _deckSelectionPanel = new DeckSelectionPanel();
            _deckSelectionPanel.Bind(_root);
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
            _settingsOverlay = _root.Q<VisualElement>("settings-overlay");
            _masterVolumeSlider = _root.Q<Slider>("master-volume-slider");
            _musicVolumeSlider = _root.Q<Slider>("music-volume-slider");
            _soundVolumeSlider = _root.Q<Slider>("sound-volume-slider");
            _masterVolumeFill = _root.Q<VisualElement>("master-volume-fill");
            _musicVolumeFill = _root.Q<VisualElement>("music-volume-fill");
            _soundVolumeFill = _root.Q<VisualElement>("sound-volume-fill");
            _masterVolumeValue = _root.Q<Label>("master-volume-value");
            _musicVolumeValue = _root.Q<Label>("music-volume-value");
            _soundVolumeValue = _root.Q<Label>("sound-volume-value");
            _fullscreenToggle = _root.Q<Toggle>("fullscreen-toggle");
            _settingsResetButton = _root.Q<Button>("settings-reset-button");
            _settingsCloseButton = _root.Q<Button>("settings-close-button");
            _settingsXButton = _root.Q<Button>("settings-x-button");
            _helpOverlay = _root.Q<VisualElement>("help-overlay");
            _helpCloseButton = _root.Q<Button>("help-close-button");

            if (_endTurnButton != null) _endTurnButton.clicked += () => EndTurnRequested?.Invoke();
            if (_skipButton != null) _skipButton.clicked += () => SkipDeployRequested?.Invoke();
            if (_withdrawButton != null) _withdrawButton.clicked += () => WithdrawRequested?.Invoke();
            if (_bombDeployButton != null) _bombDeployButton.clicked += () => BombDeployRequested?.Invoke();
            if (_bombSkipButton != null) _bombSkipButton.clicked += () => BombSkipRequested?.Invoke();
            if (_spawnerDeployButton != null) _spawnerDeployButton.clicked += () => SpawnerDeployRequested?.Invoke();
            if (_spawnerSkipButton != null) _spawnerSkipButton.clicked += () => SpawnerSkipRequested?.Invoke();
            if (_historyButton != null) _historyButton.clicked += ToggleLogPanel;
            if (_pauseButton != null) { _pauseButton.clicked -= OpenPausePanel; _pauseButton.clicked += OpenPausePanel; }
            if (_settingsButton != null) { _settingsButton.clicked -= OpenSettingsPanel; _settingsButton.clicked += OpenSettingsPanel; }
            if (_helpButton != null) { _helpButton.clicked -= OpenHelpPanel; _helpButton.clicked += OpenHelpPanel; }
            if (_pauseCloseButton != null) { _pauseCloseButton.clicked -= ClosePausePanel; _pauseCloseButton.clicked += ClosePausePanel; }
            if (_pauseContinueButton != null) { _pauseContinueButton.clicked -= ClosePausePanel; _pauseContinueButton.clicked += ClosePausePanel; }
            if (_pauseRestartButton != null) { _pauseRestartButton.clicked -= RestartFromPause; _pauseRestartButton.clicked += RestartFromPause; }
            if (_pauseExitButton != null) { _pauseExitButton.clicked -= ExitFromPause; _pauseExitButton.clicked += ExitFromPause; }
            if (_settingsResetButton != null) { _settingsResetButton.clicked -= ResetPlayerSettings; _settingsResetButton.clicked += ResetPlayerSettings; }
            if (_settingsCloseButton != null) { _settingsCloseButton.clicked -= ApplyAndCloseSettingsPanel; _settingsCloseButton.clicked += ApplyAndCloseSettingsPanel; }
            if (_settingsXButton != null) { _settingsXButton.clicked -= ApplyAndCloseSettingsPanel; _settingsXButton.clicked += ApplyAndCloseSettingsPanel; }
            if (_helpCloseButton != null) { _helpCloseButton.clicked -= CloseHelpPanel; _helpCloseButton.clicked += CloseHelpPanel; }
            if (_masterVolumeSlider != null)
            {
                _masterVolumeSlider.UnregisterValueChangedCallback(OnMasterVolumeChanged);
                _masterVolumeSlider.RegisterValueChangedCallback(OnMasterVolumeChanged);
            }
            if (_musicVolumeSlider != null)
            {
                _musicVolumeSlider.UnregisterValueChangedCallback(OnMusicVolumeChanged);
                _musicVolumeSlider.RegisterValueChangedCallback(OnMusicVolumeChanged);
            }
            if (_soundVolumeSlider != null)
            {
                _soundVolumeSlider.UnregisterValueChangedCallback(OnSoundVolumeChanged);
                _soundVolumeSlider.RegisterValueChangedCallback(OnSoundVolumeChanged);
            }
            var restart = _root.Q<Button>("restart-button");
            if (restart != null) restart.clicked += () => RestartRequested?.Invoke();
            if (_menuButton != null) _menuButton.clicked += () => MenuRequested?.Invoke();
            var menuDeckSelect = _root.Q<Button>("menu-deck-select-button");
            if (menuDeckSelect != null) menuDeckSelect.clicked += () => DeckSelectRequested?.Invoke();
            var menuResume = _root.Q<Button>("menu-resume-button");
            if (menuResume != null) menuResume.clicked += () => MenuResumeRequested?.Invoke();
            var menuRestart = _root.Q<Button>("menu-restart-button");
            if (menuRestart != null) menuRestart.clicked += () => RestartRequested?.Invoke();
            // 退出游戏是宿主环境行为（关播放模式 / 结束进程），HUD 只上报点击，具体怎么做由 GameBootstrap 决定。
            var menuQuit = _root.Q<Button>("menu-quit-button");
            if (menuQuit != null) menuQuit.clicked += () => QuitRequested?.Invoke();
            // 调试入口不再写在 UXML 里：正式包连这两个按钮的文案都不能有，
            // 因此只在调试构建里由代码插入（见 CreateDebugEntryButtons）。
#if UNITY_EDITOR
            CreateDebugEntryButtons();
#endif
            var victoryRestart = _root.Q<Button>("victory-restart-button");
            if (victoryRestart != null) victoryRestart.clicked += () => RestartRequested?.Invoke();
            if (_victoryOverlay != null) _victoryOverlay.style.display = DisplayStyle.None;
            if (_victoryArt != null) _victoryArt.style.display = DisplayStyle.None;
            if (_defeatArt != null) _defeatArt.style.display = DisplayStyle.None;
            if (_modalOverlay != null) _modalOverlay.style.display = DisplayStyle.None;
            if (_pauseOverlay != null) _pauseOverlay.style.display = DisplayStyle.None;
            if (_settingsOverlay != null) _settingsOverlay.style.display = DisplayStyle.None;
            if (_helpOverlay != null) _helpOverlay.style.display = DisplayStyle.None;
            if (_errorLabel != null) _errorLabel.style.display = DisplayStyle.None;
            if (_withdrawButton != null) _withdrawButton.style.display = DisplayStyle.None;
            // 战斗界面是默认视图：原有面板（日志、Boss 状态与选中详情）先隐藏，只在齿轮菜单里显示。
            SetMenuVisible(false);
        }

#if UNITY_EDITOR
        /// <summary>
        /// 在编辑器里插入两个调试入口按钮：“顶部工具条的快捷入口”与“设置页底部的「开发者配置」”。
        /// 输入：无（用 Bind 已查到的 _root / _settingsOverlay）；输出：无。
        /// 为什么用代码而不是 UXML：正式包必须连按钮文案（例如“开发者配置”）都找不到，
        /// 而 UXML 是被无条件打包的资源；放在条件编译里才能保证字符串不出现在正式包中。
        /// 幂等：Bind 可能被重复调用，已存在同名按钮时直接返回，避免叠出两个入口。
        /// </summary>
        void CreateDebugEntryButtons()
        {
            var toolbar = _root.Q<VisualElement>(className: "entry-toolbar");
            if (toolbar != null && _root.Q<Button>("quick-debug-button") == null)
            {
                var quickDebug = new Button { name = "quick-debug-button", tooltip = "配置 / 调试", text = string.Empty };
                quickDebug.AddToClassList("icon-button");
                quickDebug.AddToClassList("settings-button");
                // 顶部顺序是：暂停、设置、快捷调试、日志、帮助；插到第 3 个位置保证外观不变。
                toolbar.Insert(Mathf.Min(2, toolbar.childCount), quickDebug);
                quickDebug.clicked += () => DebugRequested?.Invoke();
            }

            // 设置页的底部操作区（utility-actions）是玩家可达到的位置，第二个入口放在这里：
            // 旧菜单遮罩（menu-overlay）已经没有任何入口，挂在那里等于把调试入口藏死。
            var settingsActions = _settingsOverlay?.Q<VisualElement>(className: "utility-actions");
            if (settingsActions != null && _root.Q<Button>("settings-debug-button") == null)
            {
                var debug = new Button { name = "settings-debug-button", text = "开发者配置" };
                debug.AddToClassList("action-button");
                debug.AddToClassList("secondary-action");
                debug.AddToClassList("settings-debug-button");
                settingsActions.Insert(0, debug);
                debug.clicked += OpenDebugFromSettings;
            }
        }
#endif

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
            if (_bossPhaseLabel != null) _bossPhaseLabel.text = BuildBossPhaseSummary(snapshot);
            if (_phaseLabel != null) _phaseLabel.text = phaseLine;
            if (_detailLabel != null) _detailLabel.text = detailLine;
            if (_sidePanel != null)
            {
                var showDetails = selection.Kind != SelectionKind.None || !string.IsNullOrEmpty(phaseLine);
                _sidePanel.style.display = _menuVisible || showDetails ? DisplayStyle.Flex : DisplayStyle.None;
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
            FillDetailFaces(selection, snapshot, configuration);
            if (_graveCount != null) _graveCount.text = snapshot.Graveyard.Count.ToString();
            RenderDrawPile(snapshot, configuration);
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

        /// <summary>输入只读快照，输出顶部常驻摘要；只呈现结算结果，不在 UI 推算阶段或战舰状态。</summary>
        string BuildBossPhaseSummary(GameSnapshot snapshot)
        {
            var summary = $"Boss 第 {(int)snapshot.BossPhase + 1} 阶段" + (snapshot.BossInvulnerable ? " · 锁血" : "");
            if (snapshot.BossWarships.Count == 0) return summary;
            return summary + $"\n战舰 待部署 {snapshot.BossWarships.Count(s => s.Status == BossWarshipStatus.Pending)}" +
                $" · 存活 {snapshot.BossWarships.Count(s => s.Status == BossWarshipStatus.Alive)}" +
                $" · 已击败 {snapshot.BossWarships.Count(s => s.Status == BossWarshipStatus.Defeated)}";
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
        /// 打开玩家设置面板并读取当前已保存的设备设置。
        /// 输入：无；输出：显示设置遮罩并通过 PauseChanged(true) 冻结对局。
        /// 为什么每次打开都重新读取：调试或平台代码可能在面板关闭期间改动声音或全屏状态，控件必须反映最新值。
        /// </summary>
        void OpenSettingsPanel()
        {
            if (_settingsOverlay == null) return;
            if (_masterVolumeSlider != null)
                _masterVolumeSlider.value = PlayerPrefs.GetFloat(_masterVolumePreferenceKey, 0.8f) * 100f;
            if (_musicVolumeSlider != null)
                _musicVolumeSlider.value = PlayerPrefs.GetFloat(_musicVolumePreferenceKey, 0.6f) * 100f;
            if (_soundVolumeSlider != null)
                _soundVolumeSlider.value = PlayerPrefs.GetFloat(_soundVolumePreferenceKey, 0.8f) * 100f;
            if (_fullscreenToggle != null)
                _fullscreenToggle.value = PlayerPrefs.GetInt(_fullscreenPreferenceKey, Screen.fullScreen ? 1 : 0) != 0;
            RefreshVolumePreview(_masterVolumeSlider, _masterVolumeValue, _masterVolumeFill);
            RefreshVolumePreview(_musicVolumeSlider, _musicVolumeValue, _musicVolumeFill);
            RefreshVolumePreview(_soundVolumeSlider, _soundVolumeValue, _soundVolumeFill);
            _settingsOverlay.style.display = DisplayStyle.Flex;
            PauseChanged?.Invoke(true);
        }

        /// <summary>
        /// 保存玩家设置并关闭设置面板。
        /// 输入：设置面板中的主音量百分比与全屏开关；输出：立即更新 Unity 设备状态并写入 PlayerPrefs。
        /// 为什么在关闭时统一应用：避免拖动滑块或试切全屏时反复写盘和切换窗口模式。
        /// </summary>
        void ApplyAndCloseSettingsPanel()
        {
            if (_masterVolumeSlider != null)
            {
                var volume = Mathf.Clamp01(_masterVolumeSlider.value / 100f);
                AudioListener.volume = volume;
                PlayerPrefs.SetFloat(_masterVolumePreferenceKey, volume);
            }
            if (_musicVolumeSlider != null)
                PlayerPrefs.SetFloat(_musicVolumePreferenceKey, Mathf.Clamp01(_musicVolumeSlider.value / 100f));
            if (_soundVolumeSlider != null)
                PlayerPrefs.SetFloat(_soundVolumePreferenceKey, Mathf.Clamp01(_soundVolumeSlider.value / 100f));
            if (_fullscreenToggle != null)
            {
                Screen.fullScreen = _fullscreenToggle.value;
                PlayerPrefs.SetInt(_fullscreenPreferenceKey, _fullscreenToggle.value ? 1 : 0);
            }
            PlayerPrefs.Save();
            CloseSettingsPanel();
        }

        /// <summary>
        /// 把设置表单恢复为默认值，但在玩家点击“保存并关闭”前不改变设备状态。
        /// 输入：无；输出：主音量回到 100%，显示模式回到全屏。
        /// 为什么只改表单：玩家仍有机会查看或改变选择，避免一次误点立即切换窗口。
        /// </summary>
        void ResetPlayerSettings()
        {
            if (_masterVolumeSlider != null) _masterVolumeSlider.value = 100f;
            if (_musicVolumeSlider != null) _musicVolumeSlider.value = 100f;
            if (_soundVolumeSlider != null) _soundVolumeSlider.value = 100f;
            if (_fullscreenToggle != null) _fullscreenToggle.value = true;
        }

        /// <summary>
        /// 响应主音量滑条变化并同步百分比与橙色填充长度。
        /// 输入：UI Toolkit 的浮点变更事件；输出：只更新设置面板预览，不在拖动过程中写磁盘。
        /// </summary>
        void OnMasterVolumeChanged(ChangeEvent<float> changeEvent) =>
            RefreshVolumePreview(_masterVolumeSlider, _masterVolumeValue, _masterVolumeFill);

        /// <summary>
        /// 响应音乐音量滑条变化并同步百分比与橙色填充长度。
        /// 输入：UI Toolkit 的浮点变更事件；输出：只更新设置面板预览，最终值在返回时保存。
        /// </summary>
        void OnMusicVolumeChanged(ChangeEvent<float> changeEvent) =>
            RefreshVolumePreview(_musicVolumeSlider, _musicVolumeValue, _musicVolumeFill);

        /// <summary>
        /// 响应音效音量滑条变化并同步百分比与橙色填充长度。
        /// 输入：UI Toolkit 的浮点变更事件；输出：只更新设置面板预览，最终值在返回时保存。
        /// </summary>
        void OnSoundVolumeChanged(ChangeEvent<float> changeEvent) =>
            RefreshVolumePreview(_soundVolumeSlider, _soundVolumeValue, _soundVolumeFill);

        /// <summary>
        /// 把一个音量滑条的数值同步到独立百分比文本和美术填充条。
        /// 输入：滑条、百分比标签、填充裁切窗口；输出：标签显示 0% 到 100%，裁切窗口覆盖轨道 85% 的有效内宽。
        /// 为什么乘以 0.85：轨道端盖与源图透明边合计约占 15%，填充只能覆盖中间内槽，否则会穿出白色端盖。
        /// </summary>
        static void RefreshVolumePreview(Slider slider, Label valueLabel, VisualElement fill)
        {
            if (slider == null) return;
            var percent = Mathf.Clamp(slider.value, 0f, 100f);
            if (valueLabel != null) valueLabel.text = $"{Mathf.RoundToInt(percent)}%";
            if (fill != null) fill.style.width = Length.Percent(percent * 0.85f);
        }

        /// <summary>
        /// 关闭设置面板并恢复对局。
        /// 输入：无；输出：隐藏设置遮罩并通过 PauseChanged(false) 恢复输入与时间。
        /// 该方法由保存流程与开发者配置跳转共用，确保恢复路径一致。
        /// </summary>
        void CloseSettingsPanel()
        {
            if (_settingsOverlay == null) return;
            _settingsOverlay.style.display = DisplayStyle.None;
            PauseChanged?.Invoke(false);
        }

#if UNITY_EDITOR
        /// <summary>
        /// 从设置页打开开发者配置面板。
        /// 输入：无；输出：先关闭普通设置，再触发 DebugRequested。
        /// 为什么不把调试项放在顶部：它只在编辑器里存在，应与玩家常用入口明确分层。
        /// 为什么整段包在 #if UNITY_EDITOR 里：它引用的 DebugRequested 只在编辑器编译，
        /// 玩家程序集里没有这个事件，不守卫会直接编译失败。
        /// </summary>
        void OpenDebugFromSettings()
        {
            CloseSettingsPanel();
            DebugRequested?.Invoke();
        }
#endif

        /// <summary>
        /// 打开规则帮助面板。
        /// 输入：无；输出：显示规则摘要并通过 PauseChanged(true) 冻结对局。
        /// 为什么帮助也暂停：阅读时不能让 Boss 动画或计时继续推进，也要阻止点击穿透到棋盘。
        /// </summary>
        void OpenHelpPanel()
        {
            if (_helpOverlay == null) return;
            _helpOverlay.style.display = DisplayStyle.Flex;
            PauseChanged?.Invoke(true);
        }

        /// <summary>
        /// 关闭规则帮助面板。
        /// 输入：无；输出：隐藏帮助遮罩并通过 PauseChanged(false) 恢复对局。
        /// </summary>
        void CloseHelpPanel()
        {
            if (_helpOverlay == null) return;
            _helpOverlay.style.display = DisplayStyle.None;
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
            sb.Append("Boss 第 ").Append((int)snapshot.BossPhase + 1).Append(" 阶段").AppendLine(snapshot.BossInvulnerable ? " · 锁血，无法被攻击" : "");
            if (snapshot.BossWarships.Count > 0)
                sb.Append("战舰：待部署 ").Append(snapshot.BossWarships.Count(s => s.Status == BossWarshipStatus.Pending))
                    .Append(" · 存活 ").Append(snapshot.BossWarships.Count(s => s.Status == BossWarshipStatus.Alive))
                    .Append(" · 已击败 ").Append(snapshot.BossWarships.Count(s => s.Status == BossWarshipStatus.Defeated)).AppendLine();
            foreach (var bomb in snapshot.BossBombs)
                sb.Append("炸弹 ").Append(bomb.Position).Append(" · 第 ").Append(bomb.DueRound).Append(" 回合玩家行动前爆炸 · 伤害 ").Append(bomb.Damage).AppendLine();
            if (snapshot.BossBombPending) sb.AppendLine("炸弹机会：请先布置或跳过");
            if (snapshot.BossSpawnerPending) sb.AppendLine("刷怪卡机会：请先部署或跳过");
            var spawner = snapshot.Units.FirstOrDefault(u => u.IsBossSpawner);
            if (spawner != null)
                sb.Append("刷怪卡：").Append(spawner.Hp).AppendLine(" 血 · 从部署后下一次 Boss 回合起尝试生成一架战机");
            if (snapshot.PlayerControlsBoss)
            {
                var bossName = configuration != null && configuration.Cards.TryGetValue(configuration.Rules.BossCardType, out var boss) ? boss.Name : configuration?.Rules.BossCardType;
                sb.Append("手动 Boss 模式：放牌 ").Append(configuration?.Rules.BossPlays ?? 0).Append(" 张（").Append(bossName).Append("）");
                return sb.ToString();
            }
            sb.Append(snapshot.BossPhase == BossPhase.PhaseOne ? "AI 兵位：" : "固定兵位（补兵已暂停）：");
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
            if (snapshot.BossInvulnerable) return "Boss 二阶段锁血：击败两艘战舰以解除锁血";
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
                // 单位详情必须用单位快照（当前攻击/射程/移动与一次性减攻状态），不能用卡种基础配置。
                var text = configuration.Cards.TryGetValue(unit.TypeId, out var arch) ? CardText.UnitDetail(arch, unit) : string.Empty;
                return text + Environment.NewLine + $"行动机会 {(unit.CanAct ? "可用" : unit.Acted ? "已用完" : "维持费不足")}";
            }
            if (selection.Kind == SelectionKind.Home)
            {
                var home = snapshot.Homes.FirstOrDefault(h => h.Id == selection.Id);
                if (home == null) return string.Empty;
                return $"{(home.Owner == Side.Player ? "玩家" : "Boss")}的家 · 生命 {home.Hp}{(home.Owner == Side.Boss && snapshot.BossInvulnerable ? "（锁血，无法被攻击）" : "")} · {(home.Acted ? "本回合已移动" : "本回合可移动一格")}";
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
        /// 刷新牌堆计数：正中一个剩余总张数，底部一行小字给阶级构成。
        /// 输入：当前快照与配置；输出：无。
        /// 为什么总数直接取 DrawPile.Count 而不是三个阶级相加：卡种未注册（配置被改坏）时
        /// 该张卡不计入任何阶级，相加会少算；总数必须以快照为准，阶级小字只作为辅助信息。
        /// 为什么把三行竖排压成一行小字：面板改成图片徽章后，正中只能有一个数字可读；
        /// 玩家仍需要知道剩余牌里的阶级构成，所以保留成底部的一行小字。
        /// </summary>
        void RenderDrawPile(GameSnapshot snapshot, GameConfiguration configuration)
        {
            var basic = 0;
            var intermediate = 0;
            var advanced = 0;
            if (configuration != null)
            {
                foreach (var card in snapshot.DrawPile)
                {
                    var archetype = configuration.FindCard(card.TypeId);
                    if (archetype == null) continue;
                    switch (archetype.Tier)
                    {
                        case CardTier.Basic: basic++; break;
                        case CardTier.Intermediate: intermediate++; break;
                        case CardTier.Advanced: advanced++; break;
                    }
                }
            }
            if (_drawCount != null) _drawCount.text = snapshot.DrawPile.Count.ToString();
            if (_drawTiers != null) _drawTiers.text = $"高 {advanced} · 中 {intermediate} · 初 {basic}";
        }

        /// <summary>携带选择面板（供 GameBootstrap 打开、订阅确认/取消事件）。</summary>
        public DeckSelectionPanel DeckSelectionPanel => _deckSelectionPanel;

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
            _menuVisible = visible;
            ShowElement(_detailLeft, !visible && HasDetailContent(_detailLeftTitle, _detailLeftBody));
            ShowElement(_detailRight, !visible && HasDetailContent(_detailRightTitle, _detailRightBody));
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
        /// 判断详情面板是否含有可展示内容。
        /// 输入：详情标题与正文标签；输出：任一标签含非空文本时为 true。
        /// 为什么需要：菜单关闭或无选择时不能仅按“处于战斗界面”就显示空面板，否则大面板会覆盖生命与能量组件。
        /// </summary>
        static bool HasDetailContent(Label title, Label body)
        {
            return !string.IsNullOrEmpty(title?.text) || !string.IsNullOrEmpty(body?.text);
        }

        /// <summary>
        /// 填充左右详情信息栏（单选中槽）。
        /// 输入：当前选择、快照与配置；输出：无。
        /// 行为：我方手牌/单位使用左侧信息栏，敌方单位使用右侧信息栏；名称、属性与规则补充分别落在卡面式层级中。
        /// 为什么分手牌与单位：手牌显示配置基础值，场上单位必须显示快照里的当前生命、攻击和移动，避免减攻或受伤后仍显示初始值。
        /// 为什么保持单槽：界面同一时刻只有一个选择，另一侧不显示，避免空信息栏遮挡棋盘或造成“两侧都被选中”的误导。
        /// </summary>
        void FillDetailFaces(Selection selection, GameSnapshot snapshot, GameConfiguration configuration)
        {
            CardArchetype leftArchetype = null;
            CardArchetype rightArchetype = null;
            UnitState leftUnit = null;
            UnitState rightUnit = null;

            if (snapshot != null && configuration != null)
            {
                if (selection.Kind == SelectionKind.Hand)
                {
                    var card = snapshot.Hand.FirstOrDefault(c => c.Id == selection.Id);
                    if (card != null) configuration.Cards.TryGetValue(card.TypeId, out leftArchetype);
                }
                else if (selection.Kind == SelectionKind.Unit)
                {
                    var unit = snapshot.Units.FirstOrDefault(u => u.Id == selection.Id);
                    if (unit != null && configuration.Cards.TryGetValue(unit.TypeId, out var unitArchetype))
                    {
                        if (unit.Owner == Side.Player)
                        {
                            leftArchetype = unitArchetype;
                            leftUnit = unit;
                        }
                        else
                        {
                            rightArchetype = unitArchetype;
                            rightUnit = unit;
                        }
                    }
                }
            }

            ApplyDetailFace(
                _detailLeftTitle, _detailLeftMeta, _detailLeftTrait, _detailLeftBody,
                _detailLeftAttack, _detailLeftHealth, _detailLeftMove,
                leftArchetype, leftUnit, Side.Player);
            ApplyDetailFace(
                _detailRightTitle, _detailRightMeta, _detailRightTrait, _detailRightBody,
                _detailRightAttack, _detailRightHealth, _detailRightMove,
                rightArchetype, rightUnit, Side.Boss);

            ShowElement(_detailLeft, !_menuVisible && HasDetailContent(_detailLeftTitle, _detailLeftBody));
            ShowElement(_detailRight, !_menuVisible && HasDetailContent(_detailRightTitle, _detailRightBody));
        }

        /// <summary>
        /// 把一个卡种或场上单位写入一侧的竖向卡面信息区。
        /// 输入：名称、阵营阶级、特性标签、规则说明、三项属性标签、卡种、可选单位快照与所属阵营；输出：无。
        /// 行为：单位存在时使用当前攻击、生命和移动；手牌使用卡种基础值；没有补充规则时隐藏“特性”。
        /// 为什么集中处理：左右两侧结构完全相同，统一写入可避免数值口径、英文阶级和空状态出现分叉。
        /// </summary>
        static void ApplyDetailFace(
            Label title, Label meta, Label trait, Label body,
            Label attack, Label health, Label move,
            CardArchetype archetype, UnitState unit, Side owner)
        {
            if (archetype == null)
            {
                if (title != null) title.text = string.Empty;
                if (meta != null) meta.text = string.Empty;
                if (body != null) body.text = string.Empty;
                if (attack != null) attack.text = "0";
                if (health != null) health.text = "0";
                if (move != null) move.text = "0";
                ShowElement(trait, false);
                return;
            }

            if (title != null) title.text = archetype.Name;
            if (meta != null)
                meta.text = $"{(owner == Side.Player ? "我方" : "敌方")}·{CardFaceTierLabel(archetype.Tier)}";

            var combat = archetype.Combat;
            if (attack != null) attack.text = (unit != null ? unit.Attack : combat?.Attack ?? 0).ToString();
            if (health != null) health.text = (unit != null ? unit.Hp : combat?.Hp ?? 0).ToString();
            if (move != null) move.text = (unit != null ? unit.MoveDistance : combat?.MoveDistance ?? 0).ToString();

            var fullDetail = unit == null
                ? CardText.DetailLine(archetype, owner)
                : CardText.UnitDetail(archetype, unit);
            var supplemental = RemoveLeadingDetailLines(fullDetail, unit == null ? 2 : 3);
            if (unit != null)
            {
                var action = $"行动机会：{(unit.CanAct ? "可用" : unit.Acted ? "已用完" : "维持费不足")}";
                supplemental = string.IsNullOrWhiteSpace(supplemental)
                    ? action
                    : supplemental + Environment.NewLine + action;
            }
            if (body != null) body.text = supplemental;
            ShowElement(trait, !string.IsNullOrWhiteSpace(supplemental));
        }

        /// <summary>
        /// 把规则阶级转换为实体卡面使用的英文短标签。
        /// 输入：卡种阶级；输出：JUNIOR、MIDDLE、SENIOR 或 BOSS。
        /// 为什么单独转换：规则与选卡界面继续使用“初级/中级/高级”，只有战斗详情需要与现有卡面美术统一。
        /// </summary>
        static string CardFaceTierLabel(CardTier tier)
        {
            switch (tier)
            {
                case CardTier.Basic: return "JUNIOR";
                case CardTier.Intermediate: return "MIDDLE";
                case CardTier.Advanced: return "SENIOR";
                case CardTier.None: return "BOSS";
                default: return "UNKNOWN";
            }
        }
        /// <summary>
        /// 去掉完整详情中已经由名称、副标题和图标数值表达的前置行。
        /// 输入：完整多行详情与要移除的行数；输出：只保留规则、限制及状态说明的文本。
        /// 为什么需要：继续复用 CardText 的唯一规则文案，同时避免在信息栏里重复显示卡名和同一组属性。
        /// </summary>
        static string RemoveLeadingDetailLines(string detail, int count)
        {
            if (string.IsNullOrEmpty(detail) || count <= 0) return detail ?? string.Empty;
            var lines = detail.Replace("\r\n", "\n").Split('\n');
            return string.Join(Environment.NewLine, lines.Skip(Math.Min(count, lines.Length))).Trim();
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
