using UnityEngine;

namespace Spotlight
{
    /// <summary>刷怪能力只读配置子资产。输入为 Excel 编译结果，输出为 Core 和表现组件共用的卡种引用。</summary>
    [GeneratedConfig]
    public sealed class SpawnConfigSO : ScriptableObject, ISpawnConfig
    {
        /// <summary>稳定生成卡种 ID；不保存来源实例或计时。</summary>
        [SerializeField] string _spawnCardType;
        /// <inheritdoc />
        public string SpawnCardType => _spawnCardType;
        /// <summary>仅供编辑器发布器写入已校验引用；输入为卡种 ID，输出为更新后的子资产。</summary>
        public void EditorApply(string spawnCardType) { _spawnCardType = spawnCardType; }
    }
}
