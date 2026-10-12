// 卡牌表现反馈：使用 DOTween 实现必要的选中、部署、移动与受击动效。
// 边界：动画只读取事件并操作 RectTransform/Image，绝不回写规则状态；
// 对象被销毁、复用或重开时统一 Kill，避免残留 Tween 操作已回收的对象。
using DG.Tweening;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

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
        /// <summary>炸弹闪光颜色，由既有攻击目标表现配置提供。</summary>
        Color _bombColor = Color.red;
        /// <summary>此组件拥有的临时炸弹闪光，用于停止与销毁时完整回收。</summary>
        readonly HashSet<GameObject> _bombEffects = new HashSet<GameObject>();
        /// <summary>取消代号使旧爆炸协程在 KillAll 后不再创建新效果。</summary>
        int _bombPlaybackVersion;

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
            _bombColor = CardText.Parse(presentation.AttackTargetColor, Color.red);
        }

        /// <summary>
        /// 播放炸弹部署闪光。输入：目标格矩形；输出：不参与结算且不遮挡点击的临时视觉对象。
        /// 时长和颜色由 Configure 的表现配置提供，外层可直接调用以替换或复用表现。
        /// </summary>
        public void PlayBombDeploy(RectTransform cell) => FlashBombCell(cell);

        /// <summary>等待当前炸弹闪光完成或被停止。输入：无；输出：表现协程，不影响任何规则数值。</summary>
        public IEnumerator WaitForBombEffects()
        {
            while (_bombEffects.Count > 0) yield return null;
        }

        /// <summary>等待指定卡牌的补间完成或取消。输入：可为空的卡牌；输出：表现协程，销毁后立即结束等待。</summary>
        public IEnumerator WaitForCard(Card card)
        {
            var rect = Rect(card);
            while (rect != null && DOTween.IsTweening(rect)) yield return null;
        }

        /// <summary>
        /// 逐格播放已结算的十字路径。输入：规则层路径与格子定位函数；输出：可等待的表现协程。
        /// 不推导伤害、不访问单位生命；即使目标已阵亡也能显示格子效果，KillAll 可取消后续步骤。
        /// </summary>
        public IEnumerator PlayBombExplosion(IReadOnlyList<BombBlastStep> steps, Func<Cell, RectTransform> cellRect)
        {
            var version = _bombPlaybackVersion;
            foreach (var step in steps)
            {
                if (version != _bombPlaybackVersion || this == null) yield break;
                FlashBombCell(cellRect(step.Position));
                if (_feedbackSeconds > 0) yield return new WaitForSecondsRealtime(_feedbackSeconds);
            }
        }

        /// <summary>
        /// 创建单格闪光并淡出。输入：格子矩形；输出：自动清理的 DOTween 表现。
        /// 绑定目标并统一 OnComplete/OnKill 清理，避免对象销毁后继续写入 Image。
        /// </summary>
        void FlashBombCell(RectTransform cell)
        {
            if (cell == null) return;
            var effect = new GameObject("BossBombFlash", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)effect.transform;
            rect.SetParent(cell, false);
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var graphic = effect.GetComponent<Image>();
            graphic.raycastTarget = false;
            graphic.color = _bombColor;
            _bombEffects.Add(effect);
            DOTween.To(() => graphic != null ? graphic.color.a : 0f,
                alpha => { if (graphic != null) { var color = graphic.color; color.a = alpha; graphic.color = color; } },
                0f, _feedbackSeconds).SetUpdate(true).SetTarget(rect)
                .OnComplete(() => ReleaseBombEffect(effect)).OnKill(() => ReleaseBombEffect(effect));
        }

        /// <summary>回收单格闪光。输入：效果对象；输出：无，可重复调用以统一完成/停止路径。</summary>
        void ReleaseBombEffect(GameObject effect)
        {
            _bombEffects.Remove(effect);
            if (effect != null) Destroy(effect);
        }

        /// <summary>取消炸弹表现。输入：是否补完当前闪光；输出：清空临时对象并阻止旧协程继续创建。</summary>
        void ClearBombEffects(bool complete)
        {
            _bombPlaybackVersion++;
            foreach (var effect in new List<GameObject>(_bombEffects))
            {
                if (effect != null) { effect.transform.DOKill(complete); ReleaseBombEffect(effect); }
            }
            _bombEffects.Clear();
        }

        /// <summary>销毁时只取消本组件拥有的炸弹补间，不补完，避免访问已销毁的棋盘矩形。</summary>
        void OnDestroy() => ClearBombEffects(false);

        /// <summary>播放部署动效：从 0 放大到 1。输入：目标卡牌；输出：无。</summary>
        public void PlayDeploy(Card card)
        {
            var rt = Rect(card);
            if (rt == null) return;
            Kill(card);
            rt.localScale = Vector3.zero;
            rt.DOScale(Vector3.one, _feedbackSeconds).SetEase(Ease.OutBack).SetUpdate(true);
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
                .SetUpdate(true)
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
            rt.DOPunchScale(Vector3.one * 0.18f, _feedbackSeconds, 6, 0.6f).SetUpdate(true);
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
            ClearBombEffects(true);
            DOTween.KillAll();
        }

        /// <summary>取得卡牌的矩形变换。输入：卡牌；输出：RectTransform，卡牌为空时返回 null。</summary>
        static RectTransform Rect(Card card) => card != null ? card.transform as RectTransform : null;
    }
}
