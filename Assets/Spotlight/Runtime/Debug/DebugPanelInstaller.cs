// 仅编辑器：把调试配置面板挂到运行时引导器上。
// 为什么需要这一层：调试面板已移入仅编辑器程序集（Spotlight.Runtime.Debug），
// 运行层程序集不能反向引用它，因此只能由本文件在编辑器加载时把实现注册到 GameBootstrap 暴露的空委托上。
// 正式包里整个程序集不参与编译，钩子永远是 null，也不会在类型表里留下调试类型名。
#if UNITY_EDITOR
using UnityEditor;

namespace Spotlight
{
    /// <summary>
    /// 调试面板接线器：编辑器加载时注册挂载与查询钩子。
    /// 输入：无（由 <see cref="InitializeOnLoadAttribute"/> 触发）；输出：无（副作用是设置 GameBootstrap 的静态钩子）。
    /// 为什么用 InitializeOnLoad：每次域重载（含进入 Play）都会重新注册，避免静态委托在重载后变空。
    /// </summary>
    [InitializeOnLoad]
    internal static class DebugPanelInstaller
    {
        /// <summary>注册钩子。输入：无；输出：无。</summary>
        static DebugPanelInstaller()
        {
            GameBootstrap.DebugPanelAttach = Attach;
            GameBootstrap.DebugPanelOpenQuery = QueryOpen;
        }

        /// <summary>
        /// 在引导器所在对象上创建并绑定调试面板。
        /// 输入：对局引导器；输出：无。
        /// 为什么不写进场景：调试面板只在编辑器中存在，写进场景会让正式包留下无脚本组件。
        /// </summary>
        static void Attach(GameBootstrap bootstrap)
        {
            if (bootstrap == null) return;
            var panel = bootstrap.GetComponent<ConfigDebugPanel>();
            if (panel == null) panel = bootstrap.gameObject.AddComponent<ConfigDebugPanel>();
            panel.Bind(bootstrap);
        }

        /// <summary>
        /// 查询调试面板是否打开。
        /// 输入：对局引导器；输出：面板存在且打开时为 true。
        /// 用于暂停面板关闭后恢复模态闸门，避免把已打开的调试面板“解锁”。
        /// </summary>
        static bool QueryOpen(GameBootstrap bootstrap)
        {
            if (bootstrap == null) return false;
            var panel = bootstrap.GetComponent<ConfigDebugPanel>();
            return panel != null && panel.IsOpen;
        }
    }
}
#endif
