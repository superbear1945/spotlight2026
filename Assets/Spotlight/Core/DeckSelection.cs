// 抽牌堆构成与玩家选卡：把“抽牌堆张数 + 三阶级比例 + 三阶级携带上限”变成具体的抽牌堆清单。
// 为什么放在规则内核：抽牌堆清单本身就是规则输入，携带上限又决定玩家能带什么，
// 因此分配算法与上限校验必须能被 EditMode 直接测试，且不引用 UnityEngine。
// 名词约定见 docs/游戏内名词.md：这里的配置只描述“开局怎么凑牌”，运行中的来源一律叫“抽牌堆”。
using System;
using System.Collections.Generic;
using System.Linq;

namespace Spotlight
{
    /// <summary>
    /// 抽牌堆构成规则（Rules 表中的 7 个字段）。
    /// 输入：Rules 行或直接给定的 7 个数值；输出：只读规则对象，供分配算法、选卡界面与校验共用。
    /// 为什么独立于 MatchConfig：这 7 个值只在“构牌 + 选卡”时使用，规则层（GameSession）完全不读，
    /// 放进 MatchConfig 会让回合规则看起来依赖它们。
    /// 契约：本类只保存数值，不做校验；校验统一由 ConfigValidation.ValidateDeckRules 报告。
    /// </summary>
    public sealed class DeckConfig
    {
        /// <summary>抽牌堆总张数（配置要求至少 3）。</summary>
        public int DrawPileSize { get; }
        /// <summary>初级卡占比（百分比整数）。</summary>
        public int RatioBasic { get; }
        /// <summary>中级卡占比（百分比整数）。</summary>
        public int RatioIntermediate { get; }
        /// <summary>高级卡占比（百分比整数）。</summary>
        public int RatioAdvanced { get; }
        /// <summary>每局最多携带的初级卡种数量。</summary>
        public int MaxTypesBasic { get; }
        /// <summary>每局最多携带的中级卡种数量。</summary>
        public int MaxTypesIntermediate { get; }
        /// <summary>每局最多携带的高级卡种数量。</summary>
        public int MaxTypesAdvanced { get; }

        /// <summary>
        /// 从 Rules 行复制。输入：Rules 表唯一一行；输出：抽牌堆规则快照。
        /// 为什么复制而非引用：调试草稿会被反复修改，进行中的对局不能中途改变构牌规则。
        /// </summary>
        public DeckConfig(RulesRow row)
            : this(row.drawPileSize, row.tierRatioBasic, row.tierRatioIntermediate, row.tierRatioAdvanced,
                row.maxTypesBasic, row.maxTypesIntermediate, row.maxTypesAdvanced)
        {
        }

        /// <summary>按 7 个数值构造。输入：张数 / 三比例 / 三上限；输出：只读规则对象（不做校验）。</summary>
        public DeckConfig(int drawPileSize, int ratioBasic, int ratioIntermediate, int ratioAdvanced,
            int maxTypesBasic, int maxTypesIntermediate, int maxTypesAdvanced)
        {
            DrawPileSize = drawPileSize;
            RatioBasic = ratioBasic;
            RatioIntermediate = ratioIntermediate;
            RatioAdvanced = ratioAdvanced;
            MaxTypesBasic = maxTypesBasic;
            MaxTypesIntermediate = maxTypesIntermediate;
            MaxTypesAdvanced = maxTypesAdvanced;
        }

        /// <summary>读取某阶级的比例。输入：玩家阶级；输出：百分比整数（非玩家阶级为 0）。</summary>
        public int Ratio(CardTier tier)
        {
            switch (tier)
            {
                case CardTier.Basic: return RatioBasic;
                case CardTier.Intermediate: return RatioIntermediate;
                case CardTier.Advanced: return RatioAdvanced;
                default: return 0;
            }
        }

        /// <summary>读取某阶级的携带上限。输入：玩家阶级；输出：配置上限（非玩家阶级为 0）。</summary>
        public int MaxTypes(CardTier tier)
        {
            switch (tier)
            {
                case CardTier.Basic: return MaxTypesBasic;
                case CardTier.Intermediate: return MaxTypesIntermediate;
                case CardTier.Advanced: return MaxTypesAdvanced;
                default: return 0;
            }
        }

