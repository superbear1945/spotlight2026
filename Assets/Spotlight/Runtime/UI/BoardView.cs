// 棋盘视图（UGUI）：构建格网、放置单位与家、渲染合法目标高亮，并把点击转换为坐标事件。
// 与规则层的分工：视图不判断合法性，只显示 GetLegalActions 的结果；点击是否成功由规则层决定。
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Spotlight
{
    /// <summary>
    /// 棋盘显示与交互组件。
    /// 输入：Build 时传入本局配置与配置资产；Render 时传入快照、合法动作与当前选择。
    /// 输出：CellClicked 事件（坐标）与屏幕上的格子/单位/高亮显示。
    /// 为什么需要它：棋盘是唯一把抽象坐标映射到屏幕位置的地方，
    /// 同时负责左右镜像显示，使玩家始终在屏幕左侧、操作方向与文档一致。
    /// </summary>
    public sealed class BoardView : MonoBehaviour
    {
        /// <summary>格网父节点；构建时确保存在 GridLayoutGroup。</summary>
        [SerializeField] RectTransform _cellRoot;
        /// <summary>格子 Prefab。</summary>
        [SerializeField] BoardCellView _cellPrefab;
        /// <summary>卡种缺少生成 Prefab 时使用的回退模板。</summary>
        [SerializeField] Card _cardFallbackPrefab;
        /// <summary>家使用的显示 Prefab（无 CardDefinitionSO，走自定义文本模式）。</summary>
        [SerializeField] Card _homePrefab;

        /// <summary>棋盘格被点击时触发的事件；参数为内部坐标。</summary>
        public event Action<Cell> CellClicked;

        /// <summary>内部坐标 → 格子视图。</summary>
        readonly Dictionary<Cell, BoardCellView> _cells = new Dictionary<Cell, BoardCellView>();
        /// <summary>单位 ID → 卡牌视图。</summary>
        readonly Dictionary<long, Card> _unitCards = new Dictionary<long, Card>();
        /// <summary>实体 ID（单位或家）→ 上一帧所在格，用于移动动画起点。</summary>
        readonly Dictionary<long, Cell> _lastPositions = new Dictionary<long, Cell>();

        /// <summary>本局配置索引；Build 时写入。</summary>
        GameConfiguration _config;
        /// <summary>配置资产，用于按卡种取 Prefab。</summary>
        SpotlightConfigAsset _asset;

        /// <summary>本局列数，供视图换算显示坐标。</summary>
        int _columns = 10;
        /// <summary>本局行数。</summary>
        int _rows = 5;
        /// <summary>玩家是否显示在屏幕左侧（始终为 true，Boss 侧镜像）。</summary>
        bool _playerOnLeft = true;
        /// <summary>是否还需要在布局完成后重算格子尺寸（Build 时 RectTransform 可能尚未布局）。</summary>
        bool _needsCellSize;
        /// <summary>格网布局组件缓存，避免每帧 GetComponent。</summary>
        GridLayoutGroup _grid;

        /// <summary>单位 ID → 卡牌视图的只读访问，供反馈与调试面板定位动画对象。</summary>
        public IReadOnlyDictionary<long, Card> UnitCards => _unitCards;

        /// <summary>
        /// 构建棋盘格网。
        /// 输入：本局配置与配置资产；输出：无。
        /// 行为：销毁旧格网，按配置行/列创建格子，并按“内部行 0 在底部”的顺序排列。
        /// </summary>
        public void Build(GameConfiguration config, SpotlightConfigAsset asset)
        {
            _config = config;
            _asset = asset;
            _rows = config.Rules.Rows;
            _columns = config.Rules.Columns;
            _playerOnLeft = config.Rules.PlayerOnLeft;

            foreach (var pair in _cells) if (pair.Value != null) Destroy(pair.Value.gameObject);
            _cells.Clear();
            ClearUnits();

            if (_cellRoot == null || _cellPrefab == null) return;
            var grid = _cellRoot.GetComponent<GridLayoutGroup>();
            if (grid == null) grid = _cellRoot.gameObject.AddComponent<GridLayoutGroup>();
            _grid = grid;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = _columns;
            grid.spacing = new Vector2(2f, 2f);
            grid.padding = new RectOffset(2, 2, 2, 2);
            // 先用回退尺寸建格子，待首次布局完成后再按真实尺寸重算（见 RecalculateCellSize）。
            grid.cellSize = new Vector2(64f, 64f);
            _needsCellSize = true;

            // 显示顺序：从顶部行开始逐行填充，因此内部行 Rows-1 排在第一行。
            for (var uiRow = 0; uiRow < _rows; uiRow++)
                for (var uiCol = 0; uiCol < _columns; uiCol++)
                {
                    var cell = ToInternal(uiRow, uiCol);
                    var view = Instantiate(_cellPrefab, _cellRoot);
                    view.name = $"Cell_{cell.Row}_{cell.Col}";
                    view.Initialize(cell, c => CellClicked?.Invoke(c));
                    _cells[cell] = view;
                }
            RecalculateCellSize();
        }

        /// <summary>
        /// 按格网容器的真实尺寸重算格子大小。
        /// 输入：无；输出：无（尺寸仍不可用时保留上次结果，下次 Render 会重试）。
        /// 为什么需要：在 Start 阶段 RectTransform 可能还没完成布局，
        /// 直接测量会得到 0 或旧值，造成格子溢出屏幕或压在面板下面。
        /// </summary>
        void RecalculateCellSize()
        {
            if (_grid == null || _cellRoot == null) return;
            LayoutRebuilder.ForceRebuildLayoutImmediate(_cellRoot);
            var size = _cellRoot.rect;
            if (size.width < 1f || size.height < 1f) return;
            _grid.cellSize = new Vector2(
                Mathf.Max(16f, (size.width - _grid.spacing.x * (_columns - 1) - _grid.padding.left - _grid.padding.right) / _columns),
                Mathf.Max(16f, (size.height - _grid.spacing.y * (_rows - 1) - _grid.padding.top - _grid.padding.bottom) / _rows));
            _needsCellSize = false;
        }

        /// <summary>显示坐标（左上起）→ 内部坐标（左下起、按玩家视角镜像）。</summary>
        Cell ToInternal(int uiRow, int uiCol)
        {
            var row = _rows - 1 - uiRow;
            var col = _playerOnLeft ? uiCol : _columns - 1 - uiCol;
            return new Cell(row, col);
        }

        /// <summary>销毁全部单位与家视图。输入：无；输出：无。用途：重开与重建棋盘前清理。</summary>
        public void ClearUnits()
        {
            foreach (var pair in _unitCards) if (pair.Value != null) Destroy(pair.Value.gameObject);
            _unitCards.Clear();
            _lastPositions.Clear();
        }

        /// <summary>
        /// 取得某实体当前所在格的屏幕矩形。
        /// 输入：内部坐标；输出：格子 RectTransform（不存在时返回 null）。
        /// </summary>
        public RectTransform CellRect(Cell cell) => _cells.TryGetValue(cell, out var view) ? view.Rect : null;

        /// <summary>
        /// 渲染一帧棋盘状态。
        /// 输入：快照、当前选中对象的合法动作与选择上下文；输出：无。
        /// 顺序：先清高亮 → 画高亮 → 同步家与单位视图 → 更新选中标记。
        /// </summary>
        public void Render(GameSnapshot snapshot, LegalActions actions, Selection selection)
        {
            if (_cells.Count == 0) return;
            if (_needsCellSize) RecalculateCellSize();
            foreach (var pair in _cells) pair.Value.SetHighlight(null);
            ApplyHighlights(actions);
            SyncHomes(snapshot);
            SyncUnits(snapshot);
            ApplySelection(selection);
        }

        /// <summary>
        /// 按合法动作列表画高亮。
        /// 输入：合法动作；输出：无。
        /// 规则：同一格同时属于多类动作时按 部署 → 攻击 → 互换 → 移动 的优先级取色，
        /// 与“攻击/互换比移动更需要注意力”的交互直觉一致。
        /// </summary>
        void ApplyHighlights(LegalActions actions)
        {
            var presentation = _config != null ? _config.Presentation : null;
            Highlight(actions.Deploy, "deploy", presentation);
            Highlight(actions.Attack, "attack", presentation);
            Highlight(actions.Swap, "swap", presentation);
            Highlight(actions.Move, "move", presentation);
        }

        /// <summary>给一组格设置同一种语义高亮。输入：格列表、语义名与表现配置；输出：无。</summary>
        void Highlight(IReadOnlyList<Cell> cells, string kind, PresentationConfig presentation)
        {
            if (cells == null) return;
            var color = CardText.HighlightColor(kind, presentation);
            if (!color.HasValue) return;
            foreach (var cell in cells)
                if (_cells.TryGetValue(cell, out var view)) view.SetHighlight(color);
        }

        /// <summary>同步家的显示。输入：快照；输出：无。</summary>
        void SyncHomes(GameSnapshot snapshot)
        {
            foreach (var home in snapshot.Homes)
            {
                Place(home.Id, home.Position, () => CreateHome(home));
                if (_unitCards.TryGetValue(home.Id, out var card) && card != null)
                    card.BindHome(home.Owner, home, CardText.OwnerColor(home.Owner, _config.Presentation));
            }
        }

        /// <summary>同步单位显示（新增、移动、移除、刷新数值）。输入：快照；输出：无。</summary>
        void SyncUnits(GameSnapshot snapshot)
        {
            var alive = new HashSet<long>();
            foreach (var unit in snapshot.Units)
            {
                alive.Add(unit.Id);
                Place(unit.Id, unit.Position, () => CreateUnit(unit));
                if (_unitCards.TryGetValue(unit.Id, out var card) && card != null)
                {
                    card.BindUnit(_asset != null ? _asset.Find(unit.Source.TypeId) : null, unit);
                    card.SetBaseColor(SequenceColor(unit.Source.TypeId, unit.Source.Owner));
                }
            }
            var removed = new List<long>();
            foreach (var pair in _unitCards)
                if (!alive.Contains(pair.Key) && !snapshot.Homes.Any(h => h.Id == pair.Key)) removed.Add(pair.Key);
            foreach (var id in removed)
            {
                if (_unitCards[id] != null) Destroy(_unitCards[id].gameObject);
                _unitCards.Remove(id);
                _lastPositions.Remove(id);
            }
        }

        /// <summary>
        /// 把实体视图放到指定格；不存在时先创建。
        /// 输入：ID、坐标与创建委托；输出：无。
        /// 不变式：只要没有正在播放的移动动画，卡牌最终一定挂在快照坐标对应的格子上且偏移为零。
        /// 因此规则层只要移动了单位，界面就必然跟着移动，不会因为动画被中断而把棋子留在旧格。
        /// </summary>
        void Place(long id, Cell cell, Func<Card> create)
        {
            if (!_unitCards.TryGetValue(id, out var card) || card == null)
            {
                card = create();
                if (card == null) return;
                // 棋盘卡牌只负责显示：关掉射线检测，让点击穿透到格子（BoardCellView）上。
                card.SetRaycastTarget(false);
                _unitCards[id] = card;
                _lastPositions[id] = cell;
            }
            var rect = CellRect(cell);
            if (rect == null) return;
            var rt = (RectTransform)card.transform;
            if (rt.parent != rect)
            {
                rt.SetParent(rect, false);
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                rt.localScale = Vector3.one;
                rt.localRotation = Quaternion.identity;
            }
            else if (!card.IsMoveAnimating && rt.anchoredPosition != Vector2.zero)
            {
                // 位置校准：动画被中断（或补间根本没启动）时，卡牌可能停留在旧格附近；
                // 没有动画在跑就必须立刻回到格心，避免出现“日志说移动了、棋子却还在原地”。
                rt.anchoredPosition = Vector2.zero;
            }
            // 每次同步都刷新尺寸：格子尺寸会在首次布局完成后变化。
            // 注意：中心锚点下 sizeDelta 就是绝对尺寸，不能用负数偏移。
            var cellSize = rect.rect.size;
            rt.sizeDelta = new Vector2(Mathf.Max(12f, cellSize.x - 8f), Mathf.Max(12f, cellSize.y - 8f));
            _lastPositions[id] = cell;
        }

        /// <summary>
        /// 计算从 from 格移动到 to 格的显示位移。
        /// 输入：起点格与终点格；输出：起点相对于终点的 anchoredPosition 偏移。
        /// 用途：移动动画先摆到偏移位置，再回到 0，从而在不改变父节点层级的前提下表现位移。
        /// </summary>
        public Vector2 CellOffset(Cell from, Cell to)
        {
            return _cells.TryGetValue(from, out var a) && _cells.TryGetValue(to, out var b)
                ? a.Rect.anchoredPosition - b.Rect.anchoredPosition
                : Vector2.zero;
        }

        /// <summary>创建家视图。输入：家状态；输出：卡牌视图。</summary>
        Card CreateHome(HomeState home)
        {
            if (_homePrefab == null) return null;
            var card = Instantiate(_homePrefab);
            card.BindHome(home.Owner, home, CardText.OwnerColor(home.Owner, _config.Presentation));
            return card;
        }

        /// <summary>创建单位视图。输入：单位状态；输出：卡牌视图。</summary>
        Card CreateUnit(UnitState unit)
        {
            var card = CardFactory.Create(_asset, _cardFallbackPrefab, transform, unit.Source.TypeId);
            if (card != null) card.SetBaseColor(SequenceColor(unit.Source.TypeId, unit.Source.Owner));
            return card;
        }

        /// <summary>
        /// 计算卡面底色：分类色与阵营色的混合。
        /// 输入：卡种 ID 与阵营；输出：底色。既表达“这是什么卡”，也表达“属于谁”。
        /// </summary>
        Color SequenceColor(string typeId, Side owner)
        {
            var definition = _asset != null ? _asset.Find(typeId) : null;
            var category = definition == null ? CardCategory.Special
                : definition.CategoryText == "resource" ? CardCategory.Resource
                : definition.CategoryText == "attack" ? CardCategory.Attack : CardCategory.Special;
            var presentation = _config != null ? _config.Presentation : null;
            return Color.Lerp(CardText.CategoryColor(category, presentation), CardText.OwnerColor(owner, presentation), 0.35f);
        }

        /// <summary>更新选中标记。输入：选择上下文；输出：无。</summary>
        void ApplySelection(Selection selection)
        {
            foreach (var pair in _unitCards)
                if (pair.Value != null) pair.Value.SetSelected(selection.Kind == SelectionKind.Unit && selection.Id == pair.Key);
        }

        /// <summary>
        /// 取得实体的上一帧坐标，用于移动动画起点。
        /// 输入：实体 ID；输出：坐标，未知时返回 null。
        /// </summary>
        public Cell? LastPosition(long id) => _lastPositions.TryGetValue(id, out var cell) ? cell : (Cell?)null;

        /// <summary>
        /// 按实体 ID 取得卡牌视图。
        /// 输入：实体 ID（单位或家）；输出：卡牌视图，未找到时为 null。
        /// </summary>
        public Card GetCard(long id) => _unitCards.TryGetValue(id, out var card) ? card : null;
    }
}
