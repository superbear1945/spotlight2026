// 对局规则内核。
// 边界：本文件不引用 UnityEngine，不依赖场景、帧率、动画或 Inspector。
// 所有玩法修改都必须经 Execute 进入；展示层只读取快照，避免画面与规则各自持有一套状态。
using System;
using System.Collections.Generic;
using System.Linq;

namespace Spotlight
{
    /// <summary>
    /// 一局游戏的唯一状态所有者：校验命令、执行结算、驱动回合、同步 Boss AI。
    /// 输入：已完成校验的 GameConfiguration、随机源与日志容量。
    /// 输出：CommandResult（成功状态 + 有序事件）与 GameSnapshot（只读副本）。
    /// 为什么需要：玩家与 AI 必须共用同一规则入口，动画不得决定结算结果；
    /// 配置在构造时确定，调试“应用并重开”必须创建新对局，不能在旧局中原地修改配置。
    /// 身份约定：CardRecord 是可循环的来源卡；UnitState 是一次部署产生的战斗实体。
    /// 单位只保存来源卡的值副本（SourceCardId/TypeId/Owner），不引用 CardRecord；
    /// 玩家卡在成功部署时进入墓地，单位阵亡或被覆盖升级不再重复回收，牌堆总数恒定。
    /// </summary>
    public sealed class GameSession
    {
        /// <summary>本局不可变配置索引。</summary>
        readonly GameConfiguration _config;
        /// <summary>洗牌与 Boss 技能的随机源，固定种子可复现牌序和随机选格。</summary>
        readonly IRandomSource _random;
        /// <summary>玩家抽牌堆。</summary>
        readonly List<CardRecord> _draw = new List<CardRecord>();
        /// <summary>玩家手牌。</summary>
        readonly List<CardRecord> _hand = new List<CardRecord>();
        /// <summary>玩家墓地：成功部署或主动弃置的玩家卡；抽牌堆耗尽时整体洗入抽牌堆。</summary>
        readonly List<CardRecord> _grave = new List<CardRecord>();
        /// <summary>Boss 独立回收区，不参与玩家洗牌。</summary>
        readonly List<CardRecord> _bossGrave = new List<CardRecord>();
        /// <summary>在场单位。</summary>
        readonly List<UnitState> _units = new List<UnitState>();
        /// <summary>双方的家。</summary>
        readonly List<HomeState> _homes = new List<HomeState>();
        /// <summary>AI 兵位状态。</summary>
        readonly List<BossReplacement> _replacements = new List<BossReplacement>();
        /// <summary>尚未到期的地面炸弹，不参与单位占位与卡牌流转。</summary>
        readonly List<BossBombState> _bossBombs = new List<BossBombState>();
        /// <summary>玩家行动前尚待 Boss 处理的独立炸弹机会。</summary>
        bool _bossBombPending;
        /// <summary>Boss 回合开始统计所得的刷怪卡部署机会；处理前阻止普通操作。</summary>
        bool _bossSpawnerPending;
        /// <summary>有界历史日志，供 UI 与调试面板读取。</summary>
        readonly List<GameEvent> _log = new List<GameEvent>();
        /// <summary>当前一次外部命令连带产生的全部事件（含自动 Boss 回合）；_log 则是有界历史。</summary>
        readonly List<GameEvent> _pendingEvents = new List<GameEvent>();
        /// <summary>当前命令的阶段快照，保留同步 AI 结算中的展示时点。</summary>
        readonly List<PresentationFrame> _presentationFrames = new List<PresentationFrame>();
        /// <summary>已分配给展示阶段的事件数量，保证每个事件只播放一次。</summary>
        int _presentedEventCount;
        /// <summary>历史日志容量。</summary>
        readonly int _logLimit;

        /// <summary>卡牌记录 ID 计数器。</summary>
        long _nextCard;
        /// <summary>单位 ID 计数器。</summary>
        long _nextUnit;
        /// <summary>部署序号计数器。</summary>
        long _order;
        /// <summary>当前完整回合号。</summary>
        long _round = 1;
        /// <summary>玩家当前资源。</summary>
        long _resources;
        /// <summary>当前行动方。</summary>
        Side _activeSide;
        /// <summary>当前阶段。</summary>
        MatchPhase _phase;
        /// <summary>对局结果。</summary>
        MatchOutcome _outcome;
        /// <summary>当前行动方剩余出牌/放牌次数。</summary>
        int _plays;
        /// <summary>已完成行动回合计数；双方各完成一次后回合号递增。</summary>
        int _turnsCompleted;
        /// <summary>调试模态窗口的规则层输入闸门，防止 UI 遮罩被穿透。</summary>
        bool _inputBlocked;

        // 内部行号向上增加。并列路径按上、右、下、左展开；本项目没有斜移，也没有反击。
        /// <summary>四向移动/邻接顺序：上、右、下、左，用于 BFS 并列路径的确定性选择。</summary>
        static readonly Cell[] _directions = { new Cell(1, 0), new Cell(0, 1), new Cell(-1, 0), new Cell(0, -1) };

        /// <summary>本局规则便捷访问。</summary>
        MatchConfig Rules => _config.Rules;

        /// <summary>对外暴露的不可变配置索引，供 UI 查询卡种名称与属性。</summary>
        public GameConfiguration Configuration => _config;

        /// <summary>开局时的展示阶段；Boss 先手的自动行动也在构造内完成，因此单独保留供界面首次播放。</summary>
        public IReadOnlyList<PresentationFrame> InitialPresentationFrames { get; }

        /// <summary>
        /// 建立家、来源卡与兵位，然后按先手顺序进入首个行动回合。
        /// 输入：configuration 为已校验配置；randomSource 用于洗牌和技能选格；logLimit 为历史日志容量。
        /// 输出：可直接接收命令的对局实例。
        /// 特殊情况：Boss 先手且为 AI 控制时，构造结束前就完成首次部署与行动，与玩法文档一致。
        /// </summary>
        public GameSession(GameConfiguration configuration, IRandomSource randomSource, int logLimit)
        {
            _config = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _random = randomSource ?? throw new ArgumentNullException(nameof(randomSource));
            if (logLimit < 1) throw new ArgumentOutOfRangeException(nameof(logLimit));
            _logLimit = logLimit;
            _resources = Rules.InitialResources;

            // 家的位置由“边缘居中、偶数行偏下”规则推导：行号 floor(rows/2)，列号取己方边缘。
            foreach (var side in new[] { Side.Player, Side.Boss })
                _homes.Add(new HomeState { Owner = side, Hp = side == Side.Player ? _config.PlayerHomeHp : _config.BossHomeHp, Position = new Cell(Rules.Rows / 2, IsLeft(side) ? 0 : Rules.Columns - 1) });

            foreach (var e in _config.Deck)
                for (var i = 0; i < e.Count; i++) _draw.Add(NewCard(e.TypeId, Side.Player));
            Shuffle(_draw);

            // 行比例换算使用 Floor(x + .5) 实现非负数四舍五入，不使用银行家舍入。
            foreach (var s in _config.BossSlots)
                _replacements.Add(new BossReplacement { SlotId = s.Id, PreferredCell = new Cell((int)Math.Floor((Rules.Rows - 1) * s.RowFraction + .5), IsLeft(Side.Boss) ? s.EdgeOffset : Rules.Columns - 1 - s.EdgeOffset), DueRound = _round });

            BeginTurn(Rules.FirstSide);
            if (_activeSide == Side.Boss && !Rules.PlayerControlsBoss) RunBoss();
            if (_presentationFrames.Count > 0) CapturePresentationFrame();
            InitialPresentationFrames = Array.AsReadOnly(_presentationFrames.ToArray());
        }

        /// <summary>
        /// 调试模态窗口的规则层输入闸门。
        /// 输入：是否屏蔽输入；输出：无。
        /// 为什么需要：仅靠 UI 遮罩无法防止点击穿透，规则层必须同时拒绝命令与高亮。
        /// </summary>
        public void SetInputBlocked(bool blocked) { _inputBlocked = blocked; }

        /// <summary>
        /// 生成当前状态的展示快照。
        /// 输入：无；输出：状态副本与只读集合。
        /// 保证：旧快照不随后续命令变化，读取者也无法修改真实对局。
        /// </summary>
        public GameSnapshot GetSnapshot() => new GameSnapshot
        {
            Round = _round,
            ActiveSide = _activeSide,
            Phase = _phase,
            Outcome = _outcome,
            Resources = _resources,
            PlaysRemaining = _plays,
            PlayerControlsBoss = Rules.PlayerControlsBoss,
            BossRespawnDelay = Rules.BossRespawnDelay,
            BossBombPending = _bossBombPending,
            BossSpawnerPending = _bossSpawnerPending,
            BossBombs = Array.AsReadOnly(_bossBombs.ToArray()),
            Hand = Array.AsReadOnly(_hand.ToArray()),
            DrawPile = Array.AsReadOnly(_draw.ToArray()),
            Graveyard = Array.AsReadOnly(_grave.ToArray()),
            BossGraveyard = Array.AsReadOnly(_bossGrave.ToArray()),
            Units = Array.AsReadOnly(_units.Select(u => u.Copy()).ToArray()),
            Homes = Array.AsReadOnly(_homes.Select(h => h.Copy()).ToArray()),
            BossReplacements = Array.AsReadOnly(_replacements.Select(r => r.Copy(_round)).ToArray()),
            Log = Array.AsReadOnly(_log.ToArray())
        };

