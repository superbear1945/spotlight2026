// 击杀恢复行动能力配置资产（维京长船-2056）。
// 生成方式：编译管线为启用该能力的卡种生成 <id>_KillAction 子资产，挂在卡种定义资产上；
// 该能力没有数值参数，子资产存在本身即“已启用”，规则内核通过 IKillActionConfig 只读判定。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 击杀恢复行动的只读配置资产（空标记）。
    /// 输入：无需任何数值；输出：非 null 即表示该卡种拥有“直接攻击击杀后可再次行动”的能力。
    /// 为什么做成空资产：能力没有回合上限、累计次数等可填参数，多序列化一个恒为 true 的布尔值
    /// 只会制造两处可能不一致的启用状态；沿用“子资产存在即启用”的现有约定最不容易出错。
    /// </summary>
    [GeneratedConfig]
    public sealed class KillActionConfigSO : ScriptableObject, IKillActionConfig
    {
        /// <summary>
        /// 编译期写入（无字段）。输入：无；输出：无。
        /// 保留同名入口只为让发布器对所有能力子资产走同一套调用方式，避免特例分支。
        /// </summary>
        public void EditorApply()
        {
        }
    }
}
