// 配置编译的诊断与数据载体类型。
// 为什么单独成文件：读取、校验、发布三个阶段都要产出“可定位到 文件/表/行/列 的问题”，
// 共用一个不可变的问题类型可以保证最终报告顺序稳定、信息完整。
using System;
using System.Collections.Generic;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// 一条配置问题。
    /// 输入：由读取/校验/发布阶段构造；输出：可直接打印到 Console 或汇总进报告的中文描述。
    /// 为什么带行号列号：策划需要直接定位到单元格，只说“配置有错”无法修复。
    /// </summary>
    public sealed class ConfigIssue
    {
        /// <summary>问题所在文件（通常是工作簿的相对路径）。</summary>
        public string File { get; set; }
        /// <summary>问题所在工作表；不针对具体表时为“（工作簿）”。</summary>
        public string Sheet { get; set; }
        /// <summary>问题所在行号（1 起，与 Excel 行号一致）；0 表示不适用。</summary>
        public int Row { get; set; }
        /// <summary>问题所在列号（1 起，与 Excel 列号一致）；0 表示不适用。</summary>
        public int Column { get; set; }
        /// <summary>面向策划的问题原因。</summary>
        public string Reason { get; set; }

        /// <summary>
        /// 构造一条问题。
        /// 输入：文件、工作表、行列与原因；输出：问题对象。
        /// </summary>
        public ConfigIssue(string file, string sheet, int row, int column, string reason)
        {
            File = file;
            Sheet = sheet;
            Row = row;
            Column = column;
            Reason = reason;
        }

        /// <summary>把问题格式化为“文件!表:行:列 原因”。输入：无；输出：单行文本。</summary>
        public override string ToString()
        {
            var location = Sheet;
            if (Row > 0) location += ":" + Row;
            if (Column > 0) location += ":" + Column;
            return $"{File}!{location} {Reason}";
        }
    }

    /// <summary>
    /// 已按工作表读入内存的工作簿数据。
    /// 输入：WorkbookReader 读出的二维字符串数组；输出：供表映射使用的只读字典。
    /// 为什么统一转成字符串：结构校验必须先判断“单元格类型是否合法”，
    /// 因此读取阶段不做类型转换，避免非法值在早期被静默吞掉。
    /// </summary>
    public sealed class WorkbookData
    {
        /// <summary>工作簿的绝对路径。</summary>
        public string SourcePath { get; }

        /// <summary>工作表名 → 二维单元格（[行, 列]，第 0 行为表头）。</summary>
        public IReadOnlyDictionary<string, string[,]> Sheets { get; }

        /// <summary>构造工作簿数据。输入：源路径与已读入的工作表；输出：不可变数据容器。</summary>
        public WorkbookData(string sourcePath, IReadOnlyDictionary<string, string[,]> sheets)
        {
            SourcePath = sourcePath;
            Sheets = sheets;
        }

        /// <summary>
        /// 取得某工作表。
        /// 输入：工作表名；输出：二维单元格（不存在时返回 null）。
        /// </summary>
        public string[,] GetSheet(string name) => Sheets.TryGetValue(name, out var cells) ? cells : null;
    }

    /// <summary>
    /// 工作簿读取结果。
    /// 输入：WorkbookReader.Read 的返回；输出：数据与结构问题的组合。
    /// </summary>
    public sealed class WorkbookReadResult
    {
        /// <summary>读入的数据；结构校验失败时可能为 null。</summary>
        public WorkbookData Data { get; set; }
        /// <summary>结构校验问题列表。</summary>
        public List<ConfigIssue> Issues { get; } = new List<ConfigIssue>();
        /// <summary>是否没有结构问题。</summary>
        public bool Success => Issues.Count == 0;
    }

    /// <summary>
    /// 编译报告。
    /// 输入：ConfigCompiler.Compile 的返回；输出：菜单、门禁与测试共享的统一结果。
    /// </summary>
    public sealed class CompileReport
    {
        /// <summary>问题列表（已按 文件/表/行/列 排序）。</summary>
        public List<ConfigIssue> Issues { get; } = new List<ConfigIssue>();
        /// <summary>本次编译产出的资产路径（发布被跳过后为空）。</summary>
        public List<string> GeneratedAssetPaths { get; } = new List<string>();
        /// <summary>源工作簿的内容摘要（SHA256 十六进制）。</summary>
        public string SourceHash { get; set; }
        /// <summary>源工作簿相对路径。</summary>
        public string SourcePath { get; set; }
        /// <summary>校验通过后的文档；失败时为 null（供测试与调试面板复用）。</summary>
        public ConfigDocument Document { get; set; }
        /// <summary>是否完全成功。</summary>
        public bool Success => Issues.Count == 0;

        /// <summary>
        /// 追加一条问题。
        /// 输入：文件、表、行列与原因；输出：无。用于把各阶段的问题汇总到同一报告。
        /// </summary>
        public void Add(string file, string sheet, int row, int column, string reason)
            => Issues.Add(new ConfigIssue(file, sheet, row, column, reason));

        /// <summary>
        /// 追加一批问题。
        /// 输入：问题序列；输出：无。
        /// </summary>
        public void AddRange(IEnumerable<ConfigIssue> issues)
        {
            if (issues == null) return;
            Issues.AddRange(issues);
        }

        /// <summary>
        /// 按 文件 → 表 → 行 → 列 排序，保证报告顺序稳定、便于比对。
        /// 输入：无；输出：无。
        /// </summary>
        public void Sort()
        {
            Issues.Sort((a, b) =>
            {
                var byFile = string.Compare(a.File, b.File, StringComparison.Ordinal);
                if (byFile != 0) return byFile;
                var bySheet = string.Compare(a.Sheet, b.Sheet, StringComparison.Ordinal);
                if (bySheet != 0) return bySheet;
                var byRow = a.Row.CompareTo(b.Row);
                if (byRow != 0) return byRow;
                return a.Column.CompareTo(b.Column);
            });
        }
    }
}
