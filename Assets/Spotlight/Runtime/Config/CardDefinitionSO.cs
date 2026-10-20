// 卡种定义资产：一个卡种的全部共享配置入口。
// 生成方式：每张 Cards 表数据行生成一个定义资产，启用的五种能力 SO 作为其子资产集中保存，
// 保证同一卡种的数值只有一份来源；Prefab 只保存组件组合与引用，不重复存数值。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 单个卡种的只读定义资产。
    /// 输入：编译器写入的标识、分类、部署开关、组件 SO 与卡牌 Prefab；
    /// 输出：规则内核使用的 CardArchetype、UI 使用的展示信息与实例化模板。
    /// 为什么需要它：把“数值在 SO、外观在 Prefab”对应起来，编辑器改显示名不会破坏升级关联
    /// （关联只依赖稳定 ID），运行与构建门禁也能按此资产核对 Prefab 组件组合。
    /// </summary>
    [GeneratedConfig]
    public sealed class CardDefinitionSO : ScriptableObject
    {
        /// <summary>稳定 ID，字母开头，唯一；升级关联与卡组引用都使用它。</summary>
        [SerializeField] string _id;
        /// <summary>展示名称（已去除首尾空格）。</summary>
        [SerializeField] string _name;
        /// <summary>分类文本：resource / attack / special。</summary>
        [SerializeField] string _category;
        /// <summary>独立于卡牌分类的阶级；玩家卡为 basic/intermediate/advanced，Boss 专用卡为 none。</summary>
        [SerializeField] string _tier;
        /// <summary>外观模板键；当前用于把卡种映射到 Prefab 外观模板。</summary>
        [SerializeField] string _viewKey;
        /// <summary>是否必须覆盖指定己方资源单位才能部署。</summary>
        [SerializeField] bool _requiresUpgrade;
        /// <summary>覆盖升级来源卡种 ID（稳定 ID，不受改名影响）。</summary>
        [SerializeField] string _upgradeFrom;
        /// <summary>是否只能部署在己方区域。</summary>
        [SerializeField] bool _deployHomeOnly;
        /// <summary>是否禁止部署在任何同类单位的四邻。</summary>
        [SerializeField] bool _avoidAdjacentSameType;
        /// <summary>成功部署的一次性资源消耗；0 表示免费，资源不足时该卡不可部署。</summary>
        [SerializeField] int _deployCost;
        /// <summary>部署时四邻必须存在的单位分类文本（resource/attack/special）；空表示没有邻域要求。</summary>
        [SerializeField] string _deployRequireAdjacentCategory;
        /// <summary>四邻中该分类单位的最小数量；0 表示没有邻域要求。</summary>
        [SerializeField] int _deployRequireAdjacentCount;
        /// <summary>是否允许攻击友方单位并把结算改为治疗（仅攻击卡可启用）。</summary>
        [SerializeField] bool _attacksAlliesAsHeal;
        /// <summary>战斗组件配置；为 null 表示该卡种没有战斗组件。</summary>
        [SerializeField] CombatConfigSO _combat;
        /// <summary>资源组件配置；为 null 表示没有资源组件。</summary>
        [SerializeField] ResourceConfigSO _resource;
        /// <summary>传送组件配置；为 null 表示没有传送组件。</summary>
        [SerializeField] TeleportConfigSO _teleport;
        /// <summary>一次性部署减攻组件配置；为 null 表示该卡种没有这项能力。</summary>
        [SerializeField] DeploymentPenaltyConfigSO _deploymentPenalty;
        /// <summary>入场标记配置；为 null 表示该卡种没有这项能力。</summary>
        [SerializeField] MarkOnEnterConfigSO _markOnEnter;
        /// <summary>死亡加攻配置；为 null 表示该卡种没有这项能力。</summary>
        [SerializeField] DeathGrowthConfigSO _deathGrowth;
        /// <summary>即时卡效果配置；为 null 表示该卡部署后留在场上。</summary>
        [SerializeField] InstantEffectConfigSO _instant;
        /// <summary>刷怪能力配置，禁用时为空。</summary>
        [SerializeField] SpawnConfigSO _spawn;
        /// <summary>四邻空格限制配置；为 null 表示没有这项能力。</summary>
        [SerializeField] AdjacentClearanceConfigSO _adjacentClearance;
        /// <summary>击杀恢复行动配置；为 null 表示没有这项能力。</summary>
        [SerializeField] KillActionConfigSO _killAction;
        /// <summary>伤害标记配置；为 null 表示没有这项能力。</summary>
        [SerializeField] DamageMarkConfigSO _damageMark;
        /// <summary>卡牌 Prefab，由外观模板生成；手牌与棋盘共用同一模板。</summary>
        [SerializeField] Card _prefab;
        /// <summary>卡图 Sprite；为 null 表示没有卡图，卡面回退到纯色矩形。</summary>
        [SerializeField] Sprite _art;
        /// <summary>卡图来源文本（Cards.cardArt 的原值），用于 Inspector 回显与 SO→行对象的往返。</summary>
        [SerializeField] string _artPath;

        // 注意：这里刻意不保存源工作簿/工作表名。卡种资产会被主配置引用、随包发布，
        // 存了就会在拆包后读到“Assets/Spotlight/Config/Spotlight.xlsx”。
        // 溯源信息只写在不进包的 Generated/ConfigManifest.asset 与编辑器侧的校验报告里。

        /// <summary>稳定卡种 ID。</summary>
        public string Id => _id;
        /// <summary>展示名称。</summary>
        public string Name => _name;
        /// <summary>分类文本，运行期由 ToArchetype 转成枚举。</summary>
        public string CategoryText => _category;
        /// <summary>卡种阶级文本，转为配置的严格枚举时不推测缺失值。</summary>
        public string TierText => _tier;
        /// <summary>外观模板键。</summary>
        public string ViewKey => _viewKey;
        /// <summary>是否必须覆盖升级。</summary>
        public bool RequiresUpgrade => _requiresUpgrade;
        /// <summary>升级来源卡种 ID。</summary>
        public string UpgradeFrom => _upgradeFrom;
        /// <summary>是否只能部署在己方区域。</summary>
        public bool DeployHomeOnly => _deployHomeOnly;
        /// <summary>是否禁止四邻部署同卡种。</summary>
        public bool AvoidAdjacentSameType => _avoidAdjacentSameType;
        /// <summary>成功部署的一次性资源消耗；0 表示免费。</summary>
        public int DeployCost => _deployCost;
        /// <summary>部署时四邻必须存在的单位分类文本；空表示无要求。</summary>
        public string DeployRequireAdjacentCategory => _deployRequireAdjacentCategory;
        /// <summary>四邻中该分类单位的最小数量；0 表示无要求。</summary>
        public int DeployRequireAdjacentCount => _deployRequireAdjacentCount;
        /// <summary>是否允许攻击友方并改为治疗。</summary>
        public bool AttacksAlliesAsHeal => _attacksAlliesAsHeal;
        /// <summary>战斗配置 SO（可能为 null）。</summary>
        public CombatConfigSO Combat => _combat;
        /// <summary>资源配置 SO（可能为 null）。</summary>
        public ResourceConfigSO Resource => _resource;
        /// <summary>传送配置 SO（可能为 null）。</summary>
        public TeleportConfigSO Teleport => _teleport;
        /// <summary>一次性部署减攻配置 SO（可能为 null）。</summary>
        public DeploymentPenaltyConfigSO DeploymentPenalty => _deploymentPenalty;
        /// <summary>入场标记配置 SO（可能为 null）。</summary>
        public MarkOnEnterConfigSO MarkOnEnter => _markOnEnter;
        /// <summary>死亡加攻配置 SO（可能为 null）。</summary>
        public DeathGrowthConfigSO DeathGrowth => _deathGrowth;
        /// <summary>即时卡效果配置 SO（可能为 null）。</summary>
        public InstantEffectConfigSO Instant => _instant;
        /// <summary>通用刷怪配置子资产（可能为 null）。</summary>
        public SpawnConfigSO Spawn => _spawn;
        /// <summary>四邻空格限制配置 SO（可能为 null）。</summary>
        public AdjacentClearanceConfigSO AdjacentClearance => _adjacentClearance;
        /// <summary>击杀恢复行动配置 SO（可能为 null）。</summary>
        public KillActionConfigSO KillAction => _killAction;
        /// <summary>伤害标记配置 SO（可能为 null）。</summary>
        public DamageMarkConfigSO DamageMark => _damageMark;
        /// <summary>卡牌 Prefab，UI 实例化手牌与棋盘单位时使用。</summary>
        public Card Prefab => _prefab;
        /// <summary>卡图 Sprite（可能为 null）；卡视图据此决定显示立绘还是纯色矩形。</summary>
        public Sprite Art => _art;
        /// <summary>卡图来源文本（Cards.cardArt 原值），供调试与往返核对。</summary>
        public string ArtPath => _artPath;

        /// <summary>
        /// 把 SO 还原成表行对象。
        /// 输入：无；输出：与 Cards 表结构一致的 CardRow。
        /// 为什么需要：配置校验、JSON 导出与 CardArchetype 构造都只认识行对象，
        /// 用它作为唯一中间格式可以避免“SO 路径”与“文档路径”出现两套业务规则。
        /// </summary>
        public CardRow ToCardRow()
        {
            return new CardRow
            {
                id = _id,
                name = _name,
                category = _category,
                tier = _tier,
                upgradeFrom = _upgradeFrom,
                viewKey = _viewKey,
                hp = _combat != null ? _combat.Hp : 1,
                atk = _combat != null ? _combat.Attack : 0,
                range = _combat != null ? _combat.Range : 0,
                moveDistance = _combat != null ? _combat.MoveDistance : 0,
                canAttackResources = _combat != null && _combat.CanAttackResources,
                canSwap = _combat != null && _combat.CanSwap,
                spawnComponent = _spawn != null,
                spawnCardType = _spawn != null ? _spawn.SpawnCardType : string.Empty,
                produce = _resource != null ? _resource.Produce : 0,
                upkeep = _resource != null ? _resource.Upkeep : 0,
                resourceComponent = _resource != null,
                teleportComponent = _teleport != null,
                permitsAttackDeploy = _teleport != null && _teleport.PermitsAttackDeploy,
                deployHomeOnly = _deployHomeOnly,
                avoidAdjacentSameType = _avoidAdjacentSameType,
                requiresUpgrade = _requiresUpgrade,
                deploymentPenaltyComponent = _deploymentPenalty != null,
                deploymentAttackReduction = _deploymentPenalty != null ? _deploymentPenalty.AttackReduction : 0,
                deployCost = _deployCost,
                deployRequireAdjacentCategory = _deployRequireAdjacentCategory,
                deployRequireAdjacentCount = _deployRequireAdjacentCount,
                markOnEnterComponent = _markOnEnter != null,
                markOnEnterRadius = _markOnEnter != null ? _markOnEnter.Radius : 0,
                markRewardResource = _markOnEnter != null ? _markOnEnter.Reward : 0,
                deathGrowthComponent = _deathGrowth != null,
                deathGrowthPerKill = _deathGrowth != null ? _deathGrowth.PerKill : 0,
                deathGrowthLimit = _deathGrowth != null ? _deathGrowth.Limit : 0,
                leaveAfterDeploy = _instant != null,
                deployGainResource = _instant != null ? _instant.GainResource : 0,
                attacksAlliesAsHeal = _attacksAlliesAsHeal,
                adjacentClearanceComponent = _adjacentClearance != null,
                adjacentClearanceMinEmpty = _adjacentClearance != null ? _adjacentClearance.MinEmptyNeighbors : 0,
                killActionComponent = _killAction != null,
                damageMarkComponent = _damageMark != null,
                damageMarkDamagePerStack = _damageMark != null ? _damageMark.DamagePerStack : 0,
                cardArt = _artPath ?? string.Empty
            };
        }

        /// <summary>
        /// 构造规则内核使用的共享描述。
        /// 输入：无；输出：CardArchetype（能力接口直接指向组件 SO，未启用的能力为 null）。
        /// </summary>
        public CardArchetype ToArchetype()
        {
            var row = ToCardRow();
            return new CardArchetype(row, _combat, _resource, _teleport, _deploymentPenalty, _markOnEnter, _deathGrowth, _instant, _spawn, _adjacentClearance, _killAction, _damageMark);
        }

        /// <summary>
        /// 编译期写入全部字段。
        /// 输入：已校验的行数据、组件 SO、Prefab 与卡图；输出：无。
        /// 说明：只允许编辑器编译器调用；运行时不修改已发布资产。
        /// 一次性减攻配置为 null 时引用被清空，因此 SO 与 Excel 行始终一致（禁用即无能力）。
        /// 不再写入源工作簿/工作表名：这两项会随包发布，只保留在 ConfigManifest 与校验报告里。
        /// </summary>
        public void EditorApply(CardRow row, CombatConfigSO combat, ResourceConfigSO resource, TeleportConfigSO teleport, DeploymentPenaltyConfigSO deploymentPenalty, Card prefab, MarkOnEnterConfigSO markOnEnter = null, DeathGrowthConfigSO deathGrowth = null, InstantEffectConfigSO instant = null,
            SpawnConfigSO spawn = null, AdjacentClearanceConfigSO adjacentClearance = null, KillActionConfigSO killAction = null, DamageMarkConfigSO damageMark = null,
            Sprite art = null, string artPath = null)
        {
            _id = row.id;
            _name = row.name.Trim();
            _category = row.category;
            _tier = row.tier;
            _viewKey = row.viewKey;
            _requiresUpgrade = row.requiresUpgrade;
            _upgradeFrom = row.upgradeFrom;
            _deployHomeOnly = row.deployHomeOnly;
            _avoidAdjacentSameType = row.avoidAdjacentSameType;
            _deployCost = row.deployCost;
            _deployRequireAdjacentCategory = row.deployRequireAdjacentCategory;
            _deployRequireAdjacentCount = row.deployRequireAdjacentCount;
            _attacksAlliesAsHeal = row.attacksAlliesAsHeal;
            _combat = combat;
            _resource = resource;
            _teleport = teleport;
            _deploymentPenalty = deploymentPenalty;
            _markOnEnter = markOnEnter;
            _deathGrowth = deathGrowth;
            _instant = instant;
            _spawn = spawn;
            _adjacentClearance = adjacentClearance;
            _killAction = killAction;
            _damageMark = damageMark;
            _prefab = prefab;
            // 卡图与来源文本必须一起写：只写 Sprite 会让 SO→行对象的往返丢掉 Cards.cardArt，
            // 重新编译时就会被当成“没有卡图”而静默回退成纯色矩形。
            // 没有显式给 artPath 时回退用行里的 cardArt，保证任何调用路径都不会丢来源文本。
            _art = art;
            _artPath = !string.IsNullOrEmpty(artPath) ? artPath : (row.cardArt ?? string.Empty);
        }
    }
}
