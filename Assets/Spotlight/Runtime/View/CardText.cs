// 卡牌文本与配色的集中格式化工具。
// 为什么需要：手牌、棋盘单位、单位详情与日志需要一致的属性文本与语义颜色，
// 若各处各写一遍，很容易出现同一数值两种写法，也会让“分类颜色来自配置表”的约束被绕过。
using System.Text;
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 卡牌展示文本与颜色解析工具（纯函数，无状态）。
    /// 输入：卡种定义/卡种描述与表现配置；输出：可直接放入 TMP 文本或 Image 颜色的结果。
    /// </summary>
    public static class CardText
    {
        /// <summary>分类中文名。输入：分类枚举；输出：界面文本。</summary>
        public static string CategoryLabel(CardCategory category) =>
            category == CardCategory.Resource ? "资源" : category == CardCategory.Attack ? "攻击" : "特殊";

        /// <summary>
        /// 把 Cards 表的分类文本解析成枚举。
        /// 输入：resource / attack / special 文本；输出：对应枚举（其它值按特殊卡兜底）。
        /// 为什么需要：邻域部署要求在配置里以文本保存，界面需要把同一文本显示成中文分类名。
        /// </summary>
        public static CardCategory ParseCategory(string text) =>
            text == "resource" ? CardCategory.Resource : text == "attack" ? CardCategory.Attack : CardCategory.Special;

        /// <summary>
        /// 卡种属性简写（手牌与棋盘共用）。
        /// 输入：卡种定义；输出：形如 “生命 4 · 攻 2 · 程 2 · 移 3 · 产 1 · 耗 0” 的单行文本。
        /// 只显示该卡种真实拥有的能力，避免在资源卡上显示无意义的攻击字段。
        /// </summary>
        public static string StatLine(CardDefinitionSO def)
        {
            if (def == null) return string.Empty;
            return StatLine(def.ToCardRow());
        }

        /// <summary>
        /// 棋盘单位属性简写。
        /// 输入：卡种定义与当前单位快照；输出：与手牌同格式但数值来自场上的单行文本。
        /// 为什么需要它：开牌、抽牌堆与墓地中的卡牌显示基础配置，而棋盘上的单位必须显示当前攻击/射程/移动，
        /// 攻击被降到 0 时也必须能直接看到 0，不能退回显示配置初值。
        /// </summary>
        public static string StatLine(CardDefinitionSO def, UnitState unit)
        {
            if (def == null) return string.Empty;
            if (unit == null) return StatLine(def);
            var row = def.ToCardRow();
            return StatLine(row, unit.Attack, unit.Range, unit.MoveDistance, unit.MaxHp);
        }

        /// <summary>
        /// 卡种属性简写（基于规则层行对象）。
        /// 输入：Cards 行数据；输出：与 StatLine(CardDefinitionSO) 完全一致的单行文本。
        /// 为什么保留两个重载：调试面板只持有 CardRow，UI 只持有 SO，两者必须显示同一结果。
        /// </summary>
        public static string StatLine(CardRow row)
        {
            if (row == null) return string.Empty;
            return StatLine(row, row.atk, row.range, row.moveDistance, row.hp);
        }

        /// <summary>
        /// 按指定属性值拼卡面简写（手牌与棋盘共用）。
        /// 输入：卡行（只用于分类与能力开关）、当前攻击/射程/移动与生命上限；输出：单行文本。
        /// 为什么把数值作为参数：手牌传配置值、棋盘传单位快照值，两者共用一套格式与显示规则。
        /// </summary>
        static string StatLine(CardRow row, int attack, int range, int moveDistance, int hp)
        {
            if (row == null) return string.Empty;
            var sb = new StringBuilder();
            sb.Append("生命 ").Append(hp);
            // 攻击与射程只要“曾经有”就一直显示：被一次性减攻降到 0 的单位必须能看到攻 0，而不是整行消失。
            if (attack > 0 || row.atk > 0) sb.Append(" · 攻 ").Append(attack).Append(" · 程 ").Append(range);
            if (moveDistance > 0) sb.Append(" · 移 ").Append(moveDistance);
            if (row.resourceComponent) sb.Append(" · 产 ").Append(row.produce).Append(" · 耗 ").Append(row.upkeep);
            // 部署消耗只在非 0 时显示，避免免费卡的行被无关信息占满。
            if (row.deployCost > 0) sb.Append(" · 部署耗 ").Append(row.deployCost);
            if (row.requiresUpgrade && !string.IsNullOrEmpty(row.upgradeFrom)) sb.Append(" · 升级自 ").Append(row.upgradeFrom);
            return sb.ToString();
        }

        /// <summary>
        /// 一次性部署减攻的说明文本。
        /// 输入：减攻量与可选单位快照；输出：如“一次性部署减攻：待触发（减 2）”或“已消耗”的提示行。
        /// 为什么集中在这里：手牌、单位详情与棋盘单位都使用同一文案，数值来自配置而不写死。
        /// </summary>
        public static string DeploymentPenaltyLine(int reduction, UnitState unit)
        {
            if (reduction <= 0) return string.Empty;
            var state = unit == null ? "手牌待部署" : unit.DeploymentPenaltyPending ? "待触发" : "已消耗";
            return $"一次性部署减攻：{state}（减攻 {reduction}）";
        }

        /// <summary>
        /// 单位详情面板使用的完整描述。
        /// 输入：卡种描述与卡牌记录阵营；输出：多行文本，包含完整名称、分类、属性与部署限制。
        /// </summary>
        public static string DetailLine(CardArchetype arch, Side owner)
        {
            if (arch == null) return string.Empty;
            var sb = new StringBuilder();
            sb.AppendLine(arch.Name + "（" + CategoryLabel(arch.Category) + " · " + (owner == Side.Player ? "玩家" : "Boss") + "）");
            sb.Append("生命 ").Append(arch.Combat != null ? arch.Combat.Hp : 0);
            sb.Append(" · 攻击 ").Append(arch.Combat != null ? arch.Combat.Attack : 0);
            sb.Append(" · 射程 ").Append(arch.Combat != null ? arch.Combat.Range : 0);
            sb.Append(" · 移动 ").Append(arch.Combat != null ? arch.Combat.MoveDistance : 0);
            if (arch.Resource != null) sb.Append(" · 产出 ").Append(arch.Resource.Produce).Append(" · 维持 ").Append(arch.Resource.Upkeep);
            if (arch.DeployHomeOnly) sb.AppendLine().Append("部署限制：仅己方区域");
            if (arch.AvoidAdjacentSameType) sb.AppendLine().Append("部署限制：不能与同类单位四邻");
            if (arch.DeployCost > 0) sb.AppendLine().Append("部署消耗：").Append(arch.DeployCost).Append(" 资源");
            if (arch.DeployRequireAdjacentCount > 0)
                sb.AppendLine().Append("部署要求：四邻至少 ").Append(arch.DeployRequireAdjacentCount).Append(" 个").Append(CategoryLabel(ParseCategory(arch.DeployRequireAdjacentCategory))).Append("单位");
            if (arch.RequiresUpgrade) sb.AppendLine().Append("覆盖升级自：").Append(arch.UpgradeFrom);
            if (arch.Teleport != null && arch.Teleport.PermitsAttackDeploy) sb.AppendLine().Append("扩展：允许己方攻击卡部署在四邻");
            if (arch.DeploymentPenalty != null)
                sb.AppendLine().Append(DeploymentPenaltyLine(arch.DeploymentPenalty.AttackReduction, null));
            if (arch.MarkOnEnter != null)
                sb.AppendLine().Append("入场标记：半径 ").Append(arch.MarkOnEnter.Radius).Append(" 内的其它单位被标记，阵亡时给标记者 ").Append(arch.MarkOnEnter.Reward).Append(" 资源");
            if (arch.DeathGrowth != null)
                sb.AppendLine().Append("死亡加攻：任一单位阵亡时攻击 +").Append(arch.DeathGrowth.PerKill).Append("，最多触发 ").Append(arch.DeathGrowth.Limit).Append(" 次");
            if (arch.AttacksAlliesAsHeal)
                sb.AppendLine().Append("可攻击友方：对友方的攻击改为治疗（生命 + 当前攻击，受生命上限限流）");
            if (arch.Instant != null)
                sb.AppendLine().Append("即时卡：部署后立即结算（获得 ").Append(arch.Instant.GainResource).Append(" 资源）并离场");
            return sb.ToString();
        }

        /// <summary>
        /// 棋盘单位详情：卡种描述 + 当前属性与能力状态。
        /// 输入：卡种描述与单位快照；输出：多行文本。
        /// 为什么单独成方法：单位详情必须先给当前值（当前生命/上限、当前攻击/射程/移动），
        /// 再给能力状态；手牌详情仍用 DetailLine 显示基础配置。
        /// </summary>
        public static string UnitDetail(CardArchetype arch, UnitState unit)
        {
            if (arch == null || unit == null) return string.Empty;
            var combat = arch.Combat;
            var sb = new StringBuilder();
            sb.AppendLine(arch.Name + "（" + CategoryLabel(arch.Category) + " · " + (unit.Owner == Side.Player ? "玩家" : "Boss") + "）");
            sb.Append("当前生命 ").Append(unit.Hp).Append('/').Append(unit.MaxHp);
            sb.Append(" · 当前攻击 ").Append(unit.Attack);
            sb.Append(" · 射程 ").Append(unit.Range);
            sb.Append(" · 移动 ").Append(unit.MoveDistance);
            if (arch.Resource != null) sb.Append(" · 产出 ").Append(arch.Resource.Produce).Append(" · 维持 ").Append(arch.Resource.Upkeep);
            sb.AppendLine();
            sb.Append("基础配置：生命 ").Append(combat != null ? combat.Hp : 0);
            sb.Append(" · 攻击 ").Append(combat != null ? combat.Attack : 0);
            sb.Append(" · 射程 ").Append(combat != null ? combat.Range : 0);
            sb.Append(" · 移动 ").Append(combat != null ? combat.MoveDistance : 0);
            if (arch.DeployHomeOnly) sb.AppendLine().Append("部署限制：仅己方区域");
            if (arch.AvoidAdjacentSameType) sb.AppendLine().Append("部署限制：不能与同类单位四邻");
            if (arch.DeployCost > 0) sb.AppendLine().Append("部署消耗：").Append(arch.DeployCost).Append(" 资源");
            if (arch.DeployRequireAdjacentCount > 0)
                sb.AppendLine().Append("部署要求：四邻至少 ").Append(arch.DeployRequireAdjacentCount).Append(" 个").Append(CategoryLabel(ParseCategory(arch.DeployRequireAdjacentCategory))).Append("单位");
            if (arch.RequiresUpgrade) sb.AppendLine().Append("覆盖升级自：").Append(arch.UpgradeFrom);
            if (arch.Teleport != null && arch.Teleport.PermitsAttackDeploy) sb.AppendLine().Append("扩展：允许己方攻击卡部署在四邻");
            if (unit.DeploymentAttackReduction > 0)
                sb.AppendLine().Append(DeploymentPenaltyLine(unit.DeploymentAttackReduction, unit));
            if (arch.MarkOnEnter != null)
                sb.AppendLine().Append("入场标记：半径 ").Append(arch.MarkOnEnter.Radius).Append(" 内的其它单位被标记，阵亡时给标记者 ").Append(arch.MarkOnEnter.Reward).Append(" 资源");
            // 标记是永久状态：来源离场后依然显示，便于玩家预判剩余收益。
            if (unit.MarkedBy.HasValue)
                sb.AppendLine().Append("已被标记：阵亡时给").Append(unit.MarkedBy.Value == Side.Player ? "玩家" : "Boss").Append(" ").Append(unit.MarkReward).Append(" 资源");
            if (unit.DeathGrowthLimit > 0)
                sb.AppendLine().Append("死亡加攻：已触发 ").Append(unit.DeathGrowthTriggers).Append('/').Append(unit.DeathGrowthLimit).Append("（每次 +").Append(unit.DeathGrowthPerKill).Append(" 攻击）");
            return sb.ToString();
        }

        /// <summary>
        /// 解析 #RRGGBB / #RRGGBBAA 颜色文本。
        /// 输入：颜色文本与失败时的回退颜色；输出：可用颜色。
        /// </summary>
        public static Color Parse(string hex, Color fallback) => ColorUtility.TryParseHtmlString(hex, out var color) ? color : fallback;

        /// <summary>
        /// 分类语义颜色。输入：分类与表现配置；输出：该分类的代表色。
        /// </summary>
        public static Color CategoryColor(CardCategory category, PresentationConfig presentation)
        {
            if (presentation == null) return Color.white;
            return category == CardCategory.Resource
                ? Parse(presentation.ResourceColor, new Color(0.2f, 0.67f, 0.4f))
                : category == CardCategory.Attack
                    ? Parse(presentation.AttackColor, new Color(0.93f, 0.53f, 0.2f))
                    : Parse(presentation.SpecialColor, new Color(0.67f, 0.33f, 1f));
        }

        /// <summary>
        /// 阵营颜色。输入：阵营与表现配置；输出：玩家或 Boss 的代表色。
        /// </summary>
        public static Color OwnerColor(Side side, PresentationConfig presentation)
        {
            if (presentation == null) return side == Side.Player ? new Color(0.2f, 0.4f, 1f) : new Color(1f, 0.2f, 0.2f);
            return side == Side.Player
                ? Parse(presentation.PlayerColor, new Color(0.2f, 0.4f, 1f))
                : Parse(presentation.BossColor, new Color(1f, 0.2f, 0.2f));
        }

        /// <summary>
        /// 高亮语义颜色。
        /// 输入：高亮种类（deploy/move/attack/swap，其它值返回 null）与表现配置；输出：可空颜色。
        /// 视图用它把 LegalActions 的四类可选格转换成棋盘上的颜色。
        /// </summary>
        public static Color? HighlightColor(string kind, PresentationConfig presentation)
        {
            if (presentation == null) return null;
            switch (kind)
            {
                case "deploy": return Parse(presentation.DeployColor, new Color(0.2f, 1f, 0.6f));
                case "move": return Parse(presentation.MoveColor, new Color(0.2f, 0.67f, 1f));
                case "attack": return Parse(presentation.AttackTargetColor, new Color(1f, 0.2f, 0.2f));
                // 治疗复用特殊卡紫色：不新增 Presentation 列，又能与敌方目标红区分“打/奶”。
                case "heal": return Parse(presentation.SpecialColor, new Color(0.67f, 0.33f, 1f));
                case "swap": return Parse(presentation.SwapColor, new Color(0.67f, 0.2f, 1f));
                default: return null;
            }
        }
    }
}
