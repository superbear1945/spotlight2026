// 公共命令、快照与身份类型。
// 设计约束：
// 1) 状态的 setter 标记为 internal，只有同一程序集（规则内核）可以修改；外部一律通过快照读取副本。
// 2) 家使用负数实体 ID（玩家 -1、Boss -2），普通单位 ID 为正数；卡牌记录使用独立计数器，
//    因此“手牌 ID”和“单位 ID”永远不可能混淆，UI 选择上下文据此区分两类目标。
using System;
using System.Collections.Generic;

namespace Spotlight
{
    /// <summary>
    /// 棋盘整数坐标。
    /// 输入：内部行号与列号（第 0 行在底部，与 Excel 自上而下的行序相反由编译器负责翻转）；
    /// 输出：可比较、可做曼哈顿距离计算的值类型。
    /// 为什么不用 UnityEngine.Vector2Int：规则内核不允许引用 UnityEngine，EditMode 测试也无需引擎。
    /// </summary>
    public readonly struct Cell : IEquatable<Cell>
    {
        /// <summary>内部行号，0 表示最靠近玩家一侧的底行。</summary>
        public int Row { get; }
        /// <summary>内部列号，0 表示最左侧。</summary>
        public int Col { get; }

        /// <summary>构造一个棋盘坐标。输入：行、列；输出：值类型坐标。</summary>
        public Cell(int row, int col) { Row = row; Col = col; }

        /// <summary>曼哈顿距离，用于四向移动、攻击和普通邻接；刷怪技能的八邻单独枚举，不改变此度量。</summary>
        public int Distance(Cell other) => Math.Abs(Row - other.Row) + Math.Abs(Col - other.Col);

        /// <summary>值相等比较，用于去重与哈希键。</summary>
        public bool Equals(Cell other) => Row == other.Row && Col == other.Col;
        /// <inheritdoc />
        public override bool Equals(object obj) => obj is Cell c && Equals(c);
        /// <inheritdoc />
        public override int GetHashCode() => unchecked(Row * 397 ^ Col);
        /// <inheritdoc />
        public override string ToString() => $"({Row},{Col})";
        /// <summary>相等运算符，便于测试中直接比较坐标。</summary>
        public static bool operator ==(Cell a, Cell b) => a.Equals(b);
        /// <summary>不等运算符。</summary>
        public static bool operator !=(Cell a, Cell b) => !a.Equals(b);
    }

    /// <summary>
    /// 抽牌堆、手牌与墓地之间唯一流转的数据。
    /// 为什么只有三个字段：一次部署会创建全新的 UnitState，单位只复制本记录的值（ID/卡种/阵营），
    /// 不持有本记录引用；因此单位不携带伤势、位置或行动状态，牌堆永远不会被战斗状态污染，
    /// 同一张卡也可以反复部署出多个互相独立的单位。
    /// </summary>
    public sealed class CardRecord
    {
        /// <summary>卡牌实例 ID，全局唯一且递增。</summary>
        public long Id { get; }
        /// <summary>卡种 ID，用于查询 CardArchetype。</summary>
        public string TypeId { get; }
        /// <summary>所属阵营。</summary>
        public Side Owner { get; }

        /// <summary>构造一条卡牌记录。输入：实例 ID、卡种 ID、阵营；输出：不可变记录。</summary>
        public CardRecord(long id, string typeId, Side owner) { Id = id; TypeId = typeId; Owner = owner; }
    }

