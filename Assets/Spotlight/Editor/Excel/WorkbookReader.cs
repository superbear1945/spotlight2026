// 工作簿读取与结构校验。
// 为什么用 ExcelDataReader：xlsx 是压缩包 + XML，手写读取容易遗漏共享字符串、日期与内联字符串差异；
// 该库仅出现在编辑器程序集中（Assets/Plugins/ExcelDataReader/Editor），正式运行包不包含它。
using System;
using System.Collections.Generic;
using System.IO;
using ExcelDataReader;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// xlsx 读取器。
    /// 输入：工作簿绝对路径；输出：按工作表拆分的单元格与结构问题列表。
    /// 结构问题全部一次汇总，避免策划“改一个错冒出下一个错”。
    /// </summary>
    public static class WorkbookReader
    {
        /// <summary>
        /// 读取工作簿并做结构校验。
        /// 输入：xlsx 路径；输出：数据与问题（问题非空时 Data 仍会尽量填充，便于同时展示多类错误）。
        /// </summary>
        public static WorkbookReadResult Read(string xlsxPath)
        {
            var result = new WorkbookReadResult();
            if (string.IsNullOrWhiteSpace(xlsxPath) || !File.Exists(xlsxPath))
            {
                result.Issues.Add(new ConfigIssue(xlsxPath, "（工作簿）", 0, 0, "工作簿文件不存在"));
                return result;
            }

            var sheets = new Dictionary<string, string[,]>(StringComparer.Ordinal);
            try
            {
                using (var stream = File.Open(xlsxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = ExcelReaderFactory.CreateReader(stream))
                {
                    do
                    {
                        var name = string.IsNullOrWhiteSpace(reader.Name) ? $"Sheet{sheets.Count + 1}" : reader.Name;
                        sheets[name] = ReadSheet(reader);
                    }
                    while (reader.NextResult());
                }
            }
            catch (Exception e)
            {
                result.Issues.Add(new ConfigIssue(xlsxPath, "（工作簿）", 0, 0, "读取工作簿失败：" + e.Message));
                return result;
            }

            result.Data = new WorkbookData(xlsxPath, sheets);

            // 未声明的工作表：多出来的表通常是复制粘贴后的残留，直接报错以免策划误以为生效。
            foreach (var name in sheets.Keys)
                if (TableSchema.For(name) == null)
                    result.Issues.Add(new ConfigIssue(xlsxPath, name, 0, 0, "未声明的工作表（可用表：Cards/Deck/Rules/Homes/BossSlots/Presentation）"));

            foreach (var required in TableSchema.RequiredSheets)
            {
                var table = TableSchema.For(required);
                if (!sheets.TryGetValue(required, out var cells))
                {
                    result.Issues.Add(new ConfigIssue(xlsxPath, required, 0, 0, "缺失必需工作表"));
                    continue;
                }
                TableMapper.TryBuildHeader(xlsxPath, table, cells, result.Issues, out _);
            }
            return result;
        }

        /// <summary>
        /// 把一个工作表读成二维字符串数组。
        /// 输入：已定位到某工作表的 reader；输出：单元格数组（第 0 行为表头）。
        /// 所有值统一转成字符串，类型判断交给表映射阶段，避免非法值被静默丢弃。
        /// </summary>
        static string[,] ReadSheet(IExcelDataReader reader)
        {
            var rows = new List<string[]>();
            var width = 0;
            while (reader.Read())
            {
                var count = reader.FieldCount;
                if (count < 0) count = 0;
                var row = new string[count];
                for (var i = 0; i < count; i++) row[i] = Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                if (count > width) width = count;
                rows.Add(row);
            }
            var cells = new string[rows.Count, Math.Max(width, 1)];
            for (var r = 0; r < rows.Count; r++)
                for (var c = 0; c < rows[r].Length; c++)
                    cells[r, c] = rows[r][c];
            return cells;
        }
    }
}
