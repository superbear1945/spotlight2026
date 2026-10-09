// 手牌视图（UGUI）：把手牌快照映射为卡牌实例，并把点击上报为选择事件。
// 手牌不参与棋盘坐标，因此不处理高亮，只处理选中与可交互状态。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 手牌显示与交互组件。
    /// 输入：Build 时传入配置资产；Render 时传入快照与当前选择。
    /// 输出：CardClicked 事件（被点击的卡牌视图）与屏幕上的手牌列表。
    /// 为什么单独成视图：手牌数量随抽牌与弃牌变化，需要按 ID 增量同步而不是整表重建，
    /// 否则每次刷新都会打断选中状态与动画。
    /// </summary>
    public sealed class HandView : MonoBehaviour
    {
        /// <summary>手牌容器；建议挂 HorizontalLayoutGroup 并由策划设定卡牌尺寸。</summary>
        [SerializeField] RectTransform _root;
        /// <summary>卡种缺少生成 Prefab 时使用的回退模板。</summary>
        [SerializeField] Card _cardFallbackPrefab;

        /// <summary>手牌被点击时触发；参数为被点击的卡牌视图。</summary>
        public event Action<Card> CardClicked;

        /// <summary>卡牌记录 ID → 手牌视图。</summary>
        readonly Dictionary<long, Card> _cards = new Dictionary<long, Card>();
        /// <summary>配置资产，用于按卡种取 Prefab。</summary>
        SpotlightConfigAsset _asset;
        /// <summary>本局配置，用于读取表现颜色与卡种分类。</summary>
        GameConfiguration _config;

        /// <summary>手牌容器（未配置时回退到自身）。</summary>
        RectTransform Root => _root != null ? _root : (RectTransform)transform;

        /// <summary>
        /// 初始化手牌视图。
        /// 输入：配置资产；输出：无。仅保存引用，实际内容由 Render 增量同步。
        /// </summary>
        public void Build(SpotlightConfigAsset asset, GameConfiguration config)
        {
            _asset = asset;
            _config = config;
            Clear();
        }

        /// <summary>清空全部手牌视图。输入：无；输出：无。</summary>
        public void Clear()
        {
            foreach (var pair in _cards) if (pair.Value != null) Destroy(pair.Value.gameObject);
            _cards.Clear();
        }

        /// <summary>
        /// 渲染手牌。
        /// 输入：快照与当前选择；输出：无。
        /// 行为：移除已不在手牌的实例、为新手牌创建实例、更新选中与交互状态。
        /// </summary>
        public void Render(GameSnapshot snapshot, Selection selection)
        {
            var alive = new HashSet<long>();
            foreach (var record in snapshot.Hand)
            {
                alive.Add(record.Id);
                if (!_cards.TryGetValue(record.Id, out var card) || card == null)
                {
                    card = CardFactory.Create(_asset, _cardFallbackPrefab, Root, record.TypeId);
                    if (card == null) continue;
                    card.name = $"Hand_{record.TypeId}_{record.Id}";
                    EnsureLayoutSize(card);
                    var captured = card;
                    var button = card.GetComponent<UnityEngine.UI.Button>();
                    if (button != null) button.onClick.AddListener(() => CardClicked?.Invoke(captured));
                    _cards[record.Id] = card;
                }
                card.BindHand(_asset != null ? _asset.Find(record.TypeId) : null, record);
                card.SetBaseColor(CategoryColor(record.TypeId));
                card.SetSelected(selection.Kind == SelectionKind.Hand && selection.Id == record.Id);
            }
            var removed = new List<long>();
            foreach (var pair in _cards) if (!alive.Contains(pair.Key)) removed.Add(pair.Key);
            foreach (var id in removed)
            {
                if (_cards[id] != null) Destroy(_cards[id].gameObject);
                _cards.Remove(id);
            }
        }

        /// <summary>
        /// 保证手牌卡在 LayoutGroup 下有确定的建议尺寸。
        /// 输入：新建的卡牌视图；输出：无。
        /// 为什么必需：外观模板上没有 ILayoutElement，HorizontalLayoutGroup 会把子物体尺寸当作 0，
        /// 导致手牌虽然存在但完全不可见。
        /// </summary>
        static void EnsureLayoutSize(Card card)
        {
            var rect = card.transform as RectTransform;
            var size = rect != null && rect.sizeDelta.x > 1f && rect.sizeDelta.y > 1f ? rect.sizeDelta : new Vector2(120f, 160f);
            if (rect != null) rect.sizeDelta = size;
            var element = card.GetComponent<UnityEngine.UI.LayoutElement>();
            if (element == null) element = card.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
            element.preferredWidth = size.x;
            element.preferredHeight = size.y;
            element.minWidth = size.x;
            element.minHeight = size.y;
        }

        /// <summary>按卡牌记录 ID 取得手牌视图。输入：记录 ID；输出：视图，未找到时为 null。</summary>
        public Card GetCard(long id) => _cards.TryGetValue(id, out var card) ? card : null;

        /// <summary>
        /// 计算手牌底色（分类代表色，不混入阵营色，因为手牌永远属于玩家）。
        /// 输入：卡种 ID；输出：底色。卡种未注册时使用特殊卡颜色作为安全回退。
        /// </summary>
        Color CategoryColor(string typeId)
        {
            var definition = _asset != null ? _asset.Find(typeId) : null;
            var category = definition == null
                ? CardCategory.Special
                : definition.CategoryText == "resource" ? CardCategory.Resource
                : definition.CategoryText == "attack" ? CardCategory.Attack
                : CardCategory.Special;
            return CardText.CategoryColor(category, _config != null ? _config.Presentation : null);
        }

        /// <summary>手牌视图集合的只读访问，供反馈组件播放出牌动画。</summary>
        public IReadOnlyDictionary<long, Card> Cards => _cards;
    }
}