    /// <summary>
    /// 一次部署产生的可变实体。
    /// 输入：部署命令与卡牌记录；输出：棋盘上的可攻击、可移动目标。
    /// 为什么与 CardRecord 完全分离：同一张卡可以反复进入墓地、洗回、再部署，
    /// 每次部署都生成独立身份、独立生命与行动状态的单位；单位只保存来源卡牌的
    /// 值副本（ID/卡种/阵营），不持有 CardRecord 引用，因此场上的单位与抽牌堆、手牌、
    /// 墓地中的卡牌互不影响，卡牌总数不会因单位存活而增加。
    /// 覆盖升级只移除被覆盖的单位，本次部署的新单位 ID 必须与原单位不同。
    /// 属性职责：攻击/射程/移动距离/生命上限与一次性减攻状态都是**场上属性**，
    /// 部署时从只读卡种配置初始化，之后只由规则代码修改；不能被卡牌记录、SO 或表现层写回。
    /// </summary>
    public sealed class UnitState
    {
        /// <summary>单位实例 ID（正数）。</summary>
        public long Id { get; internal set; }
        /// <summary>来源卡牌实例 ID 的值副本；仅用于追溯，不引用抽牌堆/手牌/墓地中的卡牌。</summary>
        public long SourceCardId { get; internal set; }
        /// <summary>卡种 ID 的值副本，用于查询共享配置。</summary>
        public string TypeId { get; internal set; }
        /// <summary>所属阵营的值副本。</summary>
        public Side Owner { get; internal set; }
        /// <summary>当前所在格。</summary>
        public Cell Position { get; internal set; }
        /// <summary>当前生命；在场单位跨回合保留伤势。</summary>
        public int Hp { get; internal set; }
        /// <summary>生命上限：部署时从卡种配置复制；本次不新增改变上限的玩法。</summary>
        public int MaxHp { get; internal set; }
        /// <summary>当前攻击力：部署时从卡种配置复制，一次性部署减攻后持久保存，不在场上读配置。</summary>
        public int Attack { get; internal set; }
        /// <summary>当前攻击距离（曼哈顿）：部署时从卡种配置复制，用作一切在场射程判断。</summary>
        public int Range { get; internal set; }
        /// <summary>当前移动距离：部署时从卡种配置复制，用作一切在场移动范围判断。</summary>
        public int MoveDistance { get; internal set; }
        /// <summary>一次性部署减攻是否尚未触发：具有该能力的单位上场时为 true，触发一次后永久为 false。</summary>
        public bool DeploymentPenaltyPending { get; internal set; }
        /// <summary>该单位的一次性减攻量副本：没有该能力时为 0，来自卡种配置而非代码常量。</summary>
        public int DeploymentAttackReduction { get; internal set; }
        /// <summary>
        /// 标记来源阵营；null 表示未被标记。
        /// 为什么要记录来源：标记是永久状态（来源离场也不消失），阵亡时必须按“谁标记的”给对应阵营发资源。
        /// 多个来源重叠时不叠加：已被标记的单位不会改归属（先到先得）。
        /// </summary>
        public Side? MarkedBy { get; internal set; }
        /// <summary>标记奖励副本：标记时从来源配置复制，阵亡时按此值发放，不随后续配置改动而变化。</summary>
        public int MarkReward { get; internal set; }
        /// <summary>死亡加攻：每次全棋盘单位因战斗阵亡时增加的攻击力副本；没有该能力时为 0。</summary>
        public int DeathGrowthPerKill { get; internal set; }
        /// <summary>死亡加攻的次数上限副本；没有该能力时为 0。</summary>
        public int DeathGrowthLimit { get; internal set; }
        /// <summary>死亡加攻已触发次数；达到上限后不再增加攻击力。</summary>
        public int DeathGrowthTriggers { get; internal set; }
        /// <summary>本回合是否已经行动（移动、攻击或互换其一）。</summary>
        public bool Acted { get; internal set; }
        /// <summary>本回合维持费是否已支付；未支付时不能行动。</summary>
        public bool UpkeepPaid { get; internal set; }
        /// <summary>部署序号，用于资源支付与 AI 的行动顺序。</summary>
        public long DeploymentOrder { get; internal set; }
        /// <summary>AI 兵位 ID；非 AI 兵位单位该值为 null，因此不会触发补兵计时。</summary>
        public string BossSlotId { get; internal set; }
        /// <summary>是否由 Boss 刷怪技能部署的固定据点；身份不依赖卡名，不允许移动、攻击或互换。</summary>
        public bool IsBossSpawner { get; internal set; }
        /// <summary>部署时的完整回合号；刷怪卡在之后的 Boss 回合才开始生成战机。</summary>
        public long DeployedRound { get; internal set; }

        /// <summary>是否本回合仍可行动：不是固定刷怪卡、未行动且维持费已付。</summary>
        public bool CanAct => !IsBossSpawner && !Acted && UpkeepPaid;

        /// <summary>浅拷贝，供 GetSnapshot 输出与真实对局解耦。</summary>
        internal UnitState Copy() => (UnitState)MemberwiseClone();
    }

