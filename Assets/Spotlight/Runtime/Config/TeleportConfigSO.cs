// 传送组件配置 SO：仅表达“星门四邻部署扩展”。
// 生成方式：由编辑器 Excel 编译器作为卡种定义的子资产创建，Inspector 只读。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 传送能力的只读配置资产。
    /// 输入：编译期写入的 permitsAttackDeploy；输出：实现 ITeleportConfig。
    /// 为什么只有一个字段：已确认首版只实现“允许己方攻击卡部署在其四邻”，
    /// 不支持瞬移、穿墙或斜向，因此不引入多余开关，避免出现文档未定义的行为。
    /// </summary>
    [GeneratedConfig]
    public sealed class TeleportConfigSO : ScriptableObject, ITeleportConfig
    {
        /// <summary>是否允许己方攻击卡部署在该单位上下左右相邻的空格。</summary>
        [SerializeField] bool _permitsAttackDeploy;

        /// <inheritdoc />
        public bool PermitsAttackDeploy => _permitsAttackDeploy;

        /// <summary>编译期写入数值。输入：是否允许四邻部署扩展；输出：无。</summary>
        public void EditorApply(bool permitsAttackDeploy) { _permitsAttackDeploy = permitsAttackDeploy; }
    }
}
