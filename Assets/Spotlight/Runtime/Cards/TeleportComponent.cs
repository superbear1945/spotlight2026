// 传送组件：持有传送配置 SO，提供星门四邻部署扩展的开关查询。
// 首版只实现“允许己方攻击卡部署在其上下左右相邻空格”，不支持瞬移或斜向。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 卡牌的传送能力组件。
    /// 输入：生成时注入的 TeleportConfigSO；输出：PermitsAttackDeploy 只读查询。
    /// 为什么保留独立组件：星门是唯一使用该能力的卡种，但规则层只认识接口，
    /// 未来扩展同类单位无需修改内核，也不会出现按卡名分支的代码。
    /// </summary>
    public sealed class TeleportComponent : MonoBehaviour
    {
        /// <summary>传送配置资产，由生成器写入。</summary>
        [SerializeField] TeleportConfigSO _config;

        /// <summary>传送配置资产（可能为 null）。</summary>
        public TeleportConfigSO Config => _config;
        /// <summary>是否允许己方攻击卡部署在该单位四邻。</summary>
        public bool PermitsAttackDeploy => _config != null && _config.PermitsAttackDeploy;

        /// <summary>生成期注入配置。输入：传送配置 SO；输出：无。</summary>
        public void Configure(TeleportConfigSO config) { _config = config; }
    }
}
