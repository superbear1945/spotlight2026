using UnityEngine;

namespace Spotlight
{
    /// <summary>刷怪能力表现桥接。只读配置与单位快照，实际刷怪由 Core 结算；不在 Update 或动画回调创建单位。</summary>
    public sealed class SpawnComponent : MonoBehaviour
    {
        /// <summary>编译器注入的共享只读配置。</summary>
        [SerializeField] SpawnConfigSO _config;
        /// <summary>当前绑定的单位快照，手牌时为空。</summary>
        UnitState _state;
        /// <summary>生成配置，供 Prefab 门禁与详情读取。</summary>
        public SpawnConfigSO Config => _config;
        /// <summary>当前单位快照，不保存第二份计时。</summary>
        public UnitState State => _state;
        /// <summary>注入生成配置；输入为 SO，输出为组件引用更新。</summary>
        public void Configure(SpawnConfigSO config) { _config = config; }
        /// <summary>绑定当前快照或空值；输入为单位，输出为只读表现上下文。</summary>
        public void Bind(UnitState state) { _state = state; }
    }
}
