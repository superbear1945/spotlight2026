// 战斗组件配置 SO：生命、攻击、射程与移动距离。
// 生成方式：由编辑器 Excel 编译器作为卡种定义的子资产创建，Inspector 只读。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 战斗能力的只读配置资产。
    /// 输入：编译期写入的 hp/atk/range/moveDistance；输出：实现 ICombatConfig 供规则内核与战斗组件消费。
    /// 为什么使用 ScriptableObject：多个同名单位共享同一份不可变配置，避免每个实例复制数值；
    /// 同时规则内核只依赖 ICombatConfig 接口，测试可以用普通 C# 对象替代。
    /// </summary>
    [GeneratedConfig]
    public sealed class CombatConfigSO : ScriptableObject, ICombatConfig
    {
        /// <summary>初始生命与上限（正整数）。</summary>
        [SerializeField] int _hp = 1;
        /// <summary>攻击力，0 表示无攻击能力。</summary>
        [SerializeField] int _attack;
        /// <summary>曼哈顿射程，0 表示不能攻击。</summary>
        [SerializeField] int _range;
        /// <summary>单次移动最大格数，0 表示不能移动。</summary>
        [SerializeField] int _moveDistance;

        /// <inheritdoc />
        public int Hp => _hp;
        /// <inheritdoc />
        public int Attack => _attack;
        /// <inheritdoc />
        public int Range => _range;
        /// <inheritdoc />
        public int MoveDistance => _moveDistance;

        /// <summary>
        /// 编译期写入数值。
        /// 输入：已通过校验的四个数值；输出：无（原地更新本资产）。
        /// 为什么保留写入口：编译产物必须可被“全部重新编译”覆盖，而运行时不得调用。
        /// </summary>
        public void EditorApply(int hp, int attack, int range, int moveDistance)
        {
            _hp = hp;
            _attack = attack;
            _range = range;
            _moveDistance = moveDistance;
        }
    }
}
