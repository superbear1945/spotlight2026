// 配置契约与业务校验。
// 本文件刻意不引用 UnityEngine：Excel 编译器、调试 JSON 导入与 EditMode 测试可以共用同一套校验，
// 测试不需要创建场景或资源，正式运行时的 ScriptableObject 只是这些接口的一种实现。
// 行类型（*Row）描述“表形状”，CardArchetype/MatchConfig 描述“运行配置”，战斗中的可变状态不放在这里。
using System;
using System.Collections.Generic;
using System.Linq;

namespace Spotlight
{
    /// <summary>阵营。玩家与 Boss 共用同一套规则，仅配置与手牌来源不同。</summary>
    public enum Side { Player, Boss }

    /// <summary>卡种分类。分类独立保存，禁止通过“是否具有战斗组件”推断攻击卡。</summary>
    public enum CardCategory { Resource, Attack, Special }

    /// <summary>
    /// 卡牌阶级，独立于资源/攻击/特殊分类，用于卡组张数统计与后续携带限制。
    /// 输入：Cards.tier；输出：只读卡种属性。None 仅表示不适用，不能加入玩家卡组，Invalid 用于报告缺失或非法配置。
    /// 阶级不改变战斗属性、部署时机或覆盖升级规则。
    /// </summary>
    public enum CardTier { Invalid, None, Basic, Intermediate, Advanced }

    /// <summary>阶级配置的严格转换入口。输入：Excel/JSON 文本；输出：枚举，非法值保留为 Invalid 供统一校验报告。</summary>
    public static class CardTiers
    {
        /// <summary>解析阶级，不为旧配置猜测默认值。输入：basic/intermediate/advanced/none；输出：对应阶级或 Invalid。</summary>
        public static CardTier Parse(string value)
        {
            switch (value)
            {
                case "basic": return CardTier.Basic;
                case "intermediate": return CardTier.Intermediate;
                case "advanced": return CardTier.Advanced;
                case "none": return CardTier.None;
                default: return CardTier.Invalid;
            }
        }
    }

    /// <summary>
    /// 战斗基础属性配置。
    /// 为什么需要：规则内核只依赖接口，因此 SO、Excel 行对象和测试用的普通 C# 对象都能作为配置来源。
    /// 契约：Hp 是初始值/上限，不保存当前生命；Range 是曼哈顿射程；MoveDistance 为 0 表示不能移动。
    /// </summary>
    public interface ICombatConfig
    {
        /// <summary>初始生命与生命上限（正整数）。</summary>
        int Hp { get; }
        /// <summary>攻击力，0 表示没有攻击能力。</summary>
        int Attack { get; }
        /// <summary>曼哈顿攻击距离，0 表示不能攻击。</summary>
        int Range { get; }
        /// <summary>每次移动可经过的最大格数，0 表示不能移动。</summary>
        int MoveDistance { get; }
        /// <summary>是否允许直接攻击资源类单位；AI、手动命令和高亮共用。</summary>
        public bool CanAttackResources { get; }
        /// <summary>是否允许参与互换；资源分类的额外限制仍然生效。</summary>
        public bool CanSwap { get; }
    }

    /// <summary>
    /// 资源产出与维持费配置。
    /// 为什么需要：部署不消耗资源，只有每回合产出与维持费影响结算，因此单独抽出一个接口。
    /// </summary>
    public interface IResourceConfig
    {
        /// <summary>每个己方回合开始时产出的资源点数。</summary>
        int Produce { get; }
        /// <summary>每个己方回合开始时需要支付的维持费；付不起时该单位本回合不能行动。</summary>
        int Upkeep { get; }
    }

    /// <summary>
    /// 传送扩展配置。
    /// 为什么需要：首版只实现星门的“四邻部署扩展”，不代表已有单位可以瞬移或穿过单位。
    /// </summary>
    public interface ITeleportConfig
    {
        /// <summary>为 true 时，允许己方攻击卡部署在该单位上下左右相邻的空格。</summary>
        bool PermitsAttackDeploy { get; }
    }

    /// <summary>
    /// 一次性部署减攻配置。
    /// 输入：Excel 的 deploymentAttackReduction；输出：规则内核读取的只读减攻量（正整数）。
    /// 为什么需要独立接口：能力参数必须来自配置而不是代码常量（不能把“减 2”写死在规则里），
    /// 且规则内核只依赖接口，因此 SO、Excel 行对象与测试用的普通 C# 对象都能作为配置来源。
    /// 契约：只描述“每次成功上场后可以使用一次的减攻量”，不保存次数、目标与已消耗状态，
    /// 这些属于场上状态（UnitState），不属于卡种配置。
    /// </summary>
    public interface IDeploymentPenaltyConfig
    {
        /// <summary>使随后成功上场的一个同阵营攻击类单位降低的攻击力（正整数）。</summary>
        int AttackReduction { get; }
    }

    /// <summary>
    /// 入场标记配置（星棹收集系统）。
    /// 输入：Excel 的 markOnEnterRadius / markRewardResource；输出：规则内核读取的只读半径与奖励。
    /// 为什么需要独立接口：标记是按配置启用的能力，半径与奖励必须来自配置而不是代码常量；
    /// 规则内核只依赖接口，因此 SO、行对象与测试夹具都能作为配置来源。
    /// 契约：只描述“标记范围”和“阵亡奖励”，不保存谁被标记、被谁标记——那些属于场上状态（UnitState）。
    /// </summary>
    public interface IMarkOnEnterConfig
    {
        /// <summary>标记半径：1 表示以自身为中心的 3×3（不含自己）。</summary>
        int Radius { get; }
        /// <summary>被标记单位阵亡时给标记者阵营的资源点数（正整数）。</summary>
        int Reward { get; }
    }

    /// <summary>
    /// 死亡加攻配置（力场-I）。
    /// 输入：Excel 的 deathGrowthPerKill / deathGrowthLimit；输出：规则内核读取的只读加成与次数上限。
    /// 为什么需要独立接口：全棋盘任意单位因战斗阵亡时的加成幅度与上限必须来自配置而不是代码常量；
    /// 规则内核只依赖接口，因此 SO、行对象与测试夹具都能作为配置来源。
    /// 契约：只描述“每次阵亡加多少”和“最多触发几次”，不保存已触发次数——那是场上状态（UnitState）。
    /// </summary>
    public interface IDeathGrowthConfig
    {
        /// <summary>全棋盘任一单位因战斗阵亡时，本单位增加的攻击力（正整数）。</summary>
        int PerKill { get; }
        /// <summary>本单位最多触发几次该加成（正整数）；达到上限后不再增加。</summary>
        int Limit { get; }
    }

    /// <summary>
    /// 即时卡效果配置（战时储备这类“部署即结算、结算即退场”的功能卡）。
    /// 输入：Excel 的 deployGainResource；输出：规则内核读取的一次性资源收益。
    /// 为什么需要独立接口：效果数值必须来自配置而不是代码常量；
    /// 规则内核只依赖接口，因此 SO、行对象与测试夹具都能作为配置来源。
    /// 契约：只描述“结算时获得多少资源”，不描述何时退场——退场由 leaveAfterDeploy 开关决定。
    /// </summary>
    public interface IInstantEffectConfig
    {
        /// <summary>结算时立即获得的资源点数（正整数）。</summary>
        int GainResource { get; }
    }

    /// <summary>
    /// 四邻空格限制配置（后羿-II综合舰）。
    /// 输入：Excel 的 adjacentClearanceMinEmpty；输出：规则内核读取的最少空邻格数（1～4）。
    /// 为什么需要独立接口：阈值必须来自配置而不是代码常量；规则内核只依赖接口，
    /// 因此 SO、行对象与测试夹具都能作为配置来源。
    /// 契约：只描述“部署、移动与互换前后都必须满足的空格数量”，
    /// 不保存具体格子、不描述棋盘外如何计数——那属于规则实现。
    /// </summary>
    public interface IAdjacentClearanceConfig
    {
        /// <summary>上下左右四个方向中至少需要为空的数量（1～4）；斜角不计，棋盘外按空计算。</summary>
        int MinEmptyNeighbors { get; }
    }

    /// <summary>
    /// 击杀恢复行动（维京长船-2056）。
    /// 为什么做成空接口：该能力没有任何数值参数，以“非空配置”本身作为启用标识，
    /// 避免再序列化一个永远为 true 的布尔字段。
    /// 契约：只表示“本次直接攻击击杀目标后可再次行动”，
    /// 不描述恢复次数、回合上限或跨回合保存——这些属于规则语义，不是可填数值。
    /// </summary>
    public interface IKillActionConfig
    {
    }

    /// <summary>
    /// 伤害标记配置（SNP-EU）。
    /// 输入：Excel 的 damageMarkDamagePerStack；输出：每层标记在之后每次直接受伤时追加的伤害。
    /// 为什么与入场标记（IMarkOnEnterConfig）分开：两者行为完全不同——入场标记是“阵亡发资源”，
    /// 伤害标记是“直接受伤追加伤害”；复用同一接口会让两套机制互相污染。
    /// 契约：只描述每层追加值；一次直接攻击新增一层是机制语义，不是可配置项。
    /// </summary>
    public interface IDamageMarkConfig
    {
        /// <summary>每层标记在之后每次直接受伤时追加的伤害值（正整数）。</summary>
        int DamagePerStack { get; }
    }

