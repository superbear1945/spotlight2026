// 表结构声明与行映射。
// 为什么用表驱动：列名、类型与必需性是“工作簿形状”的唯一来源，
// 结构校验、默认工作簿生成与错误信息都从这里读取，避免三处各写一份列清单而互相漂移。
// 列名必须与 Core 中行类型的 public 字段名完全一致（映射通过反射按名写值）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Spotlight.EditorTools
{
    /// <summary>列的数据类型。输入：无；输出：用于类型校验与转换的枚举。</summary>
    public enum ColumnType { Text, Integer, Float, Boolean }

    /// <summary>
    /// 一列的声明。
    /// 输入：列名、类型与是否必需；输出：结构校验与类型转换使用的定义。
    /// </summary>
    public sealed class ColumnDefinition
    {
        /// <summary>列名，与行类型字段名一致。</summary>
        public string Name { get; }
        /// <summary>列的数据类型。</summary>
        public ColumnType Type { get; }
        /// <summary>是否必需（缺失或整列为空时报错）。</summary>
        public bool Required { get; }

        /// <summary>构造列声明。输入：名称、类型与必需性；输出：声明对象。</summary>
        public ColumnDefinition(string name, ColumnType type, bool required)
        {
            Name = name;
            Type = type;
            Required = required;
        }
    }

    /// <summary>
    /// 一张表的声明。
    /// 输入：工作表名与列声明；输出：结构校验与映射使用的表定义。
    /// </summary>
    public sealed class TableDefinition
    {
        /// <summary>工作表名（必须与 xlsx 中的 sheet 名完全一致）。</summary>
        public string SheetName { get; }
        /// <summary>列声明集合，顺序即默认工作簿的列顺序。</summary>
        public IReadOnlyList<ColumnDefinition> Columns { get; }

        /// <summary>构造表声明。输入：表名与列集合；输出：表定义。</summary>
        public TableDefinition(string sheetName, IReadOnlyList<ColumnDefinition> columns)
        {
            SheetName = sheetName;
            Columns = columns;
        }

        /// <summary>按列名查找声明。输入：列名；输出：声明对象（不存在时为 null）。</summary>
        public ColumnDefinition Find(string name)
        {
            for (var i = 0; i < Columns.Count; i++)
                if (string.Equals(Columns[i].Name, name, StringComparison.Ordinal)) return Columns[i];
            return null;
        }
    }

    /// <summary>
    /// 全部表的声明。
    /// 输入：无；输出：固定的表结构，被读取、映射与默认工作簿生成共用。
    /// </summary>
    public static class TableSchema
    {
        /// <summary>工作簿必须包含的工作表，顺序即默认生成顺序。</summary>
        public static readonly string[] RequiredSheets = { "Cards", "Deck", "Rules", "Homes", "BossSlots", "Presentation" };

        /// <summary>全部表声明。</summary>
        public static readonly IReadOnlyList<TableDefinition> All = new[]
        {
            new TableDefinition("Cards", new[]
            {
                new ColumnDefinition("id", ColumnType.Text, true),
                new ColumnDefinition("name", ColumnType.Text, true),
                new ColumnDefinition("category", ColumnType.Text, true),
                new ColumnDefinition("viewKey", ColumnType.Text, true),
                new ColumnDefinition("hp", ColumnType.Integer, true),
                new ColumnDefinition("atk", ColumnType.Integer, false),
                new ColumnDefinition("range", ColumnType.Integer, false),
                new ColumnDefinition("moveDistance", ColumnType.Integer, false),
                new ColumnDefinition("produce", ColumnType.Integer, false),
                new ColumnDefinition("upkeep", ColumnType.Integer, false),
                new ColumnDefinition("resourceComponent", ColumnType.Boolean, false),
                new ColumnDefinition("teleportComponent", ColumnType.Boolean, false),
                new ColumnDefinition("permitsAttackDeploy", ColumnType.Boolean, false),
                new ColumnDefinition("deployHomeOnly", ColumnType.Boolean, false),
                new ColumnDefinition("avoidAdjacentSameType", ColumnType.Boolean, false),
                new ColumnDefinition("requiresUpgrade", ColumnType.Boolean, false),
                new ColumnDefinition("upgradeFrom", ColumnType.Text, false)
            }),
            new TableDefinition("Deck", new[]
            {
                new ColumnDefinition("cardType", ColumnType.Text, true),
                new ColumnDefinition("count", ColumnType.Integer, true)
            }),
            new TableDefinition("Rules", new[]
            {
                new ColumnDefinition("rows", ColumnType.Integer, true),
                new ColumnDefinition("columns", ColumnType.Integer, true),
                new ColumnDefinition("homeRegionWidth", ColumnType.Integer, true),
                new ColumnDefinition("playerPlays", ColumnType.Integer, true),
                new ColumnDefinition("bossPlays", ColumnType.Integer, true),
                new ColumnDefinition("drawCount", ColumnType.Integer, true),
                new ColumnDefinition("handLimit", ColumnType.Integer, true),
                new ColumnDefinition("initialResources", ColumnType.Integer, true),
                new ColumnDefinition("bossRespawnDelay", ColumnType.Integer, true),
                new ColumnDefinition("playerSide", ColumnType.Text, true),
                new ColumnDefinition("firstSide", ColumnType.Text, true),
                new ColumnDefinition("bossCardType", ColumnType.Text, true),
                new ColumnDefinition("playerControlsBoss", ColumnType.Boolean, true),
                new ColumnDefinition("accumulateResources", ColumnType.Boolean, true),
                new ColumnDefinition("summoningSickness", ColumnType.Boolean, true),
                new ColumnDefinition("adjacentSwap", ColumnType.Boolean, true),
                new ColumnDefinition("lineBlocking", ColumnType.Boolean, true)
            }),
            new TableDefinition("Homes", new[]
            {
                new ColumnDefinition("owner", ColumnType.Text, true),
                new ColumnDefinition("hp", ColumnType.Integer, true),
                new ColumnDefinition("produce", ColumnType.Integer, true)
            }),
            new TableDefinition("BossSlots", new[]
            {
                new ColumnDefinition("id", ColumnType.Text, true),
                new ColumnDefinition("rowFraction", ColumnType.Float, true),
                new ColumnDefinition("edgeOffset", ColumnType.Integer, true)
            }),
            new TableDefinition("Presentation", new[]
            {
                new ColumnDefinition("referenceWidth", ColumnType.Integer, true),
                new ColumnDefinition("referenceHeight", ColumnType.Integer, true),
                new ColumnDefinition("fontSize", ColumnType.Integer, true),
                new ColumnDefinition("logLimit", ColumnType.Integer, true),
                new ColumnDefinition("moveSeconds", ColumnType.Float, true),
                new ColumnDefinition("feedbackSeconds", ColumnType.Float, true),
                new ColumnDefinition("selectedScale", ColumnType.Float, true),
                new ColumnDefinition("playerColor", ColumnType.Text, true),
                new ColumnDefinition("bossColor", ColumnType.Text, true),
                new ColumnDefinition("resourceColor", ColumnType.Text, true),
                new ColumnDefinition("attackColor", ColumnType.Text, true),
                new ColumnDefinition("specialColor", ColumnType.Text, true),
                new ColumnDefinition("moveColor", ColumnType.Text, true),
                new ColumnDefinition("attackTargetColor", ColumnType.Text, true),
                new ColumnDefinition("swapColor", ColumnType.Text, true),
                new ColumnDefinition("deployColor", ColumnType.Text, true)
            })
        };

        /// <summary>按工作表名查找表定义。输入：工作表名；输出：表定义（不存在时为 null）。</summary>
        public static TableDefinition For(string sheetName)
        {
            for (var i = 0; i < All.Count; i++)
                if (string.Equals(All[i].SheetName, sheetName, StringComparison.Ordinal)) return All[i];
            return null;
        }
    }

    /// <summary>
    /// 二维单元格 → 强类型行对象的映射器。
    /// 输入：已读入的工作表与表定义；输出：行对象列表与类型错误列表。
    /// 为什么用反射：列名即字段名，映射代码因此不需要随表结构变动而修改。
    /// </summary>
    public static class TableMapper
    {
        /// <summary>
        /// 建立表头索引并做结构校验。
        /// 输入：文件路径、表定义与工作表单元格；输出：是否成功与“列名 → 列下标”的索引。
        /// 检查：重复表头、未定义列、缺失必需列、有数据但无表头的列。
        /// </summary>
        public static bool TryBuildHeader(string file, TableDefinition table, string[,] cells, List<ConfigIssue> issues, out Dictionary<string, int> headerIndex)
        {
            headerIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            if (cells == null || cells.GetLength(0) == 0)
            {
                issues.Add(new ConfigIssue(file, table.SheetName, 0, 0, "工作表缺失或没有任何行"));
                return false;
            }

            var rowCount = cells.GetLength(0);
            var columnCount = cells.GetLength(1);
            var hasData = new bool[columnCount];
            for (var row = 1; row < rowCount; row++)
                for (var column = 0; column < columnCount; column++)
                    if (!string.IsNullOrWhiteSpace(cells[row, column])) hasData[column] = true;

            var ok = true;
            for (var column = 0; column < columnCount; column++)
            {
                var header = (cells[0, column] ?? string.Empty).Trim();
                if (header.Length == 0)
                {
                    if (hasData[column])
                    {
                        issues.Add(new ConfigIssue(file, table.SheetName, 1, column + 1, "该列有数据但没有表头"));
                        ok = false;
                    }
                    continue;
                }
                if (table.Find(header) == null)
                {
                    issues.Add(new ConfigIssue(file, table.SheetName, 1, column + 1, "未定义列：" + header));
                    ok = false;
                    continue;
                }
                if (headerIndex.ContainsKey(header))
                {
                    issues.Add(new ConfigIssue(file, table.SheetName, 1, column + 1, "重复表头：" + header));
                    ok = false;
                    continue;
                }
                headerIndex[header] = column;
            }

            foreach (var definition in table.Columns)
            {
                if (!definition.Required) continue;
                if (headerIndex.ContainsKey(definition.Name)) continue;
                issues.Add(new ConfigIssue(file, table.SheetName, 1, 0, "缺失必需列：" + definition.Name));
                ok = false;
            }
            return ok;
        }

        /// <summary>
        /// 把全部数据行映射为强类型对象。
        /// 输入：文件、表定义、单元格与表头索引；输出：行对象列表（转换错误写入 issues 并跳过该行）。
        /// 整行为空的行会被跳过，方便策划在工作表末尾留空行。
        /// </summary>
        public static List<T> MapRows<T>(string file, TableDefinition table, string[,] cells, Dictionary<string, int> headerIndex, List<ConfigIssue> issues) where T : new()
        {
            var result = new List<T>();
            var rowCount = cells.GetLength(0);
            var columnCount = cells.GetLength(1);
            for (var row = 1; row < rowCount; row++)
            {
                var empty = true;
                for (var column = 0; column < columnCount; column++)
                    if (!string.IsNullOrWhiteSpace(cells[row, column])) { empty = false; break; }
                if (empty) continue;

                var item = new T();
                var rowOk = true;
                foreach (var definition in table.Columns)
                {
                    if (!headerIndex.TryGetValue(definition.Name, out var column)) continue;
                    var raw = (cells[row, column] ?? string.Empty).Trim();
                    if (raw.Length == 0 && definition.Required && definition.Type == ColumnType.Text)
                    {
                        issues.Add(new ConfigIssue(file, table.SheetName, row + 1, column + 1, $"必需列为空：{definition.Name}"));
                        rowOk = false;
                        continue;
                    }
                    if (!TryConvert(raw, definition.Type, out var value))
                    {
                        issues.Add(new ConfigIssue(file, table.SheetName, row + 1, column + 1, $"{definition.Name} 的值 '{raw}' 不是合法的{Describe(definition.Type)}"));
                        rowOk = false;
                        continue;
                    }
                    var field = typeof(T).GetField(definition.Name, BindingFlags.Public | BindingFlags.Instance);
                    if (field == null)
                    {
                        issues.Add(new ConfigIssue(file, table.SheetName, row + 1, column + 1, $"行类型 {typeof(T).Name} 没有字段 {definition.Name}"));
                        rowOk = false;
                        continue;
                    }
                    field.SetValue(item, value);
                }
                if (rowOk) result.Add(item);
            }
            return result;
        }

        /// <summary>把列类型转成中文描述。输入：列类型；输出：用于错误信息的中文名。</summary>
        static string Describe(ColumnType type)
        {
            switch (type)
            {
                case ColumnType.Integer: return "整数";
                case ColumnType.Float: return "小数";
                case ColumnType.Boolean: return "布尔值（true/false）";
                default: return "文本";
            }
        }

        /// <summary>
        /// 按列类型转换单元格文本。
        /// 输入：原始文本与目标类型；输出：是否成功与转换结果。
        /// 空文本一律转换为类型默认值（空串/0/false），必需列的空值由 ConfigValidation 做业务判断。
        /// </summary>
        public static bool TryConvert(string raw, ColumnType type, out object value)
        {
            value = null;
            switch (type)
            {
                case ColumnType.Text:
                    value = raw ?? string.Empty;
                    return true;
                case ColumnType.Integer:
                    if (string.IsNullOrEmpty(raw)) { value = 0; return true; }
                    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) { value = integer; return true; }
                    return false;
                case ColumnType.Float:
                    if (string.IsNullOrEmpty(raw)) { value = 0f; return true; }
                    if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) { value = number; return true; }
                    return false;
                case ColumnType.Boolean:
                    if (string.IsNullOrEmpty(raw)) { value = false; return true; }
                    if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || raw == "1") { value = true; return true; }
                    if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) || raw == "0") { value = false; return true; }
                    return false;
                default:
                    return false;
            }
        }
    }
}
