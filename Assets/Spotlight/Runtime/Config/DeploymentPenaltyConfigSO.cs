// 一次性部署减攻组件配置 SO：在场期间可使用一次的减攻量。
// 生成方式：由编辑器 Excel 编译器作为卡种定义的子资产创建，Inspector 只读。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 一次性部署减攻能力的只读配置资产。
    /// 输入：编译期写入的 deploymentAttackReduction；输出：实现 IDeploymentPenaltyConfig 供规则内核消费。
    /// 为什么单独成资产：只有启用 deploymentPenaltyComponent 的卡种才挂载它，未启用的卡种在规则层得到 null，
    /// 因此“是否具有该能力”来自配置而不是猜测；减攻量也只有这一份来源，规则代码不写死数字。
    /// </summary>
    [GeneratedConfig]
    public sealed class DeploymentPenaltyConfigSO : ScriptableObject, IDeploymentPenaltyConfig
    {
        /// <summary>每次触发的减攻量（正整数）。</summary>
        [SerializeField] int _attackReduction = 1;

        /// <inheritdoc />
        public int AttackReduction => _attackReduction;

        /// <summary>编译期写入减攻量。输入：已通过校验的正整数；输出：无（原地更新本资产）。</summary>
        public void EditorApply(int attackReduction) { _attackReduction = attackReduction; }
    }
}