        /// <summary>
        /// 外部唯一修改入口。
        /// 输入：玩家或手动 Boss 的命令；输出：包含成功状态、失败原因与本次全部有序事件的结果。
        /// 为什么失败不抛异常：失败是常规流程（高亮过期、点击非法目标），需要把原因展示给玩家；
        /// 同时失败不会修改棋盘，因为各分支先完成全部校验再落状态。
        /// </summary>
        public CommandResult Execute(GameCommand command)
        {
            _pendingEvents.Clear();
            _presentationFrames.Clear();
            _presentedEventCount = 0;
            var error = command == null ? "命令为空" : ExecuteCommand(command, false);
            if (_presentationFrames.Count > 0) CapturePresentationFrame();
            return new CommandResult(error == null, error, _pendingEvents.ToArray(), _presentationFrames.ToArray());
        }

        /// <summary>
        /// 保存一个展示阶段。输入：当前状态和未分配的事件；输出：独立快照及有序事件分段。
        /// 在玩家行动前的炸弹处理、自动 Boss 普通行动边界和最终返回处调用，不改变回合推进；无新事件时不重复保存。
        /// </summary>
        void CapturePresentationFrame()
        {
            if (_presentedEventCount == _pendingEvents.Count) return;
            _presentationFrames.Add(new PresentationFrame(GetSnapshot(), _pendingEvents.Skip(_presentedEventCount).ToArray()));
            _presentedEventCount = _pendingEvents.Count;
        }

        /// <summary>
        /// 分派并结算命令。
        /// 输入：c 为命令，fromAi 标记调用者是否为内置 AI；输出：null 表示成功，否则为失败原因。
        /// 关键区别：fromAi 只豁免“手动 Boss 限制”，不豁免行动、占位、射程、维持费等任何玩法规则，
        /// 因此 AI 走的是与玩家完全相同的结算代码。
        /// </summary>
        string ExecuteCommand(GameCommand c, bool fromAi)
        {
            if (_phase == MatchPhase.Finished) return "对局已结束";
            if (!fromAi && _inputBlocked) return "配置面板打开或回合展示期间不能操作棋盘";
            var bombCommand = c.Kind == CommandKind.DeployBossBomb || c.Kind == CommandKind.SkipBossBomb;
            // 炸弹属于 Boss，但处理窗口在玩家行动前；仅这两种技能命令允许跨行动方提交。
            if (bombCommand)
            {
                if (c.Actor != Side.Boss || _activeSide != Side.Player || !_bossBombPending)
                    return "当前没有炸弹部署机会";
                if (!fromAi && !Rules.PlayerControlsBoss) return "Boss 由 AI 控制";
            }
            else if (c.Actor != _activeSide) return "不是该阵营的回合";
            if (!fromAi && _activeSide == Side.Boss && !Rules.PlayerControlsBoss) return "Boss 由 AI 控制";
            if (_phase == MatchPhase.Discard && c.Kind != CommandKind.Discard) return "请先弃牌至手牌上限";
            if (_bossBombPending && c.Kind != CommandKind.DeployBossBomb && c.Kind != CommandKind.SkipBossBomb) return "请先布置或跳过炸弹";
            var spawnerCommand = c.Kind == CommandKind.DeployBossSpawner || c.Kind == CommandKind.SkipBossSpawner;
            if (spawnerCommand && (c.Actor != Side.Boss || _activeSide != Side.Boss || !_bossSpawnerPending)) return "当前没有刷怪卡部署机会";
            if (_bossSpawnerPending && !spawnerCommand) return "请先部署或跳过刷怪卡";
            switch (c.Kind)
            {
                case CommandKind.DeployBossSpawner:
                    if (!SpawnerCells().Contains(c.Target)) return "刷怪卡必须部署在指定列范围内的空格，且场上最多一张";
                    var spawner = Place(NewCard(Rules.BossSpawnerCardType, Side.Boss), c.Target, false, null);
                    spawner.IsBossSpawner = true;
                    _bossSpawnerPending = false;
                    Emit("spawner-deploy", "刷怪卡已部署，从下个 Boss 回合开始每回合生成一架战机", spawner.Id);
                    CapturePresentationFrame();
                    break;
                case CommandKind.SkipBossSpawner:
                    _bossSpawnerPending = false;
                    Emit("spawner-skip", "Boss 跳过当次刷怪卡部署，机会不累计");
                    break;
                case CommandKind.DeployBossBomb:
                    if (!Inside(c.Target) || c.Target.Col != BossBombColumn()) return "炸弹必须部署在指定列的棋盘格内";
                    DeployBossBomb(c.Target);
                    CapturePresentationFrame();
                    break;
                case CommandKind.SkipBossBomb:
                    _bossBombPending = false;
                    Emit("bomb-skip", "Boss 跳过本回合炸弹部署");
                    break;
                case CommandKind.Deploy:
                    if (_activeSide != Side.Player) return "玩家手牌仅用于玩家回合";
                    var card = _hand.FirstOrDefault(x => x.Id == c.SubjectId);
                    if (card == null) return "手牌不存在";
                    if (_plays <= 0) return "本回合出牌次数已用完";
                    var deployType = _config.Cards[card.TypeId];
                    var deployError = DeploymentError(deployType, card.Owner, c.Target);
                    if (deployError != null) return deployError;
                    // 部署消耗：先校验再扣减，任何失败都不改变资源（失败原子性）。
                    // 只有玩家手牌路径检查它；Boss 没有资源系统，BossDeploy 不受影响。
                    if (deployType.DeployCost > _resources) return $"资源不足：部署 {deployType.Name} 需要 {deployType.DeployCost}，当前 {_resources}";
                    _resources -= deployType.DeployCost;
                    _hand.Remove(card);
                    Place(card, c.Target, false, null);
                    _plays--;
                    break;
                case CommandKind.BossDeploy:
                    if (_activeSide != Side.Boss || !Rules.PlayerControlsBoss) return "仅手动 Boss 可放牌";
                    if (_plays <= 0) return "本回合放牌次数已用完";
                    var bossType = _config.Cards[Rules.BossCardType];
                    var bossError = DeploymentError(bossType, Side.Boss, c.Target);
                    if (bossError != null) return bossError;
                    Place(NewCard(bossType.Id, Side.Boss), c.Target, false, null);
                    _plays--;
                    break;
                case CommandKind.Move:
                    var mover = _units.FirstOrDefault(u => u.Id == c.SubjectId);
                    if (!Available(mover)) return "该单位本回合不能行动";
                    var path = Paths(mover.Position);
                    if (c.Target == mover.Position || !path.TryGetValue(c.Target, out var movePath) || movePath.Count > mover.MoveDistance) return "不在合法移动范围";
                    // 空格限制只检查起点与原布局、落点与最终布局；路径中间格不检查。
                    var moveError = MovementError(mover, c.Target);
                    if (moveError != null) return moveError;
                    var origin = mover.Position;
                    mover.Position = c.Target;
                    mover.Acted = true;
                    Emit("move", $"{Type(mover).Name} 从 {origin} 移动至 {c.Target}", mover.Id);
                    // 移动会改变“谁在谁的范围内”，因此重算一次入场标记。
                    RefreshMarking();
                    break;
                case CommandKind.MoveHome:
                    var home = _homes.FirstOrDefault(h => h.Id == c.SubjectId && h.Owner == _activeSide);
                    if (home == null || home.Acted || home.Position.Distance(c.Target) != 1 || !InHomeRegion(_activeSide, c.Target) || Occupied(c.Target)) return "家只能在己方区域移动一格至空位";
                    home.Position = c.Target;
                    home.Acted = true;
                    Emit("move", $"{SideName(_activeSide)}的家移动至 {c.Target}", home.Id);
                    break;
                case CommandKind.Attack:
                    var attacker = _units.FirstOrDefault(u => u.Id == c.SubjectId);
                    if (!Available(attacker)) return "该单位本回合不能攻击";
                    var target = Targets(attacker.Owner).FirstOrDefault(t => t.Id == c.TargetId);
                    // “攻击友方即治疗”的卡可以把己方单位当作目标：不含自己，家不是单位因此不可治疗。
                    var allyTarget = target == null && Type(attacker).AttacksAlliesAsHeal
                        ? _units.FirstOrDefault(u => u.Id == c.TargetId && u.Owner == attacker.Owner && u.Id != attacker.Id)
                        : null;
                    if (target == null && allyTarget == null) return "目标不在合法攻击范围";
                    var targetPosition = target != null ? target.Position : allyTarget.Position;
                    if (!CanAttack(attacker, attacker.Position, targetPosition)) return "目标不在合法攻击范围";
                    // 治疗分支：数值 = 当前生命 + 攻击力，受生命上限限流；满血也允许（白打但仍消耗行动）。
                    // 治疗只改生命，不触发阵亡判定，因此不会因为“攻击”而误杀友方。
                    if (allyTarget != null)
                    {
                        attacker.Acted = true;
                        var healed = Math.Min(allyTarget.MaxHp, allyTarget.Hp + attacker.Attack);
                        var beforeHp = allyTarget.Hp;
                        allyTarget.Hp = healed;
                        Emit("heal", $"{Type(attacker).Name} 治疗 {Type(allyTarget).Name}，生命 {beforeHp} → {healed}", allyTarget.Id);
                        break;
                    }
                    // 只扣目标生命：不存在反击、连锁或动画回调产生的补充伤害。
                    // 伤害读取攻击者的当前攻击力（部署时从配置初始化、可被一次性减攻降低），不重读卡种配置。
                    // 统一伤害入口会把“基础伤害”与“已有伤害标记的追加伤害”一起结算，并只触发一次死亡处理。
                    attacker.Acted = true;
                    var damage = attacker.Attack;
                    DamageResult applied;
                    if (target.Unit != null)
                    {
                        applied = ResolveDirectDamage(target.Unit, damage, new DamageContext(attacker.Owner, attacker.Id, target.Unit.Id, DamageKind.DirectAttack));
                    }
                    else
                    {
                        applied = ResolveHomeDamage(target.Home, damage, new DamageContext(attacker.Owner, attacker.Id, target.Home.Id, DamageKind.DirectAttack));
                    }
                    Emit("attack", $"{Type(attacker).Name} 攻击 {target.Name}，造成 {applied.TotalDamage} 伤害", target.Id, damage: applied);
                    // 伤害标记：只有“直接攻击战斗单位且目标存活”才挂层。
                    // 家不挂层；治疗分支已在前面 break；本次追加伤害不递归不挂层。
                    var markConfig = Type(attacker).DamageMark;
                    if (markConfig != null && target.Unit != null && !applied.Killed)
                    {
                        target.Unit.AddDamageMark(markConfig.DamagePerStack);
                        Emit("damage-mark", $"{Type(attacker).Name} 使 {target.Name} 获得 1 层伤害标记（当前 {target.Unit.DamageMarkLayers} 层，每层追加 {markConfig.DamagePerStack}）", target.Unit.Id);
                    }
                    // 击杀恢复行动：只认本次攻击（包括标记追加）造成的击杀，而不是“本次命令发生过死亡”；
                    // 炸弹与其它单位的击杀不经过本分支，因此不会误恢复。
                    if (applied.Killed && target.Unit != null && Type(attacker).KillAction != null && _units.Contains(attacker))
                    {
                        attacker.Acted = false;
                        Emit("action-restored", $"{Type(attacker).Name} 击杀 {target.Name}，恢复一次行动（仅本回合有效）", attacker.Id);
                    }
                    CheckVictory();
                    break;
                case CommandKind.Swap:
                    var a = _units.FirstOrDefault(u => u.Id == c.SubjectId);
                    var b = _units.FirstOrDefault(u => u.Id == c.TargetId);
                    if (!Rules.AdjacentSwap || !Swappable(a) || !Swappable(b) || a == b || a.Position.Distance(b.Position) != 1) return "双方必须为相邻且可行动的己方非资源单位";
                    // 互换需要检查“交换前”与“交换后”两份布局，且双方中所有具有该能力的单位都要满足。
                    var swapError = SwapError(a, b);
                    if (swapError != null) return swapError;
                    // 互换独立于移动距离：零移动的非资源单位也能参加，双方同时消耗行动机会。
                    var old = a.Position;
                    a.Position = b.Position;
                    b.Position = old;
                    a.Acted = b.Acted = true;
                    Emit("swap", $"{Type(a).Name} {old} 与 {Type(b).Name} {a.Position} 交换位置", a.Id);
                    // 互换同样改变相对位置，重算入场标记。
                    RefreshMarking();
                    break;
                case CommandKind.Withdraw:
                    var leaving = _units.FirstOrDefault(u => u.Id == c.SubjectId);
                    // 离场只对玩家自己的在场单位开放：家不是单位（这里找不到），Boss 单位一律不可离场。
                    // 刻意不校验 Available：离场不消耗本回合行动，移动或攻击之后仍可离场，召唤失调与维持费不足也不拦。
                    // 来源卡不在这里回收——它在部署成功时就已经进入墓地，因此牌堆守恒不受离场影响。
                    if (leaving == null || leaving.Owner != Side.Player) return "只能让自己的在场单位离场";
                    Remove(leaving, Removal.Withdraw);
                    break;
                case CommandKind.Discard:
                    if (_activeSide != Side.Player || _phase != MatchPhase.Discard) return "当前不是弃牌阶段";
                    var discarded = _hand.FirstOrDefault(x => x.Id == c.SubjectId);
                    if (discarded == null) return "手牌不存在";
                    _hand.Remove(discarded);
                    _grave.Add(discarded);
                    Emit("discard", $"弃置 {_config.Cards[discarded.TypeId].Name}");
                    if (_hand.Count <= Rules.HandLimit) AdvanceTurn();
                    break;
                case CommandKind.EndTurn:
                    if (_activeSide == Side.Player && _hand.Count > Rules.HandLimit) { _phase = MatchPhase.Discard; Emit("phase", $"请弃置 {_hand.Count - Rules.HandLimit} 张牌"); }
                    else if (_activeSide == Side.Boss && Rules.PlayerControlsBoss && _plays > 0 && !c.SkipDeployment) return "尚有放牌次数，请放牌或选择跳过放牌并结束";
                    else AdvanceTurn();
                    break;
                default: return "未知命令";
            }
            return null;
        }

