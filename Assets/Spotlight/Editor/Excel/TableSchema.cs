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
        /// <summary>策划可读的字段说明，默认工作簿写入表头批注，避免说明行混入正式数据。</summary>
        public string Description { get; }

        /// <summary>构造列声明。输入：名称、类型、必需性和可选中文说明；输出：校验和批注共用的声明对象。</summary>
        public ColumnDefinition(string name, ColumnType type, bool required, string description = null)
        {
            Name = name;
            Type = type;
            Required = required;
            Description = description;
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
        public static readonly string[] RequiredSheets = { "Cards", "Rules", "Homes", "BossSlots", "Presentation" };

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
                new ColumnDefinition("upgradeFrom", ColumnType.Text, false),
                new ColumnDefinition("deploymentPenaltyComponent", ColumnType.Boolean, true, "是否启用一次性部署减攻组件，默认 false。启用后该单位在场期间可使用一次：使随后成功上场的一个同阵营攻击类单位攻击力降低 deploymentAttackReduction。未启用时必须填 false，且减攻量必须为 0。"),
                new ColumnDefinition("deploymentAttackReduction", ColumnType.Integer, true, "一次性部署减攻量，默认 0，非负整数。启用组件时必须为正整数；禁用时必须为 0。减攻下限为 0，持续到目标单位离场；京都壹号默认为 2。"),
                new ColumnDefinition("deployCost", ColumnType.Integer, true, "成功部署这张卡的一次性资源消耗，默认 0，非负整数。资源不足时该目标不合法（高亮也不会出现）；Boss 部署不受此限制。开普勒-I/II 为 10。"),
                new ColumnDefinition("deployRequireAdjacentCategory", ColumnType.Text, false, "部署时四邻（上下左右）必须存在的单位分类：resource/attack/special；默认留空表示无要求。必须与 deployRequireAdjacentCount 同时使用，数量为 0 时本列必须留空。"),
                new ColumnDefinition("deployRequireAdjacentCount", ColumnType.Integer, false, "四邻中该分类单位的最小数量，默认 0（0 表示无要求）。只统计在场单位，不含家，不区分阵营。"),
                new ColumnDefinition("markOnEnterComponent", ColumnType.Boolean, true, "是否启用入场标记能力，默认 false。启用后该单位入场时把半径内的其它单位标记（先到先得、不叠加），被标记单位阵亡时给标记者阵营发资源。未启用时必须填 false，半径与奖励必须为 0。"),
                new ColumnDefinition("markOnEnterRadius", ColumnType.Integer, true, "标记半径，默认 0，正整数。1 表示以自身为中心的 3×3 八邻（不含自己）；启用能力时必须为正整数。"),
                new ColumnDefinition("markRewardResource", ColumnType.Integer, true, "被标记单位阵亡时给标记者阵营的资源，默认 0，正整数。启用能力时必须为正整数；Boss 侧没有资源系统，Boss 标记暂不产生资源。"),
                new ColumnDefinition("deathGrowthComponent", ColumnType.Boolean, true, "是否启用死亡加攻能力，默认 false。启用后全棋盘任一单位因战斗阵亡时本单位攻击力 +deathGrowthPerKill，最多触发 deathGrowthLimit 次。未启用时必须填 false，加成与上限必须为 0。"),
                new ColumnDefinition("deathGrowthPerKill", ColumnType.Integer, true, "每次全棋盘单位因战斗阵亡时增加的攻击力，默认 0，正整数。启用能力时必须为正整数。"),
                new ColumnDefinition("deathGrowthLimit", ColumnType.Integer, true, "死亡加攻最多触发次数，默认 0，正整数。注意是“触发次数上限”，不是“攻击力上限”；达到后不再增加。"),
                new ColumnDefinition("attacksAlliesAsHeal", ColumnType.Boolean, true, "是否允许攻击友方单位并把对友方的结算改为治疗（生命 + 当前攻击，受生命上限限流），默认 false。只允许攻击类卡启用；满血也允许白打并消耗行动。"),
                new ColumnDefinition("leaveAfterDeploy", ColumnType.Boolean, true, "是否“部署即结算、结算即退场”（即时卡），默认 false。启用后该卡在场时间视为 0：不参与一次性减攻与入场标记，结算完 deployGainResource 后立即离场（原因为 Consumed，不触发补兵/死亡加攻/标记奖励）。Boss 卡种与刷怪卡卡种不得指向即时卡。"),
                new ColumnDefinition("deployGainResource", ColumnType.Integer, true, "即时卡结算时立即获得的资源，默认 0，正整数。启用 leaveAfterDeploy 时必须为正整数，禁用时必须为 0。"),
                new ColumnDefinition("tier", ColumnType.Text, true, "卡牌阶级，独立于 category：basic=初级，intermediate=中级，advanced=高级；Boss 专用卡填 none（不适用），不可加入玩家卡组。必须显式填写；仅用于卡组张数统计，暂不限制携带数量与部署回合。"),
                new ColumnDefinition("adjacentClearanceComponent", ColumnType.Boolean, true, "是否启用四邻空格限制能力，默认 false。启用后该单位在部署、移动与互换前后，上下左右四邻中至少要有 adjacentClearanceMinEmpty 格为空；斜角不计，棋盘外按空计算。未启用时必须填 false，且阈值为 0。"),
                new ColumnDefinition("adjacentClearanceMinEmpty", ColumnType.Integer, true, "四邻中至少需要为空的格数，默认 0（关闭），启用时填 1～4。范围之外的数值（包括 5）一律报错；关闭时必须为 0。后羿-II综合舰填 2。"),
                new ColumnDefinition("killActionComponent", ColumnType.Boolean, true, "是否启用击杀恢复行动，默认 false。启用后该单位的一次直接攻击击杀目标战斗单位（包括伤害标记追加击杀）时恢复一次普通行动，可连续触发，但不是重开回合：不抽牌、不产资源、不重复扣维持费，未使用机会不跨回合保存。仅攻击类卡可启用。维京长船-2056 填 true。"),
                new ColumnDefinition("damageMarkComponent", ColumnType.Boolean, true, "是否在直接攻击后施加伤害标记，默认 false。启用后每次直接攻击存活的目标战斗单位获得 1 层；新层从下一次受伤起生效，永久保留且可叠层，家不挂层。仅攻击类卡可启用，未启用时 damageMarkDamagePerStack 必须为 0。SNP-EU 填 true。"),
                new ColumnDefinition("damageMarkDamagePerStack", ColumnType.Integer, true, "每层伤害标记在之后每次直接受伤时追加的伤害，默认 0（关闭），正整数。追加伤害不递归触发标记、不挂新层，也不消耗炸弹预算；正常攻击、炸弹等直接伤害均可触发。SNP-EU 填 1。")
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
                new ColumnDefinition("lineBlocking", ColumnType.Boolean, true),
                new ColumnDefinition("bossBombInterval", ColumnType.Integer, true),
                new ColumnDefinition("bossBombDamage", ColumnType.Integer, true),
                new ColumnDefinition("bossBombColumn", ColumnType.Integer, true),
                new ColumnDefinition("bossSpawnerAttackThreshold", ColumnType.Integer, true, "刷怪卡触发总攻击力，默认 12，正整数。每个 Boss 回合开始统计一次全部在场玩家单位的攻击力（包括资源单位），大于等于此值才继续检查数量。"),
                new ColumnDefinition("bossSpawnerCountFromColumn", ColumnType.Integer, true, "攻击单位统计起始列，默认 4，正整数。从玩家一侧第 1 列起算，包含本列及之后各列；超过棋盘宽度时范围为空。每个 Boss 回合开始生效。"),
                new ColumnDefinition("bossSpawnerUnitThreshold", ColumnType.Integer, true, "前线玩家攻击类单位数量下限，默认 2，正整数。仅统计 category=attack，不以攻击力是否非零判断。与总攻击力条件同时满足才触发。"),
                new ColumnDefinition("bossSpawnerDeployFromColumn", ColumnType.Integer, true, "刷怪卡部署起始列，默认 5，正整数。从玩家一侧第 1 列起算，包含本列及之后的空格；超过棋盘宽度或没有空格则跳过当次机会，不累计。"),
                new ColumnDefinition("bossSpawnerCardType", ColumnType.Text, true, "刷怪卡卡种 ID，默认 bossSpawner，须引用已注册特殊卡。生命可配置为正整数，攻击/射程/移动为 0，无资源和传送能力。场上最多一张，不能移动或互换，部署后下一个 Boss 回合开始生成。"),
                new ColumnDefinition("bossSpawnerSpawnCardType", ColumnType.Text, true, "生成战机卡种 ID，默认 bossCombat，须引用已注册攻击类卡种。每个 Boss 回合在八邻随机空格生成一架；满格随机顶替玩家单位，无目标则暂停，下回合重试。遵守召唤失调，不占固定兵位。"),
                new ColumnDefinition("drawPileSize", ColumnType.Integer, true, "玩家抽牌堆总张数，默认 24，必须至少 3。开局按此数量生成卡牌记录；不再由 Deck 表逐卡种配置（Deck 表已废除）。"),
                new ColumnDefinition("tierRatioBasic", ColumnType.Integer, true, "初级卡在抽牌堆中的占比百分比，默认 70，必须至少 1。三阶级比例之和必须为 100，不支持 0%。"),
                new ColumnDefinition("tierRatioIntermediate", ColumnType.Integer, true, "中级卡在抽牌堆中的占比百分比，默认 20，必须至少 1。"),
                new ColumnDefinition("tierRatioAdvanced", ColumnType.Integer, true, "高级卡在抽牌堆中的占比百分比，默认 10，必须至少 1。"),
                new ColumnDefinition("maxTypesBasic", ColumnType.Integer, true, "玩家每局最多携带的初级卡种数量，默认 5，必须至少 1。实际可用上限 = min(本值, 初级卡种数)。"),
                new ColumnDefinition("maxTypesIntermediate", ColumnType.Integer, true, "玩家每局最多携带的中级卡种数量，默认 3，必须至少 1。"),
                new ColumnDefinition("maxTypesAdvanced", ColumnType.Integer, true, "玩家每局最多携带的高级卡种数量，默认 2，必须至少 1；卡池不足时按 min(本值, 卡种数) 生效。")
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
