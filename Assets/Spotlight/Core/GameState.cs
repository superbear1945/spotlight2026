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

        /// <summary>曼哈顿距离，是移动与攻击唯一使用的度量方式（本项目没有斜向）。</summary>
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
        /// <summary>本回合是否已经行动（移动、攻击或互换其一）。</summary>
        public bool Acted { get; internal set; }
        /// <summary>本回合维持费是否已支付；未支付时不能行动。</summary>
        public bool UpkeepPaid { get; internal set; }
        /// <summary>部署序号，用于资源支付与 AI 的行动顺序。</summary>
        public long DeploymentOrder { get; internal set; }
        /// <summary>AI 兵位 ID；非 AI 兵位单位该值为 null，因此不会触发补兵计时。</summary>
        public string BossSlotId { get; internal set; }

        /// <summary>是否本回合仍可行动：未行动且维持费已付。</summary>
        public bool CanAct => !Acted && UpkeepPaid;

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

    /// <summary>回合阶段：正常行动、超限弃牌、已结束。</summary>
    public enum MatchPhase { Action, Discard, Finished }

    /// <summary>对局结果。</summary>
    public enum MatchOutcome { None, PlayerWins, BossWins, Draw }

    /// <summary>命令种类。玩家与 AI 共用同一入口，区别只在 ExecuteCommand 的 fromAi 标记。</summary>
    public enum CommandKind { Deploy, BossDeploy, Move, MoveHome, Attack, Swap, Discard, EndTurn }

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
    public enum SelectionKind { None, Hand, Unit, Home, BossDeployment }

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
        /// <summary>可互换的己方单位所在格。</summary>
        public IReadOnlyList<Cell> Swap { get; internal set; } = Array.Empty<Cell>();
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

        /// <summary>构造一条事件。输入：类型、文本与主体 ID；输出：不可变事件。</summary>
        public GameEvent(string kind, string message, long subjectId = 0) { Kind = kind; Message = message; SubjectId = subjectId; }
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

        /// <summary>内部构造：只允许规则内核产生结果，外部不能伪造成功。输入：状态与事件数组；输出：不可变结果。</summary>
        internal CommandResult(bool success, string error, GameEvent[] events) { Success = success; Error = error; Events = Array.AsReadOnly(events); }
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