    /// <summary>
    /// 独立的家实体。
    /// 为什么不让家继承 Card：家不进入抽牌堆、不参与互换、没有攻击力，也不受部署限制；
    /// 强行共用会引入大量“家不适用”的分支。家以负数 ID 参与攻击目标选择与快照。
    /// </summary>
    public sealed class HomeState
    {
        /// <summary>家的实体 ID：玩家为 -1，Boss 为 -2。</summary>
        public long Id => Owner == Side.Player ? -1 : -2;
        /// <summary>归属阵营。</summary>
        public Side Owner { get; internal set; }
        /// <summary>当前所在格。</summary>
        public Cell Position { get; internal set; }
        /// <summary>当前生命。</summary>
        public int Hp { get; internal set; }
        /// <summary>本回合是否已经移动过（每回合最多移动一格）。</summary>
        public bool Acted { get; internal set; }

        /// <summary>浅拷贝，供 GetSnapshot 输出。</summary>
        internal HomeState Copy() => (HomeState)MemberwiseClone();
    }

    /// <summary>
    /// AI 独立兵位状态。
    /// 输入：SpawnSlot 定义与回合号；输出：UI 可展示的补兵计时与等待状态。
    /// HasSpawned 区分“首次部署”和“死亡补兵”：只有首次部署可以立即行动。
    /// </summary>
    public sealed class BossReplacement
    {
        /// <summary>兵位稳定 ID。</summary>
        public string SlotId { get; internal set; }
        /// <summary>首选格；被占用时由补兵规则挑选替代格。</summary>
        public Cell PreferredCell { get; internal set; }
        /// <summary>当前占据该兵位的单位 ID；null 表示空缺。</summary>
        public long? UnitId { get; internal set; }
        /// <summary>补兵到期的完整回合号；long.MaxValue 表示“被覆盖升级”而不需要补兵。</summary>
        public long DueRound { get; internal set; }
        /// <summary>距到期还剩的回合数，由快照计算并下限截断为 0。</summary>
        public long RemainingRounds { get; internal set; }
        /// <summary>是否因为棋盘没有空位而在等待。</summary>
        public bool WaitingForSpace { get; internal set; }
        /// <summary>该兵位是否已经成功部署过至少一次。</summary>
        public bool HasSpawned { get; internal set; }

        /// <summary>按当前回合号计算剩余回合的浅拷贝，供快照输出。</summary>
        internal BossReplacement Copy(long round) { var s = (BossReplacement)MemberwiseClone(); s.RemainingRounds = Math.Max(0, DueRound - round); return s; }
    }

    /// <summary>
    /// 独立地面炸弹的不可变状态，不属于卡牌记录或战斗单位，也不占格。
    /// 输入：部署时的坐标、回合和伤害；输出：规则计时与视图标记共用的数据。
    /// 不可变对象可安全共享给旧快照，爆炸时移除整条状态而不修改它。
    /// </summary>
    public sealed class BossBombState
    {
        /// <summary>部署格的内部坐标。</summary>
        public Cell Position { get; }
        /// <summary>部署时的完整回合号。</summary>
        public long DeployedRound { get; }
        /// <summary>到期的完整回合号，在该回合玩家行动前引爆。</summary>
        public long DueRound { get; }
        /// <summary>整颗炸弹的伤害预算。</summary>
        public int Damage { get; }

        /// <summary>构造炸弹。输入：坐标、部署回合、伤害；输出：下一回合到期的不可变状态。</summary>
        public BossBombState(Cell position, long deployedRound, int damage)
        { Position = position; DeployedRound = deployedRound; DueRound = deployedRound + 1; Damage = damage; }
    }

    /// <summary>
    /// 爆炸实际访问一个格子的不可变结果，供动画复现而不重新推算战斗。
    /// 输入：格子、受伤单位、扣血量与是否阵亡；输出：路径和伤害的展示数据。
    /// </summary>
    public sealed class BombBlastStep
    {
        /// <summary>实际搜索过的格子。</summary>
        public Cell Position { get; }
        /// <summary>受伤单位 ID，空格或忽略对象时为 0。</summary>
        public long UnitId { get; }
        /// <summary>此格实际消耗的伤害。</summary>
        public int Damage { get; }
        /// <summary>该单位是否因本次爆炸阵亡。</summary>
        public bool Killed { get; }

