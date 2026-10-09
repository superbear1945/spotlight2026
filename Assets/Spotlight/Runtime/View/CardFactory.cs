// 视图侧对象创建工具。
// 为什么需要：棋盘单位与手牌都需要“按卡种取 Prefab、缺失时回退到通用模板”，
// 并统一完成 Configure 装配；集中一处可避免两个视图各自处理空引用与装配顺序。
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 卡牌视图工厂（纯静态工具）。
    /// 输入：配置资产、回退 Prefab、目标父节点与卡种 ID；输出：已装配好 CardDefinitionSO 的 Card 实例。
    /// </summary>
    public static class CardFactory
    {
        /// <summary>
        /// 选择卡种对应的 Prefab。
        /// 输入：配置资产、回退 Prefab 与卡种 ID；输出：优先返回该卡种的生成 Prefab，缺失时返回回退模板。
        /// </summary>
        public static Card ResolvePrefab(SpotlightConfigAsset asset, Card fallback, string typeId)
        {
            var definition = asset != null ? asset.Find(typeId) : null;
            if (definition != null && definition.Prefab != null) return definition.Prefab;
            return fallback;
        }

        /// <summary>
        /// 实例化并装配一张卡牌视图。
        /// 输入：配置资产、回退 Prefab、父节点与卡种 ID；输出：已注入定义与组件配置的 Card。
        /// 若既没有生成 Prefab 也没有回退模板则返回 null，由调用方跳过本次显示而不是抛异常。
        /// </summary>
        public static Card Create(SpotlightConfigAsset asset, Card fallback, Transform parent, string typeId)
        {
            var prefab = ResolvePrefab(asset, fallback, typeId);
            if (prefab == null) return null;
            var card = Object.Instantiate(prefab, parent);
            var definition = asset != null ? asset.Find(typeId) : null;
            card.Configure(definition);
            return card;
        }
    }
}