        /// <summary>
        /// 计算当前选中对象的合法目标，供棋盘高亮使用。
        /// 输入：界面选择上下文；输出：四类可选格列表。
        /// 为什么不让 UI 自行推导：合法性只有一份实现，避免界面与结算分叉；
        /// 同时高亮只用于提示，命令提交时仍会复验，过期高亮无法绕过限制。
        /// </summary>
        public LegalActions GetLegalActions(Selection selection)
        {
            var actions = new LegalActions();
            if (_phase != MatchPhase.Action || _inputBlocked || (_activeSide == Side.Boss && !Rules.PlayerControlsBoss)) return actions;
            if (_bossSpawnerPending)
            {
                if (selection.Kind == SelectionKind.BossSpawnerDeployment) actions.Deploy = SpawnerCells().ToArray();
                return actions;
            }
            if (selection.Kind == SelectionKind.BossSpawnerDeployment) return actions;
            if (_bossBombPending)
            {
                if (selection.Kind == SelectionKind.BossBombDeployment)
                    actions.Deploy = Enumerable.Range(0, Rules.Rows).Select(row => new Cell(row, BossBombColumn())).ToArray();
                return actions;
            }
            if (selection.Kind == SelectionKind.BossBombDeployment) return actions;
            if (selection.Kind == SelectionKind.Hand && _activeSide == Side.Player && _plays > 0)
            {
                var card = _hand.FirstOrDefault(c => c.Id == selection.Id);
                if (card != null)
                {
                    var handType = _config.Cards[card.TypeId];
                    // 资源不足时不下发任何部署目标：高亮必须与“现在真的能部署”一致，
                    // 否则玩家会看到一片可放格却次次被拒。合法性只由规则层给出，UI 不自行推导。
                    if (handType.DeployCost <= _resources)
                        actions.Deploy = Cells().Where(p => DeploymentError(handType, _activeSide, p) == null).ToArray();
                }
            }
            else if ((selection.Kind == SelectionKind.BossDeployment || selection.Kind == SelectionKind.None) && _activeSide == Side.Boss && _plays > 0)
                actions.Deploy = Cells().Where(p => DeploymentError(_config.Cards[Rules.BossCardType], Side.Boss, p) == null).ToArray();
            else if (selection.Kind == SelectionKind.Home)
            {
                var h = _homes.FirstOrDefault(x => x.Id == selection.Id && x.Owner == _activeSide);
                if (h != null && !h.Acted) actions.Move = Neighbors(h.Position).Where(p => InHomeRegion(_activeSide, p) && !Occupied(p)).ToArray();
            }
            else if (selection.Kind == SelectionKind.Unit)
            {
                var u = _units.FirstOrDefault(x => x.Id == selection.Id);
                // 离场不是“行动”，因此不受 Available 限制：已行动、召唤失调或维持费不足的单位同样可以离场；
                // 但只在玩家自己的回合，而且只能是玩家自己的单位（Boss 单位与家都不可离场）。
                actions.CanWithdraw = _activeSide == Side.Player && u != null && u.Owner == Side.Player;
                if (Available(u))
                {
                    actions.Move = Paths(u.Position).Where(p => p.Value.Count > 0 && p.Value.Count <= u.MoveDistance && MovementError(u, p.Key) == null).Select(p => p.Key).ToArray();
                    var attackCells = Targets(_activeSide).Where(t => CanAttack(u, u.Position, t.Position)).Select(t => t.Position).ToList();
                    // 具备“攻击友方即治疗”的单位，合法目标额外包含己方单位（不含自己）。
                    // 这些格同时进入 Attack（点击链路与敌方目标一致）与 Heal（视图据此染成治疗色）。
                    if (Type(u).AttacksAlliesAsHeal)
                    {
                        var healTargets = _units
                            .Where(v => v.Id != u.Id && v.Owner == u.Owner && CanAttack(u, u.Position, v.Position))
                            .Select(v => v.Position)
                            .ToArray();
                        actions.Heal = healTargets;
                        attackCells.AddRange(healTargets);
                    }
                    actions.Attack = attackCells.ToArray();
                    // 资源卡既不发起互换也不作为互换目标，因此它选中时没有互换高亮，
                    // 点击相邻己方卡牌会落到“切换选中对象”这条分支上（与关闭 adjacentSwap 时的行为一致）。
                    if (Rules.AdjacentSwap && Swappable(u)) actions.Swap = _units.Where(v => v != u && Swappable(v) && u.Position.Distance(v.Position) == 1 && SwapError(u, v) == null).Select(v => v.Position).ToArray();
                }
            }
            return actions;
        }