        /// <summary>保存单格结算。输入：坐标与伤害结果；输出：不可变路径步骤。</summary>
        public BombBlastStep(Cell position, long unitId, int damage, bool killed)
        { Position = position; UnitId = unitId; Damage = damage; Killed = killed; }
    }

    /// <summary>回合阶段：正常行动、超限弃牌、已结束。</summary>
    public enum MatchPhase { Action, Discard, Finished }

    /// <summary>对局结果。</summary>
    public enum MatchOutcome { None, PlayerWins, BossWins, Draw }

    /// <summary>命令种类。玩家与 AI 共用同一入口，区别只在 ExecuteCommand 的 fromAi 标记。</summary>
    public enum CommandKind { Deploy, BossDeploy, Move, MoveHome, Attack, Swap, Withdraw, Discard, EndTurn, DeployBossBomb, SkipBossBomb, DeployBossSpawner, SkipBossSpawner }

    /// <summary>
    /// 不可变操作请求。
    /// 输入：由 UI 或 AI 组装；输出：交给 GameSession.Execute 校验并结算。
    /// 约定：SubjectId 可指手牌、单位或家，含义由 Kind 决定；Target/TargetId 视为不可信输入，
    /// 执行时必须复验，避免界面高亮过期后绕过规则。
    /// </summary>
    public sealed class GameCommand
    {
        /// <summary>命令种类。</summary>
        public CommandKind Kind { get; }
        /// <summary>发起阵营；必须与当前行动方一致（AI 调用同样如此）。</summary>
        public Side Actor { get; }
        /// <summary>主体实体 ID：手牌 ID、单位 ID 或家 ID。</summary>
        public long SubjectId { get; }
        /// <summary>目标格，用于部署、移动与家的移动。</summary>
        public Cell Target { get; }
        /// <summary>目标实体 ID，用于攻击与互换。</summary>
        public long TargetId { get; }
        /// <summary>手动 Boss 模式专用：跳过剩余放牌次数直接结束回合。</summary>
        public bool SkipDeployment { get; }

        /// <summary>
        /// 构造命令。输入：种类、发起方以及按种类需要的可选字段；
        /// 输出：不可变请求对象。所有参数都有默认值，调用点只填写与本次操作相关的字段。
        /// </summary>
        public GameCommand(CommandKind kind, Side actor, long subjectId = 0, Cell target = default, long targetId = 0, bool skipDeployment = false)
        { Kind = kind; Actor = actor; SubjectId = subjectId; Target = target; TargetId = targetId; SkipDeployment = skipDeployment; }
    }

    /// <summary>界面选择上下文种类。显式区分卡牌身份与单位身份，避免 ID 语义歧义。</summary>
    public enum SelectionKind { None, Hand, Unit, Home, BossDeployment, BossBombDeployment, BossSpawnerDeployment }

    /// <summary>
    /// 界面选择上下文。
    /// 输入：玩家点击的手牌、单位、家或“Boss 未选单位但仍可放牌”状态；
    /// 输出：传给 GetLegalActions 以取得高亮范围。
    /// </summary>
    public readonly struct Selection
    {
        /// <summary>选择种类。</summary>
        public SelectionKind Kind { get; }
        /// <summary>选中实体 ID；None/BossDeployment 时无意义。</summary>
        public long Id { get; }

        /// <summary>构造选择上下文。输入：种类与实体 ID；输出：值类型上下文。</summary>
        public Selection(SelectionKind kind, long id = 0) { Kind = kind; Id = id; }
    }