    // 注意：以下所有 public 字段名都是 Excel 的正式列名；只读接口属性不参与列映射，
    // 因此新增列时必须同时在本类和编辑器表结构中登记，否则会被“未定义列”校验拦截。
    /// <summary>
    /// Cards 表的一行，也是卡种定义的全部数据来源。
    /// 输入：Excel 单元格或调试 JSON；输出：被 ConfigValidation 校验后用于构造 CardArchetype。
    /// 为什么合并三个接口：一张卡最多同时具有战斗、资源、传送三种能力，用开关字段控制启用，
    /// 不建立具体卡种子类，避免继承链膨胀。
    /// </summary>
    [Serializable]
    public sealed class CardRow : ICombatConfig, IResourceConfig, ITeleportConfig, IDeploymentPenaltyConfig, ISpawnConfig
    {
        /// <summary>稳定 ID，唯一且字母开头；改名不影响引用与升级关联。</summary>
        public string id;
        /// <summary>展示名称，非空且去除首尾空格后不得重复。</summary>
        public string name;
        /// <summary>分类文本，只允许 resource / attack / special。</summary>
        public string category;
        /// <summary>卡牌阶级：basic / intermediate / advanced；Boss 专用卡填 none。缺失值不自动补齐。</summary>
        public string tier;
        /// <summary>覆盖升级来源卡种 ID；为空表示普通卡。</summary>
        public string upgradeFrom;
        /// <summary>外观模板键，运行时映射到 Prefab 外观，改动数值不会重编译外观。</summary>
        public string viewKey;
        // 生命必须为正整数，其余数值为非负整数；移动距离不设置玩法上的固定上限。
        /// <summary>初始生命与上限。</summary>
        public int hp;
        /// <summary>攻击力。</summary>
        public int atk;
        /// <summary>曼哈顿射程。</summary>
        public int range;
        /// <summary>单次移动最大格数，0 表示不能移动。</summary>
        public int moveDistance;
        /// <summary>允许直接攻击资源单位；正式 Excel/JSON 必须显式填写。</summary>
        public bool canAttackResources = true;
        /// <summary>允许参与互换；正式 Excel/JSON 必须显式填写。</summary>
        public bool canSwap = true;
        /// <summary>是否启用所属阵营回合开始时的八邻刷怪能力。</summary>
        public bool spawnComponent;
        /// <summary>刷怪能力生成的攻击类卡种 ID；禁用时为空。</summary>
        public string spawnCardType;
        /// <summary>每回合产出。</summary>
        public int produce;
        /// <summary>每回合维持费。</summary>
        public int upkeep;
        /// <summary>是否启用资源组件；为 false 时 produce/upkeep 必须为 0。</summary>
        public bool resourceComponent;
        /// <summary>是否启用传送组件；为 false 时 permitsAttackDeploy 必须为 false。</summary>
        public bool teleportComponent;
        /// <summary>是否允许己方攻击卡部署在其四邻。</summary>
        public bool permitsAttackDeploy;
        /// <summary>是否只能部署在己方区域。</summary>
        public bool deployHomeOnly;
        /// <summary>是否禁止部署在任何同类单位的四邻。</summary>
        public bool avoidAdjacentSameType;
        /// <summary>是否必须覆盖指定的己方资源单位才能部署。</summary>
        public bool requiresUpgrade;
        /// <summary>是否启用一次性部署减攻组件；为 false 时 deploymentAttackReduction 必须为 0。</summary>
        public bool deploymentPenaltyComponent;
        /// <summary>一次性部署减攻的减攻量；启用组件时必须为正整数，禁用时必须为 0。</summary>
        public int deploymentAttackReduction;
        /// <summary>成功部署这张卡的一次性资源消耗；0 表示免费，负数非法。</summary>
        public int deployCost;
        /// <summary>
        /// 部署时四邻（上下左右）必须存在的单位分类：resource / attack / special。
        /// 空表示没有邻域要求；有要求时必须同时给出 deployRequireAdjacentCount。
        /// </summary>
        public string deployRequireAdjacentCategory;
        /// <summary>四邻中该分类单位的最小数量；0 表示没有邻域要求。</summary>
        public int deployRequireAdjacentCount;
        /// <summary>是否启用入场标记能力；为 false 时 markOnEnterRadius 与 markRewardResource 必须为 0。</summary>
        public bool markOnEnterComponent;
        /// <summary>标记半径（1 表示 3×3 八邻，不含自己）；启用能力时必须为正整数。</summary>
        public int markOnEnterRadius;
        /// <summary>被标记单位阵亡时给标记者阵营的资源；启用能力时必须为正整数。</summary>
        public int markRewardResource;
        /// <summary>是否启用死亡加攻能力；为 false 时 deathGrowthPerKill 与 deathGrowthLimit 必须为 0。</summary>
        public bool deathGrowthComponent;
        /// <summary>每次全棋盘单位阵亡时增加的攻击力；启用能力时必须为正整数。</summary>
        public int deathGrowthPerKill;
        /// <summary>死亡加攻最多触发的次数；启用能力时必须为正整数。</summary>
        public int deathGrowthLimit;
        /// <summary>
        /// 是否允许攻击友方单位，并把对友方的攻击结算改为治疗。
        /// 为什么做成卡牌级布尔而不是能力子资产：它没有任何数值参数，与 deployHomeOnly 同类，
        /// 放子资产里只会让每张卡多一个空壳配置。
        /// </summary>
        public bool attacksAlliesAsHeal;
        /// <summary>
        /// 是否“部署即结算、结算即退场”（即时卡）。
        /// 为 true 时该卡在场时间视为 0：不参与一次性减攻与入场标记，结算完 deployGainResource 后立即离场。
        /// </summary>
        public bool leaveAfterDeploy;
        /// <summary>即时卡结算时立即获得的资源；leaveAfterDeploy 为 false 时必须为 0，为 true 时必须为正整数。</summary>
        public int deployGainResource;
        /// <summary>是否启用四邻空格限制能力；为 false 时 adjacentClearanceMinEmpty 必须为 0。</summary>
        public bool adjacentClearanceComponent;
        /// <summary>四邻中至少需要为空的格数（1～4）；启用能力时必须在该范围内，禁用时必须为 0。</summary>
        public int adjacentClearanceMinEmpty;
        /// <summary>是否启用击杀恢复行动（仅攻击类卡可启用）；该能力没有数值参数。</summary>
        public bool killActionComponent;
        /// <summary>是否在直接攻击后施加伤害标记（仅攻击类卡可启用）；为 false 时 damageMarkDamagePerStack 必须为 0。</summary>
        public bool damageMarkComponent;
        /// <summary>每层伤害标记在之后每次直接受伤时追加的伤害（正整数）；启用能力时必须为正整数。</summary>
        public int damageMarkDamagePerStack;

        /// <inheritdoc />
        public int Hp => hp;
        /// <inheritdoc />
        public int Attack => atk;
        /// <inheritdoc />
        public int Range => range;
        /// <inheritdoc />
        public int MoveDistance => moveDistance;
        /// <inheritdoc />
        public bool CanAttackResources => canAttackResources;
        /// <inheritdoc />
        public bool CanSwap => canSwap;
        /// <inheritdoc />
        public string SpawnCardType => spawnCardType;
        /// <inheritdoc />
        public int Produce => produce;
        /// <inheritdoc />
        public int Upkeep => upkeep;
        /// <inheritdoc />
        public bool PermitsAttackDeploy => permitsAttackDeploy;
        /// <inheritdoc />
        public int AttackReduction => deploymentAttackReduction;
    }

    /// <summary>
    /// Rules 表的唯一一行：棋盘尺寸、回合节奏与全局规则开关。
    /// 已确认不实现反击与斜移，因此本类没有对应字段，旧 JSON 中的这些字段会被导入器移除。
    /// </summary>
    [Serializable]
    public sealed class RulesRow
    {
        /// <summary>棋盘行数（内部第 0 行在最下方）。</summary>
        public int rows;
        /// <summary>棋盘列数，至少 2。</summary>
        public int columns;
        /// <summary>己方区域宽度（列数），默认为 2；区域固定不随家移动。</summary>
        public int homeRegionWidth;
        /// <summary>玩家每回合出牌次数上限。</summary>
        public int playerPlays;
        /// <summary>Boss 每回合放牌次数上限，AI 模式用于补兵与首部署的组合。</summary>
        public int bossPlays;
        /// <summary>玩家每回合抽牌数量，0 表示不抽牌。</summary>
        public int drawCount;
        /// <summary>手牌上限；结束回合时超限必须先弃牌。</summary>
        public int handLimit;
        /// <summary>初始资源点数，默认 0。</summary>
        public int initialResources;
        /// <summary>兵位阵亡后补兵间隔（完整回合数），至少为 1。</summary>
        public int bossRespawnDelay;
        /// <summary>玩家所在边缘：left / right。</summary>
        public string playerSide;
        /// <summary>先手阵营：player / boss。</summary>
        public string firstSide;
        /// <summary>Boss 使用的卡种 ID。</summary>
        public string bossCardType;
        /// <summary>是否由玩家手动操作 Boss（否则由 AI 控制）。</summary>
        public bool playerControlsBoss;
        /// <summary>资源是否跨回合累积；关闭则每回合开始清零。</summary>
        public bool accumulateResources;
        /// <summary>新部署单位是否受召唤失调（当回合不能行动）。</summary>
        public bool summoningSickness;
        /// <summary>是否允许相邻己方单位互换位置。</summary>
        public bool adjacentSwap;
        /// <summary>是否开启同一行/列的单位遮挡。</summary>
        public bool lineBlocking;
        /// <summary>Boss 炸弹部署间隔（完整回合），至少为 1。</summary>
        public int bossBombInterval;
        /// <summary>整颗炸弹共享的伤害预算，至少为 1。</summary>
        public int bossBombDamage;
        /// <summary>从屏幕左侧起算的部署列号（从 1 开始），实际不超过棋盘列数的一半向下取整。</summary>
        public int bossBombColumn;
        /// <summary>刷怪卡部署所需的玩家在场单位总攻击力下限，正整数。</summary>
        public int bossSpawnerAttackThreshold;
        /// <summary>统计玩家攻击类单位的起始列（玩家一侧起算，1 开始，包含本列）。</summary>
        public int bossSpawnerCountFromColumn;
        /// <summary>指定列范围内玩家攻击类单位的数量下限，正整数。</summary>
        public int bossSpawnerUnitThreshold;
        /// <summary>刷怪卡部署起始列（玩家一侧起算，1 开始，包含本列）；超过棋盘宽度时无合法格。</summary>
        public int bossSpawnerDeployFromColumn;
        /// <summary>技能部署的刷怪卡卡种 ID，引用不具备攻击、移动、资源与传送能力的特殊卡。</summary>
        public string bossSpawnerCardType;
        /// <summary>二阶段锁血阈值，正数且小于 Boss 初始生命。</summary>
        public int bossPhaseTwoHp;
        /// <summary>战舰首选列，从玩家一侧起算，必须在棋盘内。</summary>
        public int bossWarshipColumn;
        /// <summary>两艘阶段战舰的卡种引用，须具备战斗与刷怪能力。</summary>
        public string bossWarshipCardType;
        /// <summary>抽牌堆总张数（正整数，至少 3）。开局按此数量生成卡牌记录，不再由 Deck 表逐卡种配置。</summary>
        public int drawPileSize;
        /// <summary>初级卡在抽牌堆中的占比（百分比整数，至少 1）。</summary>
        public int tierRatioBasic;
        /// <summary>中级卡在抽牌堆中的占比（百分比整数，至少 1）。</summary>
        public int tierRatioIntermediate;
        /// <summary>高级卡在抽牌堆中的占比（百分比整数，至少 1）。</summary>
        public int tierRatioAdvanced;
        /// <summary>玩家每局最多携带的初级卡种数量（至少 1）。</summary>
        public int maxTypesBasic;
        /// <summary>玩家每局最多携带的中级卡种数量（至少 1）。</summary>
        public int maxTypesIntermediate;
        /// <summary>玩家每局最多携带的高级卡种数量（至少 1）。</summary>
        public int maxTypesAdvanced;
    }

