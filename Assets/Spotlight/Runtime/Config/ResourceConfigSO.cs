// 资源组件配置 SO：每回合产出与维持费。
// 生成方式：由编辑器 Excel 编译器作为卡种定义的子资产创建，Inspector 只读。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 资源能力的只读配置资产。
    /// 输入：编译期写入的 produce/upkeep；输出：实现 IResourceConfig。
    /// 为什么单独成资产：只有启用 resourceComponent 的卡种才会挂载它，
    /// 未启用的卡种在规则层得到 null，因此“资源卡”的身份来自配置而不是猜测。
    /// </summary>
    [GeneratedConfig]
    public sealed class ResourceConfigSO : ScriptableObject, IResourceConfig
    {
        /// <summary>每个己方回合开始时产出的资源。</summary>
        [SerializeField] int _produce;
        /// <summary>每个己方回合开始时需要支付的维持费。</summary>
        [SerializeField] int _upkeep;

        /// <inheritdoc />
        public int Produce => _produce;
        /// <inheritdoc />
        public int Upkeep => _upkeep;

        /// <summary>编译期写入数值。输入：产出与维持费；输出：无。</summary>
        public void EditorApply(int produce, int upkeep) { _produce = produce; _upkeep = upkeep; }
    }
}