        /// <summary>
        /// 有效携带上限 = min(配置上限, 该阶级池中卡种数)。
        /// 输入：阶级与该阶级可用卡种数；输出：玩家实际能勾选的卡种数量。
        /// 为什么需要：配置上限先写 2、卡池暂时只有 1 种时，界面不该逼玩家勾满 2 种，
        /// 配置校验也用同一口径判断“分配张数是否够用”，避免卡池不足时被迫改 Excel。
        /// </summary>
        public int EffectiveMaxTypes(CardTier tier, int poolTypeCount)
        {
            var configured = MaxTypes(tier);
            var pool = poolTypeCount < 0 ? 0 : poolTypeCount;
            return Math.Max(0, Math.Min(configured, pool));
        }
    }

    /// <summary>
    /// 抽牌堆构成算法：把比例换算成整数张数，并把每阶级张数分给玩家选中的卡种。
    /// 输入：DeckConfig、卡种顺序清单与选中的卡种 ID；输出：DeckEntry 清单（可直接交给 GameSession 洗牌）。
    /// 为什么需要唯一实现：配置校验、选卡界面预览、界面上的“×N”标注与正式开局必须用同一套算法，
    /// 否则玩家看到的预览会和实际抽牌堆不一致。
    /// 算法（四步，与设计确认一致）：
    ///   1) 各阶级 raw = floor(张数 × 比例 / 100)；
    ///   2) 每阶级至少 1 张（比例恒 >= 1，因此不会出现 0 张阶级）；
    ///   3) 合计不足张数时，缺额全部给初级；合计超出时，从张数最多的阶级扣（平手按高级→中级→初级），每阶级保底 1；
    ///   4) 把每阶级张数均分给该阶级选中的卡种，余数按 Cards 表顺序发给靠前的卡种。
    /// </summary>
    public static class DeckComposition
    {
        /// <summary>参与抽牌堆构图的三个玩家阶级，顺序固定为初级、中级、高级。</summary>
        public static readonly CardTier[] PlayerTiers = { CardTier.Basic, CardTier.Intermediate, CardTier.Advanced };

        /// <summary>判断是否为构图阶级。输入：阶级；输出：初级/中级/高级为 true。</summary>
        public static bool IsPlayerTier(CardTier tier) => tier == CardTier.Basic || tier == CardTier.Intermediate || tier == CardTier.Advanced;

        /// <summary>阶级在分配数组中的下标。输入：阶级；输出：0/1/2（非构图阶级为 -1）。</summary>
        public static int TierIndex(CardTier tier)
        {
            switch (tier)
            {
                case CardTier.Basic: return 0;
                case CardTier.Intermediate: return 1;
                case CardTier.Advanced: return 2;
                default: return -1;
            }
        }

        /// <summary>阶级的中文名。输入：阶级；输出：初级/中级/高级等文本，用于错误信息与界面。</summary>
        public static string TierLabel(CardTier tier)
        {
            switch (tier)
            {
                case CardTier.Basic: return "初级";
                case CardTier.Intermediate: return "中级";
                case CardTier.Advanced: return "高级";
                case CardTier.None: return "不适用";
                default: return "未配置";
            }
        }

        /// <summary>
        /// 计算三阶级各分到多少张（不含具体卡种）。
        /// 输入：抽牌堆规则；输出：长度 3 的数组，下标与 <see cref="PlayerTiers"/> 一致，总和等于 DrawPileSize（合法配置下）。
        /// 说明：合法配置由 ConfigValidation 保证张数至少 3、三比例均至少 1，因此本函数总能凑满；
        /// 非法输入时按“每阶级至少 1”尽量分配并提前返回，不抛异常（保证界面预览不会崩溃）。
        /// </summary>
        public static int[] AllocateTierCounts(DeckConfig config)
        {
            var counts = new int[3];
            if (config == null) return counts;
            var size = config.DrawPileSize < 0 ? 0 : config.DrawPileSize;
            for (var i = 0; i < counts.Length; i++)
            {
                var tier = PlayerTiers[i];
                counts[i] = (int)((long)size * config.Ratio(tier) / 100);
                if (counts[i] < 1) counts[i] = 1;
            }
            var total = counts[0] + counts[1] + counts[2];
            if (total < size)
            {
                counts[0] += size - total;
                return counts;
            }
            var excess = total - size;
            // 超出只可能来自第 2 步的保底抬升；从张数最多的阶级扣，平手时高级优先，任何阶级都不降到 0。
            while (excess > 0)
            {
                var pick = -1;
                for (var i = counts.Length - 1; i >= 0; i--)
                    if (counts[i] > 1 && (pick < 0 || counts[i] > counts[pick])) pick = i;
                if (pick < 0) break;
                counts[pick]--;
                excess--;
            }
            return counts;
        }