    /// <summary>
    /// Homes 表的一行：玩家与 Boss 的家各一行。
    /// 只保存生命与产出；坐标由“边缘居中”规则推导，不再由表格提供，避免旧坐标字段残留。
    /// </summary>
    [Serializable]
    public sealed class HomeRow
    {
        /// <summary>归属阵营：player / boss。</summary>
        public string owner;
        /// <summary>家的生命值（正整数）。</summary>
        public int hp;
        /// <summary>家每回合产出；Boss 家必须为 0。</summary>
        public int produce;
    }

    /// <summary>
    /// BossSlots 表的一行：AI 的三个兵位。
    /// 输入：rowFraction 为 0..1 的行比例，edgeOffset 为从己方边缘向内计数的列偏移；
    /// 输出：按“非负数四舍五入”换算出的首选格 PreferredCell。
    /// </summary>
    [Serializable]
    public sealed class BossSlotRow
    {
        /// <summary>兵位稳定 ID，用于快照中的补兵计时与等待状态查询。</summary>
        public string id;
        /// <summary>行比例 0..1；默认三个兵位取 0、0.5、1。</summary>
        public double rowFraction;
        /// <summary>从 Boss 区域靠玩家一侧的边缘向内数的列偏移，必须小于己方区域宽度。</summary>
        public int edgeOffset;
    }

    /// <summary>
    /// Presentation 表的唯一一行：尺寸、字号、动画时长与语义颜色。
    /// 为什么单独成表：表现参数可以独立调整，规则代码不读取这些字段，也不按具体卡名分支。
    /// </summary>
    [Serializable]
    public sealed class PresentationRow
    {
        /// <summary>UI 参考分辨率宽。</summary>
        public int referenceWidth;
        /// <summary>UI 参考分辨率高。</summary>
        public int referenceHeight;
        /// <summary>基准字号。</summary>
        public int fontSize;
        /// <summary>界面保留的日志条数上限。</summary>
        public int logLimit;
        /// <summary>移动动画时长（秒），必须为非负有限数。</summary>
        public float moveSeconds;
        /// <summary>受击/反馈动画时长（秒），必须为非负有限数。</summary>
        public float feedbackSeconds;
        /// <summary>选中放大倍率，必须为正有限数。</summary>
        public float selectedScale;
        /// <summary>玩家方颜色 #RRGGBB 或 #RRGGBBAA。</summary>
        public string playerColor;
        /// <summary>Boss 方颜色。</summary>
        public string bossColor;
        /// <summary>资源卡颜色。</summary>
        public string resourceColor;
        /// <summary>攻击卡颜色。</summary>
        public string attackColor;
        /// <summary>特殊卡颜色。</summary>
        public string specialColor;
        /// <summary>可移动高亮颜色。</summary>
        public string moveColor;
        /// <summary>可攻击高亮颜色。</summary>
        public string attackTargetColor;
        /// <summary>可互换高亮颜色。</summary>
        public string swapColor;
        /// <summary>可部署高亮颜色。</summary>
        public string deployColor;
    }

    /// <summary>
    /// Excel 编译产物与调试 JSON 的完整文档。
    /// 输入：编译器从工作簿构造，或调试面板从 JSON 反序列化；
    /// 输出：交给 ConfigValidation 校验，再交给 GameConfiguration 建立只读索引。
    /// 修改草稿不代表对当前对局生效——调试应用必须创建新对局。
    /// </summary>
    [Serializable]
    public sealed class ConfigDocument
    {
        /// <summary>文档版本；当前只接受 2（去掉 Deck 表、Rules 增加抽牌堆与阶级比例），用于拒绝无法识别的旧结构。</summary>
        public int schemaVersion = 2;
        /// <summary>Cards 表行集合。</summary>
        public CardRow[] cards;
        /// <summary>Rules 表行集合，必须恰好一行。</summary>
        public RulesRow[] rules;
        /// <summary>Homes 表行集合，必须恰好两行（player 与 boss）。</summary>
        public HomeRow[] homes;
        /// <summary>BossSlots 表行集合，必须恰好三行。</summary>
        public BossSlotRow[] bossSlots;
        /// <summary>Presentation 表行集合，必须恰好一行。</summary>
        public PresentationRow[] presentation;
    }

    /// <summary>
    /// 单个卡种的共享只读描述。
    /// 输入：一张已校验的 CardRow 与四个能力接口实现；
    /// 输出：规则内核查询名称、分类、属性和部署限制的唯一入口。
    /// 为什么不存在子类：所有卡种行为差异都由分类与开关字段表达，规则代码不按卡名分支。
    /// </summary>
    public sealed class CardArchetype
    {
        /// <summary>稳定卡种 ID。</summary>
        public string Id { get; }
        /// <summary>已去除首尾空格的展示名。</summary>
        public string Name { get; }
        /// <summary>分类，决定部署范围与 AI 优先级。</summary>
        public CardCategory Category { get; }
        /// <summary>独立于分类的共享阶级；不参与战斗结算。</summary>
        public CardTier Tier { get; }
        /// <summary>战斗属性；为 null 表示该卡种没有战斗组件。</summary>
        public ICombatConfig Combat { get; }
        /// <summary>资源属性；为 null 表示没有资源组件。</summary>
        public IResourceConfig Resource { get; }
        /// <summary>传送属性；为 null 表示没有传送组件。</summary>
        public ITeleportConfig Teleport { get; }
        /// <summary>一次性部署减攻属性；为 null 表示该卡种没有这项能力。</summary>
        public IDeploymentPenaltyConfig DeploymentPenalty { get; }
        /// <summary>通用刷怪能力；不依赖卡名、Boss 阶段或部署来源。</summary>
        public ISpawnConfig Spawn { get; }
        /// <summary>是否必须覆盖指定资源单位才能部署。</summary>
        public bool RequiresUpgrade { get; }
        /// <summary>覆盖升级来源卡种 ID。</summary>
        public string UpgradeFrom { get; }
        /// <summary>是否只能部署在己方区域。</summary>
        public bool DeployHomeOnly { get; }
        /// <summary>是否禁止部署在同类单位四邻。</summary>
        public bool AvoidAdjacentSameType { get; }
        /// <summary>成功部署的一次性资源消耗；0 表示免费。Boss 部署不检查该值。</summary>
        public int DeployCost { get; }
        /// <summary>部署时四邻必须存在的单位分类文本；空表示没有邻域要求。</summary>
        public string DeployRequireAdjacentCategory { get; }
        /// <summary>四邻中该分类单位的最小数量；0 表示没有邻域要求。</summary>
        public int DeployRequireAdjacentCount { get; }
        /// <summary>入场标记能力；为 null 表示没有该能力。</summary>
        public IMarkOnEnterConfig MarkOnEnter { get; }
        /// <summary>死亡加攻能力；为 null 表示没有该能力。</summary>
        public IDeathGrowthConfig DeathGrowth { get; }
        /// <summary>是否可攻击友方并改为治疗（仅攻击类卡可配置）。</summary>
        public bool AttacksAlliesAsHeal { get; }
        /// <summary>即时卡效果；为 null 表示该卡部署后留在场上。</summary>
        public IInstantEffectConfig Instant { get; }
        /// <summary>四邻空格限制能力；为 null 表示没有该能力。</summary>
        public IAdjacentClearanceConfig AdjacentClearance { get; }
        /// <summary>击杀恢复行动能力；为 null 表示没有该能力（非空即启用）。</summary>
        public IKillActionConfig KillAction { get; }
        /// <summary>伤害标记能力；为 null 表示没有该能力。</summary>
        public IDamageMarkConfig DamageMark { get; }

        /// <summary>
        /// 由一行已校验数据构造共享描述。
        /// 输入：row 提供标识与开关，combat/resource/teleport 提供各能力数值（可为 null）；
        /// 输出：供 GameConfiguration 索引的不可变对象。
        /// 新能力参数追加在末尾，既有调用点与测试夹具不需修改。
        /// </summary>
        public CardArchetype(CardRow row, ICombatConfig combat, IResourceConfig resource, ITeleportConfig teleport, IDeploymentPenaltyConfig deploymentPenalty = null, IMarkOnEnterConfig markOnEnter = null, IDeathGrowthConfig deathGrowth = null, IInstantEffectConfig instant = null,
            ISpawnConfig spawn = null, IAdjacentClearanceConfig adjacentClearance = null, IKillActionConfig killAction = null, IDamageMarkConfig damageMark = null)
        {
            Id = row.id;
            Name = row.name?.Trim();
            Category = row.category == "resource" ? CardCategory.Resource : row.category == "attack" ? CardCategory.Attack : CardCategory.Special;
            Tier = CardTiers.Parse(row.tier);
            Combat = combat;
            Resource = resource;
            Teleport = teleport;
            DeploymentPenalty = deploymentPenalty;
            MarkOnEnter = markOnEnter;
            DeathGrowth = deathGrowth;
            Spawn = spawn;
            RequiresUpgrade = row.requiresUpgrade;
            UpgradeFrom = row.upgradeFrom;
            DeployHomeOnly = row.deployHomeOnly;
            AvoidAdjacentSameType = row.avoidAdjacentSameType;
            DeployCost = row.deployCost;
            // 邻域分类统一去掉首尾空格，避免 “resource ” 这类工作簿笔误绕过校验。
            DeployRequireAdjacentCategory = row.deployRequireAdjacentCategory == null ? string.Empty : row.deployRequireAdjacentCategory.Trim();
            DeployRequireAdjacentCount = row.deployRequireAdjacentCount;
            AttacksAlliesAsHeal = row.attacksAlliesAsHeal;
            Instant = instant;
            AdjacentClearance = adjacentClearance;
            KillAction = killAction;
            DamageMark = damageMark;
        }

