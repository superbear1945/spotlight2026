using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Spotlight.Tests
{
    /// <summary>测试用真实 UGUI 手势驱动器。输入：卡牌和屏幕坐标；输出：标准指针事件，避免绕过拖动入口。</summary>
    public static class HandDragInput
    {
        /// <summary>把矩形中心换算到屏幕。输入：矩形；输出：使用所属 Canvas 相机计算的屏幕坐标。</summary>
        public static Vector2 Center(RectTransform rect)
        {
            var canvas = rect.GetComponentInParent<Canvas>();
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            return RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
        }

        /// <summary>从真实射线命中的手牌发起左键手势。输入：卡牌；输出：后续拖动共用的指针数据。</summary>
        public static PointerEventData Begin(Card card)
        {
            Canvas.ForceUpdateCanvases();
            var point = Center((RectTransform)card.transform);
            var data = new PointerEventData(EventSystem.current)
            {
                position = point, pressPosition = point, button = PointerEventData.InputButton.Left,
                pointerId = -1, eligibleForClick = true
            };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(data, hits);
            Assert.That(hits.Count, Is.GreaterThan(0), "手牌位置未命中任何 UI");
            Assert.That(hits[0].gameObject.GetComponentInParent<Card>(), Is.EqualTo(card), "手牌被其它 UI 挡住");
            data.pointerPressRaycast = hits[0];
            data.pointerCurrentRaycast = hits[0];
            data.pointerDrag = card.gameObject;
            ExecuteEvents.Execute(card.gameObject, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(card.gameObject, data, ExecuteEvents.initializePotentialDrag);
            data.dragging = true;
            ExecuteEvents.Execute(card.gameObject, data, ExecuteEvents.beginDragHandler);
            return data;
        }

        /// <summary>更新指针并触发拖动。输入：卡牌、指针、目标屏幕点；输出：卡面和合法落点变化。</summary>
        public static void Move(Card card, PointerEventData data, Vector2 point)
        {
            data.delta = point - data.position;
            data.position = point;
            ExecuteEvents.Execute(card.gameObject, data, ExecuteEvents.dragHandler);
        }

        /// <summary>按 UGUI 顺序松手。输入：卡牌和指针；输出：至多一次部署或回位，不额外伪造点击。</summary>
        public static void End(Card card, PointerEventData data)
        {
            ExecuteEvents.Execute(card.gameObject, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(card.gameObject, data, ExecuteEvents.endDragHandler);
            data.dragging = false;
        }

        /// <summary>完成一次拖拽部署。输入：手牌、棋盘及内部坐标；输出：真实界面事件产生的规则结果。</summary>
        public static void Deploy(Card card, BoardView board, Cell cell)
        {
            var data = Begin(card);
            Move(card, data, Center(board.CellRect(cell)));
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(data, hits);
            Assert.That(board.HoveredDeploymentCell, Is.EqualTo(cell),
                $"落点 {cell} / 屏幕 {data.position} 未获准，首个命中：{(hits.Count > 0 ? hits[0].gameObject.name : "无")}");
            End(card, data);
        }
    }
}