        /// <summary>
        /// 校验一次部署。
        /// 输入：卡种、部署方与目标格；输出：null 表示合法，否则为玩家可读的失败原因。
        /// 覆盖范围：棋盘边界、占位、覆盖升级目标、己方区域限制、星门四邻部署扩展、同卡种四邻限制。
        /// </summary>
        string DeploymentError(CardArchetype type, Side owner, Cell p)
        {
            if (!Inside(p)) return "棋盘外不能部署";
            var existing = _units.FirstOrDefault(u => u.Position == p);
            if (type.RequiresUpgrade)
            {
                if (existing == null || existing.Owner != owner || existing.TypeId != type.UpgradeFrom) return "需要覆盖指定的己方资源单位";
            }
            else if (Occupied(p)) return "格子已被占用";
            if (type.DeployHomeOnly && !InHomeRegion(owner, p)) return "必须部署在己方区域";
            if (type.Category == CardCategory.Attack && !InHomeRegion(owner, p) && !_units.Any(u => u.Owner == owner && Type(u).Teleport?.PermitsAttackDeploy == true && u.Position.Distance(p) == 1)) return "攻击卡需要己方区域或己方星门四邻";
            if (type.AvoidAdjacentSameType && _units.Any(u => u.TypeId == type.Id && u.Position.Distance(p) == 1)) return "不能与同卡种单位四邻部署";
            // 四邻空格限制：部署按“新单位已经占用目标格”的最终布局计算，
            // 棋盘外四个方向直接计为空，斜角不计，炸弹不占格。
            var clearance = ClearanceError(type, p, cell => cell == p || Occupied(cell));
            if (clearance != null) return clearance;
            // 邻域部署要求：统计目标格四邻（上下左右）中指定分类的在场单位数量。
            // 不区分阵营（敌方同类单位同样计数），家不是单位因此不参与统计。
            if (type.DeployRequireAdjacentCount > 0)
            {
                var required = type.DeployRequireAdjacentCount;
                var category = CategoryOf(type.DeployRequireAdjacentCategory);
                var found = Neighbors(p).Count(neighbor => _units.Any(u => u.Position == neighbor && Type(u).Category == category));
                if (found < required) return $"四邻需要至少 {required} 个{CategoryName(category)}单位（当前 {found}）";
            }
            return null;
        }

        /// <summary>
        /// 把 Cards 表的分类文本解析成分类枚举。
        /// 输入：resource / attack / special 文本；输出：对应枚举。
        /// 为什么需要：邻域部署要求以配置文本形式给出；非法文本已被 ConfigValidation 拦截，
        /// 这里按“其它=特殊卡”兜底，保证即使有人绕过校验也不会抛异常中断对局。
        /// </summary>
        static CardCategory CategoryOf(string text) =>
            text == "resource" ? CardCategory.Resource : text == "attack" ? CardCategory.Attack : CardCategory.Special;

        /// <summary>
        /// 分类中文名，仅用于玩家可读的失败原因与日志。
        /// 输入：分类枚举；输出：资源卡 / 攻击卡 / 特殊卡。
        /// </summary>
        static string CategoryName(CardCategory category) =>
            category == CardCategory.Resource ? "资源卡" : category == CardCategory.Attack ? "攻击卡" : "特殊卡";

        /// <summary>
        /// 在指定格创建新单位。
        /// 输入：来源卡、目标格、firstSpawn（是否为 AI 兵位的首次成功部署）、兵位 ID（可空）。
        /// 输出：已初始化场上属性、并已完成一次性减攻结算的新 UnitState。
        /// 规则：目标格已有单位时先按“被覆盖升级”移除；新单位总是按配置满生命，不继承旧单位任何属性或能力状态；
        /// 玩家卡在本次成功部署时进入墓地，成为可再次抽到的循环卡；
        /// 场上单位只保存值副本，与墓地中这张卡互不影响。
        /// firstSpawn 是唯一豁免召唤失调的入口：死亡补兵必须遵守“刚上场不能行动”。
        /// 所有创建路径（玩家部署、手动 Boss、AI 首次与补兵、刷怪卡战机）都只经过这里一次，
        /// 因此一次性减攻只会在本函数内结算一次，调用方不可重复处理。
        /// </summary>
        UnitState Place(CardRecord card, Cell p, bool firstSpawn, string slotId)
        {
            var covered = _units.FirstOrDefault(u => u.Position == p);
            if (covered != null) Remove(covered, Removal.Upgrade);
            var type = _config.Cards[card.TypeId];
            var combat = type.Combat;
            var penalty = type.DeploymentPenalty;
            var growth = type.DeathGrowth;
            var u = new UnitState
            {
                Id = ++_nextUnit,
                SourceCardId = card.Id,
                TypeId = card.TypeId,
                Owner = card.Owner,
                Position = p,
                Hp = combat != null ? combat.Hp : 1,
                MaxHp = combat != null ? combat.Hp : 1,
                Attack = combat != null ? combat.Attack : 0,
                Range = combat != null ? combat.Range : 0,
                MoveDistance = combat != null ? combat.MoveDistance : 0,
                DeploymentPenaltyPending = penalty != null,
                DeploymentAttackReduction = penalty != null ? penalty.AttackReduction : 0,
                DeathGrowthPerKill = growth != null ? growth.PerKill : 0,
                DeathGrowthLimit = growth != null ? growth.Limit : 0,
                Acted = !firstSpawn && Rules.SummoningSickness,
                UpkeepPaid = true,
                DeploymentOrder = ++_order,
                BossSlotId = slotId
            };
            _units.Add(u);
            u.DeployedRound = _round;
            // 玩家卡：成功部署即进入墓地，成为可再次抽到的循环卡；
            // 场上单位只保存值副本，与墓地中的这张卡互不影响。
            if (card.Owner == Side.Player) _grave.Add(card);
            Emit("deploy", $"{SideName(card.Owner)}部署 {type.Name} 至 {p}", u.Id);
            // 即时卡：在场时间视为 0。必须跳过一次性减攻与入场标记，
            // 否则一张“打完就走”的卡可以白嫖减攻/标记；结算完效果后立即以 Consumed 原因离场，
            // 这样补兵、死亡加攻、标记奖励（都只认 Death）天然不会被触发。
            if (type.Instant != null)
            {
                _resources += type.Instant.GainResource;
                Emit("instant", $"{type.Name} 立即结算：获得 {type.Instant.GainResource} 资源", u.Id);
                Remove(u, Removal.Consumed);
                return u;
            }
            ApplyDeploymentPenalty(u);
            // 新单位可能是标记来源（自己入场时把范围内单位标记），也可能是被标记对象（落入已有来源的范围）。
            RefreshMarking();
            return u;
        }

        /// <summary>
        /// 重新结算入场标记（星棹收集系统）。
        /// 输入：无（读取当前棋盘）；输出：无。
        /// 规则：具有该能力的在场单位会把半径内（切比雪夫距离，半径 1 即 3×3 八邻）的其它单位标记为“自己阵营”；
        /// 只标记尚未被标记的单位——多来源重叠不叠加、先到先得；标记永久保留，来源离场或目标离开范围都不撤销。
        /// 为什么每次部署/移动/互换后整体重扫：标记只在“进入范围那一刻”生效，重扫等价于逐次判定，
        /// 又不必维护“上次范围集合”，实现更难出错；棋盘规模（默认 5×10）下开销可忽略。
        /// </summary>
        void RefreshMarking()
        {
            foreach (var source in _units)
            {
                var ability = Type(source).MarkOnEnter;
                if (ability == null) continue;
                foreach (var target in _units)
                {
                    if (target.Id == source.Id || target.MarkedBy != null) continue;
                    var distance = Math.Max(Math.Abs(target.Position.Row - source.Position.Row), Math.Abs(target.Position.Col - source.Position.Col));
                    if (distance > ability.Radius) continue;
                    target.MarkedBy = source.Owner;
                    target.MarkReward = ability.Reward;
                    Emit("mark", $"{Type(source).Name} 标记 {Type(target).Name}（阵亡时给{SideName(source.Owner)} {ability.Reward} 资源）", target.Id);
                }
            }
        }

        /// <summary>
        /// 结算新上场攻击类单位的一次性部署减攻。
        /// 输入：刚刚入场（已加入 _units、已有 DeploymentOrder）的新单位；输出：无。
        /// 规则：只有目标为攻击类卡时寻找来源；只考虑同阵营、仍然在场、更早入场且效果未消耗的来源，
        /// 按 DeploymentOrder 升序取最早的一个；每次入场最多消耗一个来源，其余来源等后续目标。
        /// 减攻量取来源单位的配置副本，结果下限为 0；原攻击为 0 时同样消耗一次效果，不保留“剩余减攻”。
        /// 为什么只在 Place 中调用：玩家、手动 Boss、AI 首次/补兵与刷怪卡生成共用同一创建流程，
        /// 放在调用方会让不同入口各自实现一遍，容易重复触发或漏触发。
        /// </summary>
        void ApplyDeploymentPenalty(UnitState target)
        {
            if (Type(target).Category != CardCategory.Attack) return;
            var source = _units
                .Where(u => u.Owner == target.Owner && u.Id != target.Id && u.DeploymentOrder < target.DeploymentOrder && u.DeploymentPenaltyPending)
                .OrderBy(u => u.DeploymentOrder)
                .FirstOrDefault();
            if (source == null) return;
            var reduction = source.DeploymentAttackReduction;
            var before = target.Attack;
            target.Attack = Math.Max(0, before - reduction);
            source.DeploymentPenaltyPending = false;
            Emit("deployment-penalty",
                $"{Type(source).Name} 触发一次性部署减攻：{Type(target).Name} 攻击力 {before} → {target.Attack}（配置减攻 {reduction}）",
                source.Id);
        }

        /// <summary>
        /// 单位离开棋盘的原因。
        /// 为什么单独建枚举：阵亡、升级、主动离场与技能顶替在补兵与日志语义上不同，
        /// 使用命名原因避免布尔标记混淆，并确保玩家卡不会被重复回收。
        /// </summary>
        enum Removal
        {
            /// <summary>被覆盖升级：只移除旧单位，兵位永不自动补回。</summary>
            Upgrade,
            /// <summary>阵亡：AI 兵位按补兵间隔计时。</summary>
            Death,
            /// <summary>玩家主动离场：不消耗行动、不回收来源卡、不触发补兵。</summary>
            Withdraw,
            /// <summary>刷怪技能直接顶替玩家单位；不造成伤害，不重复回收来源卡。</summary>
            ReplacedBySpawn,
            /// <summary>即时卡结算完成后的自动离场：不是阵亡，不触发补兵、死亡加攻或标记奖励。</summary>
            Consumed
        }