        /// <summary>
        /// 由表行构造共享描述，并把全部数值复制成不可变值。
        /// 输入：已校验的 CardRow（Excel 映射或调试草稿）；输出：与草稿完全脱钩的 CardArchetype。
        /// 为什么必须复制：CardRow 是可变的中间态，调试面板会原地改字段；若规则直接引用行对象，
        /// 进行中的对局与后续部署会偷偷读到改动后的草稿。SO 路径不经过本方法（SO 本身只读）。
        /// </summary>
        public static CardArchetype FromRow(CardRow row)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            return new CardArchetype(row,
                new CombatValues(row.hp, row.atk, row.range, row.moveDistance, row.canAttackResources, row.canSwap),
                row.resourceComponent ? new ResourceValues(row.produce, row.upkeep) : null,
                row.teleportComponent ? new TeleportValues(row.permitsAttackDeploy) : null,
                row.deploymentPenaltyComponent ? new DeploymentPenaltyValues(row.deploymentAttackReduction) : null,
                row.markOnEnterComponent ? new MarkOnEnterValues(row.markOnEnterRadius, row.markRewardResource) : null,
                row.deathGrowthComponent ? new DeathGrowthValues(row.deathGrowthPerKill, row.deathGrowthLimit) : null,
                row.leaveAfterDeploy ? new InstantEffectValues(row.deployGainResource) : null,
                spawn: row.spawnComponent ? new SpawnValues(row.spawnCardType) : null,
                adjacentClearance: row.adjacentClearanceComponent ? new AdjacentClearanceValues(row.adjacentClearanceMinEmpty) : null,
                killAction: row.killActionComponent ? new KillActionValues() : null,
                damageMark: row.damageMarkComponent ? new DamageMarkValues(row.damageMarkDamagePerStack) : null);
        }
    }

    /// <summary>
    /// 战斗数值的不可变副本。
    /// 输入：构造参数；输出：实现 ICombatConfig 的只读对象。
    /// 为什么需要：把可变表行的数值与对局脱钩，旧快照与后续部署都不会因草稿被改而改变。
    /// </summary>
    sealed class CombatValues : ICombatConfig
    {
        /// <inheritdoc />
        public int Hp { get; }
        /// <inheritdoc />
        public int Attack { get; }
        /// <inheritdoc />
        public int Range { get; }
        /// <inheritdoc />
        public int MoveDistance { get; }
        /// <summary>是否允许直接攻击资源类单位；AI、手动命令和高亮共用。</summary>
        public bool CanAttackResources { get; }
        /// <summary>是否允许参与互换；资源分类的额外限制仍然生效。</summary>
        public bool CanSwap { get; }

        /// <summary>复制战斗数值。输入：生命、攻击、射程与移动距离；输出：只读副本。</summary>
        public CombatValues(int hp, int attack, int range, int moveDistance, bool canAttackResources, bool canSwap)
        {
            Hp = hp;
            Attack = attack;
            Range = range;
            MoveDistance = moveDistance;
            CanAttackResources = canAttackResources;
            CanSwap = canSwap;
        }
    }

    /// <summary>
    /// 资源数值的不可变副本。输入：产出与维持费；输出：实现 IResourceConfig 的只读对象。
    /// </summary>
    sealed class ResourceValues : IResourceConfig
    {
        /// <inheritdoc />
        public int Produce { get; }
        /// <inheritdoc />
        public int Upkeep { get; }

        /// <summary>复制资源数值。输入：产出与维持费；输出：只读副本。</summary>
        public ResourceValues(int produce, int upkeep)
        {
            Produce = produce;
            Upkeep = upkeep;
        }
    }

    /// <summary>
    /// 传送数值的不可变副本。输入：四邻部署扩展开关；输出：实现 ITeleportConfig 的只读对象。
    /// </summary>
    sealed class TeleportValues : ITeleportConfig
    {
        /// <inheritdoc />
        public bool PermitsAttackDeploy { get; }

        /// <summary>复制传送数值。输入：是否允许四邻部署攻击卡；输出：只读副本。</summary>
        public TeleportValues(bool permitsAttackDeploy)
        {
            PermitsAttackDeploy = permitsAttackDeploy;
        }
    }

    /// <summary>
    /// 一次性部署减攻数值的不可变副本。输入：减攻量；输出：实现 IDeploymentPenaltyConfig 的只读对象。
    /// </summary>
    sealed class DeploymentPenaltyValues : IDeploymentPenaltyConfig
    {
        /// <inheritdoc />
        public int AttackReduction { get; }

        /// <summary>复制减攻量。输入：每次触发的减攻量；输出：只读副本。</summary>
        public DeploymentPenaltyValues(int attackReduction)
        {
            AttackReduction = attackReduction;
        }
    }

    /// <summary>
    /// 入场标记数值的不可变副本。输入：标记半径与阵亡奖励；输出：实现 IMarkOnEnterConfig 的只读对象。
    /// </summary>
    sealed class MarkOnEnterValues : IMarkOnEnterConfig
    {
        /// <inheritdoc />
        public int Radius { get; }
        /// <inheritdoc />
        public int Reward { get; }

        /// <summary>复制标记配置。输入：半径与奖励；输出：与草稿完全脱钩的只读副本。</summary>
        public MarkOnEnterValues(int radius, int reward)
        {
            Radius = radius;
            Reward = reward;
        }
    }

    /// <summary>
    /// 死亡加攻数值的不可变副本。输入：每次加成与次数上限；输出：实现 IDeathGrowthConfig 的只读对象。
    /// </summary>
    sealed class DeathGrowthValues : IDeathGrowthConfig
    {
        /// <inheritdoc />
        public int PerKill { get; }
        /// <inheritdoc />
        public int Limit { get; }

        /// <summary>复制死亡加攻配置。输入：每次加成与次数上限；输出：与草稿完全脱钩的只读副本。</summary>
        public DeathGrowthValues(int perKill, int limit)
        {
            PerKill = perKill;
            Limit = limit;
        }
    }

    /// <summary>
    /// 即时卡效果的不可变副本。输入：一次性资源收益；输出：实现 IInstantEffectConfig 的只读对象。
    /// </summary>
    sealed class InstantEffectValues : IInstantEffectConfig
    {
        /// <inheritdoc />
        public int GainResource { get; }

        /// <summary>复制即时效果。输入：一次性资源收益；输出：与草稿完全脱钩的只读副本。</summary>
        public InstantEffectValues(int gainResource)
        {
            GainResource = gainResource;
        }
    }

    /// <summary>
    /// 四邻空格限制数值的不可变副本。输入：最少空邻格数；输出：实现 IAdjacentClearanceConfig 的只读对象。
    /// </summary>
    sealed class AdjacentClearanceValues : IAdjacentClearanceConfig
    {
        /// <inheritdoc />
        public int MinEmptyNeighbors { get; }

        /// <summary>复制空格阈值。输入：1～4 的最少空邻格数；输出：与草稿脱钩的只读副本。</summary>
        public AdjacentClearanceValues(int minEmptyNeighbors)
        {
            MinEmptyNeighbors = minEmptyNeighbors;
        }
    }

    /// <summary>
    /// 击杀恢复行动的不可变标记。输入：无；输出：实现 IKillActionConfig 的只读对象。
    /// 为什么没有字段：能力本身没有参数，存在与否就是开关。
    /// </summary>
    sealed class KillActionValues : IKillActionConfig
    {
    }

    /// <summary>
    /// 伤害标记数值的不可变副本。输入：每层追加伤害；输出：实现 IDamageMarkConfig 的只读对象。
    /// </summary>
    sealed class DamageMarkValues : IDamageMarkConfig
    {
        /// <inheritdoc />
        public int DamagePerStack { get; }

        /// <summary>复制每层追加伤害。输入：正整数；输出：与草稿脱钩的只读副本。</summary>
        public DamageMarkValues(int damagePerStack)
        {
            DamagePerStack = damagePerStack;
        }
    }

    /// <summary>
    /// 规则不可变快照。
    /// 为什么需要：调试草稿在面板中被反复修改，若规则直接引用草稿对象，进行中的对局会在中途改变约束；
    /// 因此构造时一次性拷贝，之后对局与草稿完全脱钩。
    /// </summary>
    public sealed class MatchConfig
    {
        /// <summary>棋盘行数。</summary>
        public int Rows { get; }
        /// <summary>棋盘列数。</summary>
        public int Columns { get; }
        /// <summary>己方区域宽度。</summary>
        public int HomeRegionWidth { get; }
        /// <summary>玩家每回合出牌次数。</summary>
        public int PlayerPlays { get; }
        /// <summary>Boss 每回合放牌次数。</summary>
        public int BossPlays { get; }
        /// <summary>玩家每回合抽牌数。</summary>
        public int DrawCount { get; }
        /// <summary>手牌上限。</summary>
        public int HandLimit { get; }
        /// <summary>初始资源。</summary>
        public int InitialResources { get; }
        /// <summary>补兵间隔（完整回合）。</summary>
        public int BossRespawnDelay { get; }
        /// <summary>Boss 炸弹部署间隔（完整回合）。</summary>
        public int BossBombInterval { get; }
        /// <summary>炸弹总伤害，各方向共享。</summary>
        public int BossBombDamage { get; }
        /// <summary>炸弹期望显示列号（从 1 开始）。</summary>
        public int BossBombColumn { get; }
        /// <summary>玩家在场单位总攻击力下限。</summary>
        public int BossSpawnerAttackThreshold { get; }
        /// <summary>玩家攻击类单位统计起始列（玩家一侧起算）。</summary>
        public int BossSpawnerCountFromColumn { get; }
        /// <summary>前线玩家攻击类单位数量下限。</summary>
        public int BossSpawnerUnitThreshold { get; }
        /// <summary>刷怪卡部署起始列（玩家一侧起算）。</summary>
        public int BossSpawnerDeployFromColumn { get; }
        /// <summary>刷怪卡的配置引用。</summary>
        public string BossSpawnerCardType { get; }
        /// <summary>一阶段生命下限与二阶段触发阈值。</summary>
        public int BossPhaseTwoHp { get; }
        /// <summary>战舰部署列，玩家视角一基坐标。</summary>
        public int BossWarshipColumn { get; }
        /// <summary>阶段战舰卡种引用。</summary>
        public string BossWarshipCardType { get; }
        /// <summary>玩家是否在左侧。</summary>
        public bool PlayerOnLeft { get; }
        /// <summary>先手阵营。</summary>
        public Side FirstSide { get; }
        /// <summary>Boss 使用的卡种 ID。</summary>
        public string BossCardType { get; }
        /// <summary>是否由玩家手动操作 Boss。</summary>
        public bool PlayerControlsBoss { get; }
        /// <summary>资源是否跨回合累积。</summary>
        public bool AccumulateResources { get; }
        /// <summary>是否启用召唤失调。</summary>
        public bool SummoningSickness { get; }
        /// <summary>是否允许相邻己方单位互换。</summary>
        public bool AdjacentSwap { get; }
        /// <summary>是否开启行列遮挡。</summary>
        public bool LineBlocking { get; }

        /// <summary>
        /// 从已校验的 Rules 行拷贝出不可变规则集。
        /// 输入：r 为 Rules 表唯一一行；输出：本局使用的只读规则。
        /// </summary>
        public MatchConfig(RulesRow r)
        {
            Rows = r.rows;
            Columns = r.columns;
            HomeRegionWidth = r.homeRegionWidth;
            PlayerPlays = r.playerPlays;
            BossPlays = r.bossPlays;
            DrawCount = r.drawCount;
            HandLimit = r.handLimit;
            InitialResources = r.initialResources;
            BossRespawnDelay = r.bossRespawnDelay;
            BossBombInterval = r.bossBombInterval;
            BossBombDamage = r.bossBombDamage;
            BossBombColumn = r.bossBombColumn;
            BossSpawnerAttackThreshold = r.bossSpawnerAttackThreshold;
            BossSpawnerCountFromColumn = r.bossSpawnerCountFromColumn;
            BossSpawnerUnitThreshold = r.bossSpawnerUnitThreshold;
            BossSpawnerDeployFromColumn = r.bossSpawnerDeployFromColumn;
            BossSpawnerCardType = r.bossSpawnerCardType;
            BossPhaseTwoHp = r.bossPhaseTwoHp;
            BossWarshipColumn = r.bossWarshipColumn;
            BossWarshipCardType = r.bossWarshipCardType;
            PlayerOnLeft = r.playerSide == "left";
            FirstSide = r.firstSide == "player" ? Side.Player : Side.Boss;
            BossCardType = r.bossCardType;
            PlayerControlsBoss = r.playerControlsBoss;
            AccumulateResources = r.accumulateResources;
            SummoningSickness = r.summoningSickness;
            AdjacentSwap = r.adjacentSwap;
            LineBlocking = r.lineBlocking;
        }
    }

    /// <summary>
    /// 表现配置的只读副本，供 UI 层使用，不参与任何战斗计算。
    /// 颜色保留为 #RRGGBB / #RRGGBBAA 字符串，由 UI 层转换为引擎颜色，因此本文件不引用 UnityEngine。
    /// </summary>
    public sealed class PresentationConfig
    {
        /// <summary>UI 参考分辨率宽。</summary>
        public int ReferenceWidth { get; }
        /// <summary>UI 参考分辨率高。</summary>
        public int ReferenceHeight { get; }
        /// <summary>基准字号。</summary>
        public int FontSize { get; }
        /// <summary>日志保留条数。</summary>
        public int LogLimit { get; }
        /// <summary>移动动画时长（秒）。</summary>
        public float MoveSeconds { get; }
        /// <summary>反馈动画时长（秒）。</summary>
        public float FeedbackSeconds { get; }
        /// <summary>选中放大倍率。</summary>
        public float SelectedScale { get; }
        /// <summary>玩家方颜色文本。</summary>
        public string PlayerColor { get; }
        /// <summary>Boss 方颜色文本。</summary>
        public string BossColor { get; }
        /// <summary>资源卡颜色文本。</summary>
        public string ResourceColor { get; }
        /// <summary>攻击卡颜色文本。</summary>
        public string AttackColor { get; }
        /// <summary>特殊卡颜色文本。</summary>
        public string SpecialColor { get; }
        /// <summary>可移动高亮颜色文本。</summary>
        public string MoveColor { get; }
        /// <summary>可攻击高亮颜色文本。</summary>
        public string AttackTargetColor { get; }
        /// <summary>可互换高亮颜色文本。</summary>
        public string SwapColor { get; }
        /// <summary>可部署高亮颜色文本。</summary>
        public string DeployColor { get; }

        /// <summary>
        /// 从已校验的 Presentation 行拷贝表现参数。
        /// 输入：p 为 Presentation 表唯一一行；输出：UI 层只读的表现配置。
        /// </summary>
        public PresentationConfig(PresentationRow p)
        {
            ReferenceWidth = p.referenceWidth;
            ReferenceHeight = p.referenceHeight;
            FontSize = p.fontSize;
            LogLimit = p.logLimit;
            MoveSeconds = p.moveSeconds;
            FeedbackSeconds = p.feedbackSeconds;
            SelectedScale = p.selectedScale;
            PlayerColor = p.playerColor;
            BossColor = p.bossColor;
            ResourceColor = p.resourceColor;
            AttackColor = p.attackColor;
            SpecialColor = p.specialColor;
            MoveColor = p.moveColor;
            AttackTargetColor = p.attackTargetColor;
            SwapColor = p.swapColor;
            DeployColor = p.deployColor;
        }
    }

    /// <summary>
    /// 一局游戏的全部不可变配置索引。
    /// 输入：已校验的 ConfigDocument，或已由 ScriptableObject 组装好的构成要素；
    /// 输出：GameSession 构造所需的规则、卡种、卡组、兵位与家数值。
    /// 为什么提供两个构造重载：Excel/调试走文档校验路径，正式运行包直接从 SO 组装，不再需要文档中间态。
    /// </summary>
    public sealed class GameConfiguration
    {
        /// <summary>本局规则。</summary>
        public MatchConfig Rules { get; }
        /// <summary>抽牌堆构成规则：张数、三阶级比例与三阶级携带上限。</summary>
        public DeckConfig DeckConfig { get; }
        /// <summary>卡种索引，键为稳定 ID。</summary>
        public IReadOnlyDictionary<string, CardArchetype> Cards { get; }
        /// <summary>按 Cards 表顺序排列的卡种清单；选卡界面与“余数发给靠前卡种”均以此顺序为准。</summary>
        public IReadOnlyList<CardArchetype> CardOrder { get; }
        /// <summary>本局玩家抽牌堆清单（含数量）；由选卡结果与阶级比例生成，不再来自 Excel 逐卡种配置。</summary>
        public IReadOnlyList<DeckEntry> Deck { get; }
        /// <summary>AI 兵位定义。</summary>
        public IReadOnlyList<SpawnSlot> BossSlots { get; }
        /// <summary>玩家家生命。</summary>
        public int PlayerHomeHp { get; }
        /// <summary>Boss 家生命。</summary>
        public int BossHomeHp { get; }
        /// <summary>玩家家每回合产出。</summary>
        public int PlayerHomeProduce { get; }
        /// <summary>表现参数，供 UI 层读取；规则层不读取。</summary>
        public PresentationConfig Presentation { get; }

        /// <summary>
        /// 文档路径构造：先验证完整 ConfigDocument，再建立只读索引。
        /// 输入：d 为 Excel 编译或调试 JSON 得到的文档；cards 为可选的外部卡种实现（如 SO）。
        /// 输出：可用于创建对局的配置；任何关联错误都会在创建对局前抛出，避免半成品对局。
        /// 抽牌堆说明：文档不再包含 Deck 表，这里用“默认选卡”（每阶级按上限取满）+ 阶级比例生成一份抽牌堆，
        /// 真正的玩家选卡结果由 DeckSelectionState 在开局时另行覆盖。
        /// </summary>
        public GameConfiguration(ConfigDocument d, IEnumerable<CardArchetype> cards = null)
        {
            var errors = ConfigValidation.Validate(d);
            if (errors.Count != 0) throw new ArgumentException(string.Join("\n", errors));
            Rules = new MatchConfig(d.rules[0]);
            DeckConfig = new DeckConfig(d.rules[0]);
            var ordered = (cards ?? d.cards.Select(CardArchetype.FromRow)).ToArray();
            CardOrder = Array.AsReadOnly(ordered);
            Cards = new System.Collections.ObjectModel.ReadOnlyDictionary<string, CardArchetype>(
                ordered.ToDictionary(c => c.Id));
            if (!DeckComposition.TryBuildDefault(DeckConfig, ordered, out var defaultDeck, out var deckError))
                throw new ArgumentException(deckError);
            Deck = defaultDeck;
            BossSlots = Array.AsReadOnly(d.bossSlots.Select(s => new SpawnSlot(s.id, s.rowFraction, s.edgeOffset)).ToArray());
            PlayerHomeHp = d.homes.Single(h => h.owner == "player").hp;
            BossHomeHp = d.homes.Single(h => h.owner == "boss").hp;
            PlayerHomeProduce = d.homes.Single(h => h.owner == "player").produce;
            Presentation = new PresentationConfig(d.presentation[0]);
        }

        /// <summary>
        /// 直接组装路径：正式运行包从只读 SO 组装，不经过 ConfigDocument 中间态，避免运行时反序列化开销。
        /// 输入：已经由编辑器编译器校验过的规则、抽牌堆规则、卡种、抽牌堆、兵位与家数值。
        /// 输出：可用于创建对局的配置；这里仍做一次防御性组合校验，防止生成资产被手工篡改。
        /// </summary>
        public GameConfiguration(MatchConfig rules, IEnumerable<CardArchetype> cards, IEnumerable<DeckEntry> deck,
            IEnumerable<SpawnSlot> bossSlots, int playerHomeHp, int bossHomeHp, int playerHomeProduce, PresentationConfig presentation,
            DeckConfig deckConfig, bool requireDrawPileSize = true)
        {
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            if (cards == null) throw new ArgumentNullException(nameof(cards));
            if (deck == null) throw new ArgumentNullException(nameof(deck));
            if (bossSlots == null) throw new ArgumentNullException(nameof(bossSlots));
            if (deckConfig == null) throw new ArgumentNullException(nameof(deckConfig));
            var errors = ValidateComposition(rules, deckConfig, cards, deck, bossSlots, playerHomeHp, bossHomeHp, playerHomeProduce, requireDrawPileSize);
            if (errors.Count != 0) throw new ArgumentException(string.Join("\n", errors));
            var ordered = cards.ToArray();
            var map = ordered.ToDictionary(c => c.Id);
            CardOrder = Array.AsReadOnly(ordered);
            Cards = new System.Collections.ObjectModel.ReadOnlyDictionary<string, CardArchetype>(map);
            DeckConfig = deckConfig;
            Deck = Array.AsReadOnly(deck.ToArray());
            BossSlots = Array.AsReadOnly(bossSlots.ToArray());
            PlayerHomeHp = playerHomeHp;
            BossHomeHp = bossHomeHp;
            PlayerHomeProduce = playerHomeProduce;
            Presentation = presentation;
        }

        /// <summary>
        /// 按卡种 ID 查卡种。输入：稳定 ID；输出：卡种，未注册时为 null。
        /// 用途：选卡与抽牌堆生成需要按 ID 校验与定位，避免界面层重复维护映射。
        /// </summary>
        public CardArchetype FindCard(string typeId)
        {
            if (string.IsNullOrEmpty(typeId)) return null;
            return Cards.TryGetValue(typeId, out var card) ? card : null;
        }

        /// <summary>
        /// 复制一份只换抽牌堆的配置。
        /// 输入：新的 DeckEntry 清单与是否要求“总张数等于 drawPileSize”；
        /// 输出：除 Deck 外完全相同的配置。
        /// 为什么需要：玩家选卡与调试强制牌堆都只改变抽牌堆，不应重新解析文档或重建卡种。
        /// 调试强制牌堆传 requireDrawPileSize=false，避免为了压测而被迫凑满配置张数。
        /// </summary>
        public GameConfiguration WithDeck(IEnumerable<DeckEntry> deck, bool requireDrawPileSize = true) =>
            new GameConfiguration(Rules, CardOrder, deck, BossSlots, PlayerHomeHp, BossHomeHp, PlayerHomeProduce, Presentation, DeckConfig, requireDrawPileSize);

        /// <summary>
        /// 组合路径的防御性校验。
        /// 输入：构造参数；输出：错误描述列表，为空表示组合合法。
        /// 为什么需要：SO 资产可能被手工改动，运行前必须用与文档路径一致的业务约束复核引用关系。
        /// </summary>
        static List<string> ValidateComposition(MatchConfig rules, DeckConfig deckConfig, IEnumerable<CardArchetype> cards, IEnumerable<DeckEntry> deck,
            IEnumerable<SpawnSlot> bossSlots, int playerHomeHp, int bossHomeHp, int playerHomeProduce, bool requireDrawPileSize)
        {
            var errors = new List<string>();
            var list = cards.Where(c => c != null).ToArray();
            if (list.Length == 0) errors.Add("卡表为空");
            if (list.Length != cards.Count()) errors.Add("卡表含空对象");
            var ids = new HashSet<string>(list.Select(c => c.Id), StringComparer.Ordinal);
            if (ids.Count != list.Length) errors.Add("卡种 ID 重复");
            if (list.Any(c => string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Name))) errors.Add("卡种 ID 或名称为空");
            if (list.Any(c => c.Tier == CardTier.Invalid)) errors.Add("卡种 tier 缺失或非法，请补齐阶级");
            if (list.Any(c => c.RequiresUpgrade && (string.IsNullOrEmpty(c.UpgradeFrom) || !ids.Contains(c.UpgradeFrom)))) errors.Add("升级目标缺失");
            if (list.Any(c => c.DeploymentPenalty != null && c.DeploymentPenalty.AttackReduction < 1)) errors.Add("部署减攻配置非法：启用组件时减攻量必须为正整数");
            if (list.Any(c => c.DeployCost < 0)) errors.Add("部署消耗不得为负");
            if (list.Any(c => c.DeployRequireAdjacentCount > 0 && string.IsNullOrEmpty(c.DeployRequireAdjacentCategory))) errors.Add("邻域部署要求缺少分类");
            if (list.Any(c => c.MarkOnEnter != null && (c.MarkOnEnter.Radius < 1 || c.MarkOnEnter.Reward < 1))) errors.Add("入场标记配置非法：启用时半径与奖励必须为正整数");
            if (list.Any(c => c.DeathGrowth != null && (c.DeathGrowth.PerKill < 1 || c.DeathGrowth.Limit < 1))) errors.Add("死亡加攻配置非法：启用时加成与次数上限必须为正整数");
            if (list.Any(c => c.Instant != null && c.Instant.GainResource < 1)) errors.Add("即时卡配置非法：结算资源必须为正整数");
            // 新能力的组合路径防守：SO 被手工改动或旧 JSON 残留都可能绕过文档校验。
            if (list.Any(c => c.AdjacentClearance != null && (c.AdjacentClearance.MinEmptyNeighbors < 1 || c.AdjacentClearance.MinEmptyNeighbors > 4)))
                errors.Add("四邻空格限制配置非法：启用时最少空格数必须在 1～4 之间");
            if (list.Any(c => c.KillAction != null && c.Category != CardCategory.Attack))
                errors.Add("击杀恢复行动配置非法：仅攻击类卡可启用");
            if (list.Any(c => c.DamageMark != null && (c.DamageMark.DamagePerStack < 1 || c.Category != CardCategory.Attack)))
                errors.Add("伤害标记配置非法：启用时每层追加伤害必须为正整数，且仅攻击类卡可启用");
            // 即时卡只在玩家手牌路径生效；Boss 卡种/刷怪卡指向即时卡会让补兵与生成逻辑面对“已移除的单位”。
            if (list.SingleOrDefault(c => c.Id == rules.BossCardType)?.Instant != null) errors.Add("Boss 卡种不能是即时卡");
            if (list.SingleOrDefault(c => c.Id == rules.BossSpawnerCardType)?.Instant != null) errors.Add("刷怪卡卡种不能是即时卡");
            if (!ids.Contains(rules.BossCardType)) errors.Add("Boss 卡种未注册");
            else if (!rules.PlayerControlsBoss && list.Single(c => c.Id == rules.BossCardType).Category != CardCategory.Attack) errors.Add("AI 模式 Boss 必须使用攻击卡");
            var deckList = deck.ToArray();
            if (deckList.Any(e => e == null || e.Count < 0 || !ids.Contains(e.TypeId))) errors.Add("卡组引用非法");
            if (deckList.Any(e => e != null && e.Count > 0 && list.Any(c => c.Id == e.TypeId && c.Tier == CardTier.None)))
                errors.Add("Deck：tier=none（不适用）的卡种不能加入玩家卡组");
            if (deckList.GroupBy(e => e == null ? string.Empty : e.TypeId, StringComparer.Ordinal).Any(g => g.Count() > 1))
                errors.Add("卡组：同一卡种不得重复登记");
            // 抽牌堆总张数必须等于配置值：它决定“每回合抽 5 张能撑几回合”，与比例一起构成可校验的不变量。
            // 调试强制牌堆（requireDrawPileSize=false）是唯一豁免路径，仅存在于本次运行会话。
            if (requireDrawPileSize && deckList.Sum(e => e == null ? 0 : e.Count) != deckConfig.DrawPileSize)
                errors.Add($"卡组：总张数必须等于 drawPileSize（{deckConfig.DrawPileSize}）");
            ConfigValidation.ValidateDeckRules(deckConfig, list, errors);
            var slotList = bossSlots.ToArray();
            if (slotList.Length != 3) errors.Add("必须恰好三个兵位");
            if (slotList.Any(s => s == null || string.IsNullOrWhiteSpace(s.Id) || s.RowFraction < 0 || s.RowFraction > 1) || slotList.Select(s => s.Id).Distinct().Count() != slotList.Length) errors.Add("兵位定义非法");
            if (playerHomeHp < 1 || bossHomeHp < 1 || playerHomeProduce < 0) errors.Add("家数值非法");
            if (rules.BossBombInterval < 1 || rules.BossBombDamage < 1 || rules.BossBombColumn < 1) errors.Add("Rules：bossBombInterval、bossBombDamage、bossBombColumn 必须至少为 1");
            if (rules.Rows < 1 || rules.Columns < 2 || rules.HomeRegionWidth < 1 || rules.HomeRegionWidth > rules.Columns) errors.Add("棋盘尺寸非法");
            ConfigValidation.ValidateBossSpawner(rules, list, errors);
            ConfigValidation.ValidateBossPhases(rules, list, bossHomeHp, errors);
            return errors;
        }
    }

    /// <summary>卡组中的一条：卡种 ID 与初始张数。</summary>
    public sealed class DeckEntry
    {
        /// <summary>引用的卡种 ID。</summary>
        public string TypeId { get; }
        /// <summary>初始张数。</summary>
        public int Count { get; }

        /// <summary>构造一条卡组记录。输入：卡种 ID 与张数；输出：不可变条目。</summary>
        public DeckEntry(string id, int count) { TypeId = id; Count = count; }
    }

    /// <summary>AI 兵位定义：首选格由行比例与边缘偏移推导。</summary>
    public sealed class SpawnSlot
    {
        /// <summary>兵位稳定 ID。</summary>
        public string Id { get; }
        /// <summary>行比例 0..1。</summary>
        public double RowFraction { get; }
        /// <summary>从己方边缘向内数的列偏移。</summary>
        public int EdgeOffset { get; }

        /// <summary>构造一个兵位定义。输入：ID、行比例与列偏移；输出：不可变兵位。</summary>
        public SpawnSlot(string id, double fraction, int offset) { Id = id; RowFraction = fraction; EdgeOffset = offset; }
    }

    /// <summary>
    /// 跨入口共用的业务校验。
    /// 输入：任意来源的 ConfigDocument；输出：全部可发现错误的文本列表（为空表示通过）。
    /// 职责边界：单元格类型、未知列、公式与合并单元格由 Excel/JSON 入口先检查；
    /// 本类只负责领域约束，因此测试可以直接构造对象验证规则，不依赖工作簿。
    /// </summary>
    public static class ConfigValidation
    {
        /// <summary>
        /// 汇总全部可发现的业务错误。
        /// 输入：待校验文档；输出：错误列表。
        /// 为什么缺表时提前返回：后续规则需要 cards/rules/homes 存在，继续检查只会产生空引用噪音。
        /// </summary>
        public static List<string> Validate(ConfigDocument d)
        {
            var errors = new List<string>();
            if (d == null) { errors.Add("配置为空"); return errors; }
            if (d.schemaVersion != 2) errors.Add("不支持的 schemaVersion（当前为 2；Deck 表已废除，请重新编译配置）");
            if (d.cards == null || d.cards.Length == 0 || d.cards.Any(x => x == null)) errors.Add("Cards 必须有卡种且不能含空行对象");
            if (d.rules == null || d.rules.Length != 1 || d.rules[0] == null) errors.Add("Rules 必须恰好一行");
            if (d.homes == null || d.homes.Length != 2 || d.homes.Any(x => x == null)) errors.Add("Homes 必须分别定义 player 与 boss");
            if (d.bossSlots == null || d.bossSlots.Length != 3 || d.bossSlots.Any(x => x == null)) errors.Add("BossSlots 必须恰好三个兵位");
            if (d.presentation == null || d.presentation.Length != 1 || d.presentation[0] == null) errors.Add("Presentation 必须恰好一行");
            if (errors.Count > 0) return errors;

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < d.cards.Length; i++)
            {
                var c = d.cards[i];
                var p = $"Cards 行 {i + 2} ({c.id})";
                if (string.IsNullOrWhiteSpace(c.id) || !System.Text.RegularExpressions.Regex.IsMatch(c.id, @"^[A-Za-z][A-Za-z0-9_-]*$") || !ids.Add(c.id)) errors.Add(p + "：id 须唯一且为字母开头的字母/数字/_/-");
                if (string.IsNullOrWhiteSpace(c.name) || !names.Add(c.name.Trim())) errors.Add(p + "：名称为空或重复");
                if (c.category != "resource" && c.category != "attack" && c.category != "special") errors.Add(p + "：category 非法");
                if (CardTiers.Parse(c.tier) == CardTier.Invalid) errors.Add(p + "：tier 缺失或非法，请填写 basic（初级）/intermediate（中级）/advanced（高级）/none（不适用）");
                if (c.hp < 1 || c.atk < 0 || c.range < 0 || c.moveDistance < 0 || c.produce < 0 || c.upkeep < 0) errors.Add(p + "：生命必须正数，其他数值不得为负");
                if (!c.resourceComponent && (c.produce != 0 || c.upkeep != 0)) errors.Add(p + "：资源字段需要 resourceComponent");
                if (!c.teleportComponent && c.permitsAttackDeploy) errors.Add(p + "：部署扩展需要 teleportComponent");
                if (c.deploymentAttackReduction < 0) errors.Add(p + "：deploymentAttackReduction 不得为负");
                if (!c.deploymentPenaltyComponent && c.deploymentAttackReduction != 0) errors.Add(p + "：未启用 deploymentPenaltyComponent 时 deploymentAttackReduction 必须为 0");
                if (c.deploymentPenaltyComponent && c.deploymentAttackReduction < 1) errors.Add(p + "：启用 deploymentPenaltyComponent 时 deploymentAttackReduction 必须为正整数");
                if (c.requiresUpgrade && c.category != "resource") errors.Add(p + "：仅资源卡可覆盖升级");
                if (!c.requiresUpgrade && !string.IsNullOrEmpty(c.upgradeFrom)) errors.Add(p + "：未启用升级却填写 upgradeFrom");
                if (string.IsNullOrWhiteSpace(c.viewKey)) errors.Add(p + "：viewKey 不能为空");
                if (c.deployCost < 0) errors.Add(p + "：deployCost 不得为负");
                if (c.markOnEnterRadius < 0 || c.markRewardResource < 0) errors.Add(p + "：markOnEnterRadius / markRewardResource 不得为负");
                if (!c.markOnEnterComponent && (c.markOnEnterRadius != 0 || c.markRewardResource != 0)) errors.Add(p + "：未启用 markOnEnterComponent 时标记半径与奖励必须为 0");
                if (c.markOnEnterComponent && (c.markOnEnterRadius < 1 || c.markRewardResource < 1)) errors.Add(p + "：启用 markOnEnterComponent 时标记半径与奖励必须为正整数");
                if (c.deathGrowthPerKill < 0 || c.deathGrowthLimit < 0) errors.Add(p + "：deathGrowthPerKill / deathGrowthLimit 不得为负");
                if (!c.deathGrowthComponent && (c.deathGrowthPerKill != 0 || c.deathGrowthLimit != 0)) errors.Add(p + "：未启用 deathGrowthComponent 时加成与次数上限必须为 0");
                if (c.deathGrowthComponent && (c.deathGrowthPerKill < 1 || c.deathGrowthLimit < 1)) errors.Add(p + "：启用 deathGrowthComponent 时加成与次数上限必须为正整数");
                // 只有攻击卡才能把“对友方的攻击”改成治疗：资源卡/特殊卡本来就按攻击力判定能否攻击，
                // 允许它们配置该开关会让语义含糊（例如资源卡打友方回血）。
                if (c.attacksAlliesAsHeal && c.category != "attack") errors.Add(p + "：attacksAlliesAsHeal 仅攻击卡可启用");
                if (c.deployGainResource < 0) errors.Add(p + "：deployGainResource 不得为负");
                if (!c.leaveAfterDeploy && c.deployGainResource != 0) errors.Add(p + "：未启用 leaveAfterDeploy 时 deployGainResource 必须为 0");
                if (c.leaveAfterDeploy && c.deployGainResource < 1) errors.Add(p + "：启用 leaveAfterDeploy 时 deployGainResource 必须为正整数");
                // 四邻空格限制：关闭时必须为 0，启用时必须是 1～4（四个方向全部为空即 4）。
                // 允许 0 作为关闭值以外的任何值都会被沉默地当成“没有要求”，因此必须显式拦截。
                if (c.adjacentClearanceMinEmpty < 0 || c.adjacentClearanceMinEmpty > 4) errors.Add(p + "：adjacentClearanceMinEmpty 必须在 0～4 之间");
                if (!c.adjacentClearanceComponent && c.adjacentClearanceMinEmpty != 0) errors.Add(p + "：未启用 adjacentClearanceComponent 时 adjacentClearanceMinEmpty 必须为 0");
                if (c.adjacentClearanceComponent && (c.adjacentClearanceMinEmpty < 1 || c.adjacentClearanceMinEmpty > 4)) errors.Add(p + "：启用 adjacentClearanceComponent 时 adjacentClearanceMinEmpty 必须为 1～4");
                // 击杀恢复行动与伤害标记都只能挂在攻击类卡上，与 attacksAlliesAsHeal 的分类约束一致。
                if (c.killActionComponent && c.category != "attack") errors.Add(p + "：killActionComponent 仅攻击卡可启用");
                if (c.damageMarkDamagePerStack < 0) errors.Add(p + "：damageMarkDamagePerStack 不得为负");
                if (!c.damageMarkComponent && c.damageMarkDamagePerStack != 0) errors.Add(p + "：未启用 damageMarkComponent 时 damageMarkDamagePerStack 必须为 0");
                if (c.damageMarkComponent && c.damageMarkDamagePerStack < 1) errors.Add(p + "：启用 damageMarkComponent 时 damageMarkDamagePerStack 必须为正整数");
                if (c.damageMarkComponent && c.category != "attack") errors.Add(p + "：damageMarkComponent 仅攻击卡可启用");
                if (c.deployRequireAdjacentCount < 0) errors.Add(p + "：deployRequireAdjacentCount 不得为负");
                // 邻域要求与分类必须成对出现：数量大于 0 时分类必须合法，分类非空时数量必须大于 0。
                // 为什么不允许“填了分类但数量为 0”：那会被当成“无要求”而悄悄通过，属于最危险的一类静默笔误。
                var adjacency = (c.deployRequireAdjacentCategory ?? string.Empty).Trim();
                if (c.deployRequireAdjacentCount > 0)
                {
                    if (adjacency != "resource" && adjacency != "attack" && adjacency != "special")
                        errors.Add(p + "：启用邻域部署要求时 deployRequireAdjacentCategory 必须为 resource/attack/special");
                }
                else if (adjacency.Length != 0)
                {
                    errors.Add(p + "：deployRequireAdjacentCount 为 0 时 deployRequireAdjacentCategory 必须留空");
                }
            }
            foreach (var c in d.cards.Where(c => c.requiresUpgrade))
            {
                var source = d.cards.FirstOrDefault(s => s.id == c.upgradeFrom);
                if (source == null || source.id == c.id || source.category != "resource") errors.Add($"Cards ({c.id}) upgradeFrom：必须引用其他资源卡");
            }

            var r = d.rules[0];
            // 即时卡只在玩家手牌部署路径生效：Boss 兵位与刷怪卡若指向即时卡，会出现“创建后立刻移除”的异常补兵。
            var bossCardRow = d.cards.FirstOrDefault(x => x.id == r.bossCardType);
            if (bossCardRow != null && bossCardRow.leaveAfterDeploy) errors.Add("Rules bossCardType：Boss 卡种不能是即时卡");
            var spawnerCardRow = d.cards.FirstOrDefault(x => x.id == r.bossSpawnerCardType);
            if (spawnerCardRow != null && spawnerCardRow.leaveAfterDeploy) errors.Add("Rules bossSpawnerCardType：刷怪卡卡种不能是即时卡");
            if (r.rows < 1 || r.columns < 2 || (long)r.rows * r.columns > int.MaxValue) errors.Add("Rules：棋盘至少 1 行 2 列，格数不能溢出");
            if (r.homeRegionWidth < 1 || r.homeRegionWidth > r.columns) errors.Add("Rules homeRegionWidth：必须在 1 至列数之间");
            if (r.playerPlays < 0 || r.bossPlays < 0 || r.drawCount < 0 || r.handLimit < 0 || r.initialResources < 0 || r.bossRespawnDelay < 1) errors.Add("Rules：次数/资源不可为负，bossRespawnDelay 至少为 1");
            if (r.playerSide != "left" && r.playerSide != "right") errors.Add("Rules playerSide：只能为 left/right");
            if (r.firstSide != "player" && r.firstSide != "boss") errors.Add("Rules firstSide：只能为 player/boss");
            if (r.bossBombInterval < 1 || r.bossBombDamage < 1 || r.bossBombColumn < 1) errors.Add("Rules：bossBombInterval、bossBombDamage、bossBombColumn 必须至少为 1");
            var boss = d.cards.FirstOrDefault(c => c.id == r.bossCardType);
            if (boss == null || (!r.playerControlsBoss && boss.category != "attack")) errors.Add("Rules bossCardType：AI 必须使用攻击卡，手动模式必须使用已注册卡种");
            ValidateBossSpawner(new MatchConfig(r), d.cards.Select(CardArchetype.FromRow), errors);
            ValidateBossPhases(new MatchConfig(r), d.cards.Select(CardArchetype.FromRow), d.homes.FirstOrDefault(h => h.owner == "boss")?.hp ?? 0, errors);
            foreach (var card in d.cards)
                if (!card.spawnComponent && !string.IsNullOrEmpty(card.spawnCardType)) errors.Add($"Cards {card.id}：spawnCardType 需要启用 spawnComponent");
            // 抽牌堆规则（张数 / 三阶级比例 / 三阶级携带上限）参与业务校验，
            // 因此 Excel 编译、调试面板与运行前复核都使用同一套检查，不存在“只有表格入口能拦住”的情况。
            ValidateDeckRules(new DeckConfig(r), d.cards.Select(CardArchetype.FromRow), errors);

            if (d.homes.Count(h => h.owner == "player") != 1 || d.homes.Count(h => h.owner == "boss") != 1 || d.homes.Any(h => h.hp < 1 || h.produce < 0 || (h.owner == "boss" && h.produce != 0))) errors.Add("Homes：阵营唯一、生命正数、产出非负且 Boss 产出为零");
            if (d.bossSlots.Select(s => s.id).Distinct().Count() != d.bossSlots.Length || d.bossSlots.Any(s => string.IsNullOrWhiteSpace(s.id) || double.IsNaN(s.rowFraction) || s.rowFraction < 0 || s.rowFraction > 1 || s.edgeOffset < 0 || s.edgeOffset >= r.homeRegionWidth)) errors.Add("BossSlots：ID、行比例或边缘偏移非法");

            var pz = d.presentation[0];
            if (pz.referenceWidth < 1 || pz.referenceHeight < 1 || pz.fontSize < 1 || pz.logLimit < 1 || !Finite(pz.moveSeconds) || !Finite(pz.feedbackSeconds) || !Finite(pz.selectedScale) || pz.moveSeconds < 0 || pz.feedbackSeconds < 0 || pz.selectedScale <= 0) errors.Add("Presentation：尺寸/字号/日志数/动画参数非法");
            foreach (var f in typeof(PresentationRow).GetFields().Where(f => f.FieldType == typeof(string)))
                if (!System.Text.RegularExpressions.Regex.IsMatch((string)f.GetValue(pz) ?? "", "^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$")) errors.Add("Presentation " + f.Name + "：需要 #RRGGBB 或 #RRGGBBAA");
            return errors;
        }

        /// <summary>
        /// 共用刷怪技能业务校验。输入：规则、卡种及问题列表；输出：追加全部可发现的问题。
        /// 文档与 SO 组合入口均调用，防止手工破坏资产后绕过 Excel 校验；列号不钳制到棋盘宽度。
        /// </summary>
        internal static void ValidateBossSpawner(MatchConfig rules, IEnumerable<CardArchetype> cards, List<string> errors)
        {
            if (rules.BossSpawnerAttackThreshold < 1) errors.Add("Rules bossSpawnerAttackThreshold：必须为正整数，旧配置请补齐字段");
            if (rules.BossSpawnerCountFromColumn < 1) errors.Add("Rules bossSpawnerCountFromColumn：必须为正整数（玩家一侧起算）");
            if (rules.BossSpawnerUnitThreshold < 1) errors.Add("Rules bossSpawnerUnitThreshold：必须为正整数");
            if (rules.BossSpawnerDeployFromColumn < 1) errors.Add("Rules bossSpawnerDeployFromColumn：必须为正整数（玩家一侧起算）");
            var spawner = cards.FirstOrDefault(c => c.Id == rules.BossSpawnerCardType);
            if (string.IsNullOrWhiteSpace(rules.BossSpawnerCardType) || spawner == null)
                errors.Add("Rules bossSpawnerCardType：必须引用已注册的刷怪卡");
            else if (spawner.Category != CardCategory.Special || spawner.Combat == null || spawner.Combat.Hp < 1 ||
                spawner.Combat.Attack != 0 || spawner.Combat.Range != 0 || spawner.Combat.MoveDistance != 0 ||
                spawner.Resource != null || spawner.Teleport != null || spawner.RequiresUpgrade || spawner.Spawn == null || spawner.Combat.CanSwap)
                errors.Add("Rules bossSpawnerCardType：须为生命正数、攻击/射程/移动为零且无资源、传送和升级能力的特殊卡");
            foreach (var source in cards.Where(c => c.Spawn != null))
            {
                var fighter = cards.FirstOrDefault(c => c.Id == source.Spawn.SpawnCardType);
                if (fighter == null || fighter.Category != CardCategory.Attack || fighter.Combat == null || fighter.Combat.Hp < 1)
                    errors.Add($"Cards {source.Id} spawnCardType：必须引用生命正数的已注册攻击类卡种");
            }
        }

        /// <summary>校验阶段参数及战舰能力组合。输入：规则、卡种、Boss 初始生命；输出：追加全部错误，避免锁血后无法完成阶段。</summary>
        internal static void ValidateBossPhases(MatchConfig rules, IEnumerable<CardArchetype> cards, int bossHp, List<string> errors)
        {
            if (rules.BossPhaseTwoHp < 1 || rules.BossPhaseTwoHp >= bossHp)
                errors.Add("Rules bossPhaseTwoHp：必须大于 0 且小于 Boss 初始生命，旧配置请补齐阶段字段");
            if (rules.BossWarshipColumn < 1 || rules.BossWarshipColumn > rules.Columns)
                errors.Add("Rules bossWarshipColumn：必须位于棋盘列范围内");
            var ship = cards.FirstOrDefault(c => c.Id == rules.BossWarshipCardType);
            if (ship == null || ship.Category != CardCategory.Attack || ship.Spawn == null || ship.Combat == null ||
                ship.Combat.Hp < 1 || ship.Combat.Attack < 1 || ship.Combat.Range < 1 || ship.Combat.MoveDistance < 1 ||
                ship.Combat.CanAttackResources || !ship.Combat.CanSwap || ship.RequiresUpgrade)
                errors.Add("Rules bossWarshipCardType：须引用可移动、可攻击、可互换、不可攻击资源单位且具有刷怪能力的攻击卡");
            // 二阶段完成依赖两艘真实在场战舰的阵亡，部署即离场会永久卡住存活记录。
            if (ship != null && ship.Instant != null)
                errors.Add("Rules bossWarshipCardType：战舰不能是即时卡");
        }

        /// <summary>
        /// 共用抽牌堆规则校验。输入：抽牌堆规则与卡种清单；输出：追加全部可发现的问题。
        /// 规则来源：抽牌堆张数至少 3；三阶级比例均至少 1 且和为 100；三阶级携带上限均至少 1；
        /// 每个阶级至少有一张可携带卡种；按比例算出的张数不得少于“有效携带上限”，
        /// 否则玩家勾满该阶级时会有卡种一张也抽不到（“每个选中的卡都能抽出来至少一次”的配置期保证）。
        /// 有效携带上限 = min(配置上限, 该阶级卡种数)，因此卡池暂时不足时不必改上限。
        /// </summary>
        internal static void ValidateDeckRules(DeckConfig deck, IEnumerable<CardArchetype> cards, List<string> errors)
        {
            if (deck == null) { errors.Add("抽牌堆规则缺失"); return; }
            if (deck.DrawPileSize < 3) errors.Add("Rules drawPileSize：抽牌堆张数必须至少为 3");
            if (deck.RatioBasic < 1 || deck.RatioIntermediate < 1 || deck.RatioAdvanced < 1)
                errors.Add("Rules tierRatioBasic/Intermediate/Advanced：三阶级比例都必须至少为 1（不支持 0%）");
            else if (deck.RatioBasic + deck.RatioIntermediate + deck.RatioAdvanced != 100)
                errors.Add("Rules tierRatioBasic/Intermediate/Advanced：三阶级比例之和必须为 100");
            if (deck.MaxTypesBasic < 1 || deck.MaxTypesIntermediate < 1 || deck.MaxTypesAdvanced < 1)
                errors.Add("Rules maxTypesBasic/Intermediate/Advanced：三阶级携带上限都必须至少为 1");

            var list = (cards ?? Enumerable.Empty<CardArchetype>()).Where(c => c != null).ToArray();
            var pool = new Dictionary<CardTier, int>
            {
                [CardTier.Basic] = list.Count(c => c.Tier == CardTier.Basic),
                [CardTier.Intermediate] = list.Count(c => c.Tier == CardTier.Intermediate),
                [CardTier.Advanced] = list.Count(c => c.Tier == CardTier.Advanced)
            };
            foreach (var tier in DeckComposition.PlayerTiers)
            {
                if (pool[tier] < 1)
                {
                    errors.Add($"Cards tier：{DeckComposition.TierLabel(tier)}没有任何可携带卡种，请至少配置一张{DeckComposition.TierLabel(tier)}卡");
                    continue;
                }
                // 只有在张数与比例本身合法时才检查分配，避免一个笔误刷出一串衍生错误。
                if (deck.DrawPileSize < 3 || deck.RatioBasic < 1 || deck.RatioIntermediate < 1 || deck.RatioAdvanced < 1
                    || deck.RatioBasic + deck.RatioIntermediate + deck.RatioAdvanced != 100) continue;
                var capacity = deck.EffectiveMaxTypes(tier, pool[tier]);
                var allocated = DeckComposition.AllocateTierCounts(deck)[DeckComposition.TierIndex(tier)];
                if (allocated < capacity)
                    errors.Add($"Rules：按 drawPileSize 与阶级比例，{DeckComposition.TierLabel(tier)}只能分到 {allocated} 张，少于携带上限 {capacity} 种；请调整比例或上级上限");
            }
        }

        /// <summary>判断浮点值是否为有限数；NaN/无穷会让动画参数比较失效，因此单独抽取。</summary>
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
