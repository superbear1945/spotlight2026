// 卡图路径解析：把 Cards.cardArt 的文本换算成项目内资产路径。
// 为什么单独抽成静态类：路径规则（纯文件名 → 默认目录，或 Assets/ 开头原样使用）是纯字符串逻辑；
// 抽出来之后 EditMode 测试可以直接覆盖它，不需要在工程里准备真实 PNG，
// 也就不会因为 CI 没有取到 LFS 素材而让测试变红。
using System;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// Cards.cardArt 的路径解析器。
    /// 输入：卡图列原始文本；输出：项目内资产路径（如 Assets/.../ge1.png）或错误说明。
    /// 为什么需要它：列值支持“只写文件名”的简写，简写要落到一个固定目录上；
    /// 把这条规则集中在一处，校验、发布与测试才不会各写一份而互相漂移。
    /// </summary>
    public static class CardArtResolver
    {
        /// <summary>只填文件名时使用的默认卡图目录（项目根相对路径，统一用正斜杠）。</summary>
        public const string DefaultFolder = "Assets/Imported/SpotlightArt/CardArt";

        /// <summary>
        /// 解析 cardArt 文本。
        /// 输入：列值（可为空、可为纯文件名、可为 Assets/ 开头的完整路径）。
        /// 输出：返回值表示“是否配置了卡图”（留空返回 false，属于合法状态）；
        ///       assetPath 为解析结果；error 非 null 表示写法非法，调用方必须按错误处理而不是静默跳过。
        /// 为什么用“返回值 + error”两个信号：留空和写错是两种完全不同的情况，
        /// 前者是“没有卡图，回退纯色矩形”，后者必须让编译失败，否则策划会以为图生效了。
        /// </summary>
        public static bool TryResolve(string value, out string assetPath, out string error)
        {
            assetPath = null;
            error = null;
            var raw = (value ?? string.Empty).Trim();
            if (raw.Length == 0) return false;

            // 三条合法性的硬约束与 ConfigValidation 保持一致：只用正斜杠、不许跳出目录、只收 png。
            if (raw.IndexOf('\\') >= 0) { error = "不能使用反斜杠，请用 / 分隔路径"; return false; }
            if (raw.IndexOf("..", StringComparison.Ordinal) >= 0) { error = "不能包含 .."; return false; }
            if (!raw.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { error = "必须是 .png 文件"; return false; }

            if (raw.StartsWith("Assets/", StringComparison.Ordinal))
            {
                assetPath = raw;
                return true;
            }

            // 只剩“纯文件名”一种合法写法；带斜杠但不是 Assets/ 开头的一律拒绝，
            // 因为相对路径会随当前工作目录变化，属于不可复现的写法。
            if (raw.IndexOf('/') >= 0)
            {
                error = "只能填文件名（自动解析到默认卡图目录）或以 Assets/ 开头的完整路径";
                return false;
            }

            assetPath = DefaultFolder + "/" + raw;
            return true;
        }
    }
}
