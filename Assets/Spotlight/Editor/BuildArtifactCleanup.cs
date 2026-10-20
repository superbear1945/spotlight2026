// 构建产物清理：删掉 Unity/Burst 自动写在输出目录里的调试信息，避免随包发出构建机绝对路径。
// 为什么必须做成 IPostprocessBuildWithReport 而不是只改 BuildEntry：
// 清理属于“任何一次打包都要做”的收尾动作，而 CI 用的是 game-ci 内置构建脚本，不经过 BuildEntry；
// 只有挂到 Unity 的构建回调上，才能保证菜单打包、命令行打包与 CI 打包都执行同一套清理。
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// 构建后清理器：删除 Burst 调试信息目录与遗留的 Burst 符号文件。
    /// 输入：Unity 的构建报告（<see cref="BuildReport.summary"/> 的 outputPath）；
    /// 输出：无（清理动作写在 Player 构建输出目录内，不触碰任何源码或 Library 缓存）。
    /// 为什么需要：`*_BurstDebugInformation_DoNotShip/Data/Plugins/x86_64/lib_burst_generated.txt`
    /// 里带着构建机的绝对路径（Temp/Burst、Library/Burst、编辑器安装路径）。
    /// Unity 自己在目录名里写了 DoNotShip，但它会随 `<输出目录>/` 一起被压缩/上传，
    /// 于是玩家拿到的包里就多出一条“这台机器把工程放在哪”的线索。
    /// 目录本身不参与运行（真正必要的是 `_Data/Plugins/x86_64/lib_burst_generated.dll`），删掉不影响启动。
    /// </summary>
    public sealed class BuildArtifactCleanup : IPostprocessBuildWithReport
    {
        /// <summary>Burst 调试信息目录后缀（Unity 生成时会在前面拼产品名）。</summary>
        public const string BurstDebugDirectorySuffix = "_BurstDebugInformation_DoNotShip";

        /// <summary>Burst 生成的符号文件名；部分 Unity 版本会把它留在插件目录里。</summary>
        public const string BurstDebugPdbFileName = "lib_burst_generated.pdb";

        /// <summary>
        /// 回调顺序：放在默认值即可，清理必须在 Player 产物写完之后执行。
        /// </summary>
        public int callbackOrder => 0;

        /// <summary>
        /// 构建结束后清理输出目录。
        /// 输入：构建报告；输出：无。
        /// 失败只记错误日志、不抛异常：包体已经生成，此时让构建失败只会让人误以为没出包；
        /// 真正的兜底是 scripts/check-build-hygiene.sh 与打包前的目录断言。
        /// </summary>
        public void OnPostprocessBuild(BuildReport report)
        {
            try
            {
                var outputPath = report != null && report.summary.outputPath != null ? report.summary.outputPath : null;
                if (string.IsNullOrEmpty(outputPath)) return;
                var outputDirectory = Directory.Exists(outputPath) ? outputPath : Path.GetDirectoryName(outputPath);
                if (string.IsNullOrEmpty(outputDirectory)) return;

                var removed = CleanBuildArtifacts(outputDirectory);
                if (removed.Count > 0)
                    Debug.Log("已清理构建输出中的调试信息：" + string.Join("、", removed));
            }
            catch (Exception e)
            {
                Debug.LogError("构建后清理调试信息失败，请手动删除输出目录下的 *_" + BurstDebugDirectorySuffix + "：" + e.Message);
            }
        }

        /// <summary>
        /// 删除输出目录内的 Burst 调试信息目录与符号文件。
        /// 输入：Player 构建输出目录的绝对路径（不存在时视为无事可做）；
        /// 输出：被删除条目的相对路径列表（测试与日志据此断言，空列表表示本来就没有）。
        /// 为什么单独抽成静态纯函数：清理逻辑要能被 EditMode 测试用临时目录直接验证，
        /// 而不必真的打一次包（真实打包要几十秒且依赖构建模块）。
        /// </summary>
        public static List<string> CleanBuildArtifacts(string outputDirectory)
        {
            var removed = new List<string>();
            if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory)) return removed;

            foreach (var directory in Directory.GetDirectories(outputDirectory, "*" + BurstDebugDirectorySuffix, SearchOption.AllDirectories))
            {
                if (!Directory.Exists(directory)) continue;
                var relative = MakeRelative(outputDirectory, directory);
                Directory.Delete(directory, true);
                removed.Add(relative);
            }

            foreach (var file in Directory.GetFiles(outputDirectory, BurstDebugPdbFileName, SearchOption.AllDirectories))
            {
                if (!File.Exists(file)) continue;
                var relative = MakeRelative(outputDirectory, file);
                File.Delete(file);
                removed.Add(relative);
            }

            return removed;
        }

        /// <summary>
        /// 计算用于日志与断言的相对路径（统一用 /，避免 Windows 反斜杠让日志与断言各说各话）。
        /// 输入：基准目录与目标路径；输出：相对路径文本（不在基准目录内时退回绝对路径）。
        /// 不用 Path.GetRelativePath：它要求 .NET Standard 2.1，而工程的 API 兼容级别不保证它可用。
        /// </summary>
        static string MakeRelative(string baseDirectory, string path)
        {
            var baseFull = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            var relative = full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase) ? full.Substring(baseFull.Length) : full;
            return relative.Replace('\\', '/');
        }

        // 扩展点（P2-4，暂不实施）：彻底剥离托管程序集里内嵌的 CodeView 路径需要在此处
        // 重写 PE 的 debug directory，代价是必须自己维护 PE 解析并验证不破坏 Mono 的加载。
        // 首版接受现状：它只泄露源码文件清单，不暴露数值与玩法，也不进入任何 UI 文案。
    }
}
