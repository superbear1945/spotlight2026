// 入场标记能力配置资产（星棹收集系统）。
// 生成方式：编译管线为启用该能力的卡种生成 <id>_MarkOnEnter 子资产，挂在卡种定义资产上；
// 数值只来自 Excel，规则内核通过 IMarkOnEnterConfig 只读访问，不引用 ScriptableObject。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 入场标记的只读配置资产。
    /// 输入：编译期写入的标记半径与阵亡奖励；输出：规则内核读取的半径与奖励。
    /// 为什么需要它：标记是“按配置启用的能力”，数值必须来自 Excel 而不是代码常量；
    /// 与一次性部署减攻同形（子资产 + 只读接口），因此校验、发布与回滚逻辑可以完全复用。
    /// </summary>
    [GeneratedConfig]
    public sealed class MarkOnEnterConfigSO : ScriptableObject, IMarkOnEnterConfig
    {
        /// <summary>标记半径：1 表示以自身为中心的 3×3（不含自己）。</summary>
        [SerializeField] int _radius;
        /// <summary>被标记单位阵亡时给标记者阵营的资源点数。</summary>
        [SerializeField] int _reward;

        /// <inheritdoc />
        public int Radius => _radius;
        /// <inheritdoc />
        public int Reward => _reward;

        /// <summary>
        /// 编译期写入配置。
        /// 输入：已校验的半径与奖励；输出：无。
        /// 只允许编辑器编译器调用；运行时不修改已发布资产。
        /// </summary>
        public void EditorApply(int radius, int reward)
        {
            _radius = radius;
            _reward = reward;
        }
    }
}