        /// <summary>
        /// 把单位移出棋盘。
        /// 输入：单位与离场原因；输出：无。
        /// 玩家卡：部署时已经进入墓地，这里不再回收，避免同一张卡重复入堆（牌堆总数因此恒定）——
        /// 主动离场同样不回收，所以“抽牌堆 + 手牌 + 墓地”的总数不受离场影响。
        /// Boss 卡：不参与玩家循环，任何原因移除都写入独立回收区，供快照展示。
        /// 补兵计时只对 AI 兵位阵亡（Death）时启动；覆盖升级与主动离场把到期回合设为 long.MaxValue，永不自动补回。
        /// </summary>
        void Remove(UnitState u, Removal reason)
        {
            _units.Remove(u);
            // 标记奖励：只在“因战斗阵亡”时结算。覆盖升级、被战机顶替与主动离场都不算死亡，
            // 因此不会给标记者发资源；标记本身已随单位消失，不需要额外清理。
            if (reason == Removal.Death && u.MarkedBy.HasValue && u.MarkReward > 0)
            {
                if (u.MarkedBy.Value == Side.Player)
                {
                    _resources += u.MarkReward;
                    Emit("mark-reward", $"被标记单位阵亡：玩家获得 {u.MarkReward} 资源", u.Id);
                }
                else
                {
                    // Boss 目前没有资源系统：不发放资源，只留可追溯日志，便于将来接入。
                    Emit("mark-reward", $"被标记单位阵亡：Boss 侧没有资源系统，未发放 {u.MarkReward} 资源", u.Id);
                }
            }
            // 死亡加攻：全棋盘任一单位“因战斗阵亡”时，所有在场且未达次数上限的该能力单位增加攻击力。
            // 与标记奖励共用同一个钩子，所以覆盖升级、被战机顶替与主动离场都不会触发加成。
            if (reason == Removal.Death)
            {
                foreach (var grower in _units)
                {
                    if (grower.DeathGrowthLimit <= 0 || grower.DeathGrowthTriggers >= grower.DeathGrowthLimit) continue;
                    grower.DeathGrowthTriggers++;
                    grower.Attack += grower.DeathGrowthPerKill;
                    Emit("death-growth", $"{Type(grower).Name} 因 {Type(u).Name} 阵亡获得 +{grower.DeathGrowthPerKill} 攻击（{grower.DeathGrowthTriggers}/{grower.DeathGrowthLimit}）", grower.Id);
                }
            }
            if (u.Owner == Side.Boss) _bossGrave.Add(new CardRecord(u.SourceCardId, u.TypeId, Side.Boss));
            if (u.BossSlotId != null)
            {
                var slot = _replacements.Single(s => s.SlotId == u.BossSlotId);
                slot.UnitId = null;
                slot.DueRound = reason == Removal.Death ? _round + Rules.BossRespawnDelay : long.MaxValue;
                slot.WaitingForSpace = false;
            }
            var kind = reason == Removal.Death ? "death" : reason == Removal.Upgrade ? "upgrade" : reason == Removal.ReplacedBySpawn ? "spawn-replace" : reason == Removal.Consumed ? "consume" : "withdraw";
            var label = reason == Removal.Death ? "阵亡" : reason == Removal.Upgrade ? "被覆盖升级" : reason == Removal.ReplacedBySpawn ? "被 Boss 战机顶替" : reason == Removal.Consumed ? "结算后离场" : "离场";
            Emit(kind, $"{Type(u).Name} {label}，场上单位移除，来源卡不重复回收", u.Id);
        }

        /// <summary>
        /// 开始指定阵营的行动回合。
        /// 输入：side 为即将行动的阵营；输出：无。
        /// 玩家顺序：恢复行动 → 引爆到期炸弹 → 可选清零资源 → 存活单位产出及维持费 → 抽牌 → 检查炸弹部署机会。
        /// AI 自动布置；手动模式必须布置或跳过后才能进行玩家普通操作。间隔一时先爆旧炸弹再布置新炸弹。
        /// Boss 顺序：读取技能条件 → 恢复已有行动 → 已有刷怪卡生成 → 新刷怪卡机会 → AI 补兵 → 普通行动。
        /// 新单位在恢复行动之后创建以保留召唤失调；Boss 不结算资源与维持费。
        /// 欠费只禁止行动，不能关闭星门部署扩展（扩展在 DeploymentError 中独立判断）。
        /// </summary>
        void BeginTurn(Side side)
        {
            // 必须在任何生成或顶替前只读取一次；欠费与已行动不改变配置攻击力和卡种分类。
            var spawnerTriggered = side == Side.Boss && MeetsSpawnerTrigger();
            _activeSide = side;
            _phase = MatchPhase.Action;
            _bossBombPending = false;
            _bossSpawnerPending = false;
            _plays = side == Side.Player ? Rules.PlayerPlays : Rules.BossPlays;
            foreach (var h in _homes.Where(h => h.Owner == side)) h.Acted = false;
            foreach (var u in _units.Where(u => u.Owner == side)) { u.Acted = false; u.UpkeepPaid = true; }
            Emit("turn", $"第 {_round} 回合 · {SideName(side)}行动");
            if (side == Side.Player)
            {
                var hadDueBomb = _bossBombs.Any(b => b.DueRound <= _round);
                ExplodeBossBombs();
                if (hadDueBomb) CapturePresentationFrame();
                if (!Rules.AccumulateResources) _resources = 0;
                _resources += _config.PlayerHomeProduce + _units.Where(u => u.Owner == side).Sum(u => (long)(Type(u).Resource?.Produce ?? 0));
                foreach (var u in _units.Where(u => u.Owner == side).OrderBy(u => u.DeploymentOrder))
                {
                    var fee = Type(u).Resource?.Upkeep ?? 0;
                    u.UpkeepPaid = _resources >= fee;
                    if (u.UpkeepPaid) _resources -= fee;
                    else Emit("upkeep", $"{Type(u).Name} 维持费不足，本回合不能行动", u.Id);
                }
                DrawCards();
                _bossBombPending = _round % Rules.BossBombInterval == 0;
                if (!Rules.PlayerControlsBoss)
                {
                    if (_bossBombPending)
                    {
                        ExecuteCommand(new GameCommand(CommandKind.DeployBossBomb, Side.Boss,
                            target: new Cell(_random.Next(Rules.Rows), BossBombColumn())), true);
                    }
                }
                else if (_bossBombPending) Emit("bomb-ready", "请先布置炸弹或跳过炸弹，再开始玩家行动");
            }
            else
            {
                ProduceSpawnerFighter();
                if (spawnerTriggered && !_units.Any(u => u.IsBossSpawner))
                {
                    var cells = SpawnerCells().ToArray();
                    if (cells.Length == 0) Emit("spawner-skip", "刷怪卡部署区域无空格，跳过当次机会");
                    else
                    {
                        _bossSpawnerPending = true;
                        if (Rules.PlayerControlsBoss) Emit("spawner-ready", "请先部署刷怪卡或跳过，再开始 Boss 普通行动");
                        else ExecuteCommand(new GameCommand(CommandKind.DeployBossSpawner, Side.Boss, target: cells[_random.Next(cells.Length)]), true);
                    }
                }
                if (!Rules.PlayerControlsBoss) SpawnBoss();
            }
        }

        /// <summary>
        /// 读取一次 Boss 回合开始的触发条件。输入：当前场上单位；输出：是否满足总攻击力和前线攻击类数量。
        /// long 求和避免大量单位或高攻击配置溢出；单位的行动状态不影响统计。
        /// </summary>
        bool MeetsSpawnerTrigger()
        {
            var playerUnits = _units.Where(u => u.Owner == Side.Player).ToArray();
            var attack = playerUnits.Sum(u => (long)u.Attack);
            return attack >= Rules.BossSpawnerAttackThreshold && playerUnits.Count(u =>
                Type(u).Category == CardCategory.Attack && PlayerColumn(u.Position) >= Rules.BossSpawnerCountFromColumn) >= Rules.BossSpawnerUnitThreshold;
        }

        /// <summary>换算玩家视角列号。输入：内部坐标；输出：从玩家一侧起算的 1 基列号，用于镜像一致的范围判断。</summary>
        int PlayerColumn(Cell cell) => Rules.PlayerOnLeft ? cell.Col + 1 : Rules.Columns - cell.Col;

        /// <summary>列出刷怪卡合法位置。输入：当前棋盘；输出：区域内空格，已有刷怪卡时为空，供命令和高亮共用。</summary>
        IEnumerable<Cell> SpawnerCells() => _units.Any(u => u.IsBossSpawner) ? Enumerable.Empty<Cell>() :
            Cells().Where(p => PlayerColumn(p) >= Rules.BossSpawnerDeployFromColumn && !Occupied(p));

