// 卡牌区域视图（UGUI）：显示玩家抽牌堆与墓地数量，并在弃牌阶段提供明确的视觉提示。
// 规则层仍是牌区状态的唯一来源；本组件只读取 GameSnapshot，不移动卡牌记录，也不推导弃牌合法性。
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Spotlight
{
    /// <summary>
    /// 玩家抽牌堆与墓地的只读显示组件。
    /// 输入：<see cref="Render"/> 接收当前对局快照。
    /// 输出：更新两个牌区的数量文本，并在规则层进入弃牌阶段时高亮墓地提示。
    /// 为什么需要独立视图：手牌由 <see cref="HandView"/> 增量创建卡牌实例，而抽牌堆与墓地只需显示聚合状态；
    /// 将两者分开可以避免为了一个计数器创建不可见卡牌，也能保证界面不会参与规则结算。
    /// </summary>
    public sealed class CardZonesView : MonoBehaviour
    {
        /// <summary>抽牌堆面板背景，用于保持牌区视觉样式。</summary>
        [SerializeField] Image _drawPilePanel;
        /// <summary>抽牌堆剩余数量文本。</summary>
        [SerializeField] TMP_Text _drawPileCountText;
        /// <summary>墓地面板背景；弃牌阶段会切换为提示色。</summary>
        [SerializeField] Image _graveyardPanel;
        /// <summary>墓地中的卡牌记录数量文本。</summary>
        [SerializeField] TMP_Text _graveyardCountText;
        /// <summary>墓地状态提示；弃牌阶段说明实际操作仍是点击手牌。</summary>
        [SerializeField] TMP_Text _graveyardHintText;
        /// <summary>墓地在普通阶段使用的底色。</summary>
        [SerializeField] Color _graveyardNormalColor = new Color(0.10f, 0.18f, 0.22f, 0.94f);
        /// <summary>墓地在规则层弃牌阶段使用的高亮色。</summary>
        [SerializeField] Color _graveyardDiscardColor = new Color(0.55f, 0.22f, 0.12f, 0.98f);

        /// <summary>最近一次渲染的抽牌堆数量，供运行诊断与视图测试读取。</summary>
        public int DrawPileCount { get; private set; }
        /// <summary>最近一次渲染的墓地数量，供运行诊断与视图测试读取。</summary>
        public int GraveyardCount { get; private set; }
        /// <summary>最近一次渲染是否处于弃牌阶段。</summary>
        public bool IsDiscardPhase { get; private set; }

        /// <summary>
        /// 根据规则快照刷新两个牌区。
        /// 输入：独立只读的 <paramref name="snapshot"/>；输出：更新数量、提示文字和墓地底色。
        /// 为什么不接收 Selection 或 LegalActions：牌区数量与当前选择无关，而弃牌阶段由快照明确给出，
        /// 视图无需也不得自行推导规则合法性。
        /// </summary>
        public void Render(GameSnapshot snapshot)
        {
            if (snapshot == null) return;

            DrawPileCount = snapshot.DrawPile != null ? snapshot.DrawPile.Count : 0;
            GraveyardCount = snapshot.Graveyard != null ? snapshot.Graveyard.Count : 0;
            IsDiscardPhase = snapshot.Phase == MatchPhase.Discard;

            if (_drawPileCountText != null) _drawPileCountText.text = DrawPileCount.ToString();
            if (_graveyardCountText != null) _graveyardCountText.text = GraveyardCount.ToString();
            if (_graveyardHintText != null)
                _graveyardHintText.text = IsDiscardPhase ? "点击手牌弃置" : "已使用卡牌";
            if (_graveyardPanel != null)
                _graveyardPanel.color = IsDiscardPhase ? _graveyardDiscardColor : _graveyardNormalColor;

            // 两个面板都只是状态显示；任何子图形都不能盖住手牌或棋盘的射线事件。
            SetRaycastTargets(_drawPilePanel, false);
            SetRaycastTargets(_graveyardPanel, false);
        }

        /// <summary>
        /// 统一设置一个牌区层级内的射线状态。
        /// 输入：牌区根 Image 与目标状态；输出：该层级所有 Graphic 使用相同的 raycastTarget。
        /// 为什么每次渲染都复验：美术替换或场景重建可能给子文本重新打开射线，集中关闭可以持续保护手牌点击。
        /// </summary>
        static void SetRaycastTargets(Image root, bool enabled)
        {
            if (root == null) return;
            foreach (var graphic in root.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = enabled;
        }
    }
}
