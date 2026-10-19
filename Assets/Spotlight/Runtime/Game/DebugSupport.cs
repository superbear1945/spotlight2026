// 调试能力开关：集中判断“当前构建是否允许出现调试入口”。
// 为什么需要：正式运行包必须完全隐藏调试入口，判断逻辑只允许有一处，
// 否则很容易出现某个面板忘记加条件编译而泄露配置编辑能力。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 调试能力判定。
    /// 输入：编译期宏；输出：是否允许创建调试对局、是否显示调试按钮。
    /// 说明：调试配置面板已改为“仅编辑器编译”（Debug 目录整目录 `#if UNITY_EDITOR`），
    /// Development Build 与正式包一样不含该面板，因此这里只认编辑器。
    /// </summary>
    public static class DebugSupport
    {
        /// <summary>
        /// 当前是否允许使用调试功能。
        /// 编辑器为 true，任何 Player 构建（含 Development Build）为 false。
        /// </summary>
        public static bool IsDebugAvailable =>
#if UNITY_EDITOR
            true;
#else
            false;
#endif

        /// <summary>
        /// 创建对局使用的随机种子。
        /// 输入：配置的可选固定种子；输出：非负种子。
        /// 说明：调试面板“应用并重开”时使用固定种子，保证同一套草稿反复试验得到相同牌序；
        /// 该能力与调试面板无关，正式包也需要，所以保持无条件编译。
        /// </summary>
        public static int ResolveSeed(int configuredSeed) => configuredSeed != 0 ? configuredSeed : System.Environment.TickCount & 0x7fffffff;
    }
}
