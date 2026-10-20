// 配置发布器：把已校验的文档写成只读 SO 与卡种 Prefab。
// 发布策略（重要）：
// 1) 卡种资产按稳定 ID 原地更新，保留 GUID，因此改名不会破坏升级关联、卡组引用与场景引用。
// 2) 组件 SO 作为卡种资产的子资产集中保存，确保同一卡种数值只有一份来源。
// 3) 全部结构/业务校验已在 ConfigCompiler 中完成；本类只在写入阶段做防御性检查。
// 4) 回滚语义：写入或产物验证失败时恢复 Generated 下本次发布前的文件与 meta，
//    同时移除本次新建文件，避免主配置恢复了但卡种子资产仍含半套新数值。
using System;
using System.Collections.Generic;
using System.IO;
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

            var previousFiles = CapturePublishedFiles();

            try
            {
                EnsureFolders();
                var template = ViewTemplateBuilder.EnsureTemplate();
                // 模板缺卡图节点时，所有卡的卡图都会静默不显示（看起来和“没配卡图”一模一样）。
                // 这里直接失败，让问题在编译阶段暴露，而不是等到运行时才发现界面不对。
                if (!ViewTemplateBuilder.HasArtNode(template))
                    throw new InvalidOperationException("外观模板缺少卡图节点 _art，请执行 Tools/Spotlight/升级卡牌外观模板 后重试");
                var config = LoadOrCreate<SpotlightConfigAsset>(ConfigAssetPath);
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
                config.EditorApply(document, definitions);
                EditorUtility.SetDirty(config);
                result.Paths.Add(ConfigAssetPath);

                var manifest = LoadOrCreate<ConfigManifest>(ManifestPath);
                manifest.EditorApply(sourceWorkbook, sourceHash, compiledAt, result.Paths);
                EditorUtility.SetDirty(manifest);
                result.Paths.Add(ManifestPath);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                result.Issues.AddRange(VerifyPrefabs(definitions, sourceWorkbook));
                if (result.Issues.Count > 0) throw new InvalidOperationException("生成产物引用验证失败");
            }
            catch (Exception e)
            {
                try
                {
                    RestorePublishedFiles(previousFiles);
                    result.Paths.Clear();
                    result.Issues.Add(new ConfigIssue(sourceWorkbook, "（发布）", 0, 0, "发布失败并已回滚全部生成产物：" + e.Message));
                }
                catch (Exception rollbackError)
                {
                    result.Issues.Add(new ConfigIssue(sourceWorkbook, "（发布）", 0, 0,
                        "发布失败：" + e.Message + "；回滚失败，请重新编译配置：" + rollbackError.Message));
                }
            }
            return result;
        }

        /// <summary>
        /// 发布前读取 Generated 的原始文件和 meta，输出路径到内容的独立副本。
        /// 不改动源 Excel 或模板；同步发布期间不会有其他发布者插入写入，故可准确区分本次新增文件。
        /// </summary>
        static Dictionary<string, byte[]> CapturePublishedFiles()
        {
            return Directory.Exists(GeneratedRoot)
                ? Directory.GetFiles(GeneratedRoot, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes)
                : new Dictionary<string, byte[]>();
        }

        /// <summary>
        /// 输入发布前文件副本，恢复本次失败前的资产、子资产、Prefab、清单及 GUID。
        /// 先保存脏对象，再批量恢复磁盘并强制导入，避免稍后 SaveAssets 把失败的内存数值重新写回。
        /// 删除范围严格限于 GeneratedRoot 内本次新增的文件，不递归删除目录或其他项目文件。
        /// </summary>
        static void RestorePublishedFiles(Dictionary<string, byte[]> previous)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.StartAssetEditing();
            try
            {
                if (Directory.Exists(GeneratedRoot))
                    foreach (var path in Directory.GetFiles(GeneratedRoot, "*", SearchOption.AllDirectories))
                        if (!previous.ContainsKey(path)) File.Delete(path);
                foreach (var file in previous) File.WriteAllBytes(file.Key, file.Value);
            }
            finally { AssetDatabase.StopAssetEditing(); }
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
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
            combat.EditorApply(row.hp, row.atk, row.range, row.moveDistance, row.canAttackResources, row.canSwap);
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

            // 新能力同样只在启用时保留子资产：禁用后必须移除，否则导入器会认为配置仍在启用。
            var penalty = EnsureSubAsset<DeploymentPenaltyConfigSO>(assetPath, definition, row.id + "_DeploymentPenalty");
            if (row.deploymentPenaltyComponent)
            {
                penalty.EditorApply(row.deploymentAttackReduction);
                EditorUtility.SetDirty(penalty);
            }
            else RemoveSubAsset(assetPath, penalty);

            // 入场标记是纯规则能力：只生成子资产，不产生 MonoBehaviour 组件，
            // 视图通过 UnitState.MarkedBy 与卡种定义读取，因此 Prefab 组合不受影响。
            var mark = EnsureSubAsset<MarkOnEnterConfigSO>(assetPath, definition, row.id + "_MarkOnEnter");
            if (row.markOnEnterComponent)
            {
                mark.EditorApply(row.markOnEnterRadius, row.markRewardResource);
                EditorUtility.SetDirty(mark);
            }
            else RemoveSubAsset(assetPath, mark);

            // 死亡加攻同样只生成子资产（纯规则能力，无 MonoBehaviour 组件）。
            var growth = EnsureSubAsset<DeathGrowthConfigSO>(assetPath, definition, row.id + "_DeathGrowth");
            if (row.deathGrowthComponent)
            {
                growth.EditorApply(row.deathGrowthPerKill, row.deathGrowthLimit);
                EditorUtility.SetDirty(growth);
            }
            else RemoveSubAsset(assetPath, growth);

            // 即时卡效果同样只生成子资产（纯规则能力，无 MonoBehaviour 组件）。
            var instant = EnsureSubAsset<InstantEffectConfigSO>(assetPath, definition, row.id + "_Instant");
            if (row.leaveAfterDeploy)
            {
                instant.EditorApply(row.deployGainResource);
                EditorUtility.SetDirty(instant);
            }
            else RemoveSubAsset(assetPath, instant);
            var spawn = EnsureSubAsset<SpawnConfigSO>(assetPath, definition, row.id + "_Spawn");
            if (row.spawnComponent)
            {
                spawn.EditorApply(row.spawnCardType);
                EditorUtility.SetDirty(spawn);
            }
            else RemoveSubAsset(assetPath, spawn);

            // 四邻空格限制：纯规则能力，只生成子资产（无 MonoBehaviour 组件）。
            var clearance = EnsureSubAsset<AdjacentClearanceConfigSO>(assetPath, definition, row.id + "_AdjacentClearance");
            if (row.adjacentClearanceComponent)
            {
                clearance.EditorApply(row.adjacentClearanceMinEmpty);
                EditorUtility.SetDirty(clearance);
            }
            else RemoveSubAsset(assetPath, clearance);

            // 击杀恢复行动：没有数值参数，子资产存在即启用；关闭时必须删除不可残留。
            var killAction = EnsureSubAsset<KillActionConfigSO>(assetPath, definition, row.id + "_KillAction");
            if (row.killActionComponent) killAction.EditorApply();
            else RemoveSubAsset(assetPath, killAction);

            // 伤害标记：纯规则能力，只生成子资产（无 MonoBehaviour 组件）。
            var damageMark = EnsureSubAsset<DamageMarkConfigSO>(assetPath, definition, row.id + "_DamageMark");
            if (row.damageMarkComponent)
            {
                damageMark.EditorApply(row.damageMarkDamagePerStack);
                EditorUtility.SetDirty(damageMark);
            }
            else RemoveSubAsset(assetPath, damageMark);

            var prefabPath = CardPrefabPath(row.id);
            // 卡图在发布阶段就解析成 Sprite 写进 SO：运行时只读 SO，不再按路径查资源，
            // 这样打包后卡图是普通资产引用，不会因为路径或打包裁剪而丢失。
            // 手牌与场地各解析一列：两列都留空是合法状态（卡面回退纯色矩形）；
            // “场地列留空沿用 cardArt” 由 CardDefinitionSO.BoardArt 在运行时决定，
            // 这里不能提前把场地图写成 cardArt，否则 SO→行对象的往返会把两列混成一个来源。
            if (!TryResolveArt(row, "cardArt", CardArtResolver.DefaultFolder, row.cardArt, sourceWorkbook, result, out var artSprite, out var artPath)) return null;
            if (!TryResolveArt(row, "cardArtUnit", CardArtResolver.UnitFolder, row.cardArtUnit, sourceWorkbook, result, out var unitArtSprite, out var unitArtPath)) return null;

            var instance = template != null ? (GameObject)PrefabUtility.InstantiatePrefab(template) : ViewTemplateBuilder.BuildTemplateObject();
            instance.name = row.id;
            var card = instance.GetComponent<Card>() ?? instance.AddComponent<Card>();
            var combatComponent = instance.GetComponent<CombatComponent>() ?? instance.AddComponent<CombatComponent>();
            var resourceComponent = SyncComponent<ResourceComponent>(instance, row.resourceComponent);
            var teleportComponent = SyncComponent<TeleportComponent>(instance, row.teleportComponent);
            var penaltyComponent = SyncComponent<DeploymentPenaltyComponent>(instance, row.deploymentPenaltyComponent);
            var spawnComponent = SyncComponent<SpawnComponent>(instance, row.spawnComponent);

            var cardSerialized = new SerializedObject(card);
            ViewTemplateBuilder.AssignReference(cardSerialized, "_definition", definition);
            ViewTemplateBuilder.AssignReference(cardSerialized, "_combat", combatComponent);
            ViewTemplateBuilder.AssignReference(cardSerialized, "_resource", row.resourceComponent ? resourceComponent : null);
            ViewTemplateBuilder.AssignReference(cardSerialized, "_teleport", row.teleportComponent ? teleportComponent : null);
            ViewTemplateBuilder.AssignReference(cardSerialized, "_deploymentPenalty", row.deploymentPenaltyComponent ? penaltyComponent : null);
            ViewTemplateBuilder.AssignReference(cardSerialized, "_spawn", row.spawnComponent ? spawnComponent : null);
            cardSerialized.ApplyModifiedPropertiesWithoutUndo();

            AssignComponentConfig(combatComponent, combat);
            if (row.resourceComponent) AssignComponentConfig(resourceComponent, resource);
            if (row.teleportComponent) AssignComponentConfig(teleportComponent, teleport);
            if (row.deploymentPenaltyComponent) AssignComponentConfig(penaltyComponent, penalty);
            if (row.spawnComponent) AssignComponentConfig(spawnComponent, spawn);

            PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            UnityEngine.Object.DestroyImmediate(instance);

            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var prefabCard = prefabAsset != null ? prefabAsset.GetComponent<Card>() : null;
            if (prefabCard == null)
            {
                result.Issues.Add(new ConfigIssue(sourceWorkbook, "Cards", 0, 0, $"卡种 {row.id} 的 Prefab 缺少 Card 组件"));
                return null;
            }

            definition.EditorApply(row, combat, row.resourceComponent ? resource : null, row.teleportComponent ? teleport : null, row.deploymentPenaltyComponent ? penalty : null, prefabCard, row.markOnEnterComponent ? mark : null, row.deathGrowthComponent ? growth : null, row.leaveAfterDeploy ? instant : null,
                row.spawnComponent ? spawn : null, row.adjacentClearanceComponent ? clearance : null, row.killActionComponent ? killAction : null, row.damageMarkComponent ? damageMark : null,
                artSprite, artPath, unitArtSprite, unitArtPath);
            EditorUtility.SetDirty(definition);
            return definition;
        }

        /// <summary>
        /// 解析一列卡图并加载成 Sprite。
        /// 输入：卡种行、列名（cardArt / cardArtUnit）、该列的默认目录、源工作簿名与发布报告；
        /// 输出：成功时 sprite / resolvedPath 给出结果（列留空时分别为 null / 空串，属合法状态）；
        ///       列值写法非法、或图片没有按 Sprite 导入时，追加一条问题并返回 false，调用方必须中止该卡种的发布。
        /// 为什么抽成方法：两列的解析与报错完全同构，复制一份最容易出现“错误信息里写错列名”，
        /// 让策划照着提示去改了另一列、却依旧编译失败。
        /// </summary>
        static bool TryResolveArt(CardRow row, string column, string defaultFolder, string value, string sourceWorkbook, PublishResult result, out Sprite sprite, out string resolvedPath)
        {
            sprite = null;
            resolvedPath = string.Empty;
            if (!CardArtResolver.TryResolve(value, defaultFolder, out var path, out var error))
            {
                // TryResolve 返回 false 且 error 为 null 只有一种情况：这一列留空，没有卡图，属合法状态。
                if (error == null) return true;
                result.Issues.Add(new ConfigIssue(sourceWorkbook, "Cards", 0, 0, $"卡种 {row.id} 的 {column} 写法非法：{error}"));
                return false;
            }

            var loaded = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (loaded == null)
            {
                result.Issues.Add(new ConfigIssue(sourceWorkbook, "Cards", 0, 0,
                    $"卡种 {row.id} 的 {column} 找不到 Sprite：{path}（PNG 必须以 Sprite (2D and UI) 导入）"));
                return false;
            }

            sprite = loaded;
            resolvedPath = path;
            return true;
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
                if ((prefab.Spawn != null) != (definition.Spawn != null) || (prefab.Spawn != null && prefab.Spawn.Config != definition.Spawn))
                    issues.Add(new ConfigIssue(sourceWorkbook, "Cards", 0, 0, $"卡种 {definition.Id} 的刷怪组件与配置引用不一致"));
                if (prefab.Combat == null) issues.Add(new ConfigIssue(sourceWorkbook, "Cards", 0, 0, $"卡种 {definition.Id} 的 Prefab 缺少战斗组件"));
                if ((prefab.Resource != null) != (definition.Resource != null)) issues.Add(new ConfigIssue(sourceWorkbook, "Cards", 0, 0, $"卡种 {definition.Id} 的资源组件与配置不一致"));
                if ((prefab.Teleport != null) != (definition.Teleport != null)) issues.Add(new ConfigIssue(sourceWorkbook, "Cards", 0, 0, $"卡种 {definition.Id} 的传送组件与配置不一致"));
                if ((prefab.DeploymentPenalty != null) != (definition.DeploymentPenalty != null)) issues.Add(new ConfigIssue(sourceWorkbook, "Cards", 0, 0, $"卡种 {definition.Id} 的一次性部署减攻组件与配置不一致"));
                if (prefab.DeploymentPenalty != null && prefab.DeploymentPenalty.Config == null) issues.Add(new ConfigIssue(sourceWorkbook, "Cards", 0, 0, $"卡种 {definition.Id} 的部署减攻组件缺少配置引用"));
                if (prefab.Resource != null && prefab.Resource.Config == null) issues.Add(new ConfigIssue(sourceWorkbook, "Cards", 0, 0, $"卡种 {definition.Id} 的资源组件缺少配置引用"));
                if (prefab.Teleport != null && prefab.Teleport.Config == null) issues.Add(new ConfigIssue(sourceWorkbook, "Cards", 0, 0, $"卡种 {definition.Id} 的传送组件缺少配置引用"));
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
