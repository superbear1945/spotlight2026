// 全局配置资产：一局游戏所需的规则、卡组、兵位、表现与卡种清单。
// 生成方式：由编辑器 Excel 编译器整体发布；运行时不修改，构建/进入 Play 前由门禁校验来源摘要。
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 自包含的只读配置资产（编译产物入口）。
    /// 输入：编译器写入的 Rules/Homes/BossSlots/Presentation/Deck 行数据与卡种定义清单；
    /// 输出：CreateConfiguration() 得到的 GameConfiguration，以及 ToDocument() 得到的可校验/可导出文档。
    /// 为什么需要单独一层：规则内核只认识纯 C# 的文档与接口，不引用 UnityEngine；
    /// 本资产负责把“场景中的 SO 引用”翻译成“内核可用的配置”，并复用同一套业务校验。
    /// </summary>
    [GeneratedConfig("Assets/Spotlight/Config/Spotlight.xlsx")]
    public sealed class SpotlightConfigAsset : ScriptableObject, IConfigSourceInfo
    {
        /// <summary>Rules 表唯一行。</summary>
        [SerializeField] RulesRow _rules = new RulesRow();
        /// <summary>Homes 表两行（player 与 boss）。</summary>
        [SerializeField] HomeRow[] _homes;
        /// <summary>BossSlots 表三行。</summary>
        [SerializeField] BossSlotRow[] _bossSlots;
        /// <summary>Presentation 表唯一行。</summary>
        [SerializeField] PresentationRow _presentation = new PresentationRow();
        /// <summary>卡种定义清单，顺序与 Cards 表一致。</summary>
        [SerializeField] CardDefinitionSO[] _cards;
        /// <summary>Deck 表行数据。</summary>
        [SerializeField] DeckRow[] _deck;
        /// <summary>源工作簿文件名，用于 Inspector 提示与过期检测。</summary>
        [SerializeField] string _sourceWorkbook;
        /// <summary>编译时源工作簿的内容摘要，用于进入 Play/构建前的过期检测。</summary>
        [SerializeField] string _sourceHash;
        /// <summary>编译时间（UTC ISO 文本），仅用于展示。</summary>
        [SerializeField] string _compiledAtUtc;

        /// <summary>规则行（只读）。</summary>
        public RulesRow RulesRow => _rules;
        /// <summary>家定义行（只读）。</summary>
        public IReadOnlyList<HomeRow> Homes => _homes ?? System.Array.Empty<HomeRow>();
        /// <summary>兵位定义行（只读）。</summary>
        public IReadOnlyList<BossSlotRow> BossSlots => _bossSlots ?? System.Array.Empty<BossSlotRow>();
        /// <summary>表现参数行（只读）。</summary>
        public PresentationRow PresentationRow => _presentation;
        /// <summary>卡种定义清单（只读）。</summary>
        public IReadOnlyList<CardDefinitionSO> Cards => _cards ?? System.Array.Empty<CardDefinitionSO>();
        /// <summary>卡组行数据（只读）。</summary>
        public IReadOnlyList<DeckRow> Deck => _deck ?? System.Array.Empty<DeckRow>();
        /// <inheritdoc />
        public string SourceWorkbook => _sourceWorkbook;
        /// <inheritdoc />
        public string SourceSheet => "(全部表)";
        /// <summary>编译时源摘要，用于构建门禁比对。</summary>
        public string SourceHash => _sourceHash;
        /// <summary>编译时间文本。</summary>
        public string CompiledAtUtc => _compiledAtUtc;

        /// <summary>
        /// 按卡种 ID 查找定义。
        /// 输入：稳定 ID；输出：定义资产，找不到时返回 null。
        /// 用途：UI 依据快照中的 TypeId 找到 Prefab 与展示名称，不需要另行维护映射表。
        /// </summary>
        public CardDefinitionSO Find(string typeId)
        {
            if (string.IsNullOrEmpty(typeId) || _cards == null) return null;
            for (var i = 0; i < _cards.Length; i++)
                if (_cards[i] != null && _cards[i].Id == typeId) return _cards[i];
            return null;
        }

        /// <summary>
        /// 还原为可校验文档。
        /// 输入：无；输出：ConfigDocument。
        /// 用途：复用 ConfigValidation 做运行前复核，也用于调试面板的 JSON 导出起点。
        /// </summary>
        public ConfigDocument ToDocument()
        {
            return new ConfigDocument
            {
                schemaVersion = 1,
                cards = (_cards ?? System.Array.Empty<CardDefinitionSO>()).Where(c => c != null).Select(c => c.ToCardRow()).ToArray(),
                deck = (_deck ?? System.Array.Empty<DeckRow>()).ToArray(),
                rules = new[] { _rules },
                homes = (_homes ?? System.Array.Empty<HomeRow>()).ToArray(),
                bossSlots = (_bossSlots ?? System.Array.Empty<BossSlotRow>()).ToArray(),
                presentation = new[] { _presentation }
            };
        }

        /// <summary>
        /// 建立本局配置。
        /// 输入：无；输出：可用于创建 GameSession 的 GameConfiguration。
        /// 行为：先做一次完整业务校验（生成资产被手工篡改时立刻失败），再建立只读索引；
        /// 卡种能力接口直接指向组件 SO，因此运行包不需要任何 Excel 解析代码。
        /// </summary>
        public GameConfiguration CreateConfiguration()
        {
            var document = ToDocument();
            var archetypes = (_cards ?? System.Array.Empty<CardDefinitionSO>()).Where(c => c != null).Select(c => c.ToArchetype()).ToArray();
            return new GameConfiguration(document, archetypes);
        }

        /// <summary>
        /// 编译期整体写入。
        /// 输入：已校验的文档、卡种定义与源摘要；输出：无。
        /// 说明：只允许编辑器编译器调用；成功后资产立即代表一套有效配置。
        /// </summary>
        public void EditorApply(ConfigDocument document, IEnumerable<CardDefinitionSO> cards, string sourceWorkbook, string sourceHash, string compiledAtUtc)
        {
            _rules = document.rules[0];
            _homes = document.homes;
            _bossSlots = document.bossSlots;
            _presentation = document.presentation[0];
            _cards = cards.ToArray();
            _deck = document.deck;
            _sourceWorkbook = sourceWorkbook;
            _sourceHash = sourceHash;
            _compiledAtUtc = compiledAtUtc;
        }
    }
}
