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
        /// <summary>
        /// 调试用强制牌堆（卡种 ID → 张数），只在本次运行会话内有效。
        /// 为什么保留：选卡界面只能勾选卡种、张数由阶级比例推导，无法构造“30 张同一种”这类压测牌堆；
        /// 这张表整体覆盖选卡结果，不写入任何资产，也不改变 Excel 配置。
        /// 全为 0 或为空表示“按选卡结果生成”。
        /// </summary>
        readonly Dictionary<string, int> _forcedDeck = new Dictionary<string, int>(StringComparer.Ordinal);

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
            _forcedDeck.Clear();
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
                if (!JsonShapeValidator.ValidateSpawnMigration(cleaned, out error)) return false;
                var document = JsonUtility.FromJson<ConfigDocument>(cleaned);
                if (document == null) { error = "JSON 无法解析为配置文档"; return false; }
                document.schemaVersion = document.schemaVersion == 0 ? 2 : document.schemaVersion;
                _document = document;
                // 导入的是另一份文档，旧的强制牌堆可能引用已不存在的卡种，一律清空。
                _forcedDeck.Clear();
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
                // 文档路径会用“默认选卡”生成一份抽牌堆；若调试面板填了强制牌堆，则整体覆盖它。
                var candidate = new GameConfiguration(_document);
                if (HasForcedDeck)
                {
                    var forced = BuildForcedDeck();
                    if (forced == null) { error = "强制牌堆引用了不存在的卡种"; return false; }
                    candidate = candidate.WithDeck(forced, false);
                }
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
        /// 按卡种 ID 查找卡行。
        /// 输入：卡种 ID；输出：卡行，找不到时为 null。
        /// </summary>
        public CardRow FindCard(string cardTypeId)
        {
            if (string.IsNullOrEmpty(cardTypeId) || _document.cards == null) return null;
            foreach (var card in _document.cards)
                if (card != null && card.id == cardTypeId) return card;
            return null;
        }

        /// <summary>
        /// 是否设置了强制牌堆（任一张数大于 0）。
        /// 输入：无；输出：需要覆盖抽牌堆时为 true。
        /// </summary>
        public bool HasForcedDeck => _forcedDeck.Values.Any(v => v > 0);

        /// <summary>
        /// 读取强制牌堆中某卡种的张数。
        /// 输入：卡种 ID；输出：张数（未登记为 0）。
        /// </summary>
        public int GetForcedDeckCount(string cardTypeId)
        {
            if (string.IsNullOrEmpty(cardTypeId)) return 0;
            return _forcedDeck.TryGetValue(cardTypeId, out var count) ? count : 0;
        }

        /// <summary>
        /// 逐键写入强制牌堆张数（不抛异常，供面板实时编辑）。
        /// 输入：卡种 ID 与非负张数；输出：无。数量 0 表示该卡种不进强制牌堆。
        /// </summary>
        public void SetForcedDeckCount(string cardTypeId, int count)
        {
            if (string.IsNullOrEmpty(cardTypeId)) return;
            var value = count < 0 ? 0 : count;
            if (value == 0) _forcedDeck.Remove(cardTypeId);
            else _forcedDeck[cardTypeId] = value;
        }

        /// <summary>
        /// 清空强制牌堆，恢复“按选卡结果生成”。
        /// 输入：无；输出：无。
        /// </summary>
        public void ClearForcedDeck() => _forcedDeck.Clear();

        /// <summary>
        /// 把强制牌堆转换为 DeckEntry 清单。
        /// 输入：无；输出：清单；任一卡种在当前文档中不存在时返回 null（由调用方给出错误）。
        /// 顺序按 Cards 表顺序，保证同一份强制牌堆每次开局完全一致。
        /// </summary>
        public IReadOnlyList<DeckEntry> BuildForcedDeck()
        {
            var entries = new List<DeckEntry>();
            foreach (var card in _document.cards ?? Array.Empty<CardRow>())
            {
                if (card == null) continue;
                if (!_forcedDeck.TryGetValue(card.id, out var count) || count <= 0) continue;
                entries.Add(new DeckEntry(card.id, count));
            }
            return entries.Count == 0 ? null : entries;
        }

        /// <summary>
        /// 统计当前抽牌堆构成（排序后供面板展示）。
        /// 输入：无；输出：总张数与初级/中级/高级张数。
        /// 来源：设置了强制牌堆时按强制牌堆统计，否则按“默认选卡 + 阶级比例”统计，
        /// 与正式开局的实际抽牌堆保持同一口径。
        /// </summary>
        public (int Total, int Basic, int Intermediate, int Advanced) GetDrawPileSummary()
        {
            var counts = new Dictionary<CardTier, int>
            {
                [CardTier.Basic] = 0, [CardTier.Intermediate] = 0, [CardTier.Advanced] = 0
            };
            var total = 0;
            if (HasForcedDeck)
            {
                foreach (var entry in BuildForcedDeck() ?? Array.Empty<DeckEntry>())
                {
                    var tier = CardTiers.Parse(FindCard(entry.TypeId)?.tier);
                    if (!counts.ContainsKey(tier)) continue;
                    counts[tier] += entry.Count;
                    total += entry.Count;
                }
                return (total, counts[CardTier.Basic], counts[CardTier.Intermediate], counts[CardTier.Advanced]);
            }
            var allocate = DeckComposition.AllocateTierCounts(new DeckConfig(_document.rules[0]));
            for (var i = 0; i < DeckComposition.PlayerTiers.Length; i++)
            {
                var tier = DeckComposition.PlayerTiers[i];
                counts[tier] = allocate[i];
                total += allocate[i];
            }
            return (total, counts[CardTier.Basic], counts[CardTier.Intermediate], counts[CardTier.Advanced]);
        }

        /// <summary>
        /// 按分类新增一个临时卡种。
        /// 输入：分类（resource/attack/special）；输出：新建的卡行。
        /// 命名与参考配置界面一致：ID 为 custom_分类_序号，名字为“新资源卡N/新战斗卡N/新特殊卡N”，
        /// 均避让已有 ID 与重名；默认 1 血 0 攻 0 移动、初级阶级。
        /// 为什么不再登记卡组数量：牌堆已由“张数 + 阶级比例”推导，具体携带哪些卡在开局选卡界面决定。
        /// </summary>
        public CardRow AddCardType(string category)
        {
            var normalized = category == "attack" || category == "special" ? category : "resource";
            var prefix = normalized == "resource" ? "新资源卡" : normalized == "special" ? "新特殊卡" : "新战斗卡";
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var card in _document.cards ?? Array.Empty<CardRow>())
                if (card != null && !string.IsNullOrWhiteSpace(card.name)) names.Add(card.name.Trim());
            var index = 1;
            while (FindCard($"custom_{normalized}_{index}") != null || names.Contains(prefix + index)) index++;
            var row = new CardRow
            {
                id = $"custom_{normalized}_{index}",
                name = prefix + index,
                category = normalized,
                tier = "basic",
                viewKey = "default",
                hp = 1,
                moveDistance = 0,
                requiresUpgrade = false,
                upgradeFrom = string.Empty
            };
            var cards = new List<CardRow>(_document.cards ?? Array.Empty<CardRow>()) { row };
            _document.cards = cards.ToArray();
            return row;
        }

        /// <summary>
        /// 读取 Boss 卡种行。
        /// 输入：无；输出：rules[0].bossCardType 指向的卡行（缺失时为 null）。
        /// </summary>
        public CardRow GetBossCard()
        {
            if (_document.rules == null || _document.rules.Length == 0 || _document.rules[0] == null) return null;
            return FindCard(_document.rules[0].bossCardType);
        }

        /// <summary>
        /// 新增一个临时卡种。
        /// 输入：卡种字段（由面板收集）；输出：是否成功与错误文本。
        /// 说明：临时卡种同样进入草稿，不写入任何资产；携带与否由选卡界面决定。
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
            (@"""row""\s*:\s*-?\d+\s*,\s*""col""\s*:\s*-?\d+", "已移除旧家坐标 row/col（改为按边缘居中规则定位）"),
            (@"""deck""\s*:\s*\[[^\]]*\]", "已移除 Deck 表：抽牌堆改由 Rules 的 drawPileSize + 三阶级比例生成，携带卡种由开局选卡决定")
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
        /// <summary>
        /// 校验能力迁移的显式字段。输入为 JSON 文本，输出为错误；不静默补默认值，避免旧草稿意外改变目标权限。
        /// Cards/Rules 是平面行数组，按行检查显式字段；数值范围和引用继续交给 ConfigValidation 汇总。
        /// </summary>
        public static bool ValidateSpawnMigration(string json, out string error)
        {
            error = null;
            if (Regex.IsMatch(json, "\"bossSpawnerSpawnCardType\"\\s*:"))
            {
                error = "旧字段 Rules.bossSpawnerSpawnCardType 已移除，请迁移至对应 Cards 行的 spawnComponent=true 与 spawnCardType，并补齐阶段配置。";
                return false;
            }
            return RequireRowFields(json, "cards", new[] { "spawnComponent", "spawnCardType", "canAttackResources", "canSwap" }, out error) &&
                RequireRowFields(json, "rules", new[] { "bossPhaseTwoHp", "bossWarshipColumn", "bossWarshipCardType" }, out error);
        }

        /// <summary>
        /// 输入平面表行 JSON、表名和必填字段，输出迁移错误。完整匹配字符串令牌，避免卡名中的括号或引号截断行。
        /// 仅检测字段存在性；格式解析仍由 JsonUtility 完成，缺失整张表由业务校验报告。
        /// </summary>
        static bool RequireRowFields(string json, string table, string[] fields, out string error)
        {
            error = null;
            const string jsonString = "\"(?:\\\\.|[^\"\\\\])*\"";
            var array = Regex.Match(json, "\"" + table + "\"\\s*:\\s*\\[((?:" + jsonString + "|[^\\]\"])*)\\]", RegexOptions.Singleline);
            if (!array.Success) return true;
            foreach (Match row in Regex.Matches(array.Groups[1].Value, "\\{(?:" + jsonString + "|[^{}\"])*\\}"))
            {
                var keys = new HashSet<string>();
                foreach (Match property in Regex.Matches(row.Value, "(?<key>" + jsonString + ")\\s*:\\s*(?:" + jsonString + "|[^,{}]+)"))
                    keys.Add(property.Groups["key"].Value.Trim('"'));
                foreach (var field in fields)
                    if (!keys.Contains(field))
                    {
                        error = "旧 " + table + " 配置缺少 " + field + "，请按新工作簿补齐字段后重新导入；迁移步骤见 docs/使用说明.md。";
                        return false;
                    }
            }
            return true;
        }

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
                var types = new[] { typeof(CardRow), typeof(RulesRow), typeof(HomeRow), typeof(BossSlotRow), typeof(PresentationRow) };
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
