// 卡牌视图组件：唯一同时用于“手牌实例”和“棋盘单位”的显示载体。
// 设计约束：具体卡种不建立子类，Card 只持有组件引用、绑定身份并协调显示。
// 身份有两种：CardRecord（牌库/手牌/墓地中的来源卡）与 UnitState（棋盘上的战斗实体）。
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Spotlight
{
    /// <summary>
    /// 卡牌/单位显示组件。
    /// 输入：BindHand（手牌身份）或 BindUnit（棋盘身份），以及生成器注入的 CardDefinitionSO；
    /// 输出：名称、属性文本、阵营与分类配色、选中与高亮状态。
    /// 为什么统一为一个组件：手牌与棋盘单位共享同一套数值与外观模板，
    /// 分成两种类型会导致同一卡种需要维护两份显示逻辑，且容易与配置分叉。
    /// </summary>
    public sealed class Card : MonoBehaviour
    {
        /// <summary>卡种定义资产；决定名称、分类与组件引用。</summary>
        [SerializeField] CardDefinitionSO _definition;
        /// <summary>战斗组件引用（该卡种未启用时为 null）。</summary>
        [SerializeField] CombatComponent _combat;
        /// <summary>资源组件引用（该卡种未启用时为 null）。</summary>
        [SerializeField] ResourceComponent _resource;
        /// <summary>传送组件引用（该卡种未启用时为 null）。</summary>
        [SerializeField] TeleportComponent _teleport;
        /// <summary>卡面底色 Image，用于表示阵营与分类。</summary>
        [SerializeField] Image _body;
        /// <summary>高亮框 Image，用于表示可部署/可移动/可攻击/可互换。</summary>
        [SerializeField] Image _highlight;
        /// <summary>选中标记根节点，用于放大或描边；可为空。</summary>
        [SerializeField] GameObject _selectedRoot;
        /// <summary>名称文本。</summary>
        [SerializeField] TMP_Text _nameLabel;
        /// <summary>属性文本（生命/攻击/射程/移动/产出/维持）。</summary>
        [SerializeField] TMP_Text _statLabel;
        /// <summary>生命文本，单独显示便于受击反馈；可为空。</summary>
        [SerializeField] TMP_Text _hpLabel;

        /// <summary>手牌身份；棋盘单位状态下为 null。</summary>
        CardRecord _record;
        /// <summary>棋盘单位身份；手牌状态下为 null。</summary>
        UnitState _unit;
        /// <summary>当前是否处于选中状态。</summary>
        bool _selected;
        /// <summary>当前高亮颜色；null 表示无高亮。</summary>
        Color? _highlightColor;
        /// <summary>无高亮时使用的底色，由视图依据 PresentationConfig 的语义颜色设置。</summary>
        Color _baseColor = Color.white;
        /// <summary>是否使用自定义文本（家没有 CardDefinitionSO，不能由 Refresh 覆盖）。</summary>
        bool _customText;

        /// <summary>卡种定义资产。</summary>
        public CardDefinitionSO Definition => _definition;
        /// <summary>手牌身份（未绑定时为 null）。</summary>
        public CardRecord Record => _record;
        /// <summary>棋盘身份（未绑定时为 null）。</summary>
        public UnitState Unit => _unit;
        /// <summary>是否绑定为棋盘单位。</summary>
        public bool IsUnit => _unit != null;
        /// <summary>当前主体 ID：棋盘单位取单位 ID，手牌取卡牌记录 ID，两者不可能混淆。</summary>
        public long SubjectId => _unit != null ? _unit.Id : _record != null ? _record.Id : 0;
        /// <summary>战斗组件（可能为 null）。</summary>
        public CombatComponent Combat => _combat;
        /// <summary>资源组件（可能为 null）。</summary>
        public ResourceComponent Resource => _resource;
        /// <summary>传送组件（可能为 null）。</summary>
        public TeleportComponent Teleport => _teleport;

        /// <summary>
        /// 解析缺失的组件引用。
        /// 输入：无；输出：无。
        /// 为什么需要：外观模板可能由策划手工调整层级，只要组件仍挂在同一 Prefab 上就应正常工作；
        /// 生成器会主动写入引用，这里的兜底只是防止模板被替换后出现空引用。
        /// </summary>
        void Awake()
        {
            if (_combat == null) _combat = GetComponent<CombatComponent>();
            if (_resource == null) _resource = GetComponent<ResourceComponent>();
            if (_teleport == null) _teleport = GetComponent<TeleportComponent>();
            if (_body == null) _body = GetComponent<Image>();
        }

        /// <summary>
        /// 生成期注入卡种定义与组件配置。
        /// 输入：定义资产；输出：无。
        /// 用途：由编辑器生成器和运行时实例化后的统一装配步骤调用。
        /// </summary>
        public void Configure(CardDefinitionSO definition)
        {
            _definition = definition;
            if (definition == null) return;
            if (_combat != null) _combat.Configure(definition.Combat);
            if (_resource != null) _resource.Configure(definition.Resource);
            if (_teleport != null) _teleport.Configure(definition.Teleport);
        }

        /// <summary>
        /// 绑定为手牌实例。
        /// 输入：卡种定义与牌库中的卡牌记录；输出：无。
        /// 说明：手牌不绑定单位状态，因此生命等属性显示的是配置初值。
        /// </summary>
        public void BindHand(CardDefinitionSO definition, CardRecord record)
        {
            Configure(definition);
            _customText = false;
            _record = record;
            _unit = null;
            if (_combat != null) _combat.Bind(null);
            if (_resource != null) _resource.Bind(null);
            Refresh();
        }

        /// <summary>
        /// 绑定为棋盘单位。
        /// 输入：卡种定义与当前单位状态；输出：无。
        /// 说明：只保存状态引用，组件通过该引用实时读取当前生命与行动状态。
        /// </summary>
        public void BindUnit(CardDefinitionSO definition, UnitState unit)
        {
            Configure(definition);
            _customText = false;
            _unit = unit;
            _record = unit != null ? unit.Source : null;
            if (_combat != null) _combat.Bind(unit);
            if (_resource != null) _resource.Bind(unit);
            Refresh();
        }

        /// <summary>
        /// 绑定为家。
        /// 输入：阵营、家状态与底色；输出：无。
        /// 为什么需要单独入口：家不属于可部署卡牌，没有 CardDefinitionSO，
        /// 也不能被后续 Refresh 覆盖文本，因此使用自定义文本模式。
        /// </summary>
        public void BindHome(Side owner, HomeState home, Color color)
        {
            _customText = true;
            _record = null;
            _unit = null;
            if (_combat != null) _combat.Bind(null);
            if (_resource != null) _resource.Bind(null);
            if (_nameLabel != null) _nameLabel.text = owner == Side.Player ? "玩家家" : "Boss 家";
            if (_statLabel != null) _statLabel.text = "生命 " + (home != null ? home.Hp : 0);
            if (_hpLabel != null) _hpLabel.text = (home != null ? home.Hp : 0).ToString();
            SetBaseColor(color);
        }

        /// <summary>
        /// 清空身份绑定。
        /// 输入：无；输出：无。
        /// 用途：对象池回收或对象复用前调用，避免残留上一局的单位引用。
        /// </summary>
        public void Unbind()
        {
            _customText = false;
            _record = null;
            _unit = null;
            if (_combat != null) _combat.Bind(null);
            if (_resource != null) _resource.Bind(null);
            SetSelected(false);
            SetHighlightColor(null);
        }

        /// <summary>
        /// 刷新显示文本与配色。
        /// 输入：无（读取当前定义与状态引用）；输出：无。
        /// 每次快照刷新后由视图调用，保证数值始终来自规则状态。
        /// </summary>
        public void Refresh()
        {
            if (_customText) return;
            if (_definition != null)
            {
                if (_nameLabel != null) _nameLabel.text = _definition.Name;
                if (_statLabel != null) _statLabel.text = CardText.StatLine(_definition);
                if (_body != null) _body.color = _highlightColor ?? _baseColor;
            }
            if (_hpLabel != null)
            {
                if (_unit != null && _combat != null) _hpLabel.text = $"{_combat.CurrentHp}/{_combat.MaxHp}";
                else _hpLabel.text = _combat != null ? _combat.MaxHp.ToString() : string.Empty;
            }
        }

        /// <summary>
        /// 设置选中状态。
        /// 输入：是否选中；输出：无。
        /// 选中只影响显示，不影响规则；点击其它己方卡牌时始终允许切换选中对象。
        /// </summary>
        public void SetSelected(bool selected)
        {
            _selected = selected;
            if (_selectedRoot != null) _selectedRoot.SetActive(selected);
        }

        /// <summary>
        /// 设置高亮颜色。
        /// 输入：颜色；null 表示关闭高亮；输出：无。
        /// 颜色由视图依据 PresentationConfig 与合法动作类型决定，卡牌本身不理解语义。
        /// </summary>
        public void SetHighlightColor(Color? color)
        {
            _highlightColor = color;
            if (_highlight != null)
            {
                _highlight.enabled = color.HasValue;
                if (color.HasValue) _highlight.color = color.Value;
            }
            if (_body != null) _body.color = color ?? _baseColor;
        }

        /// <summary>
        /// 设置无高亮时的底色。
        /// 输入：由视图依据 PresentationConfig（分类色 × 阵营色）算出的颜色；输出：无。
        /// 为什么由视图提供：语义颜色集中来自配置表，Card 不理解“资源卡应该是什么颜色”。
        /// </summary>
        public void SetBaseColor(Color color)
        {
            _baseColor = color;
            if (_body != null) _body.color = _highlightColor ?? _baseColor;
        }

        /// <summary>当前是否选中。输入：无；输出：选中状态。用于视图的点击去重。</summary>
        public bool IsSelected => _selected;
    }
}
