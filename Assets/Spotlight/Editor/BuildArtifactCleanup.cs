// 打包后清理：把 Unity 生成的调试信息从交付目录里删除。
// 为什么需要单独的后处理：Burst 调试目录名里就写着 DoNotShip，但它会在干净工作区/CI 重新生成；
// 同时 Player 构建偶尔会落下 .pdb/.mdb 符号文件。这些文件不参与运行，却会泄露构建机路径与源码文件清单。
// 用 IPostprocessBuildWithReport 而不是只改 BuildEntry：CI 的 game-ci 构建脚本不经过 BuildEntry，也要能享受同一套清理。
using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// 构建后处理器：删除 <c>*_BurstDebugInformation_DoNotShip</c> 目录与符号文件。
    /// 输入：Unity 构建报告（只用到输出路径）；输出：清理后的交付目录。
    /// 说明：只删除“调试信息”这一类产物，<c>lib_burst_generated.dll</c> 等运行时必需文件不在删除范围。
    /// </summary>
    public sealed class BuildArtifactCleanup : IPostprocessBuildWithReport
    {
        /// <summary>Burst 调试信息目录的后缀（Unity 自己标注的 DoNotShip）。</summary>
        public const string BurstDebugDirectorySuffix = "_BurstDebugInformation_DoNotShip";

        /// <summary>需要删除的符号文件扩展名。</summary>
        static readonly string[] SymbolExtensions = { ".pdb", ".mdb" };

        /// <summary>执行顺序：放在其他构建后处理之后，保证删除的是最终产物。</summary>
        public int callbackOrder => 1000;

        /// <summary>
        /// 构建完成回调。
        /// 输入：构建报告；输出：无（失败只记录日志，不改变已成功的构建结果）。
        /// </summary>
        public void OnPostprocessBuild(BuildReport report)
        {
            var outputPath = report != null ? report.summary.outputPath : null;
            if (string.IsNullOrEmpty(outputPath)) return;
            var directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
            var removed = Clean(directory);
            Debug.Log($"已清理构建目录中的调试信息 {removed} 项：{directory}");
        }

        /// <summary>
        /// 删除目录内的调试信息（Burst 调试目录与符号文件）。
        /// 输入：构建输出目录的绝对或相对路径；输出：删除的条目数量（目录与文件分别计数）。
        /// 为什么做成纯静态函数：EditMode 测试可以在临时目录上验证规则，不必真的打一次包。
        /// </summary>
        public static int Clean(string buildDirectory)
        {
            if (string.IsNullOrEmpty(buildDirectory) || !Directory.Exists(buildDirectory)) return 0;
            var removed = 0;
            foreach (var directory in Directory.GetDirectories(buildDirectory, "*" + BurstDebugDirectorySuffix, SearchOption.TopDirectoryOnly))
            {
                Directory.Delete(directory, true);
                removed++;
            }
            foreach (var extension in SymbolExtensions)
            {
                foreach (var file in Directory.GetFiles(buildDirectory, "*" + extension, SearchOption.AllDirectories))
                {
                    File.Delete(file);
                    removed++;
                }
            }
            return removed;
        }
    }
}
