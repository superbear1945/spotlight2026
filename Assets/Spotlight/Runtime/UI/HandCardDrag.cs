using DG.Tweening;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Spotlight
{
    /// <summary>
    /// 手牌专用的指针适配与拖动表现。输入：UGUI 指针事件及 HandView 的许可；
    /// 输出：向 HandView 转发手势并移动卡面，不执行规则。单独装配可保证棋盘卡牌不接收拖拽。
    /// </summary>
    public sealed class HandCardDrag : MonoBehaviour, IPointerDownHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        /// <summary>事件拥有者和只读身份的卡面。</summary>
        HandView _owner;
        Card _card;
        /// <summary>拖动矩形、原位占位、原父节点及浮动层。</summary>
        RectTransform _rect, _placeholder, _originalParent, _layer;
        /// <summary>通过组级别屏蔽射线，避免改写模板内各 Graphic 的原始属性。</summary>
        CanvasGroup _group;
        /// <summary>恢复排版所需的矩形属性，以及画布局部空间的鼠标抓取偏移。</summary>
        Vector2 _anchorMin, _anchorMax, _pivot, _size, _offset;
        Vector3 _scale;
        /// <summary>原始射线设置、手势去重和生命周期状态。</summary>
        bool _originalBlocksRaycasts, _suppressClick, _dragging, _destroying, _deferRestore;
        /// <summary>当前手势指针身份，避免其它按钮的松手结束本次拖动。</summary>
        int _pointerId;
        /// <summary>本组件拥有的回位补间；通过矩形目标绑定参与统一停止。</summary>
        Tween _returnTween;

        /// <summary>手势绑定的手牌，供协调器读取卡牌记录身份。</summary>
        public Card Card => _card;
        /// <summary>是否应吞掉点击；拖动尝试（包括被规则拒绝）均不能变成点击弃牌。</summary>
        public bool SuppressClick => _suppressClick;
        /// <summary>是否已获准跟随指针；回位补间不属于拖动。</summary>
        public bool IsDragging => _dragging;

        /// <summary>绑定手牌视图和卡面。输入：拥有者与卡面；输出：缓存引用，统一事件与清理归属。</summary>
        public void Initialize(HandView owner, Card card)
        {
            _owner = owner;
            _card = card;
            _rect = (RectTransform)transform;
            _group = GetComponent<CanvasGroup>();
            if (_group == null) _group = gameObject.AddComponent<CanvasGroup>();
        }

        /// <summary>新一轮左键按下才解除点击抑制。输入：指针；输出：重新允许未超过阈值的普通点击。</summary>
        public void OnPointerDown(PointerEventData data)
        {
            if (data.button == PointerEventData.InputButton.Left) _suppressClick = false;
        }

        /// <summary>超过 EventSystem 默认阈值时请求拖动。输入：指针；输出：转发请求并禁止本次手势点击。</summary>
        public void OnBeginDrag(PointerEventData data)
        {
            if (data.button != PointerEventData.InputButton.Left || _owner == null) return;
            _suppressClick = true;
            data.eligibleForClick = false;
            _owner.RequestDrag(this, data);
        }

        /// <summary>
        /// 经协调器许可后开始移动表现。输入：指针、同 Canvas 的浮动层及抓取瞬间的世界位置；输出：是否成功。
        /// 原位占位让其它手牌不重排；局部坐标换算保证不同 CanvasScaler 下抓取点不跳变。
        /// </summary>
        public bool BeginVisual(PointerEventData data, RectTransform layer, Vector3 grabbedPosition)
        {
            if (_dragging || _rect == null || layer == null) return false;
            _rect.DOKill(true);
            RestoreLayout();
            _originalParent = _rect.parent as RectTransform;
            if (_originalParent == null) return false;
            _anchorMin = _rect.anchorMin; _anchorMax = _rect.anchorMax;
            _pivot = _rect.pivot; _size = _rect.sizeDelta; _scale = _rect.localScale;
            _originalBlocksRaycasts = _group.blocksRaycasts;
            _placeholder = new GameObject("HandDragPlaceholder", typeof(RectTransform), typeof(LayoutElement)).GetComponent<RectTransform>();
            _placeholder.SetParent(_originalParent, false);
            _placeholder.SetSiblingIndex(_rect.GetSiblingIndex());
            _placeholder.anchorMin = _anchorMin; _placeholder.anchorMax = _anchorMax;
            _placeholder.pivot = _pivot; _placeholder.sizeDelta = _size;
            _placeholder.anchoredPosition = _rect.anchoredPosition;
            var source = GetComponent<LayoutElement>();
            var layout = _placeholder.GetComponent<LayoutElement>();
            layout.preferredWidth = source != null ? source.preferredWidth : _rect.rect.width;
            layout.preferredHeight = source != null ? source.preferredHeight : _rect.rect.height;
            layout.minWidth = source != null ? source.minWidth : _rect.rect.width;
            layout.minHeight = source != null ? source.minHeight : _rect.rect.height;
            _layer = layer;
            _rect.SetParent(layer, true);
            _rect.SetAsLastSibling();
            // 回位中重抓先清理旧补间和排版，再恢复抓取瞬间位置，不让卡面跳回手牌后再跟鼠标。
            _rect.position = grabbedPosition;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(layer, data.pressPosition, data.pressEventCamera, out var pointer);
            _offset = (Vector2)_rect.localPosition - pointer;
            _group.blocksRaycasts = false;
            _pointerId = data.pointerId;
            _dragging = true;
            return true;
        }

        /// <summary>跟随当前指针并通知协调器重查落点。输入：指针位置；输出：卡面位置与悬停事件。</summary>
        public void OnDrag(PointerEventData data)
        {
            if (!_dragging || data.pointerId != _pointerId) return;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_layer, data.position, data.pressEventCamera, out var pointer))
                _rect.localPosition = new Vector3(pointer.x + _offset.x, pointer.y + _offset.y, 0f);
            _owner.MoveDrag(this, data);
        }

        /// <summary>只为获准的本次手势提交松手事件。输入：指针；输出：由协调器决定部署或回位。</summary>
        public void OnEndDrag(PointerEventData data)
        {
            if (data.button != PointerEventData.InputButton.Left) return;
            data.eligibleForClick = false;
            if (!_dragging || data.pointerId != _pointerId) return;
            _dragging = false;
            _owner.EndDrag(this, data);
        }

        /// <summary>
        /// 无效落点平滑回到占位位置。输入：配置时长；输出：只移动视图，保持选中。
        /// 回位中恢复射线以支持重新抓取；完成与中断均恢复排版，避免卡牌永久留在浮动层。
        /// </summary>
        public void ReturnToHand(float seconds)
        {
            _dragging = false;
            if (_placeholder == null) return;
            _group.blocksRaycasts = _originalBlocksRaycasts;
            Canvas.ForceUpdateCanvases();
            if (seconds <= 0f) { RestoreLayout(); return; }
            _returnTween = _rect.DOMove(_placeholder.position, seconds).SetUpdate(true).SetTarget(_rect)
                .OnComplete(RestoreLayout).OnKill(RestoreLayout);
        }

        /// <summary>取消手势。输入：是否处于激活/禁用回调；输出：清除悬停并恢复原位，生命周期回调的重挂延后到下一帧。</summary>
        public void Cancel(bool deferLayout = false)
        {
            var wasDragging = _dragging;
            _dragging = false;
            _deferRestore = deferLayout;
            if (_returnTween != null) _returnTween.Kill(!deferLayout && gameObject.activeInHierarchy);
            RestoreLayout();
            if (wasDragging && _owner != null) _owner.CancelDrag(this);
        }

        /// <summary>恢复父节点、尺寸和顺序并释放占位。输入：无；输出：幂等清理，适用于完成及停止补间。</summary>
        void RestoreLayout()
        {
            _returnTween = null;
            if (_placeholder == null) return;
            // Unity 禁止在父节点的激活/关闭回调中重挂子物体；重新启用后再恢复，销毁时直接释放。
            if (_destroying || _deferRestore || !gameObject.activeInHierarchy) return;
            var placeholder = _placeholder;
            _placeholder = null;
            if (_rect != null && _originalParent != null)
            {
                _rect.SetParent(_originalParent, false);
                _rect.SetSiblingIndex(placeholder.GetSiblingIndex());
                _rect.anchorMin = _anchorMin; _rect.anchorMax = _anchorMax;
                _rect.pivot = _pivot; _rect.sizeDelta = _size;
                _rect.anchoredPosition = placeholder.anchoredPosition;
                _rect.localScale = _scale;
                _group.blocksRaycasts = _originalBlocksRaycasts;
            }
            placeholder.gameObject.SetActive(false);
            Destroy(placeholder.gameObject);
        }

        /// <summary>禁用时取消手势。输入：生命周期通知；输出：卡牌立即归位，避免失去松手事件后悬空。</summary>
        void OnDisable() => Cancel(true);

        /// <summary>激活/关闭回调结束后恢复排版。输入：下一帧或重新启用后的首帧；输出：安全重挂已取消的卡牌。</summary>
        void LateUpdate()
        {
            if (!_deferRestore) return;
            _deferRestore = false;
            RestoreLayout();
        }

        /// <summary>销毁时不补完、不重新挂载卡牌。输入：无；输出：停止回位并释放仍存活的占位。</summary>
        void OnDestroy()
        {
            _destroying = true;
            if (_returnTween != null) _returnTween.Kill(false);
            if (_placeholder != null) Destroy(_placeholder.gameObject);
        }
    }
}
