// 配置发布器：把已校验的文档写成只读 SO 与卡种 Prefab。
// 发布策略（重要）：
// 1) 卡种资产按稳定 ID 原地更新，保留 GUID，因此改名不会破坏升级关联、卡组引用与场景引用。
// 2) 组件 SO 作为卡种资产的子资产集中保存，确保同一卡种数值只有一份来源。
// 3) 全部结构/业务校验已在 ConfigCompiler 中完成；本类只在写入阶段做防御性检查。
// 4) 回滚语义：写入过程中出现异常时，把主配置资产恢复为上一套文档并清空本次生成的卡种清单，
//    但已经落盘的卡种资产不做颗粒度回滚（它们是幂等的，重新成功编译一次即可完全修复）。
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// 发布结果。
    /// 输入：Publish 的返回；输出：问题列表与本次产出的资产路径。
    /// </summary>
    public sealed class PublishResult
    {
        /// <summary>发布阶段发现的问题。</summary>
        public List<ConfigIssue> Issues { get; } = new List<ConfigIssue>();
        /// <summary>本次写入的资产路径（卡种资产、Prefab、主配置与清单）。</summary>
        public List<string> Paths { get; } = new List<string>();
    }

    /// <summary>
    /// 生成资产发布器。
    /// 输入：已通过全部校验的配置文档、源工作簿路径与源摘要；
    /// 输出：生成的 SO/Prefab 资产与发布问题列表。
    /// </summary>
    public static class ConfigAssetPublisher
    {
        /// <summary>生成产物根目录。</summary>
        public const string GeneratedRoot = "Assets/Spotlight/Generated";
        /// <summary>卡种资产与 Prefab 目录。</summary>
        public const string CardFolder = GeneratedRoot + "/Cards";
        /// <summary>主配置所在目录（必须名为 Resources，运行时可 Resources.Load）。</summary>
        public const string ResourcesFolder = GeneratedRoot + "/Resources";
        /// <summary>主配置资产路径。</summary>
        public const string ConfigAssetPath = ResourcesFolder + "/SpotlightConfig.asset";
        /// <summary>编译清单资产路径。</summary>
        public const string ManifestPath = GeneratedRoot + "/ConfigManifest.asset";

        /// <summary>
        /// 发布一套配置。
        /// 输入：document、源工作簿相对路径与源摘要；输出：发布结果。
        /// </summary>
        public static PublishResult Publish(ConfigDocument document, string sourceWorkbook, string sourceHash)
        {
            var result = new PublishResult();
            if (document == null)
            {
                result.Issues.Add(new ConfigIssue(sourceWorkbook, "（发布）", 0, 0, "文档为空，拒绝发布"));
                return result;
            }

            EnsureFolders();
            var template = ViewTemplateBuilder.EnsureTemplate();

            var config = LoadOrCreate<SpotlightConfigAsset>(ConfigAssetPath);
            var previousDocument = config.ToDocument();
            var previousCards = new List<CardDefinitionSO>(config.Cards);

            try
            {
                var definitions = new List<CardDefinitionSO>();
                foreach (var row in document.cards)
                {
                    var definition = PublishCard(row, template, sourceWorkbook, result);
                    if (definition == null) continue;
                    definitions.Add(definition);
                    result.Paths.Add(CardAssetPath(row.id));
                    result.Paths.Add(CardPrefabPath(row.id));
                }

                var compiledAt = DateTime.UtcNow.ToString("o");
                config.EditorApply(document, definitions, sourceWorkbook, sourceHash, compiledAt);
                EditorUtility.SetDirty(config);
                result.Paths.Add(ConfigAssetPath);

                var manifest = LoadOrCreate<ConfigManifest>(ManifestPath);
                manifest.EditorApply(sourceWorkbook, sourceHash, compiledAt, result.Paths);
                EditorUtility.SetDirty(manifest);
                result.Paths.Add(ManifestPath);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                result.Issues.AddRange(VerifyPrefabs(definitions, sourceWorkbook));
            }
            catch (Exception e)
            {
                // 回滚：主配置恢复为上一套文档，保证“失败保留上一套有效产物”。
                // 仅当上一套文档本身完整时才回写，否则（首次编译即失败）保持空资产而不是写入半截数据。
                if (previousDocument != null && previousDocument.rules != null && previousDocument.rules.Length == 1
                    && previousDocument.cards != null && previousDocument.deck != null
                    && previousDocument.homes != null && previousDocument.bossSlots != null && previousDocument.presentation != null)
                {
                    config.EditorApply(previousDocument, previousCards, sourceWorkbook, config.SourceHash ?? string.Empty, config.CompiledAtUtc ?? string.Empty);
                    EditorUtility.SetDirty(config);
                    AssetDatabase.SaveAssets();
                }
                result.Issues.Add(new ConfigIssue(sourceWorkbook, "（发布）", 0, 0, "发布失败并已回滚主配置：" + e.Message));
            }
            return result;
        }

        /// <summary>卡种资产路径。输入：卡种 ID；输出：Assets 相对路径。</summary>
        public static string CardAssetPath(string id) => $"{CardFolder}/{id}.asset";
        /// <summary>卡种 Prefab 路径。输入：卡种 ID；输出：Assets 相对路径。</summary>
        public static string CardPrefabPath(string id) => $"{CardFolder}/{id}.prefab";

        /// <summary>
        /// 发布单个卡种。
        /// 输入：已校验行数据、外观模板与源工作簿名；输出：卡种定义资产（失败时为 null 并写入问题）。
        /// </summary>
        static CardDefinitionSO PublishCard(CardRow row, GameObject template, string sourceWorkbook, PublishResult result)
        {
            var assetPath = CardAssetPath(row.id);
            var definition = AssetDatabase.LoadAssetAtPath<CardDefinitionSO>(assetPath);
            if (definition == null)
            {
                definition = ScriptableObject.CreateInstance<CardDefinitionSO>();
                AssetDatabase.CreateAsset(definition, assetPath);
            }

            var combat = EnsureSubAsset<CombatConfigSO>(assetPath, definition, row.id + "_Combat");
            combat.EditorApply(row.hp, row.atk, row.range, row.moveDistance);
            EditorUtility.SetDirty(combat);

            var resource = EnsureSubAsset<ResourceConfigSO>(assetPath, definition, row.id + "_Resource");
            if (row.resourceComponent)
            {
                resource.EditorApply(row.produce, row.upkeep);
                EditorUtility.SetDirty(resource);
            }
            else RemoveSubAsset(assetPath, resource);

            var teleport = EnsureSubAsset<TeleportConfigSO>(assetPath, definition, row.id + "_Teleport");
            if (row.teleportComponent)
            {
                teleport.EditorApply(row.permitsAttackDeploy);
                EditorUtility.SetDirty(teleport);
            }
            else RemoveSubAsset(assetPath, teleport);

            var prefabPath = CardPrefabPath(row.id);
            var instance = template != null ? (GameObject)PrefabUtility.InstantiatePrefab(template) : ViewTemplateBuilder.BuildTemplateObject();
            instance.name = row.id;
            var card = instance.GetComponent<Card>() ?? instance.AddComponent<Card>();
            var combatComponent = instance.GetComponent<CombatComponent>() ?? instance.AddComponent<CombatComponent>();
            var resourceComponent = SyncComponent<ResourceComponent>(instance, row.resourceComponent);
            var teleportComponent = SyncComponent<TeleportComponent>(instance, row.teleportComponent);

            var cardSerialized = new SerializedObject(card);
            ViewTemplateBuilder.AssignReference(cardSerialized, "_definition", definition);
            ViewTemplateBuilder.AssignReference(cardSerialized, "_combat", combatComponent);
            ViewTemplateBuilder.AssignReference(cardSerialized, "_resource", row.resourceComponent ? resourceComponent : null);
            ViewTemplateBuilder.AssignReference(cardSerialized, "_teleport", row.teleportComponent ? teleportComponent : null);
            cardSerialized.ApplyModifiedPropertiesWithoutUndo();

            AssignComponentConfig(combatComponent, combat);
            if (row.resourceComponent) AssignComponentConfig(resourceComponent, resource);
            if (row.teleportComponent) AssignComponentConfig(teleportComponent, teleport);

            PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            UnityEngine.Object.DestroyImmediate(instance);

            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var prefabCard = prefabAsset != null ? prefabAsset.GetComponent<Card>() : null;
            if (prefabCard == null)
            {
                result.Issues.Add(new ConfigIssue(sourceWorkbook, "Cards", 0, 0, $"卡种 {row.id} 的 Prefab 缺少 Card 组件"));
                return null;
            }

            definition.EditorApply(row, combat, row.resourceComponent ? resource : null, row.teleportComponent ? teleport : null, prefabCard, sourceWorkbook, "Cards");
            EditorUtility.SetDirty(definition);
            return definition;
        }

        /// <summary>
        /// 取得或创建同名子资产。
        /// 输入：宿主资产路径、宿主对象与子资产名；输出：子资产（已存在时直接复用，保证引用不丢失）。
        /// </summary>
        static T EnsureSubAsset<T>(string assetPath, UnityEngine.Object host, string name) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<T>().FirstOrDefault();
            if (existing != null) return existing;
            var created = ScriptableObject.CreateInstance<T>();
            created.name = name;
            created.hideFlags = HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(created, host);
            return created;
        }

        /// <summary>
        /// 删除不再需要的子资产。
        /// 输入：宿主资产路径与子资产；输出：无。
        /// 用途：某卡种关闭资源或传送组件后，残留的子资产必须移除，否则导入器会认为配置仍在启用。
        /// </summary>
        static void RemoveSubAsset(string assetPath, ScriptableObject sub)
        {
            if (sub == null) return;
            if (AssetDatabase.LoadAllAssetsAtPath(assetPath).Contains(sub))
            {
                AssetDatabase.RemoveObjectFromAsset(sub);
                UnityEngine.Object.DestroyImmediate(sub, true);
            }
        }

        /// <summary>
        /// 按启用状态同步 Prefab 上的能力组件。
        /// 输入：Prefab 实例与是否启用；输出：启用时的组件引用（未启用时为 null）。
        /// </summary>
        static T SyncComponent<T>(GameObject instance, bool enabled) where T : Component
        {
            var existing = instance.GetComponent<T>();
            if (enabled) return existing != null ? existing : instance.AddComponent<T>();
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing, true);
            return null;
        }

        /// <summary>
        /// 把配置 SO 写入能力组件的私有字段。
        /// 输入：组件与配置；输出：无（组件或配置为空时静默跳过）。
        /// </summary>
        static void AssignComponentConfig(Component component, ScriptableObject config)
        {
            if (component == null || config == null) return;
            var serialized = new SerializedObject(component);
            ViewTemplateBuilder.AssignReference(serialized, "_config", config);
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// 校验生成 Prefab 的组件组合与卡种启用信息一致。
        /// 输入：卡种定义列表与源工作簿名；输出：问题列表。
        /// 为什么需要：Steam/团队协作下 Prefab 可能被手工改动，运行前必须发现“配置说没有传送组件，Prefab 上却有”。
        /// </summary>
        public static List<ConfigIssue> VerifyPrefabs(IEnumerable<CardDefinitionSO> definitions, string sourceWorkbook)
        {
            var issues = new List<ConfigIssue>();
            foreach (var definition in definitions)
            {
                if (definition == null) continue;
                var prefab = definition.Prefab;
                if (prefab == null)
                {
                    issues.Add(new ConfigIssue(sourceWorkbook, "Cards", 0, 0, $"卡种 {definition.Id} 缺少 Prefab 引用"));
                    continue;
                }
                if (prefab.Combat == null) issues.Add(new ConfigIssue(sourceWorkbook, "Cards", 0, 0, $"卡种 {definition.Id} 的 Prefab 缺少战斗组件"));
                if ((prefab.Resource != null) != (definition.Resource != null)) issues.Add(new ConfigIssue(sourceWorkbook, "Cards", 0, 0, $"卡种 {definition.Id} 的资源组件与配置不一致"));
                if ((prefab.Teleport != null) != (definition.Teleport != null)) issues.Add(new ConfigIssue(sourceWorkbook, "Cards", 0, 0, $"卡种 {definition.Id} 的传送组件与配置不一致"));
            }
            return issues;
        }

        /// <summary>
        /// 取得或创建资产。
        /// 输入：资产路径；输出：资产实例。用于主配置与清单这两个单例资产。
        /// </summary>
        static T LoadOrCreate<T>(string assetPath) where T : ScriptableObject
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (asset != null) return asset;
            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, assetPath);
            return asset;
        }

        /// <summary>
        /// 确保生成目录存在。
        /// 输入：无；输出：无。
        /// 注意 Resources 目录名不能改，否则 ConfigLoader 找不到主配置。
        /// </summary>
        static void EnsureFolders()
        {
            EnsureFolder(GeneratedRoot);
            EnsureFolder(CardFolder);
            EnsureFolder(ResourcesFolder);
        }

        /// <summary>确保某个 Assets 相对目录存在。输入：目录路径；输出：无。</summary>
        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            var leaf = System.IO.Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
