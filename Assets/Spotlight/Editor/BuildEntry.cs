// 打包入口：把工程编译成 Windows 桌面包，供本地验证与交付使用。
// 为什么需要独立入口：Unity 的 -executeMethod 只能调用静态无参方法，
// 而菜单里的打包动作必须与命令行走完全相同的流程（场景列表、输出目录、失败退出码），
// 所以把流程收敛到 BuildEntry 一处，菜单只做转发；路径与场景收集被拆成纯函数以便测试固定规则。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// Windows 桌面包的命令行/菜单构建入口。
    /// 输入：Build Settings 中启用的场景（无参数）；输出：工程根目录下 <see cref="OutputDirectoryName"/> 里的可执行包。
    /// 失败时：批处理模式以退出码 1 结束（CI/脚本能直接判定），编辑器内只打印错误不关闭编辑器。
    /// </summary>
    public static class BuildEntry
    {
        /// <summary>默认输出目录名，相对工程根；该目录被 .gitignore 忽略，属于纯产物。</summary>
        public const string OutputDirectoryName = "Build";

        /// <summary>构建目标平台：仅交付 Windows 桌面包，故写死 StandaloneWindows64。</summary>
        public const BuildTarget Target = BuildTarget.StandaloneWindows64;

        /// <summary>
        /// 收集 Build Settings 中已启用场景的资产路径。
        /// 输入：无；输出：路径数组（可能为空，为空时打包会立即失败并给出提示）。
        /// 为什么单独成函数：打包前的自检与测试都需要同一份“到底要打哪些场景”的答案。
        /// </summary>
        public static string[] CollectScenePaths()
        {
            return EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
        }

        /// <summary>
        /// 计算输出目录的绝对路径。
        /// 输入：相对工程根的目录名（默认 <see cref="OutputDirectoryName"/>）；输出：绝对路径。
        /// 为什么单独成函数：让测试可以验证“产物一定落在工程目录内”，而不必真的执行一次耗时打包。
        /// </summary>
        public static string ResolveOutputDirectory(string relativeDirectory = OutputDirectoryName)
        {
            return Path.GetFullPath(Path.Combine(ConfigBuildGate.ProjectRoot, relativeDirectory));
        }

        /// <summary>菜单入口：Tools/Spotlight/打包 Windows 版本。输入：菜单点击；输出：同 <see cref="BuildWindows"/>。</summary>
        [MenuItem("Tools/Spotlight/打包 Windows 版本", priority = 60)]
        public static void BuildWindowsFromMenu()
        {
            BuildWindows();
        }

        /// <summary>
        /// 打包 Windows 64 位桌面包（唯一实际执行构建的入口）。
        /// 输入：无参数（命令行用法见下方注释）；输出：&lt;工程根&gt;/Build/&lt;产品名&gt;.exe 及同目录数据文件。
        /// 命令行用法：
        /// Unity.exe -batchmode -nographics -quit -projectPath &lt;工程根&gt;
        ///   -executeMethod Spotlight.EditorTools.BuildEntry.BuildWindows -logFile &lt;日志路径&gt;
        /// 为什么清空输出目录：Unity 只覆盖同名文件，上一次打包留下的旧 exe/数据目录会混进本次产物，
        /// 交付时无法判断包里到底是什么版本；Build/ 是 .gitignore 忽略的纯产物目录，清空不会丢源码。
        /// </summary>
        public static void BuildWindows()
        {
            try
            {
                var scenes = CollectScenePaths();
                if (scenes.Length == 0)
                {
                    Fail("Build Settings 中没有启用任何场景，请先执行 Tools/Spotlight/生成演示场景。");
                    return;
                }

                var missingScenes = scenes.Where(path => AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null).ToArray();
                if (missingScenes.Length > 0)
                {
                    Fail("Build Settings 中的场景资产不存在：" + string.Join("、", missingScenes));
                    return;
                }

                var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(Target);
                if (!BuildPipeline.IsBuildTargetSupported(buildTargetGroup, Target))
                {
                    Fail($"当前编辑器没有安装 {Target} 构建模块，无法打包。");
                    return;
                }
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(buildTargetGroup, Target))
                {
                    Fail($"切换构建目标到 {Target} 失败。");
                    return;
                }

                var outputDirectory = ResolveOutputDirectory();
                CleanOutputDirectory(outputDirectory);
                Directory.CreateDirectory(outputDirectory);
                var executablePath = Path.Combine(outputDirectory, PlayerSettings.productName + ".exe");

                // BuildOptions.None = 非 Development 包。
                // 调试面板不是“只在界面隐藏”：Runtime/Debug 下的三个文件、GameBootstrap 的 
                // _debugPanel 字段与两个调试按钮都由 UNITY_EDITOR || DEVELOPMENT_BUILD 挡在编译期之外，
                // 因此正式包里连类型名与按钮文案都不存在（见 docs/使用说明.md 第 5 节）。
                // 配置门禁（ConfigBuildProcessor）会在此步之前自动执行，不通过时抛出 BuildFailedException。
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = executablePath,
                    target = Target,
                    options = BuildOptions.None,
                });

                var summary = report.summary;
                if (summary.result != BuildResult.Succeeded)
                {
                    Fail($"打包失败：结果 {summary.result}，错误 {summary.totalErrors} 条，详见日志。");
                    return;
                }

                Debug.Log($"打包成功：{executablePath}" +
                          $"（{summary.totalSize / 1024f / 1024f:F1} MB，耗时 {summary.totalTime.TotalSeconds:F1} 秒）" +
                          $"，原始日志：{summary.outputPath}");
            }
            catch (Exception e)
            {
                Fail("打包过程中出现异常：" + e.Message);
            }
        }

        /// <summary>
        /// 清空输出目录内的旧产物（保留目录本身）。
        /// 输入：输出目录绝对路径；输出：无（目录不存在时直接返回）。
        /// 只删除目录内部条目，绝不删除目录自身，避免误删工程根。
        /// </summary>
        static void CleanOutputDirectory(string outputDirectory)
        {
            if (!Directory.Exists(outputDirectory)) return;
            foreach (var file in Directory.GetFiles(outputDirectory))
                File.Delete(file);
            foreach (var directory in Directory.GetDirectories(outputDirectory))
                Directory.Delete(directory, true);
            Debug.Log("已清空旧产物目录：" + outputDirectory);
        }

        /// <summary>
        /// 统一失败处理。
        /// 输入：可读错误原因；输出：打印错误，批处理模式下以退出码 1 结束进程。
        /// 为什么区分模式：编辑器内调用时关闭编辑器是不可接受的副作用，只有命令行才需要退出码。
        /// </summary>
        static void Fail(string message)
        {
            Debug.LogError(message);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
}
