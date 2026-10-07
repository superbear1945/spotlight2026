// 战斗组件：持有战斗配置 SO，并作为“生命/攻击/射程/移动”这些属性的统一读取入口。
// 重要约束：本组件不重复保存战斗状态。当前生命与行动状态只存在于 GameSession 的 UnitState 中，
// 组件仅保存该状态的引用，通过属性实时读取，避免“界面数值”和“规则数值”分叉。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 卡牌的战斗能力组件。
    /// 输入：生成时注入的 CombatConfigSO，运行时绑定 UnitState；
    /// 输出：Hp/Attack/Range/MoveDistance/CanAct 等只读查询，供 Card 显示与反馈使用。
    /// 为什么所有属性都走“配置 + 状态引用”而不是缓存：规则内核是唯一真相，
    /// 一旦组件缓存当前生命，动画或 UI 延迟就会与结算结果不一致。
    /// </summary>
    public sealed class CombatComponent : MonoBehaviour
    {
        /// <summary>战斗配置资产，由生成器写入。</summary>
        [SerializeField] CombatConfigSO _config;
        /// <summary>当前绑定单位；手牌状态下为 null。</summary>
        UnitState _state;

        /// <summary>战斗配置资产（可能为 null，表示未配置）。</summary>
        public CombatConfigSO Config => _config;
        /// <summary>配置的生命上限；无配置时返回 0。</summary>
        public int MaxHp => _config != null ? _config.Hp : 0;
        /// <summary>当前生命：已绑定单位时取实时值，否则回退到上限。</summary>
        public int CurrentHp => _state != null ? _state.Hp : MaxHp;
        /// <summary>攻击力。</summary>
        public int Attack => _config != null ? _config.Attack : 0;
        /// <summary>曼哈顿射程。</summary>
        public int Range => _config != null ? _config.Range : 0;
        /// <summary>单次移动最大格数。</summary>
        public int MoveDistance => _config != null ? _config.MoveDistance : 0;
        /// <summary>本回合是否仍可行动（未行动且维持费已付）。</summary>
        public bool CanAct => _state == null || _state.CanAct;

        /// <summary>
        /// 生成期注入配置。
        /// 输入：编译器生成的战斗配置 SO；输出：无。
        /// </summary>
        public void Configure(CombatConfigSO config) { _config = config; }

        /// <summary>
        /// 绑定本局单位状态。
        /// 输入：UnitState（可为 null，表示该卡牌当前不是棋盘单位）；输出：无。
        /// 只保存引用，不复制任何数值。
        /// </summary>
        public void Bind(UnitState state) { _state = state; }

        /// <summary>
        /// 读取当前绑定的单位状态。
        /// 输入：无；输出：UnitState，未绑定时为 null。
        /// 用途：Card 在刷新时判断行动状态与生命，避免各自持有第二份引用。
        /// </summary>
        public UnitState State => _state;
    }
}