        /// <summary>
        /// 让已有刷怪卡尝试生成一架战机。输入：本回合棋盘；输出：新单位、顶替或暂停事件及对应阶段快照。
        /// 八邻优先空格，其次随机顶替玩家单位；家和 Boss 单位永不顶替，失败不累计，之后每回合重试。
        /// </summary>
        void ProduceSpawnerFighter()
        {
            var spawner = _units.FirstOrDefault(u => u.IsBossSpawner);
            if (spawner == null || spawner.DeployedRound >= _round) return;
            var neighbors = new List<Cell>();
            for (var row = -1; row <= 1; row++)
                for (var col = -1; col <= 1; col++)
                {
                    var cell = new Cell(spawner.Position.Row + row, spawner.Position.Col + col);
                    if ((row != 0 || col != 0) && Inside(cell)) neighbors.Add(cell);
                }
            var candidates = neighbors.Where(p => !Occupied(p)).ToArray();
            if (candidates.Length == 0)
                candidates = neighbors.Where(p => _units.Any(u => u.Owner == Side.Player && u.Position == p)).ToArray();
            if (candidates.Length == 0)
            {
                Emit("spawner-wait", "刷怪卡周围无空格或可顶替的玩家单位，本回合暂停生成", spawner.Id);
                CapturePresentationFrame();
                return;
            }
            var target = candidates[_random.Next(candidates.Length)];
            var replaced = _units.FirstOrDefault(u => u.Position == target);
            if (replaced != null)
            {
                Remove(replaced, Removal.ReplacedBySpawn);
                CapturePresentationFrame();
            }
            var fighter = Place(NewCard(Rules.BossSpawnerSpawnCardType, Side.Boss), target, false, null);
            Emit("spawner-produce", $"刷怪卡生成 {Type(fighter).Name} 至 {target}", fighter.Id);
            CapturePresentationFrame();
        }

        /// <summary>
        /// 交出行动权。
        /// 输入：无（由结束回合或弃牌完成触发）；输出：无。
        /// 回合号递增条件：双方各完成一次行动回合；弃牌未完成时不会被调用。
        /// AI 模式下交出给 Boss 后立即同步执行 AI 行动。
        /// </summary>
        void AdvanceTurn()
        {
            if (++_turnsCompleted == 2) { _turnsCompleted = 0; _round++; }
            BeginTurn(_activeSide == Side.Player ? Side.Boss : Side.Player);
            if (_activeSide == Side.Boss && !Rules.PlayerControlsBoss) RunBoss();
        }

        /// <summary>
        /// 玩家回合开始抽牌。
        /// 输入：无（数量取自规则）；输出：无。
        /// 关键规则：逐张抽取，抽到一半抽牌堆耗尽时把墓地整体洗入抽牌堆并继续本次抽牌；
        /// 场上单位与现有手牌绝不参与洗回，因此只循环墓地中已有的卡牌，不会复制新牌；
        /// 由于每次成功部署都会把玩家卡放入墓地，随游戏推进墓地通常不会长期为空。
        /// </summary>
        void DrawCards()
        {
            var count = 0;
            while (count < Rules.DrawCount)
            {
                if (_draw.Count == 0 && _grave.Count > 0)
                {
                    _draw.AddRange(_grave);
                    _grave.Clear();
                    Shuffle(_draw);
                    Emit("shuffle", $"墓地 {_draw.Count} 张牌洗入抽牌堆");
                }
                if (_draw.Count == 0) break;
                var index = _draw.Count - 1;
                _hand.Add(_draw[index]);
                _draw.RemoveAt(index);
                count++;
            }
            Emit("draw", $"抽取 {count} 张牌，缺额 {Rules.DrawCount - count}");
        }

        /// <summary>
        /// 处理到期兵位的部署与补兵。
        /// 输入：无；输出：无。
        /// 候选格优先级：原格 → 靠玩家一侧前列 → 距原行最近 → 内部行号升序。
        /// 无空位时保留 WaitingForSpace 状态，下个 Boss 回合重试，绝不覆盖任何单位或家。
        /// </summary>
        void SpawnBoss()
        {
            foreach (var s in _replacements.Where(s => s.UnitId == null && s.DueRound <= _round))
            {
                var candidates = Cells()
                    .Where(p => InHomeRegion(Side.Boss, p) && !Occupied(p))
                    .OrderBy(p => p == s.PreferredCell ? 0 : 1)
                    .ThenBy(p => IsLeft(Side.Boss) ? -p.Col : p.Col)
                    .ThenBy(p => Math.Abs(p.Row - s.PreferredCell.Row))
                    .ThenBy(p => p.Row)
                    .ToArray();
                if (candidates.Length == 0) { s.WaitingForSpace = true; Emit("spawn-wait", $"兵位 {s.SlotId} 等待空位"); continue; }
                var u = Place(NewCard(Rules.BossCardType, Side.Boss), candidates[0], !s.HasSpawned, s.SlotId);
                s.UnitId = u.Id;
                s.HasSpawned = true;
                s.WaitingForSpace = false;
            }
        }

        /// <summary>
        /// 部署一颗延迟炸弹。输入：已经过命令校验的内部坐标；输出：无。
        /// 在 SpawnBoss 下集中放置技能实现；占用独立技能机会，不消耗放牌次数，不参与占位。
        /// 在玩家行动前部署，到期时于下一完整回合玩家行动前爆炸，AI 与手动模式共用此实现。
        /// </summary>
        void DeployBossBomb(Cell position)
        {
            var bomb = new BossBombState(position, _round, Rules.BossBombDamage);
            _bossBombs.Add(bomb);
            _bossBombPending = false;
            Emit("bomb-deploy", $"Boss 在 {position} 布置炸弹，第 {bomb.DueRound} 回合爆炸，总伤害 {bomb.Damage}", bomb: bomb);
        }

        /// <summary>
        /// 引爆全部到期炸弹。输入：当前完整回合与炸弹集合；输出：有序路径和伤害事件。
        /// 玩家回合开始时先于资源结算、部署新炸弹及普通行动调用；先中心，再按距离及屏幕上右下左搜索。
        /// 四方向共享预算，逐点扣血直到单位死亡或预算耗尽；仅玩家战斗单位受伤，其他对象可穿过。
        /// 一条射线到边界不阻止其他射线；全部搜索结束后丢弃剩余预算并移除地面标记。
        /// </summary>
        void ExplodeBossBombs()
        {
            foreach (var bomb in _bossBombs.Where(b => b.DueRound <= _round).ToArray())
            {
                var remaining = bomb.Damage;
                var steps = new List<BombBlastStep>();
                ResolveBombCell(bomb.Position, ref remaining, steps, bomb);
                var horizontal = Rules.PlayerOnLeft ? 1 : -1;
                var directions = new[] { new Cell(1, 0), new Cell(0, horizontal), new Cell(-1, 0), new Cell(0, -horizontal) };
                for (var distance = 1; distance < Math.Max(Rules.Rows, Rules.Columns) && remaining > 0; distance++)
                    foreach (var direction in directions)
                    {
                        if (remaining == 0) break;
                        var cell = new Cell(bomb.Position.Row + direction.Row * distance, bomb.Position.Col + direction.Col * distance);
                        if (Inside(cell)) ResolveBombCell(cell, ref remaining, steps, bomb);
                    }
                _bossBombs.Remove(bomb);
                // 预算消耗与总扣血必须分开记录：伤害标记追加的伤害不消耗预算，
                // 因此“初始预算 - 剩余预算”只代表预算消耗，不能再被当成实际总伤害。
                var budgetUsed = bomb.Damage - remaining;
                var totalDamage = steps.Sum(s => s.TotalDamage);
                Emit("bomb-explode", $"炸弹在 {bomb.Position} 爆炸，消耗预算 {budgetUsed}，实际造成 {totalDamage} 点伤害", bomb: bomb, blastSteps: steps.ToArray());
            }
        }

        /// <summary>
        /// 结算单格炸弹伤害。输入：格子、共享剩余预算、路径记录与所属炸弹；输出：更新预算、路径与追加伤害。
        /// 每轮先把 1 点预算换成 1 点基础伤害，再交给统一伤害入口；追加伤害只扣生命、不扣预算。
        /// 阵亡沿用 Remove，玩家来源卡早已进入墓地，此处不重复回收；空格同样记录以复现搜索动画。
        /// </summary>
        void ResolveBombCell(Cell cell, ref int remaining, List<BombBlastStep> steps, BossBombState bomb)
        {
            var unit = _units.FirstOrDefault(u => u.Owner == Side.Player && u.Position == cell);
            var budgetUsed = 0;
            var bonusDamage = 0;
            var killed = false;
            if (unit != null)
            {
                while (remaining > 0 && unit.Hp > 0)
                {
                    remaining--;
                    budgetUsed++;
                    // 炸弹上下文：归属 Boss 阵营、没有来源单位（不冒充标记施加者），根类型为 Bomb。
                    var result = ResolveDirectDamage(unit, 1, new DamageContext(Side.Boss, null, unit.Id, DamageKind.Bomb, bomb: bomb));
                    bonusDamage = (int)Math.Min(int.MaxValue, (long)bonusDamage + result.BonusDamage);
                    if (!result.Killed) continue;
                    // 追加伤害提前击杀后，剩余预算留给原搜索顺序继续寻找其它目标。
                    killed = true;
                    break;
                }
            }
            steps.Add(new BombBlastStep(cell, unit?.Id ?? 0, budgetUsed, killed, bonusDamage));
        }

        /// <summary>
        /// 换算炸弹内部列号。输入：Excel 期望显示列与棋盘宽度/镜像配置；输出：从 0 开始的内部列。
        /// 显示列从 1 开始，不超过半场向下取整，保证最小两列棋盘仍有合法目标。
        /// </summary>
        int BossBombColumn()
        {
            var displayColumn = Math.Min(Rules.BossBombColumn, Rules.Columns / 2) - 1;
            return Rules.PlayerOnLeft ? displayColumn : Rules.Columns - 1 - displayColumn;
        }

        /// <summary>
        /// 临时查询适配器，让家与普通单位共用同一套目标排序与攻击判断。
        /// 为什么不用继承：家不继承 Card，也不应进入单位列表；用轻量包装避免为排序引入实体层级。
        /// </summary>
        sealed class Target
        {
            /// <summary>目标为单位时的引用。</summary>
            public UnitState Unit;
            /// <summary>目标为家时的引用。</summary>
            public HomeState Home;
            /// <summary>用于日志的名称。</summary>
            public string Name;
            /// <summary>实体 ID：单位取自身 ID，家取负数 ID。</summary>
            public long Id => Unit?.Id ?? Home.Id;
            /// <summary>当前所在格。</summary>
            public Cell Position => Unit?.Position ?? Home.Position;
            /// <summary>当前生命。</summary>
            public int Hp => Unit?.Hp ?? Home.Hp;
        }

