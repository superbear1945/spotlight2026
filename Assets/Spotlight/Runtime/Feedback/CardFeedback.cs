// 卡牌表现反馈：使用 DOTween 实现必要的选中、部署、移动与受击动效。
// 边界：动画只读取事件并操作 RectTransform/Image，绝不回写规则状态；
// 对象被销毁、复用或重开时统一 Kill，避免残留 Tween 操作已回收的对象。
using DG.Tweening;
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 卡牌动效播放器（挂在场景根节点上）。
    /// 输入：表现配置与具体卡牌视图；输出：屏幕上的缩放/位移/颜色反馈。
    /// 为什么集中在一个组件：清理逻辑（重开、对象复用）必须统一，
    /// 分散在 Card 与各视图中容易出现“旧动画在新对象上继续播放”的问题。
    /// </summary>
    public sealed class CardFeedback : MonoBehaviour
    {
        /// <summary>移动动画时长（秒）。</summary>
        [SerializeField] float _moveSeconds = 0.2f;
        /// <summary>反馈动画时长（秒）。</summary>
        [SerializeField] float _feedbackSeconds = 0.12f;
        /// <summary>选中放大倍率。</summary>
        [SerializeField] float _selectedScale = 1.05f;

        /// <summary>
        /// 应用表现配置。
        /// 输入：来自 Excel 编译产物的表现参数；输出：无。
        /// </summary>
        public void Configure(PresentationConfig presentation)
        {
            if (presentation == null) return;
            _moveSeconds = presentation.MoveSeconds;
            _feedbackSeconds = presentation.FeedbackSeconds;
            _selectedScale = presentation.SelectedScale;
        }

        /// <summary>播放部署动效：从 0 放大到 1。输入：目标卡牌；输出：无。</summary>
        public void PlayDeploy(Card card)
        {
            var rt = Rect(card);
            if (rt == null) return;
            Kill(card);
            rt.localScale = Vector3.zero;
            rt.DOScale(Vector3.one, _feedbackSeconds).SetEase(Ease.OutBack);
        }

        /// <summary>
        /// 播放移动动效：先摆到起点偏移再回到原位。
        /// 输入：目标卡牌与 BoardView 计算出的起点相对偏移；输出：无。
        /// 关键约束：动画只是“视觉过渡”，结束时（含被中途停止）必须把卡牌放回格心，
        /// 否则卡牌会停在旧格，看起来像“规则移动了但棋子没有移动”。
        /// </summary>
        public void PlayMove(Card card, Vector2 offset)
        {
            var rt = Rect(card);
            if (rt == null) return;
            Kill(card);
            if (offset == Vector2.zero)
            {
                rt.anchoredPosition = Vector2.zero;
                return;
            }
            card.SetMoveAnimating(true);
            rt.anchoredPosition = offset;
            // 使用 DOTween 核心 API 而不是 DOTweenModuleUI 的扩展方法：
            // 模块文件编译在 Assembly-CSharp 中，asmdef 程序集看不到它，而核心 DLL 可以被自动引用。
            // SetTarget 让该补间能被 DOKill/DOKillAll 正确回收，OnComplete/OnKill 保证位移一定归零。
            DOTween.To(() => rt.anchoredPosition, value => rt.anchoredPosition = value, Vector2.zero, _moveSeconds)
                .SetEase(Ease.OutQuad)
                .SetTarget(rt)
                .OnComplete(() => FinishMove(card, rt))
                .OnKill(() => FinishMove(card, rt));
        }

        /// <summary>
        /// 移动动画收尾。
        /// 输入：卡牌与它的矩形变换；输出：无。
        /// 行为：把位移归零并解除“动画中”标记；完成与被停止都会调用，可重复调用。
        /// </summary>
        static void FinishMove(Card card, RectTransform rect)
        {
            if (rect != null) rect.anchoredPosition = Vector2.zero;
            if (card != null) card.SetMoveAnimating(false);
        }

        /// <summary>播放受击动效：短促缩放脉冲。输入：目标卡牌；输出：无。</summary>
        public void PlayHit(Card card)
        {
            var rt = Rect(card);
            if (rt == null) return;
            Kill(card);
            rt.localScale = Vector3.one;
            rt.DOPunchScale(Vector3.one * 0.18f, _feedbackSeconds, 6, 0.6f);
        }

        /// <summary>
        /// 播放选中状态变化：选中放大、取消恢复。
        /// 输入：目标卡牌与是否选中；输出：无。
        /// </summary>
        public void PlaySelect(Card card, bool selected)
        {
            var rt = Rect(card);
            if (rt == null) return;
            Kill(card);
            rt.DOScale(selected ? Vector3.one * _selectedScale : Vector3.one, _feedbackSeconds);
        }

        /// <summary>
        /// 停止某张卡牌上的全部 Tween。
        /// 输入：目标卡牌；输出：无。用途：对象复用、销毁与重开前清理。
        /// complete=true：被停止的补间会先写回它的结束值，避免弹出式动画被停在半途
        /// （否则移动中的卡牌会永久停在旧格，或部署动画停在缩放到 0 的不可见状态）。
        /// </summary>
        public void Kill(Card card)
        {
            var rt = Rect(card);
            if (rt != null) rt.DOKill(true);
            if (card != null) card.SetMoveAnimating(false);
        }

        /// <summary>
        /// 停止全部 Tween 并复位当前场景中的动画状态。
        /// 输入：无；输出：无。用途：重开对局时调用。
        /// 说明：这里不逐个取消“动画中”标记，而是在重开流程里重建全部卡牌，旧标记随对象一起消失。
        /// </summary>
        public void KillAll()
        {
            DOTween.KillAll();
        }

        /// <summary>取得卡牌的矩形变换。输入：卡牌；输出：RectTransform，卡牌为空时返回 null。</summary>
        static RectTransform Rect(Card card) => card != null ? card.transform as RectTransform : null;
    }
}