    /// <summary>
    /// 规则层计算出的可选格列表。
    /// 为什么需要：UI 不得自行推导合法性；同时高亮只用于提示，命令执行时仍会复验。
    /// </summary>
    public sealed class LegalActions
    {
        /// <summary>可部署格。</summary>
        public IReadOnlyList<Cell> Deploy { get; internal set; } = Array.Empty<Cell>();
        /// <summary>可移动格。</summary>
        public IReadOnlyList<Cell> Move { get; internal set; } = Array.Empty<Cell>();
        /// <summary>可攻击目标所在格。</summary>
        public IReadOnlyList<Cell> Attack { get; internal set; } = Array.Empty<Cell>();
        /// <summary>
        /// 可攻击的**己方**单位所在格（只有“攻击友方即治疗”的卡才有）。
        /// 为什么单独列出：这些格同时也在 Attack 里（点击链路与敌方目标一致），
        /// 单列出来只是让视图把它们高亮成治疗色，规则层不会重复判定。
        /// </summary>
        public IReadOnlyList<Cell> Heal { get; internal set; } = Array.Empty<Cell>();
        /// <summary>可互换的己方单位所在格。</summary>
        public IReadOnlyList<Cell> Swap { get; internal set; } = Array.Empty<Cell>();
        /// <summary>
        /// 选中的己方单位此刻能否离场。
        /// 为什么是标志而不是格列表：离场没有目标格（只针对自己），但它同样属于“合法动作”，
        /// 必须由规则层给出，界面不得自行推导（否则界面与结算会出现两套判断）。
        /// </summary>
        public bool CanWithdraw { get; internal set; }
    }

    /// <summary>
    /// 已经发生的领域事件。
    /// 输入：规则内核结算时产生；输出：界面日志与表现定位的输入。
    /// 约束：动画只读取事件，不得回写结算结果，否则帧率会影响对局。
    /// </summary>
    public sealed class GameEvent
    {
        /// <summary>事件类型标记（deploy/move/attack/death/turn/draw/…），供 UI 选择表现。</summary>
        public string Kind { get; }
        /// <summary>中文日志文本。</summary>
        public string Message { get; }
        /// <summary>相关实体 ID，用于把动画定位到具体单位（0 表示无主体）。</summary>
        public long SubjectId { get; }

        /// <summary>炸弹部署/爆炸的来源状态；其他事件为空。</summary>
        public BossBombState Bomb { get; }
        /// <summary>爆炸实际访问顺序与伤害结果；复制数组防止外部改变事件。</summary>
        public IReadOnlyList<BombBlastStep> BlastSteps { get; }

        /// <summary>构造事件。输入：类型、文本、主体及可选炸弹路径；输出：拥有独立只读路径的事件。</summary>
        public GameEvent(string kind, string message, long subjectId = 0, BossBombState bomb = null, BombBlastStep[] blastSteps = null)
        {
            Kind = kind; Message = message; SubjectId = subjectId; Bomb = bomb;
            BlastSteps = Array.AsReadOnly(blastSteps == null ? Array.Empty<BombBlastStep>() : (BombBlastStep[])blastSteps.Clone());
        }
    }

    /// <summary>
    /// 一次命令调用的完整结果。
    /// 输入：Execute 的返回值；输出：成功状态、失败原因与该命令的全部有序事件。
    /// 为什么事件挂在结果上：一次玩家结束回合可能连带整个 Boss 回合，UI 需要按序播放全部日志。
    /// </summary>
    public sealed class CommandResult
    {
        /// <summary>是否执行成功；失败时状态未被修改。</summary>
        public bool Success { get; }
        /// <summary>失败原因；成功时为 null。</summary>
        public string Error { get; }
        /// <summary>按发生顺序排列的事件。</summary>
        public IReadOnlyList<GameEvent> Events { get; }

        /// <summary>炸弹准备及自动 Boss 行动的有序展示阶段；为空时直接展示最终状态，事件与 Events 共用不可变对象。</summary>
        public IReadOnlyList<PresentationFrame> PresentationFrames { get; }

        /// <summary>内部构造。输入：状态、全部事件与展示阶段；输出：不可变结果，阶段只供表现读取。</summary>
        internal CommandResult(bool success, string error, GameEvent[] events, PresentationFrame[] frames)
        { Success = success; Error = error; Events = Array.AsReadOnly(events); PresentationFrames = Array.AsReadOnly(frames); }
    }

    /// <summary>
    /// 已结算阶段的展示记录。输入：独立快照与该阶段新增事件；输出：界面顺序播放的数据。
    /// 用于保留炸弹处理及自动 Boss 行动时的棋盘、回合号和日志，不暂停规则执行，也不允许动画回写规则。
    /// </summary>
    public sealed class PresentationFrame
    {
        /// <summary>该阶段结束时的独立只读快照。</summary>
        public GameSnapshot Snapshot { get; }
        /// <summary>仅本阶段产生的事件，不重复包含前一阶段事件。</summary>
        public IReadOnlyList<GameEvent> Events { get; }

