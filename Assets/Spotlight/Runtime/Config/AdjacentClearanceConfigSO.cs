// 四邻空格限制能力配置资产（后羿-II综合舰）。
// 生成方式：编译管线为启用该能力的卡种生成 <id>_AdjacentClearance 子资产，挂在卡种定义资产上；
// 阈值只来自 Excel，规则内核通过 IAdjacentClearanceConfig 只读访问，不引用 ScriptableObject。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 四邻空格限制的只读配置资产。
    /// 输入：编译期写入的最少空邻格数；输出：规则内核读取的 1～4 阈值。
    /// 为什么需要它：阈值必须来自 Excel 而不是代码常量；与部署减攻/入场标记同形（子资产 + 只读接口），
    /// 校验与发布逻辑完全复用，也便于调试面板按启用状态清空或生成。
    /// </summary>
    [GeneratedConfig]
    public sealed class AdjacentClearanceConfigSO : ScriptableObject, IAdjacentClearanceConfig
    {
        /// <summary>上下左右四个方向中至少需要为空的数量（1～4）。</summary>
        [SerializeField] int _minEmptyNeighbors;

        /// <inheritdoc />
        public int MinEmptyNeighbors => _minEmptyNeighbors;

        /// <summary>
        /// 编译期写入配置。
        /// 输入：已校验的 1～4 阈值；输出：无。
        /// 只允许编辑器编译器调用；运行时不修改已发布资产。
        /// </summary>
        public void EditorApply(int minEmptyNeighbors)
        {
            _minEmptyNeighbors = minEmptyNeighbors;
        }
    }
}