        /// <summary>
        /// 枚举攻击方的所有敌方存活目标。
        /// 输入：攻击方阵营；输出：家与敌方单位。
        /// 每次 AI 决策都重新调用，不使用缓存，保证“每次行动后按最新棋盘重新决策”。
        /// </summary>
        IEnumerable<Target> Targets(Side attacker)
        {
            foreach (var h in _homes.Where(h => h.Owner != attacker)) yield return new Target { Home = h, Name = SideName(h.Owner) + "的家" };
            foreach (var u in _units.Where(u => u.Owner != attacker)) yield return new Target { Unit = u, Name = Type(u).Name };
        }

        /// <summary>目标分类权重：玩家家 0、攻击卡 1、资源卡 2、特殊卡 3，用于 AI 的并列排序。</summary>
        int TargetKind(Target t) => t.Home != null ? 0 : Type(t.Unit).Category == CardCategory.Attack ? 1 : Type(t.Unit).Category == CardCategory.Resource ? 2 : 3;

        /// <summary>
        /// AI 的攻击优先级。
        /// 输入：行动单位与候选目标；输出：越小越优先。
        /// 规则：可一击摧毁的玩家家 → 可一击击杀的普通单位 → 玩家家 → 攻击卡 → 资源卡 → 特殊卡。
        /// 已确认“特殊卡在非致死优先级中排在资源卡之后”。当前攻击力取单位快照值，
        /// 可击杀预测包含目标既有的伤害标记追加（与真实结算一致），但不会挂新层或扣血。
        /// </summary>
        int AttackPriority(UnitState u, Target t)
        {
            var predicted = (long)u.Attack + (t.Unit != null ? t.Unit.DamageMarkBonus : 0);
            return t.Hp <= predicted ? (t.Home != null ? 0 : 1) : 2 + TargetKind(t);
        }

        /// <summary>
        /// 执行一个完整的 Boss 回合。
        /// 输入：无；输出：无。
        /// 前置：BeginTurn 已恢复 Boss 行动并补兵；炸弹统一在玩家回合开始时处理。
        /// 顺序：按部署顺序逐个行动 → 自动结束回合；每个单位基于爆炸后的最新棋盘决策。
        /// 每次行动只做一件事：能攻击就攻击，否则沿最短可通行路径移动到最近合法攻击位置；
        /// 只有“击杀恢复行动”清空 Acted 后才会重新决策，其余情况（未击杀、移动、待机）都结束该单位本回合循环。
        /// 胜负确定后立即停止后续行动。
        /// </summary>
        void RunBoss()
        {
            CapturePresentationFrame();
            foreach (var u in _units.Where(u => u.Owner == Side.Boss).OrderBy(u => u.DeploymentOrder).ToArray())
            {
                if (_phase == MatchPhase.Finished) return;
                // 击杀恢复行动会让单位重新可行动，因此必须在最新状态上循环决策；
                // 每轮都重新查询目标、占位与路径，不缓存上一次结果。
                while (u.CanAct && _units.Contains(u))
                {
                    if (_phase == MatchPhase.Finished) return;
                    var target = Targets(Side.Boss)
                        .Where(t => CanAttack(u, u.Position, t.Position))
                        .OrderBy(t => AttackPriority(u, t))
                        .ThenBy(t => t.Hp)
                        .ThenBy(t => t.Position.Row)
                        .ThenBy(t => t.Position.Col)
                        .FirstOrDefault();
                    if (target != null)
                    {
                        // 命令失败必须退出循环，避免在异常配置下反复尝试同一失败命令而卡死。
                        if (ExecuteCommand(new GameCommand(CommandKind.Attack, Side.Boss, u.Id, targetId: target.Id), true) != null) break;
                        continue;
                    }
                    if (u.Attack <= 0 || u.Range <= 0 || u.MoveDistance <= 0) { Emit("wait", $"{Type(u).Name} 待机", u.Id); break; }
                    var paths = Paths(u.Position);
                    var best = Targets(Side.Boss)
                        .SelectMany(t => paths.Where(p => p.Value.Count > 0 && CanAttack(u, p.Key, t.Position)).Select(p => new { Target = t, Path = p.Value }))
                        .OrderBy(x => x.Path.Count)
                        .ThenBy(x => TargetKind(x.Target))
                        .ThenBy(x => x.Target.Position.Row)
                        .ThenBy(x => x.Target.Position.Col)
                        .FirstOrDefault();
                    if (best != null)
                    {
                        // 最远可达落点不一定合法（四邻空格限制）：从最远处向回退，找到第一个合法落点。
                        // 远处攻击位置合法并不代表本回合实际走到的中间落点合法。
                        var steps = Math.Min(u.MoveDistance, best.Path.Count);
                        while (steps > 0 && MovementError(u, best.Path[steps - 1]) != null) steps--;
                        if (steps > 0) ExecuteCommand(new GameCommand(CommandKind.Move, Side.Boss, u.Id, best.Path[steps - 1]), true);
                        else Emit("wait", $"{Type(u).Name} 没有满足空格限制的落点，待机", u.Id);
                    }
                    else Emit("wait", $"{Type(u).Name} 无可通行路径，待机", u.Id);
                    break;
                }
            }
            CapturePresentationFrame();
            if (_phase != MatchPhase.Finished) AdvanceTurn();
        }

        /// <summary>
        /// 统一的战斗单位直接伤害结算入口。
        /// 输入：目标单位、本次基础伤害（普通攻击的当前攻击力，炸弹固定为 1）与归属上下文；
        /// 输出：本组伤害的结构化事实（基础/追加实际扣血、是否死亡、用了哪些已有层）。
        /// 结算顺序：①固定本次受伤前的标记层 →②扣基础生命 →③若已死亡只做一次 Remove(Death) 并结束
        /// →④仍存活时按已固定的层依次追加，任一步归零立即结束。
        /// 为什么必须固定层：本次攻击新挂的层只能从下一次受伤起生效；
        /// 为什么追加不递归：追加伤害不再经过挂层与恢复行动逻辑，避免无限循环。
        /// </summary>
        DamageResult ResolveDirectDamage(UnitState target, int baseDamage, DamageContext context)
        {
            if (target == null) return null;
            // 复制受伤前的层，后续挂层不会回头改变本次结算。
            var marks = target.DamageMarks;
            var layers = new DamageMarkLayer[marks.Count];
            for (var i = 0; i < layers.Length; i++) layers[i] = marks[i];

            var baseApplied = DeductHp(target, baseDamage);
            var bonusApplied = 0;
            var killed = target.Hp <= 0;
            if (killed) Remove(target, Removal.Death);
            else
            {
                foreach (var layer in layers)
                {
                    var applied = DeductHp(target, layer.Damage);
                    // 追加伤害可能让总量超出 int：用宽类型累加后截断，避免回绕成负值。
                    bonusApplied = (int)Math.Min(int.MaxValue, (long)bonusApplied + applied);
                    if (target.Hp > 0) continue;
                    // 追加击杀同样只做一次死亡处理（死亡加攻/标记奖励/兵位计时都只走这里）。
                    Remove(target, Removal.Death);
                    killed = true;
                    break;
                }
            }
            return new DamageResult(context, baseApplied, bonusApplied, killed, layers);
        }

        /// <summary>
        /// 家受到伤害的结算。
        /// 输入：目标家、基础伤害与归属上下文；输出：结构化事实（家不参与伤害标记，追加永远为 0）。
        /// 为什么不走 ResolveDirectDamage：家不是 UnitState、不会离场，且不能被施加伤害标记；
        /// 胜负检查仍由攻击分支统一调用。
        /// </summary>
        DamageResult ResolveHomeDamage(HomeState home, int damage, DamageContext context)
        {
            var applied = Math.Max(0, Math.Min(damage, home.Hp));
            home.Hp -= applied;
            return new DamageResult(context, applied, 0, false, Array.Empty<DamageMarkLayer>());
        }

        /// <summary>
        /// 扣除单位生命并返回实际扣血量。输入：单位与非负伤害；输出：实际扣掉的生命（不超过剩余生命）。
        /// 这是唯一修改单位 Hp 的底层步骤：它不触发死亡处理、不发事件，避免同一笔伤害被重复结算。
        /// </summary>
        static int DeductHp(UnitState unit, int damage)
        {
            if (damage <= 0 || unit.Hp <= 0) return 0;
            var applied = Math.Min(damage, unit.Hp);
            unit.Hp -= applied;
            return applied;
        }

        /// <summary>
        /// 判断一次攻击是否合法。
        /// 输入：攻击单位、起点与目标格；输出：是否可攻击。
        /// 规则：射程始终为曼哈顿距离；开启遮挡时只检查同行/列之间的中间格占位（不含自身起点）。
        /// </summary>
        bool CanAttack(UnitState u, Cell from, Cell to)
        {
            if (u.Attack <= 0 || from.Distance(to) < 1 || from.Distance(to) > u.Range) return false;
            if (Rules.LineBlocking && (from.Row == to.Row || from.Col == to.Col))
            {
                var dr = Math.Sign(to.Row - from.Row);
                var dc = Math.Sign(to.Col - from.Col);
                var p = new Cell(from.Row + dr, from.Col + dc);
                while (p != to) { if (Occupied(p) && p != u.Position) return false; p = new Cell(p.Row + dr, p.Col + dc); }
            }
            return true;
        }