        /// <summary>
        /// 按选中卡种生成抽牌堆清单。
        /// 输入：抽牌堆规则、按 Cards 表顺序的卡种清单、选中的卡种 ID 集合；
        /// 输出：是否成功、DeckEntry 清单与失败原因。
        /// 失败原因全部是可读中文（缺阶级、超上限、卡种不存在等），供选卡界面直接显示。
        /// </summary>
        public static bool TryBuild(DeckConfig config, IReadOnlyList<CardArchetype> cards, IEnumerable<string> selectedTypeIds,
            out IReadOnlyList<DeckEntry> deck, out string error)
        {
            deck = null;
            error = null;
            if (config == null) { error = "抽牌堆规则缺失"; return false; }
            var ordered = cards ?? Array.Empty<CardArchetype>();
            var byTier = new Dictionary<CardTier, List<string>>();
            foreach (var tier in PlayerTiers) byTier[tier] = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in selectedTypeIds ?? Enumerable.Empty<string>())
            {
                var id = (raw ?? string.Empty).Trim();
                if (id.Length == 0) continue;
                if (!seen.Add(id)) { error = $"卡种 {id} 被重复选择"; return false; }
                var card = ordered.FirstOrDefault(c => c != null && c.Id == id);
                if (card == null) { error = $"卡种 {id} 不存在"; return false; }
                if (!IsPlayerTier(card.Tier))
                {
                    error = $"卡种 {card.Name} 的阶级为{TierLabel(card.Tier)}，不能携带";
                    return false;
                }
                byTier[card.Tier].Add(id);
            }

            // 已选顺序统一按 Cards 表顺序，保证“余数发给靠前卡种”在界面与规则层完全一致。
            foreach (var tier in PlayerTiers)
                byTier[tier].Sort((a, b) => IndexOf(ordered, a).CompareTo(IndexOf(ordered, b)));

            foreach (var tier in PlayerTiers)
            {
                var label = TierLabel(tier);
                var poolCount = ordered.Count(c => c != null && c.Tier == tier);
                if (poolCount < 1) { error = $"{label}没有任何可携带卡种"; return false; }
                if (byTier[tier].Count == 0) { error = $"请至少选择一种{label}卡"; return false; }
                var capacity = config.EffectiveMaxTypes(tier, poolCount);
                if (byTier[tier].Count > capacity)
                {
                    error = $"{label}最多携带 {capacity} 种，当前选了 {byTier[tier].Count} 种";
                    return false;
                }
            }

            var counts = AllocateTierCounts(config);
            var entries = new List<DeckEntry>();
            for (var i = 0; i < PlayerTiers.Length; i++)
            {
                var tier = PlayerTiers[i];
                var ids = byTier[tier];
                var share = counts[i];
                if (share < ids.Count)
                {
                    error = $"{TierLabel(tier)}只分到 {share} 张，少于已选卡种数 {ids.Count}，无法保证每种至少 1 张";
                    return false;
                }
                var each = share / ids.Count;
                var remainder = share % ids.Count;
                for (var j = 0; j < ids.Count; j++)
                    entries.Add(new DeckEntry(ids[j], each + (j < remainder ? 1 : 0)));
            }
            deck = Array.AsReadOnly(entries.ToArray());
            return true;
        }

