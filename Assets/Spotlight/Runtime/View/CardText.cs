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
        /// 卡种属性简写（基于规则层行对象）。
        /// 输入：Cards 行数据；输出：与 StatLine(CardDefinitionSO) 完全一致的单行文本。
        /// 为什么保留两个重载：调试面板只持有 CardRow，UI 只持有 SO，两者必须显示同一结果。
        /// </summary>
        public static string StatLine(CardRow row)
        {
            if (row == null) return string.Empty;
            var sb = new StringBuilder();
            sb.Append("生命 ").Append(row.hp);
            if (row.atk > 0) sb.Append(" · 攻 ").Append(row.atk).Append(" · 程 ").Append(row.range);
            if (row.moveDistance > 0) sb.Append(" · 移 ").Append(row.moveDistance);
            if (row.resourceComponent) sb.Append(" · 产 ").Append(row.produce).Append(" · 耗 ").Append(row.upkeep);
            if (row.requiresUpgrade && !string.IsNullOrEmpty(row.upgradeFrom)) sb.Append(" · 升级自 ").Append(row.upgradeFrom);
            return sb.ToString();
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
            if (arch.RequiresUpgrade) sb.AppendLine().Append("覆盖升级自：").Append(arch.UpgradeFrom);
            if (arch.Teleport != null && arch.Teleport.PermitsAttackDeploy) sb.AppendLine().Append("扩展：允许己方攻击卡部署在四邻");
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
                case "swap": return Parse(presentation.SwapColor, new Color(0.67f, 0.2f, 1f));
                default: return null;
            }
        }
    }
}
