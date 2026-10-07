// 配置编译器：把工作簿编译成只读配置资产。
// 为什么需要独立编译步骤：正式数值必须来自表格，运行时不解析 Excel；
// 因此这里负责“读 → 结构校验 → 业务校验 → 全部通过才发布”，任何一步失败都保留上一套有效产物。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// 配置编译入口。
    /// 输入：工作簿路径（默认 Assets/Spotlight/Config/Spotlight.xlsx）与是否发布；
    /// 输出：编译报告（问题列表、生成资产路径与源文件摘要）。
    /// 菜单、构建门禁与测试都通过本类执行同一套流程，避免出现“验证通过但编译用了另一套规则”。
    /// </summary>
    public static class ConfigCompiler
    {
        /// <summary>默认工作簿的相对路径（相对 Unity 工程根目录）。</summary>
        public const string DefaultWorkbookPath = "Assets/Spotlight/Config/Spotlight.xlsx";

        /// <summary>
        /// 执行一次完整编译。
        /// 输入：workbookPath（null 表示默认路径）与 publish（false 表示只验证不写资产）；
        /// 输出：CompileReport。
        /// 失败语义：只要报告中存在任何问题，就绝不修改已有生成资产。
        /// </summary>
        public static CompileReport Compile(string workbookPath = null, bool publish = true)
        {
            var report = new CompileReport();
            var relative = string.IsNullOrWhiteSpace(workbookPath) ? DefaultWorkbookPath : workbookPath;
            var full = Path.GetFullPath(relative);
            report.SourcePath = relative;
            var displayFile = MakeDisplayPath(full);
            if (!File.Exists(full))
            {
                report.Add(displayFile, "（工作簿）", 0, 0, "找不到工作簿，请先执行 Tools/Spotlight/生成默认工作簿");
                return report;
            }
            report.SourceHash = ComputeFileHash(full);

            // 步骤 1：原始 XML 扫描（公式、错误值、合并单元格）。
            report.AddRange(XlsxInspector.Inspect(full));

            // 步骤 2：读取与结构校验（表、列、类型）。
            var read = WorkbookReader.Read(full);
            report.AddRange(read.Issues);
            if (read.Data == null) { report.Sort(); return report; }

            // 步骤 3：映射为文档。
            var document = BuildDocument(displayFile, read.Data, report.Issues);

            // 步骤 4：业务校验（与运行时使用完全相同的实现）。
            if (document != null) report.AddRange(ValidateDocument(displayFile, document));

            if (report.Issues.Count != 0) { report.Sort(); return report; }

            report.Document = document;

            // 步骤 5：发布。只有前面全部通过才会走到这里。
            if (publish)
            {
                var published = ConfigAssetPublisher.Publish(document, relative, report.SourceHash);
                report.AddRange(published.Issues);
                report.Sort();
                if (report.Success) report.GeneratedAssetPaths.AddRange(published.Paths);
                return report;
            }

            report.Sort();
            return report;
        }

        /// <summary>
        /// 把工作簿数据映射为配置文档。
        /// 输入：用于报错的显示路径、工作簿数据与问题收集列表；输出：文档（缺表时为 null）。
        /// </summary>
        public static ConfigDocument BuildDocument(string displayFile, WorkbookData data, List<ConfigIssue> issues)
        {
            var document = new ConfigDocument { schemaVersion = 1 };
            var cards = data.GetSheet("Cards");
            var deck = data.GetSheet("Deck");
            var rules = data.GetSheet("Rules");
            var homes = data.GetSheet("Homes");
            var slots = data.GetSheet("BossSlots");
            var presentation = data.GetSheet("Presentation");
            if (cards == null || deck == null || rules == null || homes == null || slots == null || presentation == null) return null;

            document.cards = Map<CardRow>(displayFile, "Cards", cards, issues);
            document.deck = Map<DeckRow>(displayFile, "Deck", deck, issues);
            document.rules = Map<RulesRow>(displayFile, "Rules", rules, issues);
            document.homes = Map<HomeRow>(displayFile, "Homes", homes, issues);
            document.bossSlots = Map<BossSlotRow>(displayFile, "BossSlots", slots, issues);
            document.presentation = Map<PresentationRow>(displayFile, "Presentation", presentation, issues);
            return document;
        }

        /// <summary>
        /// 映射单张表。
        /// 输入：文件、表名、单元格与问题收集列表；输出：行对象数组（结构非法时返回空数组）。
        /// </summary>
        static T[] Map<T>(string displayFile, string sheetName, string[,] cells, List<ConfigIssue> issues) where T : new()
        {
            var table = TableSchema.For(sheetName);
            if (!TableMapper.TryBuildHeader(displayFile, table, cells, issues, out var headerIndex)) return new T[0];
            return TableMapper.MapRows<T>(displayFile, table, cells, headerIndex, issues).ToArray();
        }

        /// <summary>
        /// 复用运行时的业务校验，并把中文错误文本转成可定位的问题。
        /// 输入：显示路径与文档；输出：问题列表。
        /// </summary>
        public static List<ConfigIssue> ValidateDocument(string displayFile, ConfigDocument document)
        {
            var issues = new List<ConfigIssue>();
            foreach (var message in ConfigValidation.Validate(document))
                issues.Add(new ConfigIssue(displayFile, GuessSheet(message), 0, 0, message));
            return issues;
        }

        /// <summary>
        /// 从业务校验文本推断所属工作表，便于报告按表分组。
        /// 输入：错误文本；输出：工作表名（无法推断时为“（业务校验）”）。
        /// </summary>
        static string GuessSheet(string message)
        {
            if (string.IsNullOrEmpty(message)) return "（业务校验）";
            if (message.StartsWith("Cards", StringComparison.Ordinal)) return "Cards";
            if (message.StartsWith("Deck", StringComparison.Ordinal)) return "Deck";
            if (message.StartsWith("Rules", StringComparison.Ordinal)) return "Rules";
            if (message.StartsWith("Homes", StringComparison.Ordinal)) return "Homes";
            if (message.StartsWith("BossSlots", StringComparison.Ordinal)) return "BossSlots";
            if (message.StartsWith("Presentation", StringComparison.Ordinal)) return "Presentation";
            return "（业务校验）";
        }

        /// <summary>
        /// 计算文件内容摘要（SHA256）。输入：文件绝对路径；输出：小写十六进制摘要。
        /// 用途：构建门禁用它判断“源工作簿是否在编译之后被修改过”。
        /// </summary>
        public static string ComputeFileHash(string path)
        {
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(stream);
                var text = new System.Text.StringBuilder(hash.Length * 2);
                foreach (var b in hash) text.Append(b.ToString("x2"));
                return text.ToString();
            }
        }

        /// <summary>
        /// 把绝对路径转成工程内相对路径（以 Assets/ 开头），便于错误信息与 Inspector 展示。
        /// 输入：绝对路径；输出：相对路径（不在 Assets 下时返回原路径）。
        /// </summary>
        public static string MakeDisplayPath(string fullPath)
        {
            var normalized = fullPath.Replace('\\', '/');
            var index = normalized.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            return index >= 0 ? normalized.Substring(index + 1) : normalized;
        }
    }
}
