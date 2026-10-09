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
    /// 身份约定：CardRecord 是可循环的来源卡；UnitState 是一次部署产生的战斗实体，两者不可混用。
    /// </summary>
    public sealed class GameSession
    {
        /// <summary>本局不可变配置索引。</summary>
        readonly GameConfiguration _config;
        /// <summary>洗牌随机源，固定种子可复现牌序。</summary>
        readonly IRandomSource _random;
        /// <summary>玩家抽牌堆。</summary>
        readonly List<CardRecord> _draw = new List<CardRecord>();
        /// <summary>玩家手牌。</summary>
        readonly List<CardRecord> _hand = new List<CardRecord>();
        /// <summary>玩家墓地；抽牌堆耗尽时整体洗回。</summary>
        readonly List<CardRecord> _grave = new List<CardRecord>();
        /// <summary>Boss 独立回收区，不参与玩家洗牌。</summary>
        readonly List<CardRecord> _bossGrave = new List<CardRecord>();
        /// <summary>在场单位。</summary>
        readonly List<UnitState> _units = new List<UnitState>();
        /// <summary>双方的家。</summary>
        readonly List<HomeState> _homes = new List<HomeState>();
        /// <summary>AI 兵位状态。</summary>
        readonly List<BossReplacement> _replacements = new List<BossReplacement>();
        /// <summary>有界历史日志，供 UI 与调试面板读取。</summary>
        readonly List<GameEvent> _log = new List<GameEvent>();
        /// <summary>当前一次外部命令连带产生的全部事件（含自动 Boss 回合）；_log 则是有界历史。</summary>
        readonly List<GameEvent> _pendingEvents = new List<GameEvent>();
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

        /// <summary>
        /// 建立家、来源卡与兵位，然后按先手顺序进入首个行动回合。
        /// 输入：configuration 为已通过结构与业务校验的本局配置；randomSource 仅用于洗牌；logLimit 为历史日志容量。
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
            var error = command == null ? "命令为空" : ExecuteCommand(command, false);
            return new CommandResult(error == null, error, _pendingEvents.ToArray());
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
            if (!fromAi && _inputBlocked) return "配置面板打开时不能操作棋盘";
            if (c.Actor != _activeSide) return "不是该阵营的回合";
            if (!fromAi && _activeSide == Side.Boss && !Rules.PlayerControlsBoss) return "Boss 由 AI 控制";
            if (_phase == MatchPhase.Discard && c.Kind != CommandKind.Discard) return "请先弃牌至手牌上限";
            switch (c.Kind)
            {
                case CommandKind.Deploy:
                    if (_activeSide != Side.Player) return "玩家手牌仅用于玩家回合";
                    var card = _hand.FirstOrDefault(x => x.Id == c.SubjectId);
                    if (card == null) return "手牌不存在";
                    if (_plays <= 0) return "本回合出牌次数已用完";
                    var deployError = DeploymentError(_config.Cards[card.TypeId], card.Owner, c.Target);
                    if (deployError != null) return deployError;
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
                    if (c.Target == mover.Position || !path.TryGetValue(c.Target, out var movePath) || movePath.Count > Type(mover).Combat.MoveDistance) return "不在合法移动范围";
                    var origin = mover.Position;
                    mover.Position = c.Target;
                    mover.Acted = true;
                    Emit("move", $"{Type(mover).Name} 从 {origin} 移动至 {c.Target}", mover.Id);
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
                    var target = Targets(attacker.Source.Owner).FirstOrDefault(t => t.Id == c.TargetId);
                    if (target == null || !CanAttack(attacker, attacker.Position, target.Position)) return "目标不在合法攻击范围";
                    // 只扣目标生命：不存在反击、连锁或动画回调产生的补充伤害。
                    attacker.Acted = true;
                    var damage = Type(attacker).Combat.Attack;
                    if (target.Unit != null)
                    {
                        target.Unit.Hp = Math.Max(0, target.Unit.Hp - damage);
                        if (target.Unit.Hp == 0) Remove(target.Unit, true);
                    }
                    else target.Home.Hp = Math.Max(0, target.Home.Hp - damage);
                    Emit("attack", $"{Type(attacker).Name} 攻击 {target.Name}，造成 {damage} 伤害", target.Id);
                    CheckVictory();
                    break;
                case CommandKind.Swap:
                    var a = _units.FirstOrDefault(u => u.Id == c.SubjectId);
                    var b = _units.FirstOrDefault(u => u.Id == c.TargetId);
                    if (!Rules.AdjacentSwap || !Available(a) || !Available(b) || a == b || a.Position.Distance(b.Position) != 1) return "双方必须为相邻且可行动的己方普通单位";
                    // 互换独立于移动距离：零移动单位也能参加，双方同时消耗行动机会。
                    var old = a.Position;
                    a.Position = b.Position;
                    b.Position = old;
                    a.Acted = b.Acted = true;
                    Emit("swap", $"{Type(a).Name} {old} 与 {Type(b).Name} {a.Position} 交换位置", a.Id);
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
            if (selection.Kind == SelectionKind.Hand && _activeSide == Side.Player && _plays > 0)
            {
                var card = _hand.FirstOrDefault(c => c.Id == selection.Id);
                if (card != null) actions.Deploy = Cells().Where(p => DeploymentError(_config.Cards[card.TypeId], _activeSide, p) == null).ToArray();
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
                if (Available(u))
                {
                    actions.Move = Paths(u.Position).Where(p => p.Value.Count > 0 && p.Value.Count <= Type(u).Combat.MoveDistance).Select(p => p.Key).ToArray();
                    actions.Attack = Targets(_activeSide).Where(t => CanAttack(u, u.Position, t.Position)).Select(t => t.Position).ToArray();
                    if (Rules.AdjacentSwap) actions.Swap = _units.Where(v => v != u && Available(v) && u.Position.Distance(v.Position) == 1).Select(v => v.Position).ToArray();
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
                if (existing == null || existing.Source.Owner != owner || existing.Source.TypeId != type.UpgradeFrom) return "需要覆盖指定的己方资源单位";
            }
            else if (Occupied(p)) return "格子已被占用";
            if (type.DeployHomeOnly && !InHomeRegion(owner, p)) return "必须部署在己方区域";
            if (type.Category == CardCategory.Attack && !InHomeRegion(owner, p) && !_units.Any(u => u.Source.Owner == owner && Type(u).Teleport?.PermitsAttackDeploy == true && u.Position.Distance(p) == 1)) return "攻击卡需要己方区域或己方星门四邻";
            if (type.AvoidAdjacentSameType && _units.Any(u => u.Source.TypeId == type.Id && u.Position.Distance(p) == 1)) return "不能与同卡种单位四邻部署";
            return null;
        }

        /// <summary>
        /// 在指定格创建新单位。
        /// 输入：来源卡、目标格、firstSpawn（是否为 AI 兵位的首次成功部署）、兵位 ID（可空）。
        /// 输出：新建的 UnitState。
        /// 规则：目标格已有单位时先按“被覆盖升级”回收；新单位总是满血，不继承旧伤势。
        /// firstSpawn 是唯一豁免召唤失调的入口：死亡补兵必须遵守“刚上场不能行动”。
        /// </summary>
        UnitState Place(CardRecord card, Cell p, bool firstSpawn, string slotId)
        {
            var covered = _units.FirstOrDefault(u => u.Position == p);
            if (covered != null) Remove(covered, false);
            var u = new UnitState { Id = ++_nextUnit, Source = card, Position = p, Hp = _config.Cards[card.TypeId].Combat.Hp, Acted = !firstSpawn && Rules.SummoningSickness, UpkeepPaid = true, DeploymentOrder = ++_order, BossSlotId = slotId };
            _units.Add(u);
            Emit("deploy", $"{SideName(card.Owner)}部署 {Type(u).Name} 至 {p}", u.Id);
            return u;
        }

        /// <summary>
        /// 把单位移出棋盘并回收来源卡。
        /// 输入：单位与 death 标记；输出：无。
        /// 补兵计时只对 AI 兵位且为阵亡时启动（death=true）：覆盖升级把到期回合设为 long.MaxValue，永不自动补回。
        /// </summary>
        void Remove(UnitState u, bool death)
        {
            _units.Remove(u);
            (u.Source.Owner == Side.Player ? _grave : _bossGrave).Add(u.Source);
            if (u.BossSlotId != null)
            {
                var slot = _replacements.Single(s => s.SlotId == u.BossSlotId);
                slot.UnitId = null;
                slot.DueRound = death ? _round + Rules.BossRespawnDelay : long.MaxValue;
                slot.WaitingForSpace = false;
            }
            Emit(death ? "death" : "upgrade", $"{Type(u).Name} {(death ? "阵亡" : "被覆盖升级")}，来源卡进入回收区", u.Id);
        }

        /// <summary>
        /// 开始指定阵营的行动回合。
        /// 输入：side 为即将行动的阵营；输出：无。
        /// 玩家顺序固定为“恢复行动 → 可选清零资源 → 全体产出 → 按部署顺序支付维持费 → 抽牌”。
        /// Boss 不使用资源，只在 AI 模式下执行首次部署或到期补兵。
        /// 欠费只禁止行动，不能关闭星门部署扩展（扩展在 DeploymentError 中独立判断）。
        /// </summary>
        void BeginTurn(Side side)
        {
            _activeSide = side;
            _phase = MatchPhase.Action;
            _plays = side == Side.Player ? Rules.PlayerPlays : Rules.BossPlays;
            foreach (var h in _homes.Where(h => h.Owner == side)) h.Acted = false;
            foreach (var u in _units.Where(u => u.Source.Owner == side)) { u.Acted = false; u.UpkeepPaid = true; }
            Emit("turn", $"第 {_round} 回合 · {SideName(side)}行动");
            if (side == Side.Player)
            {
                if (!Rules.AccumulateResources) _resources = 0;
                _resources += _config.PlayerHomeProduce + _units.Where(u => u.Source.Owner == side).Sum(u => (long)(Type(u).Resource?.Produce ?? 0));
                foreach (var u in _units.Where(u => u.Source.Owner == side).OrderBy(u => u.DeploymentOrder))
                {
                    var fee = Type(u).Resource?.Upkeep ?? 0;
                    u.UpkeepPaid = _resources >= fee;
                    if (u.UpkeepPaid) _resources -= fee;
                    else Emit("upkeep", $"{Type(u).Name} 维持费不足，本回合不能行动", u.Id);
                }
                DrawCards();
            }
            else if (!Rules.PlayerControlsBoss) SpawnBoss();
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
        /// 关键规则：逐张抽取，抽到一半牌库耗尽时把墓地整体洗回并继续本次抽牌；
        /// 场上单位与现有手牌绝不参与洗回，因此只循环已有卡牌，不会复制新牌。
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
                    Emit("shuffle", $"墓地 {_draw.Count} 张牌洗回牌库");
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
            foreach (var u in _units.Where(u => u.Source.Owner != attacker)) yield return new Target { Unit = u, Name = Type(u).Name };
        }

        /// <summary>目标分类权重：玩家家 0、攻击卡 1、资源卡 2、特殊卡 3，用于 AI 的并列排序。</summary>
        int TargetKind(Target t) => t.Home != null ? 0 : Type(t.Unit).Category == CardCategory.Attack ? 1 : Type(t.Unit).Category == CardCategory.Resource ? 2 : 3;

        /// <summary>
        /// AI 的攻击优先级。
        /// 输入：行动单位与候选目标；输出：越小越优先。
        /// 规则：可一击摧毁的玩家家 → 可一击击杀的普通单位 → 玩家家 → 攻击卡 → 资源卡 → 特殊卡。
        /// 已确认“特殊卡在非致死优先级中排在资源卡之后”。
        /// </summary>
        int AttackPriority(UnitState u, Target t) => t.Hp <= Type(u).Combat.Attack ? (t.Home != null ? 0 : 1) : 2 + TargetKind(t);

        /// <summary>
        /// 执行一个完整的 Boss 回合。
        /// 输入：无；输出：无。
        /// 顺序：按部署顺序逐个行动 → 自动结束回合。
        /// 每次行动只做一件事：能攻击就攻击，否则沿最短可通行路径移动到最近合法攻击位置；
        /// 抵达射程也不追加攻击（移动消耗整次行动）。胜负确定后立即停止后续行动。
        /// </summary>
        void RunBoss()
        {
            foreach (var u in _units.Where(u => u.Source.Owner == Side.Boss).OrderBy(u => u.DeploymentOrder).ToArray())
            {
                if (_phase == MatchPhase.Finished) return;
                if (!u.CanAct) continue;
                var target = Targets(Side.Boss)
                    .Where(t => CanAttack(u, u.Position, t.Position))
                    .OrderBy(t => AttackPriority(u, t))
                    .ThenBy(t => t.Hp)
                    .ThenBy(t => t.Position.Row)
                    .ThenBy(t => t.Position.Col)
                    .FirstOrDefault();
                if (target != null) { ExecuteCommand(new GameCommand(CommandKind.Attack, Side.Boss, u.Id, targetId: target.Id), true); continue; }
                if (Type(u).Combat.Attack <= 0 || Type(u).Combat.Range <= 0 || Type(u).Combat.MoveDistance <= 0) { Emit("wait", $"{Type(u).Name} 待机", u.Id); continue; }
                var paths = Paths(u.Position);
                var best = Targets(Side.Boss)
                    .SelectMany(t => paths.Where(p => p.Value.Count > 0 && CanAttack(u, p.Key, t.Position)).Select(p => new { Target = t, Path = p.Value }))
                    .OrderBy(x => x.Path.Count)
                    .ThenBy(x => TargetKind(x.Target))
                    .ThenBy(x => x.Target.Position.Row)
                    .ThenBy(x => x.Target.Position.Col)
                    .FirstOrDefault();
                if (best != null) ExecuteCommand(new GameCommand(CommandKind.Move, Side.Boss, u.Id, best.Path[Math.Min(Type(u).Combat.MoveDistance, best.Path.Count) - 1]), true);
                else Emit("wait", $"{Type(u).Name} 无可通行路径，待机", u.Id);
            }
            if (_phase != MatchPhase.Finished) AdvanceTurn();
        }

        /// <summary>
        /// 判断一次攻击是否合法。
        /// 输入：攻击单位、起点与目标格；输出：是否可攻击。
        /// 规则：射程始终为曼哈顿距离；开启遮挡时只检查同行/列之间的中间格占位（不含自身起点）。
        /// </summary>
        bool CanAttack(UnitState u, Cell from, Cell to)
        {
            var stats = Type(u).Combat;
            if (stats.Attack <= 0 || from.Distance(to) < 1 || from.Distance(to) > stats.Range) return false;
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
        bool Available(UnitState u) => u != null && u.Source.Owner == _activeSide && u.CanAct;

        /// <summary>按来源卡种号查询共享配置。输入：单位；输出：卡种描述。</summary>
        CardArchetype Type(UnitState u) => _config.Cards[u.Source.TypeId];

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
        void Emit(string kind, string message, long id = 0) { var e = new GameEvent(kind, message, id); _pendingEvents.Add(e); _log.Add(e); if (_log.Count > _logLimit) _log.RemoveAt(0); }

        /// <summary>阵营显示名。输入：阵营；输出：中文名称。</summary>
        static string SideName(Side side) => side == Side.Player ? "玩家" : "Boss";
    }
}
