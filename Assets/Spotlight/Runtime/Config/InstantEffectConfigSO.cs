// 即时卡效果配置资产（战时储备这类“部署即结算、结算即退场”的功能卡）。
// 生成方式：编译管线为启用该能力的卡种生成 <id>_Instant 子资产，挂在卡种定义资产上；
// 数值只来自 Excel，规则内核通过 IInstantEffectConfig 只读访问，不引用 ScriptableObject。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 即时卡效果的只读配置资产。
    /// 输入：编译期写入的一次性资源收益；输出：规则内核读取的收益值。
    /// 为什么需要它：收益必须来自 Excel 而不是代码常量；与其它能力同形（子资产 + 只读接口），
    /// 因此校验、发布与回滚逻辑完全复用。是否退场由 Cards 表的 leaveAfterDeploy 决定。
    /// </summary>
    [GeneratedConfig]
    public sealed class InstantEffectConfigSO : ScriptableObject, IInstantEffectConfig
    {
        /// <summary>结算时立即获得的资源点数。</summary>
        [SerializeField] int _gainResource;

        /// <inheritdoc />
        public int GainResource => _gainResource;

        /// <summary>
        /// 编译期写入配置。
        /// 输入：已校验的一次性资源收益；输出：无。
        /// 只允许编辑器编译器调用；运行时不修改已发布资产。
        /// </summary>
        public void EditorApply(int gainResource)
        {
            _gainResource = gainResource;
        }
    }
}
