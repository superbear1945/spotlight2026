// 运行与构建门禁：源文件摘要、生成资产与 Prefab 组合的检查。
// 为什么需要：正式数值必须来自 Excel 编译产物；如果源表改了却忘记重新编译，
// 或者生成资产被手工篡改，就必须在进入 Play / 构建之前阻断，而不是让人拿到一套来源不明的配置。
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// 配置门禁的核心校验逻辑与 Play 模式钩子。
    /// 输入：工程根目录；输出：问题描述列表（为空表示当前配置可用）。
    /// 校验逻辑被抽成可被测试直接调用的纯函数，回调里只负责“调用 + 报错 + 阻断”。
    /// </summary>
    [InitializeOnLoad]
    public static class ConfigBuildGate
    {
        /// <summary>本次进入 Play 是否已经提示过，避免反复弹日志。</summary>
        static bool _warnedThisSession;

        /// <summary>
        /// 静态构造：注册 Play 模式状态变化回调。
        /// 输入：无；输出：无。
        /// </summary>
        static ConfigBuildGate()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>
        /// 进入 Play 之前执行门禁。
        /// 输入：状态变化事件；输出：无（有问题时把状态改回 Edit 并打印原因）。
        /// </summary>
        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                var problems = Evaluate(ProjectRoot);
                if (problems.Count == 0)
                {
                    _warnedThisSession = false;
                    return;
                }
                _warnedThisSession = true;
                Debug.LogError("配置门禁阻止进入 Play：\n" + string.Join("\n", problems) +
                               "\n请执行 Tools/Spotlight/验证配置 或 Tools/Spotlight/编译全部配置。");
                EditorApplication.isPlaying = false;
            }
            else if (state == PlayModeStateChange.EnteredEditMode && _warnedThisSession)
            {
                Debug.LogWarning("上次进入 Play 被配置门禁阻止，修复后请重新运行。");
            }
        }

        /// <summary>当前工程根目录（Assets 的父目录）。</summary>
        public static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

        /// <summary>
        /// 完整门禁校验。
        /// 输入：工程根目录；输出：问题列表。
        /// 检查项：清单是否存在、源工作簿摘要是否变化、生成资产是否缺失、Prefab 组件组合是否与配置一致。
        /// </summary>
        public static List<string> Evaluate(string projectRoot)
        {
            var problems = new List<string>();
            var manifest = AssetDatabase.LoadAssetAtPath<ConfigManifest>(ConfigAssetPublisher.ManifestPath);
            if (manifest == null)
            {
                problems.Add($"缺少编译清单 {ConfigAssetPublisher.ManifestPath}，配置从未编译过");
                return problems;
            }

            var workbookFullPath = Path.GetFullPath(Path.Combine(projectRoot, ConfigCompiler.DefaultWorkbookPath));
            problems.AddRange(EvaluateSourceHash(workbookFullPath, manifest));

            var config = AssetDatabase.LoadAssetAtPath<SpotlightConfigAsset>(ConfigAssetPublisher.ConfigAssetPath);
            if (config == null) problems.Add($"缺少主配置资产 {ConfigAssetPublisher.ConfigAssetPath}");
            else
            {
                // 触发一次完整业务校验，被篡改时把异常信息当作门禁问题而不是让回调崩溃。
                try { config.CreateConfiguration(); }
                catch (System.Exception e) { problems.Add("主配置业务校验失败：" + e.Message); }
            }

            foreach (var path in manifest.GeneratedPaths)
                if (!string.IsNullOrEmpty(path) && AssetDatabase.LoadAssetAtPath<Object>(path) == null)
                    problems.Add("生成资产缺失：" + path);

            if (config != null)
            {
                var definitions = new List<CardDefinitionSO>(config.Cards);
                var prefix = "（门禁）";
                foreach (var issue in ConfigAssetPublisher.VerifyPrefabs(definitions, prefix))
                    problems.Add(issue.ToString());
            }
            return problems;
        }

        /// <summary>
        /// 只做源文件摘要比对。
        /// 输入：工作簿绝对路径与清单；输出：问题列表。
        /// 独立成函数是为了让测试可以在没有 Unity 资产的情况下验证“配置过期”判定。
        /// </summary>
        public static List<string> EvaluateSourceHash(string workbookFullPath, ConfigManifest manifest)
        {
            var problems = new List<string>();
            if (manifest == null)
            {
                problems.Add("缺少编译清单");
                return problems;
            }
            if (!File.Exists(workbookFullPath))
            {
                problems.Add("源工作簿不存在：" + workbookFullPath);
                return problems;
            }
            var current = ConfigCompiler.ComputeFileHash(workbookFullPath);
            if (!string.Equals(current, manifest.SourceHash, System.StringComparison.Ordinal))
                problems.Add($"配置已过期：{manifest.SourceWorkbookPath} 在 {manifest.CompiledAtUtc} 编译之后被修改，请重新执行 Tools/Spotlight/编译全部配置");
            return problems;
        }
    }

    /// <summary>
    /// 构建前门禁。
    /// 输入：Unity 构建流程回调；输出：无（有问题时抛出 BuildFailedException 终止构建）。
    /// </summary>
    public sealed class ConfigBuildProcessor : IPreprocessBuildWithReport
    {
        /// <summary>回调顺序，使用默认值即可。</summary>
        public int callbackOrder => 0;

        /// <summary>
        /// 构建前校验配置。
        /// 输入：构建报告；输出：无。
        /// </summary>
        public void OnPreprocessBuild(BuildReport report)
        {
            var problems = ConfigBuildGate.Evaluate(ConfigBuildGate.ProjectRoot);
            if (problems.Count == 0) return;
            throw new BuildFailedException("配置门禁阻止构建：\n" + string.Join("\n", problems));
        }
    }
}
