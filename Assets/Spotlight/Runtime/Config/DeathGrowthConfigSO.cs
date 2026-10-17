// 死亡加攻能力配置资产（力场-I）。
// 生成方式：编译管线为启用该能力的卡种生成 <id>_DeathGrowth 子资产，挂在卡种定义资产上；
// 数值只来自 Excel，规则内核通过 IDeathGrowthConfig 只读访问，不引用 ScriptableObject。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 死亡加攻的只读配置资产。
    /// 输入：编译期写入的每次加成与次数上限；输出：规则内核读取的加成与上限。
    /// 为什么需要它：加成幅度与上限必须来自 Excel 而不是代码常量；
    /// 与一次性部署减攻/入场标记同形（子资产 + 只读接口），校验与发布逻辑完全复用。
    /// </summary>
    [GeneratedConfig]
    public sealed class DeathGrowthConfigSO : ScriptableObject, IDeathGrowthConfig
    {
        /// <summary>全棋盘任一单位因战斗阵亡时增加的攻击力。</summary>
        [SerializeField] int _perKill;
        /// <summary>最多触发次数；达到后不再增加攻击力。</summary>
        [SerializeField] int _limit;

        /// <inheritdoc />
        public int PerKill => _perKill;
        /// <inheritdoc />
        public int Limit => _limit;

        /// <summary>
        /// 编译期写入配置。
        /// 输入：已校验的每次加成与次数上限；输出：无。
        /// 只允许编辑器编译器调用；运行时不修改已发布资产。
        /// </summary>
        public void EditorApply(int perKill, int limit)
        {
            _perKill = perKill;
            _limit = limit;
        }
    }
}
