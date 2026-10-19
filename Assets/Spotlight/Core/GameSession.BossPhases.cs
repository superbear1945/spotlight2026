using System;
using System.Collections.Generic;
using System.Linq;

namespace Spotlight
{
    /// <summary>Boss 阶段结算定位入口。与 GameSession 共用唯一状态；状态机只通过这些内部方法调度技能。</summary>
    public sealed partial class GameSession
    {
        /// <summary>阶段策略与转换，不保存第二套棋盘。</summary>
        readonly BossPhaseStateMachine _bossPhases = new BossPhaseStateMachine();
        /// <summary>两艘指定战舰的生命周期记录；区别于固定兵位。</summary>
        readonly List<BossWarshipState> _bossWarships = new List<BossWarshipState>();
        /// <summary>双方已经开始的行动回合次数；生成资格使用所属阵营序号而不是完整回合号。</summary>
        readonly long[] _ownerTurns = new long[2];

        /// <summary>输入为当前 Boss 生命，输出为是否达到一阶段阈值；由状态机在伤害结算后查询。</summary>
        internal bool ShouldEnterBossPhaseTwo => _homes.Single(h => h.Owner == Side.Boss).Hp <= Rules.BossPhaseTwoHp;
        /// <summary>只有两艘均真实阵亡才完成二阶段；无记录或满盘待部署绝不算完成。</summary>
        internal bool AllBossWarshipsDefeated => _bossWarships.Count == 2 && _bossWarships.All(s => s.Status == BossWarshipStatus.Defeated);

        /// <summary>阶段转换公共收尾。输入为新阶段；输出为清除旧机会、阶段日志与独立表现帧。</summary>
        internal void OnBossPhaseChanged(BossPhase phase)
        {
            _bossBombPending = false;
            _bossSpawnerPending = false;
            Emit("boss-phase", $"Boss 进入第 {(int)phase + 1} 阶段" + (phase == BossPhase.PhaseTwo ? "，锁血且无法被攻击" : "，解除锁血"));
            CapturePresentationFrame();
        }

        /// <summary>二阶段 Enter 调用一次。根据三等分点和玩家视角列创建两条待部署记录，并立即自动部署。</summary>
        internal void CreateBossWarships()
        {
            for (var index = 1; index <= 2; index++)
                _bossWarships.Add(new BossWarshipState
                {
                    Index = index,
                    PreferredCell = new Cell((int)Math.Floor((Rules.Rows - 1) * index / 3.0 + .5),
                        Rules.PlayerOnLeft ? Rules.BossWarshipColumn - 1 : Rules.Columns - Rules.BossWarshipColumn),
                    Status = BossWarshipStatus.Pending
                });
            DeployPendingWarships();
        }

        /// <summary>
        /// 输入为待部署记录与当前棋盘；输出为自动部署或等待事件，不占放牌次数、不覆盖占位、不补回阵亡战舰。
        /// 先为所有待部署战舰保留可用指定格，再按距离/行/玩家视角列寻找替代格，防止第一艘抢走第二艘指定格。
        /// </summary>
        internal void DeployPendingWarships()
        {
            var pending = _bossWarships.Where(s => s.Status == BossWarshipStatus.Pending).ToArray();
            var destinations = new Dictionary<int, Cell>();
            var reserved = new HashSet<Cell>();
            foreach (var ship in pending)
                if (!Occupied(ship.PreferredCell) && reserved.Add(ship.PreferredCell)) destinations.Add(ship.Index, ship.PreferredCell);
            foreach (var ship in pending.Where(s => !destinations.ContainsKey(s.Index)))
            {
                var cells = Cells().Where(p => !Occupied(p) && !reserved.Contains(p))
                    .OrderBy(p => p.Distance(ship.PreferredCell)).ThenBy(p => p.Row).ThenBy(PlayerColumn).ToArray();
                if (cells.Length == 0) continue;
                destinations.Add(ship.Index, cells[0]);
                reserved.Add(cells[0]);
            }
            foreach (var ship in pending)
            {
                if (!destinations.TryGetValue(ship.Index, out var cell))
                {
                    Emit("warship-wait", $"战舰 {ship.Index} 等待棋盘空位");
                    CapturePresentationFrame();
                    continue;
                }
                var unit = Place(NewCard(Rules.BossWarshipCardType, Side.Boss), cell, false, null);
                ship.UnitId = unit.Id;
                ship.Status = BossWarshipStatus.Alive;
                Emit("warship-deploy", $"战舰 {ship.Index} 自动部署至 {cell}", unit.Id);
                CapturePresentationFrame();
            }
        }

