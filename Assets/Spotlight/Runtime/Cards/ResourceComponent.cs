// 资源组件：持有资源配置 SO，提供产出、维持费与支付状态入口。
// 重要约束：不缓存当前资源；资源总量属于对局状态（GameSession），
// 这里只暴露“本回合是否已支付维持费”等与本单位相关的只读查询。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 卡牌的资源能力组件。
    /// 输入：生成时注入的 ResourceConfigSO，运行时绑定 UnitState；
    /// 输出：Produce/Upkeep/UpkeepPaid 只读查询。
    /// 为什么需要它：资源卡与特殊卡都可能需要维持费，规则层通过接口读取，
    /// 组件只作为 Prefab 上的可视化与查询入口，不参与结算。
    /// </summary>
    public sealed class ResourceComponent : MonoBehaviour
    {
        /// <summary>资源配置资产，由生成器写入。</summary>
        [SerializeField] ResourceConfigSO _config;
        /// <summary>当前绑定单位；手牌状态下为 null。</summary>
        UnitState _state;

        /// <summary>资源配置资产（可能为 null）。</summary>
        public ResourceConfigSO Config => _config;
        /// <summary>每个己方回合产出的资源。</summary>
        public int Produce => _config != null ? _config.Produce : 0;
        /// <summary>每个己方回合需要支付的维持费。</summary>
        public int Upkeep => _config != null ? _config.Upkeep : 0;
        /// <summary>本回合维持费是否已支付；未绑定时视为已支付（手牌或未上场）。</summary>
        public bool UpkeepPaid => _state == null || _state.UpkeepPaid;

        /// <summary>生成期注入配置。输入：资源配置 SO；输出：无。</summary>
        public void Configure(ResourceConfigSO config) { _config = config; }

        /// <summary>绑定本局单位状态。输入：UnitState（可为 null）；输出：无。只保存引用，不复制数值。</summary>
        public void Bind(UnitState state) { _state = state; }
    }
}
