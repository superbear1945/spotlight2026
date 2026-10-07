// 卡种定义资产：一个卡种的全部共享配置入口。
// 生成方式：每张 Cards 表数据行生成一个定义资产，三个组件 SO 作为其子资产集中保存，
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
    public sealed class CardDefinitionSO : ScriptableObject, IConfigSourceInfo
    {
        /// <summary>稳定 ID，字母开头，唯一；升级关联与卡组引用都使用它。</summary>
        [SerializeField] string _id;
        /// <summary>展示名称（已去除首尾空格）。</summary>
        [SerializeField] string _name;
        /// <summary>分类文本：resource / attack / special。</summary>
        [SerializeField] string _category;
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
        /// <summary>战斗组件配置；为 null 表示该卡种没有战斗组件。</summary>
        [SerializeField] CombatConfigSO _combat;
        /// <summary>资源组件配置；为 null 表示没有资源组件。</summary>
        [SerializeField] ResourceConfigSO _resource;
        /// <summary>传送组件配置；为 null 表示没有传送组件。</summary>
        [SerializeField] TeleportConfigSO _teleport;
        /// <summary>卡牌 Prefab，由外观模板生成；手牌与棋盘共用同一模板。</summary>
        [SerializeField] Card _prefab;
        /// <summary>源工作簿文件名，用于 Inspector 提示与校验报告。</summary>
        [SerializeField] string _sourceWorkbook;
        /// <summary>源工作表名。</summary>
        [SerializeField] string _sourceSheet;

        /// <summary>稳定卡种 ID。</summary>
        public string Id => _id;
        /// <summary>展示名称。</summary>
        public string Name => _name;
        /// <summary>分类文本，运行期由 ToArchetype 转成枚举。</summary>
        public string CategoryText => _category;
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
        /// <summary>战斗配置 SO（可能为 null）。</summary>
        public CombatConfigSO Combat => _combat;
        /// <summary>资源配置 SO（可能为 null）。</summary>
        public ResourceConfigSO Resource => _resource;
        /// <summary>传送配置 SO（可能为 null）。</summary>
        public TeleportConfigSO Teleport => _teleport;
        /// <summary>卡牌 Prefab，UI 实例化手牌与棋盘单位时使用。</summary>
        public Card Prefab => _prefab;
        /// <inheritdoc />
        public string SourceWorkbook => _sourceWorkbook;
        /// <inheritdoc />
        public string SourceSheet => _sourceSheet;

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
                upgradeFrom = _upgradeFrom,
                viewKey = _viewKey,
                hp = _combat != null ? _combat.Hp : 1,
                atk = _combat != null ? _combat.Attack : 0,
                range = _combat != null ? _combat.Range : 0,
                moveDistance = _combat != null ? _combat.MoveDistance : 0,
                produce = _resource != null ? _resource.Produce : 0,
                upkeep = _resource != null ? _resource.Upkeep : 0,
                resourceComponent = _resource != null,
                teleportComponent = _teleport != null,
                permitsAttackDeploy = _teleport != null && _teleport.PermitsAttackDeploy,
                deployHomeOnly = _deployHomeOnly,
                avoidAdjacentSameType = _avoidAdjacentSameType,
                requiresUpgrade = _requiresUpgrade
            };
        }

        /// <summary>
        /// 构造规则内核使用的共享描述。
        /// 输入：无；输出：CardArchetype（能力接口直接指向组件 SO，未启用的能力为 null）。
        /// </summary>
        public CardArchetype ToArchetype()
        {
            var row = ToCardRow();
            return new CardArchetype(row, _combat, _resource, _teleport);
        }

        /// <summary>
        /// 编译期写入全部字段。
        /// 输入：已校验的行数据、组件 SO 与 Prefab；输出：无。
        /// 说明：只允许编辑器编译器调用；运行时不修改已发布资产。
        /// </summary>
        public void EditorApply(CardRow row, CombatConfigSO combat, ResourceConfigSO resource, TeleportConfigSO teleport, Card prefab, string sourceWorkbook, string sourceSheet)
        {
            _id = row.id;
            _name = row.name.Trim();
            _category = row.category;
            _viewKey = row.viewKey;
            _requiresUpgrade = row.requiresUpgrade;
            _upgradeFrom = row.upgradeFrom;
            _deployHomeOnly = row.deployHomeOnly;
            _avoidAdjacentSameType = row.avoidAdjacentSameType;
            _combat = combat;
            _resource = resource;
            _teleport = teleport;
            _prefab = prefab;
            _sourceWorkbook = sourceWorkbook;
            _sourceSheet = sourceSheet;
        }
    }
}