        /// <summary>
        /// 四向 BFS，记录各可达空格的最短路径。
        /// 输入：起点格；输出：可达格 → 路径（不含起点）。
        /// 约定：占位格阻断通行；起点本身允许占位；并列路径按 _directions 的上、右、下、左顺序展开，
        /// 保证同一局面下 AI 的移动决策可复现。
        /// </summary>
        Dictionary<Cell, List<Cell>> Paths(Cell start)
        {
            var paths = new Dictionary<Cell, List<Cell>> { [start] = new List<Cell>() };
            var queue = new Queue<Cell>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                foreach (var n in Neighbors(p))
                {
                    if (Occupied(n) || paths.ContainsKey(n)) continue;
                    var route = new List<Cell>(paths[p]) { n };
                    paths.Add(n, route);
                    queue.Enqueue(n);
                }
            }
            return paths;
        }

        /// <summary>
        /// 一次完整伤害结算后检查双方家血量。
        /// 输入：无；输出：无。
        /// 效果：任一方家归零即结束对局（同时归零为平局），结束状态会阻止后续命令与补兵。
        /// </summary>
        void CheckVictory()
        {
            var playerDead = _homes.Single(h => h.Owner == Side.Player).Hp <= 0;
            var bossDead = _homes.Single(h => h.Owner == Side.Boss).Hp <= 0;
            if (!playerDead && !bossDead) return;
            _outcome = playerDead && bossDead ? MatchOutcome.Draw : playerDead ? MatchOutcome.BossWins : MatchOutcome.PlayerWins;
            _phase = MatchPhase.Finished;
            Emit("finished", _outcome == MatchOutcome.Draw ? "平局" : _outcome == MatchOutcome.PlayerWins ? "玩家获胜" : "Boss 获胜");
        }

        /// <summary>判断单位本回合是否可行动。输入：单位（可为 null）；输出：是否可用。</summary>
        bool Available(UnitState u) => u != null && u.Owner == _activeSide && u.CanAct;

        /// <summary>
        /// 判断单位能否参与互换。
        /// 输入：单位（可为 null）；输出：true 表示它可以作为互换的发起方或目标。
        /// 为什么单独成判定：资源卡只提供产出、按普通单位规则移动，但规则上不参与互换
        /// （见 docs/玩法.md 的互换段与 docs/游戏内名词.md 的互换条目），
        /// 因此互换需要一个比 Available 更窄的条件；与 Available 分开后，
        /// 高亮（GetLegalActions）与结算（ExecuteCommand）共用同一份判定，
        /// 不会出现“没有高亮、点下去却换了位置”的分叉。
        /// </summary>
        bool Swappable(UnitState u) => Available(u) && Type(u).Category != CardCategory.Resource;

        /// <summary>按来源卡种号查询共享配置。输入：单位；输出：卡种描述。</summary>
        CardArchetype Type(UnitState u) => _config.Cards[u.TypeId];

        /// <summary>判断阵营是否位于棋盘左侧。输入：阵营；输出：是否在左（Boss 与玩家相反）。</summary>
        bool IsLeft(Side side) => side == Side.Player ? Rules.PlayerOnLeft : !Rules.PlayerOnLeft;

        /// <summary>判断坐标是否在棋盘内。输入：坐标；输出：是否合法。</summary>
        bool Inside(Cell p) => p.Row >= 0 && p.Row < Rules.Rows && p.Col >= 0 && p.Col < Rules.Columns;

        /// <summary>
        /// 判断坐标是否位于某阵营的固定己方区域。
        /// 输入：阵营与坐标；输出：是否在区域内。
        /// 注意：区域只由边缘宽度决定，不随家的位置变化；棋盘不足四列时双方区域会重叠。
        /// </summary>
        bool InHomeRegion(Side side, Cell p) => Inside(p) && (IsLeft(side) ? p.Col < Rules.HomeRegionWidth : p.Col >= Rules.Columns - Rules.HomeRegionWidth);

        /// <summary>判断坐标是否被家或单位占用。输入：坐标；输出：是否占用。</summary>
        bool Occupied(Cell p) => _homes.Any(h => h.Position == p) || _units.Any(u => u.Position == p);

        /// <summary>
        /// 统计中心格四邻（上下左右）中为空的格数。
        /// 输入：中心格与占位查询；输出：0～4 的空格数量。
        /// 规则：四个方向必须完整枚举；棋盘外直接计为空（因此边缘贡献 1、角落贡献 2）；斜角不计。
        /// 为什么抽成查询委托：部署、移动、互换需要不同的“最终布局”，又不允许真的修改状态去试算。
        /// </summary>
        int CountEmptyOrthogonalNeighbors(Cell center, Func<Cell, bool> occupied)
        {
            var empty = 0;
            foreach (var direction in _directions)
            {
                var neighbor = new Cell(center.Row + direction.Row, center.Col + direction.Col);
                if (!Inside(neighbor)) { empty++; continue; }
                if (!occupied(neighbor)) empty++;
            }
            return empty;
        }

        /// <summary>
        /// 检查一个位置是否满足卡种的四邻空格限制。
        /// 输入：卡种、位置与占位查询；输出：null 表示无能力或满足，否则为玩家可读原因。
        /// 无该能力时直接返回 null，因此普通卡仍走原有规则（调用方无需分支）。
        /// </summary>
        string ClearanceError(CardArchetype type, Cell center, Func<Cell, bool> occupied)
        {
            var ability = type?.AdjacentClearance;
            if (ability == null) return null;
            var empty = CountEmptyOrthogonalNeighbors(center, occupied);
            return empty >= ability.MinEmptyNeighbors ? null : $"四邻至少需要 {ability.MinEmptyNeighbors} 格为空（当前 {empty}）";
        }

        /// <summary>
        /// 移动的空格限制校验。
        /// 输入：移动单位与目标格；输出：null 表示合法，否则为失败原因。
        /// 起点按原布局检查；落点按“旧格已腾空、新格已被移动者占用”的最终布局检查；路径中间格不参与。
        /// 不采用“先改坐标、失败再改回”的试算，避免失败命令污染位置、行动或事件。
        /// </summary>
        string MovementError(UnitState mover, Cell target)
        {
            var type = Type(mover);
            if (type.AdjacentClearance == null) return null;
            var origin = ClearanceError(type, mover.Position, Occupied);
            if (origin != null) return "当前位置不满足四邻空格限制：" + origin;
            var destination = ClearanceError(type, target, cell => cell == mover.Position ? false : cell == target || Occupied(cell));
            return destination == null ? null : "落点不满足四邻空格限制：" + destination;
        }

        /// <summary>
        /// 互换的空格限制校验。
        /// 输入：互换双方；输出：null 表示合法，否则为失败原因。
        /// 互换前后两个格始终各有一个单位（只是人换了位置），因此最终布局不能把两个原位置都当空；
        /// 双方中所有具有该能力的单位都必须在交换前、交换后的位置上分别满足要求。
        /// </summary>
        string SwapError(UnitState a, UnitState b)
        {
            var before = ClearanceError(Type(a), a.Position, Occupied) ?? ClearanceError(Type(b), b.Position, Occupied);
            if (before != null) return "互换前布局不满足四邻空格限制：" + before;
            // 交换只改变“谁在哪个格”，占位模式不变：两个位置都仍然被占用。
            bool FinalOccupied(Cell cell) => cell == a.Position || cell == b.Position || Occupied(cell);
            var after = ClearanceError(Type(a), b.Position, FinalOccupied) ?? ClearanceError(Type(b), a.Position, FinalOccupied);
            return after == null ? null : "互换后布局不满足四邻空格限制：" + after;
        }

        /// <summary>按内部行列顺序枚举全部格，用于部署高亮与补兵候选。</summary>
        IEnumerable<Cell> Cells() { for (var r = 0; r < Rules.Rows; r++) for (var c = 0; c < Rules.Columns; c++) yield return new Cell(r, c); }

        /// <summary>枚举四向相邻且在棋盘内的格。输入：中心格；输出：相邻格序列。</summary>
        IEnumerable<Cell> Neighbors(Cell p) { foreach (var d in _directions) { var n = new Cell(p.Row + d.Row, p.Col + d.Col); if (Inside(n)) yield return n; } }

        /// <summary>创建一条新的卡牌记录。输入：卡种 ID 与阵营；输出：带唯一实例 ID 的记录。</summary>
        CardRecord NewCard(string id, Side side) => new CardRecord(++_nextCard, id, side);

        /// <summary>
        /// Fisher–Yates 原地洗牌。
        /// 输入：待洗牌列表；输出：无（原地打乱）。
        /// 不复制卡牌身份，随机源可注入，因此固定种子时牌序完全可复现。
        /// </summary>
        void Shuffle(List<CardRecord> cards) { for (var i = cards.Count - 1; i > 0; i--) { var j = _random.Next(i + 1); var t = cards[i]; cards[i] = cards[j]; cards[j] = t; } }

        /// <summary>
        /// 记录一条领域事件。
        /// 输入：类型、文本与主体 ID；输出：无。
        /// 事件同时进入“本次命令结果”和“有界历史日志”，前者供 UI 按序播放，后者供日志面板读取。
        /// </summary>
        void Emit(string kind, string message, long id = 0, BossBombState bomb = null, BombBlastStep[] blastSteps = null, DamageResult damage = null) { var e = new GameEvent(kind, message, id, bomb, blastSteps, damage); _pendingEvents.Add(e); _log.Add(e); if (_log.Count > _logLimit) _log.RemoveAt(0); }

        /// <summary>阵营显示名。输入：阵营；输出：中文名称。</summary>
        static string SideName(Side side) => side == Side.Player ? "玩家" : "Boss";
    }
}
