// 仅编辑器：把调试配置面板接到运行时引导器上。
// 为什么需要这一层：调试面板已移入仅编辑器程序集（Spotlight.Runtime.Debug），
// 运行层程序集不能反向引用它，因此只能由本文件在编辑器加载时把实现注册到 GameBootstrap 暴露的空委托上。
// 正式包里整个程序集不参与编译，钩子永远是 null，也不会在类型表里留下调试类型名。
#if UNITY_EDITOR
using System.Runtime.CompilerServices;
using UnityEditor;

namespace Spotlight
{
    /// <summary>
    /// 调试面板接线器：编辑器加载时注册挂载与查询钩子，并按引导器缓存面板实例。
    /// 输入：无（由 <see cref="InitializeOnLoadAttribute"/> 触发）；输出：无（副作用是设置 GameBootstrap 的静态钩子）。
    /// 为什么用 InitializeOnLoad：每次域重载（含进入 Play）都会重新注册，避免静态委托在重载后变空。
    /// 为什么按引导器缓存：面板是普通类而不是组件，重复绑定会重复订阅事件并留下旧界面引用。
    /// </summary>
    [InitializeOnLoad]
    public static class DebugPanelInstaller
    {
        /// <summary>引导器 → 面板实例。用弱引用键，场景卸载后可被回收，不需要额外的销毁钩子。</summary>
        static readonly ConditionalWeakTable<GameBootstrap, ConfigDebugPanel> Panels =
            new ConditionalWeakTable<GameBootstrap, ConfigDebugPanel>();

        /// <summary>注册钩子。输入：无；输出：无。</summary>
        static DebugPanelInstaller()
        {
            GameBootstrap.DebugPanelAttach = Attach;
            GameBootstrap.DebugPanelOpenQuery = QueryOpen;
        }

        /// <summary>
        /// 为指定引导器创建（或复用）调试面板并绑定界面。
        /// 输入：对局引导器；输出：无。
        /// 为什么不写进场景：调试面板只在编辑器中存在，写进场景会让正式包留下无脚本组件。
        /// </summary>
        static void Attach(GameBootstrap bootstrap)
        {
            if (bootstrap == null) return;
            var panel = Panels.GetValue(bootstrap, _ => new ConfigDebugPanel());
            panel.Bind(bootstrap);
        }

        /// <summary>
        /// 打开指定引导器的配置面板（供编辑器菜单“在运行中打开配置面板”调用）。
        /// 输入：对局引导器；输出：无（引导器为空时忽略）。
        /// 为什么需要：面板是普通类，不再能靠 FindObjectOfType 找到，菜单必须走同一套接线器。
        /// </summary>
        public static void Open(GameBootstrap bootstrap)
        {
            if (bootstrap == null) return;
            Attach(bootstrap);
            if (Panels.TryGetValue(bootstrap, out var panel)) panel.Open();
        }

        /// <summary>
        /// 查询调试面板是否打开。
        /// 输入：对局引导器；输出：面板存在且打开时为 true。
        /// 用于暂停面板关闭后恢复模态闸门，避免把已打开的调试面板“解锁”。
        /// </summary>
        static bool QueryOpen(GameBootstrap bootstrap)
        {
            return bootstrap != null && Panels.TryGetValue(bootstrap, out var panel) && panel.IsOpen;
        }
    }
}
#endif