        /// <summary>
        /// 用“默认选卡”（每阶级按有效上限取满，顺序为 Cards 表顺序）生成抽牌堆。
        /// 输入：抽牌堆规则与卡种顺序；输出：DeckEntry 清单与失败原因。
        /// 用途：游戏启动前的初值、配置文档路径的兜底、以及调试应用后选卡失效时的回退。
        /// </summary>
        public static bool TryBuildDefault(DeckConfig config, IReadOnlyList<CardArchetype> cards,
            out IReadOnlyList<DeckEntry> deck, out string error)
        {
            var selected = new List<string>();
            if (config != null && cards != null)
            {
                foreach (var tier in PlayerTiers)
                {
                    var pool = cards.Where(c => c != null && c.Tier == tier).ToArray();
                    var capacity = config.EffectiveMaxTypes(tier, pool.Length);
                    for (var i = 0; i < capacity && i < pool.Length; i++) selected.Add(pool[i].Id);
                }
            }
            return TryBuild(config, cards, selected, out deck, out error);
        }

        /// <summary>
        /// 统计每卡种的张数。输入：抽牌堆清单；输出：卡种 ID → 张数（供选卡界面标注“×N”）。
        /// </summary>
        public static IReadOnlyDictionary<string, int> CountsByType(IEnumerable<DeckEntry> deck)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in deck ?? Enumerable.Empty<DeckEntry>())
            {
                if (entry == null) continue;
                map.TryGetValue(entry.TypeId, out var current);
                map[entry.TypeId] = current + entry.Count;
            }
            return map;
        }

        /// <summary>查找卡种在顺序清单中的下标。输入：清单与 ID；输出：下标（找不到时放到最后）。</summary>
        static int IndexOf(IReadOnlyList<CardArchetype> cards, string id)
        {
            for (var i = 0; i < cards.Count; i++)
                if (cards[i] != null && cards[i].Id == id) return i;
            return int.MaxValue;
        }
    }

    /// <summary>
    /// 玩家选卡状态（一局开始前的“携带选择”）。
    /// 输入：一份已校验配置（提供卡种池、阶级上限与抽牌堆规则）；
    /// 输出：选中的卡种 ID、每阶级计数、实时预览张数，以及据此生成的 GameConfiguration。
    /// 生命周期：只存在于本次运行会话（本项目不做持久化，每次启动重新选卡）。
    /// 为什么不是 MatchPhase：选卡发生在开局之前，运行中的“阶段”仍只有 Action/Discard/Finished。
    /// </summary>
    public sealed class DeckSelectionState
    {
        readonly GameConfiguration _configuration;
        readonly List<string> _selected = new List<string>();

        /// <summary>
        /// 建立选卡状态并填入默认选卡。
        /// 输入：已校验配置；输出：可直接用于界面的状态对象。
        /// 默认选卡 = 每阶级按有效上限取满（Cards 表顺序），即“配置允许的最大搭配”。
        /// </summary>
        public DeckSelectionState(GameConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            SelectDefaults();
        }

        /// <summary>该选卡状态对应的配置（抽牌堆规则与卡种池来源）。</summary>
        public GameConfiguration Configuration => _configuration;

        /// <summary>已选中的卡种 ID（顺序为选取顺序，界面排序请用 Pool）。</summary>
        public IReadOnlyList<string> SelectedTypeIds => _selected;

        /// <summary>某阶级的可选卡种池（按 Cards 表顺序）。输入：阶级；输出：卡种序列。</summary>
        public IEnumerable<CardArchetype> Pool(CardTier tier) => _configuration.CardOrder.Where(c => c != null && c.Tier == tier);

        /// <summary>某阶级的有效携带上限。输入：阶级；输出：玩家最多能勾选的数量。</summary>
        public int Capacity(CardTier tier) => _configuration.DeckConfig.EffectiveMaxTypes(tier, Pool(tier).Count());

        /// <summary>某阶级已选数量。输入：阶级；输出：已勾选卡种数。</summary>
        public int SelectedCount(CardTier tier) => _selected.Count(id => _configuration.FindCard(id)?.Tier == tier);

        /// <summary>是否已选中。输入：卡种 ID；输出：选中为 true。</summary>
        public bool IsSelected(string id) => id != null && _selected.Contains(id);

        /// <summary>
        /// 是否还能勾选。输入：卡种 ID；输出：非玩家阶级、不存在或该阶级已达上限时为 false。
        /// 用途：界面据此置灰；“勾不上”本身就是上限提示，不需要额外弹窗。
        /// </summary>
        public bool CanSelect(string id)
        {
            var card = _configuration.FindCard(id);
            if (card == null || !DeckComposition.IsPlayerTier(card.Tier) || IsSelected(id)) return false;
            return SelectedCount(card.Tier) < Capacity(card.Tier);
        }

        /// <summary>
        /// 勾选或取消一个卡种。输入：卡种 ID；输出：状态是否改变。
        /// 取消永远允许；勾选受有效上限约束，超限时返回 false 且不改变状态。
        /// </summary>
        public bool Toggle(string id)
        {
            if (IsSelected(id)) { _selected.Remove(id); return true; }
            if (!CanSelect(id)) return false;
            _selected.Add(id);
            return true;
        }

        /// <summary>恢复默认选卡。输入：无；输出：无（每阶级按有效上限取满）。</summary>
        public void SelectDefaults()
        {
            _selected.Clear();
            foreach (var tier in DeckComposition.PlayerTiers)
            {
                var capacity = Capacity(tier);
                var taken = 0;
                foreach (var card in Pool(tier))
                {
                    if (taken >= capacity) break;
                    _selected.Add(card.Id);
                    taken++;
                }
            }
        }

        /// <summary>
        /// 尝试把一组历史选中的卡种恢复为当前选卡。
        /// 输入：卡种 ID 集合（例如调试重开前的选卡）；输出：恢复后是否合法。
        /// 行为：逐个筛选“仍存在、属于玩家阶级、未超上限”的卡种；结果非法时返回 false，
        /// 由调用方决定是否回退默认选卡（本方法不擅自修改为默认值，便于调用方给出提示）。
        /// </summary>
        public bool TryRestore(IEnumerable<string> ids)
        {
            _selected.Clear();
            foreach (var id in ids ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (IsSelected(id)) continue;
                if (!CanSelect(id)) continue;
                _selected.Add(id);
            }
            return ValidationMessage == null;
        }

        /// <summary>
        /// 当前选卡是否可开局；不可开局时返回可读原因（null 表示合法）。
        /// 用途：界面据此禁用“确认并开局”，规则层也用它兜底，避免绕过界面提交非法选卡。
        /// </summary>
        public string ValidationMessage
        {
            get
            {
                return DeckComposition.TryBuild(_configuration.DeckConfig, _configuration.CardOrder, _selected, out _, out var error)
                    ? null
                    : error;
            }
        }

        /// <summary>
        /// 三阶级各分到多少张（实时预览）。输入：无；输出：阶级 → 张数。
        /// 与正式开局使用的分配算法完全一致，因此界面计数永远等于实际抽牌堆构成。
        /// </summary>
        public IReadOnlyDictionary<CardTier, int> PreviewTierCounts()
        {
            var counts = DeckComposition.AllocateTierCounts(_configuration.DeckConfig);
            var map = new Dictionary<CardTier, int>();
            for (var i = 0; i < DeckComposition.PlayerTiers.Length; i++) map[DeckComposition.PlayerTiers[i]] = counts[i];
            return map;
        }

        /// <summary>
        /// 每卡种本局张数（实时预览，供每行的“×N”）；选卡非法时返回空表。
        /// </summary>
        public IReadOnlyDictionary<string, int> PreviewTypeCounts()
        {
            if (!DeckComposition.TryBuild(_configuration.DeckConfig, _configuration.CardOrder, _selected, out var deck, out _))
                return new Dictionary<string, int>();
            return DeckComposition.CountsByType(deck);
        }

        /// <summary>
        /// 生成抽牌堆清单。输入：无；输出：是否成功、清单与失败原因。
        /// </summary>
        public bool TryBuildDeck(out IReadOnlyList<DeckEntry> deck, out string error) =>
            DeckComposition.TryBuild(_configuration.DeckConfig, _configuration.CardOrder, _selected, out deck, out error);

        /// <summary>
        /// 用当前选卡生成一份新配置（只替换抽牌堆）。
        /// 输入：无；输出：是否成功、新配置与失败原因。失败时不修改原配置，界面可继续调整。
        /// </summary>
        public bool TryBuildConfiguration(out GameConfiguration configuration, out string error)
        {
            configuration = null;
            if (!TryBuildDeck(out var deck, out error)) return false;
            try
            {
                configuration = _configuration.WithDeck(deck);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }
    }
}
