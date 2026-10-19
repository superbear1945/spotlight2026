namespace Spotlight
{
    /// <summary>Boss 技能阶段，与 MatchPhase 的行动/弃牌/结束阶段独立。阶段只向前推进。</summary>
    public enum BossPhase { PhaseOne, PhaseTwo, PhaseThree }

    /// <summary>阶段战舰生命周期；待部署与已击败必须区分，以免满盘时错误解除锁血。</summary>
    public enum BossWarshipStatus { Pending, Alive, Defeated }

    /// <summary>一艘指定战舰的阶段记录。真实记录由 GameSession 所有，快照取得独立副本。</summary>
    public sealed class BossWarshipState
    {
        /// <summary>一基战舰序号（1 或 2），与普通兵位无关。</summary>
        public int Index { get; internal set; }
        /// <summary>三等分行和玩家视角列换算得到的首选格。</summary>
        public Cell PreferredCell { get; internal set; }
        /// <summary>成功部署的单位 ID；待部署时为空，阵亡后保留便于追踪。</summary>
        public long? UnitId { get; internal set; }
        /// <summary>部署/存活/击败状态。</summary>
        public BossWarshipStatus Status { get; internal set; }
        /// <summary>输入为当前记录，输出为独立副本，旧快照不会随后续死亡改变。</summary>
        internal BossWarshipState Copy() => (BossWarshipState)MemberwiseClone();
    }

    /// <summary>阶段扩展契约。GameSession 提供受限结算入口；状态只负责调度，不保存单位或生命副本。</summary>
    internal interface IBossPhaseState
    {
        /// <summary>策略代表的阶段；用于规则门禁及快照。</summary>
        BossPhase Phase { get; }
        /// <summary>输入唯一会话，执行本阶段一次性进入结算，无返回值。</summary>
        void Enter(GameSession session);
        /// <summary>输入唯一会话，离开前释放阶段专属机会，不销毁公共单位。</summary>
        void Exit(GameSession session);
        /// <summary>行动恢复和通用刷怪后调度 Boss 技能；条件来自回合开始的一次读取。</summary>
        void OnBossTurnStarted(GameSession session, bool spawnerTriggered);
        /// <summary>公共炸弹引爆、资源、抽牌与刷怪之后调度玩家回合技能。</summary>
        void OnPlayerTurnStarted(GameSession session);
        /// <summary>伤害结算后、胜负判断前检查阶段转换，输入会话及状态机，无返回值。</summary>
        void AfterResolution(GameSession session, BossPhaseStateMachine machine);
    }

    /// <summary>Boss 状态机定位入口。拥有当前状态对象；转换先切换状态再调用 Enter，保证锁血立即生效。</summary>
    internal sealed class BossPhaseStateMachine
    {
        /// <summary>顺序固定的阶段策略，仅保存行为，不保存棋盘或单位副本。</summary>
        readonly IBossPhaseState[] _states = { new BossPhaseOneState(), new BossPhaseTwoState(), new BossPhaseThreeState() };
        /// <summary>当前策略；切换先更新引用，再调用新阶段的进入逻辑。</summary>
        IBossPhaseState _state;
        /// <summary>当前阶段，用于快照、目标合法性与技能门禁。</summary>
        internal BossPhase Phase => _state.Phase;
        /// <summary>创建一阶段状态；不执行技能，技能按回合入口触发。</summary>
        internal BossPhaseStateMachine() { _state = _states[0]; }
        /// <summary>切换到下一阶段。输入为会话和下一阶段；输出为阶段事件及 Enter 产生的结算。</summary>
        internal void Transition(GameSession session, BossPhase next)
        {
            if ((int)next != (int)Phase + 1) return;
            _state.Exit(session);
            _state = _states[(int)next];
            session.OnBossPhaseChanged(next);
            _state.Enter(session);
        }
        /// <summary>Boss 行动恢复、既有刷怪结算后调用；输入为回合开始读取的压力条件，输出由当前阶段决定。</summary>
        internal void OnBossTurnStarted(GameSession session, bool triggered) => _state.OnBossTurnStarted(session, triggered);
        /// <summary>玩家回合的既有炸弹、资源、抽牌结算后调用；当前阶段决定是否布置新炸弹。</summary>
        internal void OnPlayerTurnStarted(GameSession session) => _state.OnPlayerTurnStarted(session);
        /// <summary>每次伤害结算后检查转换；先于胜负判断，避免致死伤害跳过二阶段。</summary>
        internal void AfterResolution(GameSession session) => _state.AfterResolution(session, this);
    }

    /// <summary>一阶段：原炸弹、压力刷怪笼与 AI 固定兵位行为的唯一调度入口。</summary>
    internal sealed class BossPhaseOneState : IBossPhaseState
    {
        public BossPhase Phase => BossPhase.PhaseOne;
        /// <summary>一阶段无进入副作用；构造时已初始化公共对局状态。</summary>
        public void Enter(GameSession session) { }
        /// <summary>离开时由状态机统一清理技能机会，无需重复修改会话。</summary>
        public void Exit(GameSession session) { }
        /// <summary>输入为回合压力快照；输出为刷怪笼机会与 AI 补兵，保持原顺序。</summary>
        public void OnBossTurnStarted(GameSession session, bool triggered) => session.RunPhaseOneBossSkills(triggered);
        /// <summary>玩家行动前按原间隔产生炸弹机会，手动模式保留选择窗口。</summary>
        public void OnPlayerTurnStarted(GameSession session) => session.PreparePhaseOneBomb();
        /// <summary>达到阈值立即进入二阶段，伤害入口已经将生命截断到阈值。</summary>
        public void AfterResolution(GameSession session, BossPhaseStateMachine machine)
        { if (session.ShouldEnterBossPhaseTwo) machine.Transition(session, BossPhase.PhaseTwo); }
    }

    /// <summary>二阶段：锁血与两艘战舰。只重试待部署战舰，不产生一阶段新技能，也不补回已击败战舰。</summary>
    internal sealed class BossPhaseTwoState : IBossPhaseState
    {
        public BossPhase Phase => BossPhase.PhaseTwo;
        /// <summary>初始化两个记录并立即尝试部署，AI 与手动模式完全一致。</summary>
        public void Enter(GameSession session) => session.CreateBossWarships();
        /// <summary>记录保留供三阶段 HUD 查询，离开无需销毁既有单位。</summary>
        public void Exit(GameSession session) { }
        /// <summary>回合开始重试未部署战舰；不会消耗普通放牌次数。</summary>
        public void OnBossTurnStarted(GameSession session, bool triggered) => session.DeployPendingWarships();
        /// <summary>二阶段不布置新炸弹；已有炸弹由公共回合流程引爆。</summary>
        public void OnPlayerTurnStarted(GameSession session) { }
        /// <summary>两艘记录都确认阵亡后转入三阶段，待部署不计入击败。</summary>
        public void AfterResolution(GameSession session, BossPhaseStateMachine machine)
        { if (session.AllBossWarshipsDefeated) machine.Transition(session, BossPhase.PhaseThree); }
    }

    /// <summary>三阶段：解除锁血并一次性生成双列护卫舰；之后复用公共行动与已有刷怪，不补回护卫舰。</summary>
    internal sealed class BossPhaseThreeState : IBossPhaseState
    {
        public BossPhase Phase => BossPhase.PhaseThree;
        /// <summary>输入唯一会话；进入即解除锁血、清理目标格并生成护卫舰，不恢复一阶段机会。</summary>
        public void Enter(GameSession session) => session.CreateBossGuards();
        /// <summary>预留未来阶段退出清理；本版三阶段不再转换。</summary>
        public void Exit(GameSession session) { }
        /// <summary>三阶段 Boss 回合技能占位；公共行动、手动放牌和刷怪不在这里重复执行。</summary>
        public void OnBossTurnStarted(GameSession session, bool triggered) { }
        /// <summary>三阶段玩家回合技能占位；不调用一阶段炸弹调度。</summary>
        public void OnPlayerTurnStarted(GameSession session) { }
        /// <summary>三阶段结算后扩展点；本版由公共胜负检查结束对局。</summary>
        public void AfterResolution(GameSession session, BossPhaseStateMachine machine) { }
    }
}
