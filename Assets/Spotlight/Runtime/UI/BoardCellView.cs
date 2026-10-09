// 单个棋盘格的视图。
// 输入：格坐标与点击回调；输出：底色、高亮与可点击状态。
using System;
using UnityEngine;
using UnityEngine.UI;

namespace Spotlight
{
    /// <summary>
    /// 棋盘格显示组件（UGUI）。
    /// 为什么把格与单位分开：格子只负责命中与高亮，单位卡牌负责数值显示，
    /// 二者生命周期不同（单位会被销毁与重建，格子只构建一次）。
    /// </summary>
    public sealed class BoardCellView : MonoBehaviour
    {
        /// <summary>格底色 Image。</summary>
        [SerializeField] Image _background;
        /// <summary>高亮层 Image（可部署/可移动/可攻击/可互换）。</summary>
        [SerializeField] Image _highlight;
        /// <summary>点击按钮；缺省时回退到本节点上的 Image 射线检测。</summary>
        [SerializeField] Button _button;

        /// <summary>本格对应的棋盘坐标。</summary>
        public Cell Coordinate { get; private set; }
        /// <summary>本格的矩形变换，用于单位卡牌定位与动画起点计算。</summary>
        public RectTransform Rect => (RectTransform)transform;

        /// <summary>
        /// 初始化格子。
        /// 输入：坐标与点击回调；输出：无。
        /// 回调在点击时以坐标形式上报，棋盘视图据此组装命令，格子不需要理解规则。
        /// </summary>
        public void Initialize(Cell cell, Action<Cell> onClicked)
        {
            Coordinate = cell;
            if (_background == null) _background = GetComponent<Image>();
            if (_button == null) _button = GetComponent<Button>();
            if (_button != null)
            {
                _button.onClick.RemoveAllListeners();
                _button.onClick.AddListener(() => onClicked?.Invoke(Coordinate));
            }
        }

        /// <summary>
        /// 设置高亮颜色。
        /// 输入：颜色，null 表示取消高亮；输出：无。
        /// </summary>
        public void SetHighlight(Color? color)
        {
            if (_highlight == null) return;
            _highlight.enabled = color.HasValue;
            if (color.HasValue) _highlight.color = color.Value;
        }

        /// <summary>设置本格是否响应点击。输入：可交互标志；输出：无。</summary>
        public void SetInteractable(bool value)
        {
            if (_button != null) _button.interactable = value;
        }
    }
}
