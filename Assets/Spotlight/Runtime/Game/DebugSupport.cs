// 调试能力开关：集中判断“当前构建是否允许出现调试入口”。
// 为什么需要：正式运行包必须完全隐藏调试入口，判断逻辑只允许有一处，
// 否则很容易出现某个面板忘记加条件编译而泄露配置编辑能力。
// 现在调试面板整套代码都在 Spotlight.Editor 程序集里（仅编辑器编译），
// 这里的判定只剩“编辑器内是否显示入口按钮”，运行层不再依赖任何调试面板类型。
using System;
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 调试/开发能力判定与调试面板安装点。
    /// 输入：编译期宏与编辑器侧注册的回调；输出：是否允许显示调试入口、是否已挂上调试面板。
    /// </summary>
    public static class DebugSupport
    {
        /// <summary>
        /// 当前是否允许使用调试功能。
        /// 只有编辑器为 true：开发版包已不再作为交付或测试载体（所有人都在编辑器里跑），
        /// 因此正式包与 Development Build 都不提供调试入口。
        /// </summary>
        public static bool IsDebugAvailable =>
#if UNITY_EDITOR
            true;
#else
            false;
#endif

#if UNITY_EDITOR
        /// <summary>
        /// 调试面板安装器：由编辑器程序集（`ConfigPanelInstaller`）在加载时注册。
        /// 输入：当前对局引导器；输出：无（把面板组件挂到引导器上并接好入口）。
        /// 为什么用回调而不是直接引用：面板类型在 Spotlight.Editor 程序集里，
        /// 而运行层不能引用编辑器程序集（依赖方向单向：Tests → Editor → Runtime → Core）。
        /// 正式包里这个字段根本不存在（`#if UNITY_EDITOR`），因此也带不进任何调试类型名。
        /// </summary>
        public static Action<GameBootstrap> PanelInstaller;
#endif

        /// <summary>
        /// 创建调试对局使用的随机种子。
        /// 输入：编辑器或运行时可选的固定种子；输出：非负种子。
        /// 说明：调试面板“应用并重开”时使用固定种子，保证同一套草稿反复试验得到相同牌序。
        /// </summary>
        public static int ResolveSeed(int configuredSeed) => configuredSeed != 0 ? configuredSeed : System.Environment.TickCount & 0x7fffffff;
    }
}
