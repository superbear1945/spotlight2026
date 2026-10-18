// 伤害标记能力配置资产（SNP-EU）。
// 生成方式：编译管线为启用该能力的卡种生成 <id>_DamageMark 子资产，挂在卡种定义资产上；
// 每层追加伤害只来自 Excel，规则内核通过 IDamageMarkConfig 只读访问，不引用 ScriptableObject。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 伤害标记的只读配置资产。
    /// 输入：编译期写入的每层追加伤害；输出：规则内核读取的追加伤害值（正整数）。
    /// 为什么需要它：每层追加值必须来自 Excel 而不是代码常量；也与“入场标记”彻底分开，
    /// 避免两套机制因为共用配置字段而互相影响。
    /// </summary>
    [GeneratedConfig]
    public sealed class DamageMarkConfigSO : ScriptableObject, IDamageMarkConfig
    {
        /// <summary>每层标记在之后每次直接受伤时追加的伤害值。</summary>
        [SerializeField] int _damagePerStack;

        /// <inheritdoc />
        public int DamagePerStack => _damagePerStack;

        /// <summary>
        /// 编译期写入配置。
        /// 输入：已校验的每层追加伤害；输出：无。
        /// 只允许编辑器编译器调用；运行时不修改已发布资产。
        /// </summary>
        public void EditorApply(int damagePerStack)
        {
            _damagePerStack = damagePerStack;
        }
    }
}