        /// <summary>
        /// 三阶段 Enter 的一次性技能。输入为当前 Boss 家位置和配置引用；输出为两列护卫舰及死亡、部署表现帧。
        /// 先固定目标并清理全部占位单位，再生成，避免新护卫舰参与同一批顶替的死亡被动。
        /// 双方的家永不顶替，越界前列直接跳过；地面炸弹不占格，也不被清理。
        /// </summary>
        internal void CreateBossGuards()
        {
            var home = _homes.Single(h => h.Owner == Side.Boss);
            var columns = new[] { home.Position.Col + (Rules.PlayerOnLeft ? -1 : 1), home.Position.Col };
            var types = new[] { Rules.BossPhaseThreeFrontCardType, Rules.BossPhaseThreeRearCardType };
            var destinations = new List<KeyValuePair<Cell, string>>();
            for (var index = 0; index < columns.Length; index++)
            {
                if (columns[index] < 0 || columns[index] >= Rules.Columns) continue;
                for (var row = 0; row < Rules.Rows; row++)
                {
                    var cell = new Cell(row, columns[index]);
                    if (_homes.Any(h => h.Position == cell)) continue;
                    destinations.Add(new KeyValuePair<Cell, string>(cell, types[index]));
                }
            }
            foreach (var destination in destinations)
            {
                var occupied = _units.FirstOrDefault(u => u.Position == destination.Key);
                if (occupied == null) continue;
                Remove(occupied, Removal.Death);
                CapturePresentationFrame();
            }
            foreach (var destination in destinations)
            {
                var guard = Place(NewCard(destination.Value, Side.Boss), destination.Key, false, null);
                guard.AiTargetPolicy = AiTargetPolicy.BossGuard;
                Emit("guard-deploy", $"三阶段护卫舰 {Type(guard).Name} 自动部署至 {destination.Key}", guard.Id);
                CapturePresentationFrame();
            }
        }

        /// <summary>
        /// 护卫舰的统一索敌顺序。输入为已通过权限/射程或可达性过滤的目标；输出为稳定有序目标。
        /// 攻击类优先按到 Boss 家的当前距离、当前攻击力排序，其后为家、特殊、资源；同分按位置和身份排序。
        /// 攻击与追击共享此入口，避免移动时重新使用普通 AI 的最近目标或斩杀优先级。
        /// </summary>
        IOrderedEnumerable<Target> OrderGuardTargets(IEnumerable<Target> targets)
        {
            var home = _homes.Single(h => h.Owner == Side.Boss).Position;
            return targets.OrderBy(t => t.Unit != null && Type(t.Unit).Category == CardCategory.Attack ? 0 :
                    t.Home != null ? 1 : Type(t.Unit).Category == CardCategory.Special ? 2 : 3)
                .ThenBy(t => t.Unit != null && Type(t.Unit).Category == CardCategory.Attack ? t.Position.Distance(home) : 0)
                .ThenByDescending(t => t.Unit != null && Type(t.Unit).Category == CardCategory.Attack ? t.Unit.Attack : 0)
                .ThenBy(t => t.Position.Row).ThenBy(t => PlayerColumn(t.Position)).ThenBy(t => t.Id);
        }

        /// <summary>攻击目标资格共用入口。输入为攻击者和目标；输出为资格，不包含距离；AI、手动命令和高亮统一调用。</summary>
        bool CanTarget(UnitState attacker, Target target) =>
            (target.Home == null || target.Home.Owner != Side.Boss || _bossPhases.Phase != BossPhase.PhaseTwo) &&
            (target.Unit == null || attacker.CanAttackResources || Type(target.Unit).Category != CardCategory.Resource);
    }
}
