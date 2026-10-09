// xlsx 原始 XML 扫描：检测公式、错误值与合并单元格。
// 为什么不能只依赖 ExcelDataReader：它按“取值”工作，公式单元格只会返回缓存结果，
// 无法区分“常量”和“公式”，也无法报告错误值与合并区域。
// 正式数值必须可追溯，因此这里直接扫描 xl/worksheets/*.xml 的原始标记。
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Xml;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// xlsx 结构检查器。
    /// 输入：`.xlsx` 文件路径；输出：公式、错误值、合并数据单元格的问题列表。
    /// 纯函数、不依赖 Unity，便于 EditMode 测试直接调用。
    /// </summary>
    public static class XlsxInspector
    {
        /// <summary>关系命名空间，用于从 sheet 元素读取 r:id。</summary>
        const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        /// <summary>
        /// 扫描一个 xlsx 文件。
        /// 输入：xlsx 路径；输出：问题列表（为空表示没有公式/错误值/合并数据区）。
        /// </summary>
        public static List<ConfigIssue> Inspect(string xlsxPath)
        {
            var issues = new List<ConfigIssue>();
            if (string.IsNullOrWhiteSpace(xlsxPath) || !File.Exists(xlsxPath))
            {
                issues.Add(new ConfigIssue(xlsxPath, "（工作簿）", 0, 0, "工作簿文件不存在"));
                return issues;
            }
            try
            {
                using (var archive = ZipFile.OpenRead(xlsxPath))
                {
                    var sheets = ReadSheetMap(archive, xlsxPath, issues);
                    foreach (var pair in sheets) InspectSheet(archive, xlsxPath, pair.Key, pair.Value, issues);
                }
            }
            catch (Exception e)
            {
                issues.Add(new ConfigIssue(xlsxPath, "（工作簿）", 0, 0, "无法解析 xlsx：" + e.Message));
            }
            return issues;
        }

        /// <summary>
        /// 读取工作表名 → zip 内部部件路径的映射。
        /// 输入：zip 与文件路径；输出：工作表名到部件路径的字典。
        /// 通过 xl/workbook.xml 与 xl/_rels/workbook.xml.rels 交叉引用得到。
        /// </summary>
        static Dictionary<string, string> ReadSheetMap(ZipArchive archive, string file, List<ConfigIssue> issues)
        {
            var map = new Dictionary<string, string>();
            var workbook = archive.GetEntry("xl/workbook.xml");
            var rels = archive.GetEntry("xl/_rels/workbook.xml.rels");
            if (workbook == null || rels == null)
            {
                issues.Add(new ConfigIssue(file, "（工作簿）", 0, 0, "缺少 xl/workbook.xml 或关系文件，xlsx 结构不完整"));
                return map;
            }

            var relations = new Dictionary<string, string>();
            using (var stream = rels.Open())
            using (var reader = XmlReader.Create(stream))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") continue;
                    var id = reader.GetAttribute("Id");
                    var target = reader.GetAttribute("Target");
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target)) relations[id] = NormalizePartPath(target);
                }
            }

            using (var stream = workbook.Open())
            using (var reader = XmlReader.Create(stream))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sheet") continue;
                    var name = reader.GetAttribute("name");
                    var relationId = reader.GetAttribute("id", RelationshipNamespace);
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(relationId)) continue;
                    if (relations.TryGetValue(relationId, out var part)) map[name] = part;
                }
            }
            return map;
        }

        /// <summary>
        /// 把关系文件里的 Target 规范化为 zip 内路径。输入：原始 Target；输出：形如 xl/worksheets/sheet1.xml 的路径。
        /// </summary>
        static string NormalizePartPath(string target)
        {
            var value = target.Replace('\\', '/');
            if (value.StartsWith("/")) return value.TrimStart('/');
            if (value.StartsWith("xl/")) return value;
            return "xl/" + value;
        }

        /// <summary>
        /// 扫描单个工作表部件。
        /// 输入：zip、文件路径、工作表名与部件路径；输出：把发现的问题写入 issues。
        /// 规则：`<f>` 视为公式；`t="e"` 视为错误值；覆盖第 2 行及以后的合并区域视为合并数据单元格。
        /// </summary>
        static void InspectSheet(ZipArchive archive, string file, string sheetName, string partPath, List<ConfigIssue> issues)
        {
            var entry = archive.GetEntry(partPath);
            if (entry == null)
            {
                issues.Add(new ConfigIssue(file, sheetName, 0, 0, "工作表部件缺失：" + partPath));
                return;
            }
            using (var stream = entry.Open())
            using (var reader = XmlReader.Create(stream))
            {
                string cellReference = null;
                string cellType = null;
                var isFormula = false;
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        switch (reader.LocalName)
                        {
                            case "c":
                                cellReference = reader.GetAttribute("r");
                                cellType = reader.GetAttribute("t");
                                isFormula = false;
                                break;
                            case "f":
                                // 普通公式与共享公式都会出现 <f>，都属于“正式数据区使用公式”。
                                isFormula = true;
                                break;
                            case "mergeCell":
                                ReportMerge(file, sheetName, reader.GetAttribute("ref"), issues);
                                break;
                        }
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "c")
                    {
                        var (row, column) = ParseCellReference(cellReference);
                        if (isFormula) issues.Add(new ConfigIssue(file, sheetName, row, column, "正式数据区禁止使用公式"));
                        if (cellType == "e") issues.Add(new ConfigIssue(file, sheetName, row, column, "单元格为错误值"));
                        cellReference = null;
                        cellType = null;
                        isFormula = false;
                    }
                }
            }
        }

        /// <summary>
        /// 报告与数据区重叠的合并单元格。
        /// 输入：文件、表与合并范围（如 A1:B3）；输出：无（把问题写入 issues）。
        /// 表头行（第 1 行）的合并会导致表头重复或缺失，同样被拒绝。
        /// </summary>
        static void ReportMerge(string file, string sheetName, string range, List<ConfigIssue> issues)
        {
            if (string.IsNullOrEmpty(range)) return;
            var parts = range.Split(':');
            var (startRow, startColumn) = ParseCellReference(parts[0]);
            var (endRow, endColumn) = parts.Length > 1 ? ParseCellReference(parts[1]) : (startRow, startColumn);
            if (endRow < startRow) (startRow, endRow) = (endRow, startRow);
            if (endColumn < startColumn) (startColumn, endColumn) = (endColumn, startColumn);
            issues.Add(new ConfigIssue(file, sheetName, startRow, startColumn,
                $"禁止合并单元格（范围 {range}，覆盖第 {startRow}-{endRow} 行、第 {startColumn}-{endColumn} 列）"));
        }

        /// <summary>
        /// 解析单元格引用。
        /// 输入：形如 AB12 的引用（可为 null）；输出：1 起的 (行, 列)，无法解析时为 (0,0)。
        /// </summary>
        public static (int Row, int Column) ParseCellReference(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return (0, 0);
            var column = 0;
            var index = 0;
            while (index < reference.Length && char.IsLetter(reference[index]))
            {
                column = column * 26 + (char.ToUpperInvariant(reference[index]) - 'A' + 1);
                index++;
            }
            var digits = reference.Substring(index);
            return int.TryParse(digits, out var row) ? (row, column) : (0, 0);
        }
    }
}