        /// <summary>构造阶段。输入：已复制的快照和新事件数组；输出：只读展示记录，只有内核可创建。</summary>
        internal PresentationFrame(GameSnapshot snapshot, GameEvent[] events)
        { Snapshot = snapshot; Events = Array.AsReadOnly(events); }
    }

    /// <summary>
    /// 对局的独立展示快照。
    /// 输入：GetSnapshot()；输出：UI、日志与调试面板读取的数据。
    /// 保证：集合只读、可变实体已复制，因此持有旧快照不会因后续命令而变化，也不会被外部改回真实对局。
    /// </summary>
    public sealed class GameSnapshot
    {
        /// <summary>当前完整回合号，从 1 开始。</summary>
        public long Round { get; internal set; }
        /// <summary>当前行动方。</summary>
        public Side ActiveSide { get; internal set; }
        /// <summary>当前阶段。</summary>
        public MatchPhase Phase { get; internal set; }
        /// <summary>对局结果。</summary>
        public MatchOutcome Outcome { get; internal set; }
        /// <summary>玩家当前资源。</summary>
        public long Resources { get; internal set; }
        /// <summary>当前行动方剩余出牌/放牌次数。</summary>
        public int PlaysRemaining { get; internal set; }
        /// <summary>Boss 是否由玩家手动控制。</summary>
        public bool PlayerControlsBoss { get; internal set; }
        /// <summary>补兵间隔，UI 用于展示剩余回合。</summary>
        public int BossRespawnDelay { get; internal set; }
        /// <summary>玩家行动前必须先部署或跳过炸弹；只有手动控制 Boss 的技能准备窗口可能为 true。</summary>
        public bool BossBombPending { get; internal set; }
        /// <summary>Boss 行动前必须先部署或跳过刷怪卡；只保存当次机会，不跨回合累计。</summary>
        public bool BossSpawnerPending { get; internal set; }
        /// <summary>尚未爆炸的不可变地面标记。</summary>
        public IReadOnlyList<BossBombState> BossBombs { get; internal set; } = Array.Empty<BossBombState>();
        /// <summary>玩家手牌。</summary>
        public IReadOnlyList<CardRecord> Hand { get; internal set; }
        /// <summary>玩家抽牌堆。</summary>
        public IReadOnlyList<CardRecord> DrawPile { get; internal set; }
        /// <summary>玩家墓地：成功部署或主动弃置的玩家卡；抽牌堆耗尽时整体洗入抽牌堆。</summary>
        public IReadOnlyList<CardRecord> Graveyard { get; internal set; }
        /// <summary>Boss 独立回收区，不参与玩家洗牌。</summary>
        public IReadOnlyList<CardRecord> BossGraveyard { get; internal set; }
        /// <summary>在场单位副本。</summary>
        public IReadOnlyList<UnitState> Units { get; internal set; }
        /// <summary>双方的家副本。</summary>
        public IReadOnlyList<HomeState> Homes { get; internal set; }
        /// <summary>AI 兵位状态副本。</summary>
        public IReadOnlyList<BossReplacement> BossReplacements { get; internal set; }
        /// <summary>有界历史日志，容量由配置的 logLimit 决定。</summary>
        public IReadOnlyList<GameEvent> Log { get; internal set; }
    }

    /// <summary>
    /// 洗牌使用的最小随机接口。
    /// 为什么需要抽象：固定种子的实现可以让 EditMode 测试复现同一牌序，避免随机失败；
    /// 同时避免依赖 UnityEngine.Random 的全局状态。
    /// </summary>
    public interface IRandomSource
    {
        /// <summary>返回 [0, exclusiveMaximum) 的整数。输入：上界（不含）；输出：随机下标。</summary>
        int Next(int exclusiveMaximum);
    }

    /// <summary>
    /// 标准随机实现，使用 System.Random 并固定种子。
    /// 输入：整数种子；输出：可复现的随机序列。
    /// </summary>
    public sealed class SeededRandom : IRandomSource
    {
        /// <summary>底层随机数发生器，仅本类使用。</summary>
        readonly Random _random;

        /// <summary>构造可复现随机源。输入：种子；输出：随机源实例。</summary>
        public SeededRandom(int seed) { _random = new Random(seed); }

        /// <inheritdoc />
        public int Next(int exclusiveMaximum) => _random.Next(exclusiveMaximum);
    }
}
