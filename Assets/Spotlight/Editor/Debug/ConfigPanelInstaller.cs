// 调试面板安装器：把配置面板挂到对局引导器上，并把 HUD 的调试入口接到面板上。
// 为什么需要单独一个类：面板类型在 Spotlight.Editor 程序集里（仅编辑器编译，正式包不包含），
// 而运行层的 GameBootstrap / HudView 不能引用编辑器程序集（依赖方向单向：Tests → Editor → Runtime → Core）。
// 因此运行层只留一个回调点（DebugSupport.PanelInstaller），由这里的 [InitializeOnLoad] 在编辑器加载时注册。
using System;
using UnityEditor;
using UnityEngine;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// 编辑器侧的调试面板安装器。
    /// 输入：进入 Play 的 <see cref="GameBootstrap"/>（由运行层回调传入）；
    /// 输出：挂好的 <see cref="ConfigDebugPanel"/> 组件，以及“齿轮菜单 / 快捷按钮 → 打开面板”的接线。
    /// 为什么用 [InitializeOnLoad]：编辑器一加载程序集就把回调交给运行层，早于任何 Play 会话，
    /// 因此不需要在场景里序列化任何调试组件（场景里挂组件会让类名随包发布）。
    /// </summary>
    [InitializeOnLoad]
    public static class ConfigPanelInstaller
    {
        /// <summary>当前会话的面板实例；面板是普通类，必须由这里持有引用避免被回收。</summary>
        static ConfigDebugPanel _panel;

        /// <summary>
        /// 静态构造：把安装动作注册到运行层的回调点。
        /// 输入：无；输出：无。
        /// 说明：每次域重载都会重新注册；正式包里 DebugSupport.PanelInstaller 不存在（被 #if 掉），
        /// 这段代码本身也编在仅编辑器的程序集里。
        /// </summary>
        static ConfigPanelInstaller()
        {
            // PanelInstaller 是 Action<GameBootstrap>（无返回值），这里包一层丢弃返回值。
            DebugSupport.PanelInstaller = bootstrap => { EnsurePanel(bootstrap); };
        }

        /// <summary>
        /// 确保引导器上有配置面板，并把它接到 HUD 的调试入口上（幂等）。
        /// 输入：对局引导器（为空时直接返回 null）；输出：面板组件（供“在运行中打开配置面板”菜单使用）。
        /// 为什么幂等：GameBootstrap 可能重复调用安装（重开局、重复 Bind），重复挂组件会叠出两套面板。
        /// </summary>
        public static ConfigDebugPanel EnsurePanel(GameBootstrap bootstrap)
        {
            if (bootstrap == null) return null;
            var hud = bootstrap.GetComponent<HudView>();
            if (hud == null || hud.Root == null)
            {
                Debug.LogWarning("ConfigPanelInstaller：HUD 尚未绑定，无法创建配置面板。");
                return null;
            }

            // 面板不是 MonoBehaviour（编辑器程序集的脚本不能 AddComponent），因此用普通类 + 静态引用。
            // 根节点变了（重新进入 Play、界面重建）就重建一份，避免继续操作已经作废的元素树。
            if (_panel == null || !ReferenceEquals(_panel.Root, hud.Root))
            {
                _panel = new ConfigDebugPanel();
                _panel.Bind(bootstrap, hud.Root);
                // 入口按钮只在编辑器里由 HudView 创建；这里负责把点击接到“打开面板”上（先减后加，避免重复订阅）。
                hud.DebugRequested -= _panel.Open;
                hud.DebugRequested += _panel.Open;
            }
            return _panel;
        }

        /// <summary>
        /// 菜单入口用：在 Play 模式下打开当前场景的配置面板。
        /// 输入：无；输出：无（不在 Play 模式或场景里没有引导器时只给出提示）。
        /// 为什么在这里补一次 EnsurePanel：菜单可能在面板安装回调之前被点（例如手动进入 Play 后立即点菜单），
        /// 这里兜底保证“菜单 → 打开面板”永远可用。
        /// </summary>
        public static void OpenPanelInPlayMode()
        {
            if (!Application.isPlaying) { Debug.LogWarning("请先进入 Play 模式。"); return; }
            var bootstrap = UnityEngine.Object.FindObjectOfType<GameBootstrap>();
            if (bootstrap == null) { Debug.LogWarning("当前场景中没有 GameBootstrap，无法打开配置面板。"); return; }
            var panel = EnsurePanel(bootstrap);
            if (panel == null) { Debug.LogWarning("配置面板创建失败。"); return; }
            panel.Open();
        }
    }
}
