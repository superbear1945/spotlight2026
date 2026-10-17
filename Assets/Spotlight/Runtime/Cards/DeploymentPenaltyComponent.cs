// 一次性部署减攻组件：持有减攻配置，并绑定场上单位的“待触发 / 已消耗”状态。
// 重要约束：本组件不保存任何权威战斗数据。减攻量与剩余次数只存在于 UnitState，
// 组件仅持有该状态的引用并实时读取，避免“界面显示”和“规则结算”分叉。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 卡牌的一次性部署减攻能力组件。
    /// 输入：生成时注入的 DeploymentPenaltyConfigSO，运行时绑定 UnitState；
    /// 输出：ReduceAmount / Pending / Consumed 等只读查询，供卡面与详情展示。
    /// 为什么需要它：能力是“按配置启用的可复用能力”，规则层只认识 IDeploymentPenaltyConfig，
    /// Prefab 侧需要一个可序列化的查询入口；组件本身不参与结算，也不订阅任何事件。
    /// </summary>
    public sealed class DeploymentPenaltyComponent : MonoBehaviour
    {
        /// <summary>减攻配置资产，由生成器写入；未启用该能力的卡种为 null。</summary>
        [SerializeField] DeploymentPenaltyConfigSO _config;
        /// <summary>当前绑定单位；手牌状态下为 null。</summary>
        UnitState _state;

        /// <summary>减攻配置资产（可能为 null，表示该卡种没有这项能力）。</summary>
        public DeploymentPenaltyConfigSO Config => _config;
        /// <summary>减攻量：已绑定时取单位副本（配置值在部署那一刻的快照），否则取配置值。</summary>
        public int ReduceAmount => _state != null ? _state.DeploymentAttackReduction : _config != null ? _config.AttackReduction : 0;
        /// <summary>是否还有一次未触发的减攻：未绑定时按“手牌未上场”返回 false。</summary>
        public bool Pending => _state != null && _state.DeploymentPenaltyPending;
        /// <summary>是否已经消耗掉本次在场期间的效果：只有绑定单位且配置了能力时才可能为 true。</summary>
        public bool Consumed => _state != null && _state.DeploymentAttackReduction > 0 && !_state.DeploymentPenaltyPending;

        /// <summary>生成期注入配置。输入：减攻配置 SO（可为 null）；输出：无。</summary>
        public void Configure(DeploymentPenaltyConfigSO config) { _config = config; }

        /// <summary>绑定本局单位状态。输入：UnitState（可为 null 表示不是棋盘单位）；输出：无。只保存引用。</summary>
        public void Bind(UnitState state) { _state = state; }

        /// <summary>读取当前绑定的单位状态。输入：无；输出：UnitState，未绑定时为 null。</summary>
        public UnitState State => _state;
    }
}
