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

    // 注意：以下所有 public 字段名都是 Excel 的正式列名；只读接口属性不参与列映射，
    // 因此新增列时必须同时在本类和编辑器表结构中登记，否则会被“未定义列”校验拦截。
    /// <summary>
    /// Cards 表的一行，也是卡种定义的全部数据来源。
    /// 输入：Excel 单元格或调试 JSON；输出：被 ConfigValidation 校验后用于构造 CardArchetype。
    /// 为什么合并三个接口：一张卡最多同时具有战斗、资源、传送三种能力，用开关字段控制启用，
    /// 不建立具体卡种子类，避免继承链膨胀。
    /// </summary>
    [Serializable]
    public sealed class CardRow : ICombatConfig, IResourceConfig, ITeleportConfig
    {
        /// <summary>稳定 ID，唯一且字母开头；改名不影响引用与升级关联。</summary>
        public string id;
        /// <summary>展示名称，非空且去除首尾空格后不得重复。</summary>
        public string name;
        /// <summary>分类文本，只允许 resource / attack / special。</summary>
        public string category;
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

        /// <inheritdoc />
        public int Hp => hp;
        /// <inheritdoc />
        public int Attack => atk;
        /// <inheritdoc />
        public int Range => range;
        /// <inheritdoc />
        public int MoveDistance => moveDistance;
        /// <inheritdoc />
        public int Produce => produce;
        /// <inheritdoc />
        public int Upkeep => upkeep;
        /// <inheritdoc />
        public bool PermitsAttackDeploy => permitsAttackDeploy;
    }

    /// <summary>
    /// Deck 表的一行：一种卡及其初始数量。
    /// 输入：Excel 单元格；输出：GameConfiguration.Deck 中的一条 DeckEntry。
    /// 为什么允许 count 为 0：配置面板支持“新增卡种但暂不上场”，数量 0 表示只注册不打牌。
    /// </summary>
    [Serializable]
    public sealed class DeckRow
    {
        /// <summary>引用的卡种 ID，必须在 Cards 表中存在。</summary>
        public string cardType;
        /// <summary>初始张数，非负整数。</summary>
        public int count;
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
        /// <summary>文档版本；当前只接受 1，用于拒绝无法识别的旧结构。</summary>
        public int schemaVersion = 1;
        /// <summary>Cards 表行集合。</summary>
        public CardRow[] cards;
        /// <summary>Deck 表行集合。</summary>
        public DeckRow[] deck;
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
    /// 输入：一张已校验的 CardRow 与三个能力接口实现；
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
        /// <summary>战斗属性；为 null 表示该卡种没有战斗组件。</summary>
        public ICombatConfig Combat { get; }
        /// <summary>资源属性；为 null 表示没有资源组件。</summary>
        public IResourceConfig Resource { get; }
        /// <summary>传送属性；为 null 表示没有传送组件。</summary>
        public ITeleportConfig Teleport { get; }
        /// <summary>是否必须覆盖指定资源单位才能部署。</summary>
        public bool RequiresUpgrade { get; }
        /// <summary>覆盖升级来源卡种 ID。</summary>
        public string UpgradeFrom { get; }
        /// <summary>是否只能部署在己方区域。</summary>
        public bool DeployHomeOnly { get; }
        /// <summary>是否禁止部署在同类单位四邻。</summary>
        public bool AvoidAdjacentSameType { get; }

        /// <summary>
        /// 由一行已校验数据构造共享描述。
        /// 输入：row 提供标识与开关，combat/resource/teleport 提供各能力数值（可为 null）；
        /// 输出：供 GameConfiguration 索引的不可变对象。
        /// </summary>
        public CardArchetype(CardRow row, ICombatConfig combat, IResourceConfig resource, ITeleportConfig teleport)
        {
            Id = row.id;
            Name = row.name.Trim();
            Category = row.category == "resource" ? CardCategory.Resource : row.category == "attack" ? CardCategory.Attack : CardCategory.Special;
            Combat = combat;
            Resource = resource;
            Teleport = teleport;
            RequiresUpgrade = row.requiresUpgrade;
            UpgradeFrom = row.upgradeFrom;
            DeployHomeOnly = row.deployHomeOnly;
            AvoidAdjacentSameType = row.avoidAdjacentSameType;
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
        /// <summary>卡种索引，键为稳定 ID。</summary>
        public IReadOnlyDictionary<string, CardArchetype> Cards { get; }
        /// <summary>玩家初始卡组清单（含数量）。</summary>
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
        /// </summary>
        public GameConfiguration(ConfigDocument d, IEnumerable<CardArchetype> cards = null)
        {
            var errors = ConfigValidation.Validate(d);
            if (errors.Count != 0) throw new ArgumentException(string.Join("\n", errors));
            Rules = new MatchConfig(d.rules[0]);
            Cards = new System.Collections.ObjectModel.ReadOnlyDictionary<string, CardArchetype>(
                (cards ?? d.cards.Select(c => new CardArchetype(c, c, c.resourceComponent ? (IResourceConfig)c : null, c.teleportComponent ? (ITeleportConfig)c : null))).ToDictionary(c => c.Id));
            Deck = Array.AsReadOnly(d.deck.Select(e => new DeckEntry(e.cardType, e.count)).ToArray());
            BossSlots = Array.AsReadOnly(d.bossSlots.Select(s => new SpawnSlot(s.id, s.rowFraction, s.edgeOffset)).ToArray());
            PlayerHomeHp = d.homes.Single(h => h.owner == "player").hp;
            BossHomeHp = d.homes.Single(h => h.owner == "boss").hp;
            PlayerHomeProduce = d.homes.Single(h => h.owner == "player").produce;
            Presentation = new PresentationConfig(d.presentation[0]);
        }

        /// <summary>
        /// 直接组装路径：正式运行包从只读 SO 组装，不经过 ConfigDocument 中间态，避免运行时反序列化开销。
        /// 输入：已经由编辑器编译器校验过的规则、卡种、卡组、兵位与家数值。
        /// 输出：可用于创建对局的配置；这里仍做一次防御性组合校验，防止生成资产被手工篡改。
        /// </summary>
        public GameConfiguration(MatchConfig rules, IEnumerable<CardArchetype> cards, IEnumerable<DeckEntry> deck,
            IEnumerable<SpawnSlot> bossSlots, int playerHomeHp, int bossHomeHp, int playerHomeProduce, PresentationConfig presentation)
        {
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            if (cards == null) throw new ArgumentNullException(nameof(cards));
            if (deck == null) throw new ArgumentNullException(nameof(deck));
            if (bossSlots == null) throw new ArgumentNullException(nameof(bossSlots));
            var errors = ValidateComposition(rules, cards, deck, bossSlots, playerHomeHp, bossHomeHp, playerHomeProduce);
            if (errors.Count != 0) throw new ArgumentException(string.Join("\n", errors));
            var map = cards.ToDictionary(c => c.Id);
            Cards = new System.Collections.ObjectModel.ReadOnlyDictionary<string, CardArchetype>(map);
            Deck = Array.AsReadOnly(deck.ToArray());
            BossSlots = Array.AsReadOnly(bossSlots.ToArray());
            PlayerHomeHp = playerHomeHp;
            BossHomeHp = bossHomeHp;
            PlayerHomeProduce = playerHomeProduce;
            Presentation = presentation;
        }

        /// <summary>
        /// 组合路径的防御性校验。
        /// 输入：构造参数；输出：错误描述列表，为空表示组合合法。
        /// 为什么需要：SO 资产可能被手工改动，运行前必须用与文档路径一致的业务约束复核引用关系。
        /// </summary>
        static List<string> ValidateComposition(MatchConfig rules, IEnumerable<CardArchetype> cards, IEnumerable<DeckEntry> deck,
            IEnumerable<SpawnSlot> bossSlots, int playerHomeHp, int bossHomeHp, int playerHomeProduce)
        {
            var errors = new List<string>();
            var list = cards.Where(c => c != null).ToArray();
            if (list.Length == 0) errors.Add("卡表为空");
            if (list.Length != cards.Count()) errors.Add("卡表含空对象");
            var ids = new HashSet<string>(list.Select(c => c.Id), StringComparer.Ordinal);
            if (ids.Count != list.Length) errors.Add("卡种 ID 重复");
            if (list.Any(c => string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Name))) errors.Add("卡种 ID 或名称为空");
            if (list.Any(c => c.RequiresUpgrade && (string.IsNullOrEmpty(c.UpgradeFrom) || !ids.Contains(c.UpgradeFrom)))) errors.Add("升级目标缺失");
            if (!ids.Contains(rules.BossCardType)) errors.Add("Boss 卡种未注册");
            else if (!rules.PlayerControlsBoss && list.Single(c => c.Id == rules.BossCardType).Category != CardCategory.Attack) errors.Add("AI 模式 Boss 必须使用攻击卡");
            var deckList = deck.ToArray();
            if (deckList.Any(e => e == null || e.Count < 0 || !ids.Contains(e.TypeId))) errors.Add("卡组引用非法");
            var slotList = bossSlots.ToArray();
            if (slotList.Length != 3) errors.Add("必须恰好三个兵位");
            if (slotList.Any(s => s == null || string.IsNullOrWhiteSpace(s.Id) || s.RowFraction < 0 || s.RowFraction > 1) || slotList.Select(s => s.Id).Distinct().Count() != slotList.Length) errors.Add("兵位定义非法");
            if (playerHomeHp < 1 || bossHomeHp < 1 || playerHomeProduce < 0) errors.Add("家数值非法");
            if (rules.Rows < 1 || rules.Columns < 2 || rules.HomeRegionWidth < 1 || rules.HomeRegionWidth > rules.Columns) errors.Add("棋盘尺寸非法");
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
            if (d.schemaVersion != 1) errors.Add("不支持的 schemaVersion");
            if (d.cards == null || d.cards.Length == 0 || d.cards.Any(x => x == null)) errors.Add("Cards 必须有卡种且不能含空行对象");
            if (d.deck == null || d.deck.Any(x => x == null)) errors.Add("Deck 表缺失或含空对象");
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
                if (c.hp < 1 || c.atk < 0 || c.range < 0 || c.moveDistance < 0 || c.produce < 0 || c.upkeep < 0) errors.Add(p + "：生命必须正数，其他数值不得为负");
                if (!c.resourceComponent && (c.produce != 0 || c.upkeep != 0)) errors.Add(p + "：资源字段需要 resourceComponent");
                if (!c.teleportComponent && c.permitsAttackDeploy) errors.Add(p + "：部署扩展需要 teleportComponent");
                if (c.requiresUpgrade && c.category != "resource") errors.Add(p + "：仅资源卡可覆盖升级");
                if (!c.requiresUpgrade && !string.IsNullOrEmpty(c.upgradeFrom)) errors.Add(p + "：未启用升级却填写 upgradeFrom");
                if (string.IsNullOrWhiteSpace(c.viewKey)) errors.Add(p + "：viewKey 不能为空");
            }
            foreach (var c in d.cards.Where(c => c.requiresUpgrade))
            {
                var source = d.cards.FirstOrDefault(s => s.id == c.upgradeFrom);
                if (source == null || source.id == c.id || source.category != "resource") errors.Add($"Cards ({c.id}) upgradeFrom：必须引用其他资源卡");
            }

            var deckIds = new HashSet<string>();
            long total = 0;
            foreach (var e in d.deck)
            {
                if (e.count < 0 || !ids.Contains(e.cardType) || !deckIds.Add(e.cardType)) errors.Add($"Deck ({e.cardType})：数量、卡种或重复条目非法");
                total += e.count;
            }
            if (total > int.MaxValue) errors.Add("Deck 总数量溢出");

            var r = d.rules[0];
            if (r.rows < 1 || r.columns < 2 || (long)r.rows * r.columns > int.MaxValue) errors.Add("Rules：棋盘至少 1 行 2 列，格数不能溢出");
            if (r.homeRegionWidth < 1 || r.homeRegionWidth > r.columns) errors.Add("Rules homeRegionWidth：必须在 1 至列数之间");
            if (r.playerPlays < 0 || r.bossPlays < 0 || r.drawCount < 0 || r.handLimit < 0 || r.initialResources < 0 || r.bossRespawnDelay < 1) errors.Add("Rules：次数/资源不可为负，bossRespawnDelay 至少为 1");
            if (r.playerSide != "left" && r.playerSide != "right") errors.Add("Rules playerSide：只能为 left/right");
            if (r.firstSide != "player" && r.firstSide != "boss") errors.Add("Rules firstSide：只能为 player/boss");
            var boss = d.cards.FirstOrDefault(c => c.id == r.bossCardType);
            if (boss == null || (!r.playerControlsBoss && boss.category != "attack")) errors.Add("Rules bossCardType：AI 必须使用攻击卡，手动模式必须使用已注册卡种");

            if (d.homes.Count(h => h.owner == "player") != 1 || d.homes.Count(h => h.owner == "boss") != 1 || d.homes.Any(h => h.hp < 1 || h.produce < 0 || (h.owner == "boss" && h.produce != 0))) errors.Add("Homes：阵营唯一、生命正数、产出非负且 Boss 产出为零");
            if (d.bossSlots.Select(s => s.id).Distinct().Count() != d.bossSlots.Length || d.bossSlots.Any(s => string.IsNullOrWhiteSpace(s.id) || double.IsNaN(s.rowFraction) || s.rowFraction < 0 || s.rowFraction > 1 || s.edgeOffset < 0 || s.edgeOffset >= r.homeRegionWidth)) errors.Add("BossSlots：ID、行比例或边缘偏移非法");

            var pz = d.presentation[0];
            if (pz.referenceWidth < 1 || pz.referenceHeight < 1 || pz.fontSize < 1 || pz.logLimit < 1 || !Finite(pz.moveSeconds) || !Finite(pz.feedbackSeconds) || !Finite(pz.selectedScale) || pz.moveSeconds < 0 || pz.feedbackSeconds < 0 || pz.selectedScale <= 0) errors.Add("Presentation：尺寸/字号/日志数/动画参数非法");
            foreach (var f in typeof(PresentationRow).GetFields().Where(f => f.FieldType == typeof(string)))
                if (!System.Text.RegularExpressions.Regex.IsMatch((string)f.GetValue(pz) ?? "", "^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$")) errors.Add("Presentation " + f.Name + "：需要 #RRGGBB 或 #RRGGBBAA");
            return errors;
        }

        /// <summary>判断浮点值是否为有限数；NaN/无穷会让动画参数比较失效，因此单独抽取。</summary>
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
