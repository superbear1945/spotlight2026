// 调试配置草稿：只在当前运行会话内存在，不写回 Excel，也不修改生成资产。
// 为什么单独抽出纯 C# 类：JSON 迁移、校验与候选对局创建都可以在不打开界面的情况下被测试覆盖。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 调试面板使用的配置草稿。
    /// 输入：当前生效的配置资产（初次载入或“恢复默认”）；或导出的 JSON 文本。
    /// 输出：新的 GameConfiguration（仅在“应用并重开”成功时）、JSON 文本与错误/提示列表。
    /// 生命周期：仅在本次运行会话内保留；退出 Play 或重启程序后重新从 Excel 产物读取。
    /// </summary>
    public sealed class DebugConfigDraft
    {
        /// <summary>草稿对应的源资产，用于“恢复默认”。</summary>
        SpotlightConfigAsset _asset;
        /// <summary>当前草稿文档；未应用前不影响进行中的对局。</summary>
        ConfigDocument _document;

        /// <summary>当前草稿文档（只读使用；修改请走提供的方法）。</summary>
        public ConfigDocument Document => _document;

        /// <summary>
        /// 用配置资产的内容初始化草稿。
        /// 输入：当前生效资产；输出：与正式配置一致的草稿副本。
        /// </summary>
        public DebugConfigDraft(SpotlightConfigAsset asset)
        {
            ResetToAsset(asset);
        }

        /// <summary>
        /// 恢复为正式配置内容。
        /// 输入：当前生效资产（可为 null，表示清空草稿）；输出：无。
        /// 说明：只复制行数据，不复制资产引用，因此后续编辑不会污染正式产物。
        /// </summary>
        public void ResetToAsset(SpotlightConfigAsset asset)
        {
            _asset = asset;
            _document = asset != null ? asset.ToDocument() : new ConfigDocument();
        }

        /// <summary>
        /// 导出草稿 JSON。
        /// 输入：无；输出：缩进后的 JSON 文本，可直接粘贴回面板或存档对比。
        /// </summary>
        public string ExportJson() => JsonUtility.ToJson(_document, true);

        /// <summary>
        /// 导入 JSON 草稿。
        /// 输入：JSON 文本；输出：是否成功、错误文本与迁移提示列表。
        /// 行为：先移除文档明确列出的废弃字段（cost、斜移、反击、chainDeployOnly、旧家坐标）并记录提示，
        /// 再反序列化；类型不匹配（例如布尔字段写成字符串）会直接失败，旧功能不会被恢复。
        /// </summary>
        public bool Import(string json, out string error, out IReadOnlyList<string> notices)
        {
            error = null;
            var noticeList = new List<string>();
            notices = noticeList;
            if (string.IsNullOrWhiteSpace(json)) { error = "JSON 文本为空"; return false; }
            try
            {
                var cleaned = LegacyJsonMigration.Strip(json, noticeList);
                // JsonUtility 对类型不匹配极为宽松（会把 "yes" 静默当成 false），
                // 因此必须在反序列化之前自行检查数值/布尔字段的形状。
                if (!JsonShapeValidator.TryValidate(cleaned, out error)) return false;
                var document = JsonUtility.FromJson<ConfigDocument>(cleaned);
                if (document == null) { error = "JSON 无法解析为配置文档"; return false; }
                document.schemaVersion = document.schemaVersion == 0 ? 1 : document.schemaVersion;
                _document = document;
                return true;
            }
            catch (Exception e)
            {
                error = "JSON 解析失败：" + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 业务校验草稿。
        /// 输入：无；输出：错误列表（为空表示可用）。
        /// </summary>
        public IReadOnlyList<string> Validate() => ConfigValidation.Validate(_document);

        /// <summary>
        /// 尝试把草稿转换为可用的对局配置，并创建一个候选对局。
        /// 输入：无；输出：是否成功、配置对象与错误文本。
        /// 为什么创建候选对局：Boss 卡种、兵位与家数值等组合问题只有真正开局才会暴露；
        /// 在替换当前对局之前先开局一次，失败时原对局与草稿都能完整保留。
        /// </summary>
        public bool TryCreateConfiguration(out GameConfiguration configuration, out string error)
        {
            configuration = null;
            error = null;
            var errors = Validate();
            if (errors.Count != 0) { error = string.Join("\n", errors); return false; }
            try
            {
                var candidate = new GameConfiguration(_document);
                // 候选对局只用于验证组合是否可开局，不保留任何状态。
                _ = new GameSession(candidate, new SeededRandom(1), 16);
                configuration = candidate;
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>
        /// 新增一个临时卡种。
        /// 输入：卡种字段（由面板收集）；输出：是否成功与错误文本。
        /// 说明：临时卡种同样进入草稿，不写入任何资产；数量默认 0，需要在卡组中另行引用。
        /// </summary>
        public bool AddCardType(CardRow row, out string error)
        {
            error = null;
            if (row == null) { error = "卡种为空"; return false; }
            var list = (_document.cards ?? Array.Empty<CardRow>()).ToList();
            if (list.Any(c => c != null && c.id == row.id)) { error = "卡种 ID 已存在"; return false; }
            list.Add(row);
            _document.cards = list.ToArray();
            var errors = ConfigValidation.Validate(_document);
            if (errors.Count != 0) { error = string.Join("\n", errors); return false; }
            return true;
        }

        /// <summary>
        /// 修改某个卡种的卡组数量。
        /// 输入：卡种 ID 与数量；输出：是否成功与错误文本。
        /// 数量为 0 表示保留卡种但不进入卡组，与玩法文档一致。
        /// </summary>
        public bool SetDeckCount(string cardTypeId, int count, out string error)
        {
            error = null;
            if (count < 0) { error = "数量不能为负数"; return false; }
            var deck = (_document.deck ?? Array.Empty<DeckRow>()).ToList();
            var entry = deck.FirstOrDefault(e => e != null && e.cardType == cardTypeId);
            if (entry == null) deck.Add(new DeckRow { cardType = cardTypeId, count = count });
            else entry.count = count;
            _document.deck = deck.ToArray();
            return true;
        }
    }

    /// <summary>
    /// 旧格式 JSON 迁移工具。
    /// 输入：可能包含废弃字段的 JSON 文本；输出：移除废弃字段后的文本与提示列表。
    /// 为什么用文本级移除：废弃字段已经不在当前文档类型中，JsonUtility 会静默忽略，
    /// 若不做显式移除就无法提示策划“该功能已删除”，也无法阻止旧字段被再次导出。
    /// </summary>
    public static class LegacyJsonMigration
    {
        /// <summary>已确认删除的字段名。键为字段名，值为面向策划的提示文本。</summary>
        static readonly (string Pattern, string Notice)[] _deprecated =
        {
            (@"""cost""\s*:\s*-?\d+(\.\d+)?", "已移除部署资源费用字段 cost（卡牌不再有部署费用）"),
            (@"""diagonalMove""\s*:\s*(true|false)", "已移除斜移开关 diagonalMove（只支持上下左右移动）"),
            (@"""counterAttack""\s*:\s*(true|false)", "已移除反击开关 counterAttack（彻底移除反击）"),
            (@"""chainDeployOnly""\s*:\s*(true|false)", "已移除 chainDeployOnly 开关"),
            (@"""row""\s*:\s*-?\d+\s*,\s*""col""\s*:\s*-?\d+", "已移除旧家坐标 row/col（改为按边缘居中规则定位）")
        };

        /// <summary>
        /// 移除废弃字段。
        /// 输入：JSON 文本与提示收集列表；输出：清理后的文本。
        /// 移除后同时修复可能出现的多余逗号（开头逗号与尾随逗号），保证结果仍是合法 JSON。
        /// </summary>
        public static string Strip(string json, List<string> notices)
        {
            if (string.IsNullOrEmpty(json)) return json;
            var result = json;
            foreach (var (pattern, notice) in _deprecated)
            {
                if (!Regex.IsMatch(result, pattern)) continue;
                result = Regex.Replace(result, pattern + @"\s*,?", string.Empty);
                result = Regex.Replace(result, @"([\[{])\s*,", "$1");
                result = Regex.Replace(result, @",\s*([}\]])", "$1");
                notices?.Add(notice);
            }
            return result;
        }
    }

    /// <summary>
    /// JSON 字段类型检查器。
    /// 输入：待检查的 JSON 文本；输出：是否合法与错误描述。
    /// 为什么需要：JsonUtility 会把类型错误（例如布尔字段写成字符串）静默降级为默认值，
    /// 而配置面板必须“非法输入阻止导入”，否则策划会误以为改动已经生效。
    /// 字段名与类型直接从行类型的 public 字段反射得到，新增字段不需要维护第二份清单。
    /// </summary>
    public static class JsonShapeValidator
    {
        /// <summary>字段名 → 期望类型；“?” 表示同名但类型冲突，跳过检查。</summary>
        static Dictionary<string, string> _kinds;

        /// <summary>
        /// 校验 JSON 中已知字段的值形状。
        /// 输入：JSON 文本；输出：是否合法与错误描述。
        /// 只校验已知数值/布尔字段；字符串字段与未知字段不在此处理。
        /// </summary>
        public static bool TryValidate(string json, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(json)) return true;
            foreach (var pair in Kinds)
            {
                if (pair.Value == "?") continue;
                var matches = Regex.Matches(json, "\"" + Regex.Escape(pair.Key) + "\"\\s*:\\s*([^,}\\]\\s]+)");
                foreach (Match match in matches)
                {
                    var value = match.Groups[1].Value.Trim();
                    if (IsValid(value, pair.Value)) continue;
                    error = $"字段 {pair.Key} 的值 {value} 不是合法的{Describe(pair.Value)}";
                    return false;
                }
            }
            return true;
        }

        /// <summary>已知字段及其期望类型（惰性构建并缓存）。</summary>
        static Dictionary<string, string> Kinds
        {
            get
            {
                if (_kinds != null) return _kinds;
                var map = new Dictionary<string, string>(System.StringComparer.Ordinal);
                var types = new[] { typeof(CardRow), typeof(DeckRow), typeof(RulesRow), typeof(HomeRow), typeof(BossSlotRow), typeof(PresentationRow) };
                foreach (var type in types)
                    foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    {
                        var kind = KindOf(field.FieldType);
                        if (kind == null) continue;
                        if (map.TryGetValue(field.Name, out var existing) && existing != kind) map[field.Name] = "?";
                        else map[field.Name] = kind;
                    }
                _kinds = map;
                return _kinds;
            }
        }

        /// <summary>把字段类型映射为检查类型。输入：字段类型；输出：检查类型名（不需要检查时为 null）。</summary>
        static string KindOf(System.Type type)
        {
            if (type == typeof(int)) return "integer";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "float";
            if (type == typeof(bool)) return "bool";
            return null;
        }

        /// <summary>判断一个值是否满足期望类型。输入：值文本与检查类型；输出：是否合法。</summary>
        static bool IsValid(string value, string kind)
        {
            switch (kind)
            {
                case "integer": return Regex.IsMatch(value, @"^-?\d+$");
                case "float": return Regex.IsMatch(value, @"^-?\d+(\.\d+)?([eE][+-]?\d+)?$");
                case "bool": return value == "true" || value == "false";
                default: return true;
            }
        }

        /// <summary>把检查类型转成中文描述。输入：检查类型；输出：中文名。</summary>
        static string Describe(string kind)
        {
            switch (kind)
            {
                case "integer": return "整数";
                case "float": return "小数";
                case "bool": return "布尔值（true/false）";
                default: return "值";
            }
        }
    }
}
