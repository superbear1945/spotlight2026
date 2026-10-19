// 卡牌外观模板构建器。
// 为什么需要：生成的卡种 Prefab 必须基于一个“手工可维护”的外观模板克隆，
// 否则每次重新编译都会覆盖策划调整过的层级与样式；本工具只在模板缺失时创建一份最小占位外观。
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// 卡牌外观模板（`Assets/Spotlight/Runtime/View/CardTemplate.prefab`）的构建工具。
    /// 输入：无；输出：可被生成器克隆的模板 Prefab，或未保存的临时模板对象。
    /// 模板包含 `Card` 与四个能力组件，并已接好文本/图片引用。
    /// </summary>
    public static class ViewTemplateBuilder
    {
        /// <summary>模板 Prefab 的固定路径。</summary>
        public const string TemplatePath = "Assets/Spotlight/Runtime/View/CardTemplate.prefab";

        /// <summary>
        /// 确保模板存在。
        /// 输入：force 为 true 时强制重建；输出：模板 Prefab 实例对象（失败时为 null）。
        /// 已有模板且未强制重建时直接返回现有对象，保护手工外观。
        /// </summary>
        public static GameObject EnsureTemplate(bool force = false)
        {
            if (!force)
            {
                var existing = AssetDatabase.LoadAssetAtPath<GameObject>(TemplatePath);
                if (existing != null)
                {
                    // 旧工程里的模板是在“卡图”功能之前生成的，没有 Art 子节点；
                    // 这里做一次幂等补齐，否则模板与全部卡种变体上的 _art 引用都是空的，卡图永远不显示。
                    EnsureArtNode();
                    return AssetDatabase.LoadAssetAtPath<GameObject>(TemplatePath);
                }
            }
            var temp = BuildTemplateObject();
            var directory = System.IO.Path.GetDirectoryName(TemplatePath);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory)) System.IO.Directory.CreateDirectory(directory);
            var prefab = PrefabUtility.SaveAsPrefabAsset(temp, TemplatePath);
            Object.DestroyImmediate(temp);
            AssetDatabase.SaveAssets();
            return prefab;
        }

        /// <summary>
        /// 判断现有模板是否已经接好卡图节点。
        /// 输入：无；输出：模板存在且 Card._art 已指向一个 Image 时为 true。
        /// 为什么需要：卡图是“有则显示、无则回退纯色”的软特性，
        /// 一旦模板没接上，表现会和“这张卡没配卡图”完全一样，必须能主动发现而不是靠肉眼。
        /// </summary>
        public static bool HasArtNode()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TemplatePath);
            return prefab != null && HasArtNode(prefab);
        }

        /// <summary>
        /// 判断指定模板对象是否已经接好卡图节点。
        /// 输入：模板 Prefab 资产对象；输出：_art 引用非空时为 true。
        /// </summary>
        public static bool HasArtNode(GameObject prefab)
        {
            if (prefab == null) return false;
            var card = prefab.GetComponent<Card>();
            if (card == null) return false;
            var property = new SerializedObject(card).FindProperty("_art");
            return property != null && property.objectReferenceValue != null;
        }

        /// <summary>
        /// 给现有模板补齐卡图节点（幂等）。
        /// 输入：无；输出：本次是否真的做了修改。
        /// 为什么需要：模板是手工可维护的资产，重建模板会覆盖策划调好的外观；
        /// 而新增的 Art 节点只影响“卡图显示”这一件事，所以用“只补缺失节点”的方式升级旧模板。
        /// 改完后 25 个卡种 Prefab 作为模板变体会自动继承该节点，不需要重新编译配置。
        /// </summary>
        public static bool EnsureArtNode()
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(TemplatePath);
            if (existing == null || HasArtNode(existing)) return false;

            var contents = PrefabUtility.LoadPrefabContents(TemplatePath);
            try
            {
                var card = contents.GetComponent<Card>();
                if (card == null) return false;
                var serialized = new SerializedObject(card);
                if (serialized.FindProperty("_art") == null) return false;
                var rect = (RectTransform)contents.transform;
                var art = CreateImage("Art", rect, Color.white);
                art.preserveAspect = true;
                // 第一个子节点 = 紧贴底色之上、高亮框与文本之下。
                art.transform.SetAsFirstSibling();
                AssignReference(serialized, "_art", art);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(contents, TemplatePath);
                AssetDatabase.SaveAssets();
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// 构建一个未保存的模板对象。
        /// 输入：无；输出：包含 Card 与四个能力组件、以及高亮/名称/属性/生命子节点的 GameObject。
        /// 生成器在模板缺失时也会调用它，保证即使没有手工模板也能产出可运行的占位外观。
        /// 为什么模板带全部能力组件：发布器会在克隆后按配置保留或移除它们（SyncComponent），
        /// 因此模板只需提供最多的一组能力入口，未启用的能力会在每个卡种 Prefab 上被剔除。
        /// </summary>
        public static GameObject BuildTemplateObject()
        {
            var root = new GameObject("CardTemplate", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Card), typeof(CombatComponent), typeof(ResourceComponent), typeof(TeleportComponent), typeof(DeploymentPenaltyComponent));
            var rect = (RectTransform)root.transform;
            rect.sizeDelta = new Vector2(120f, 160f);

            var body = root.GetComponent<Image>();
            body.color = Color.white;

            // 卡图节点先创建：它必须是第一个子节点，渲染顺序才是“底色 → 卡图 → 高亮框 → 文本”。
            // preserveAspect 让不同长宽比的卡图不被棋盘格子拉伸变形。
            var art = CreateImage("Art", rect, Color.white);
            art.preserveAspect = true;

            var highlight = CreateImage("Highlight", rect, new Color(0.2f, 1f, 0.6f, 0.35f));
            highlight.enabled = false;
            var nameLabel = CreateText("Name", rect, "卡牌", 16, TextAlignmentOptions.Top);
            var statLabel = CreateText("Stat", rect, "生命 1", 12, TextAlignmentOptions.Center);
            var hpLabel = CreateText("HP", rect, "1", 18, TextAlignmentOptions.BottomRight);

            var card = root.GetComponent<Card>();
            var serialized = new SerializedObject(card);
            AssignReference(serialized, "_body", body);
            AssignReference(serialized, "_art", art);
            AssignReference(serialized, "_highlight", highlight);
            AssignReference(serialized, "_nameLabel", nameLabel);
            AssignReference(serialized, "_statLabel", statLabel);
            AssignReference(serialized, "_hpLabel", hpLabel);
            AssignReference(serialized, "_combat", root.GetComponent<CombatComponent>());
            AssignReference(serialized, "_resource", root.GetComponent<ResourceComponent>());
            AssignReference(serialized, "_teleport", root.GetComponent<TeleportComponent>());
            AssignReference(serialized, "_deploymentPenalty", root.GetComponent<DeploymentPenaltyComponent>());
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return root;
        }

        /// <summary>
        /// 创建一张占位图片子节点。
        /// 输入：名字、父矩形与颜色；输出：Image 组件（锚点铺满父节点）。
        /// </summary>
        static Image CreateImage(string name, RectTransform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            Stretch(rect);
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>
        /// 创建一段占位文本。
        /// 输入：名字、父矩形、初始文本、字号与对齐；输出：TMP 文本组件（锚点铺满父节点）。
        /// 字体优先使用中文字体资产，缺失时退回 TMP 默认字体。
        /// </summary>
        static TextMeshProUGUI CreateText(string name, RectTransform parent, string text, int size, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            Stretch(rect);
            var label = go.GetComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = size;
            label.alignment = alignment;
            label.raycastTarget = false;
            label.color = Color.black;
            // 字体不是必需项：即使字体资产缺失也要能生成占位模板，否则整个配置发布会因外观问题失败。
            try
            {
                var font = CjkFontSetup.EnsureFontAssets();
                if (font != null) label.font = font;
                else Debug.LogWarning("未取得中文字体资产，占位模板中文可能显示为方框。");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("设置中文字体失败，使用 TMP 默认字体：" + e.Message);
            }
            return label;
        }

        /// <summary>
        /// 把矩形锚点设为铺满父节点。输入：矩形；输出：无。
        /// </summary>
        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// 写入一个对象引用类型的私有序列化字段。
        /// 输入：SerializedObject、字段名与引用；输出：无（字段缺失时静默跳过，避免字段改名导致工具崩溃）。
        /// </summary>
        public static void AssignReference(SerializedObject serialized, string fieldName, Object value)
        {
            var property = serialized.FindProperty(fieldName);
            if (property == null) return;
            property.objectReferenceValue = value;
        }
    }
}
