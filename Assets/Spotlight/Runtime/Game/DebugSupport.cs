// 调试能力开关：集中判断“当前构建是否允许出现调试入口”。
// 为什么需要：正式运行包必须完全隐藏调试入口，判断逻辑只允许有一处，
// 否则很容易出现某个面板忘记加条件编译而泄露配置编辑能力。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 调试/开发能力判定。
    /// 输入：编译期宏；输出：是否允许创建调试对局、是否显示调试按钮。
    /// </summary>
    public static class DebugSupport
    {
        /// <summary>
        /// 当前是否允许使用调试功能。
        /// 编辑器或 Development Build 为 true，正式构建为 false。
        /// </summary>
        public static bool IsDebugAvailable =>
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            true;
#else
            false;
#endif

        /// <summary>
        /// 创建调试对局使用的随机种子。
        /// 输入：编辑器或运行时可选的固定种子；输出：非负种子。
        /// 说明：调试面板“应用并重开”时使用固定种子，保证同一套草稿反复试验得到相同牌序。
        /// </summary>
        public static int ResolveSeed(int configuredSeed) => configuredSeed != 0 ? configuredSeed : System.Environment.TickCount & 0x7fffffff;
    }
}
