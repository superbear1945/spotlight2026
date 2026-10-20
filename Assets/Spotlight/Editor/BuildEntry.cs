// 打包入口：把工程编译成 Windows 桌面包与 Android 安装包，供本地验证与交付使用。
// 为什么需要独立入口：Unity 的 -executeMethod 只能调用静态无参方法，
// 而菜单里的打包动作必须与命令行走完全相同的流程（场景列表、输出目录、失败退出码），
// 所以把流程收敛到 BuildEntry 一处，菜单只做转发；路径与场景收集被拆成纯函数以便测试固定规则。
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// Windows / Android 双平台的命令行与菜单构建入口。
    /// 输入：Build Settings 中启用的场景；可选命令行参数 <c>-buildVersion &lt;x.y.z&gt;</c> 与
    /// <c>-androidVersionCode &lt;正整数&gt;</c>（CI 发版用，由 <see cref="BuildVersionOverride"/> 读入并在构建后还原）。
    /// 输出：&lt;工程根&gt;/Build/windows/（exe + 数据目录）与 &lt;工程根&gt;/Build/android/*.apk。
    /// 失败时：批处理模式以退出码 1 结束（CI/脚本能直接判定），编辑器内只打印错误不关闭编辑器。
    /// 为什么按平台分子目录：两种产物一个是目录包、一个是单文件，混在同一层里既无法整体压缩交付，
    /// 也无法判断某个残留文件属于哪个平台；分开后每个子目录就是一份可直接压缩的完整包。
    /// </summary>
    public static class BuildEntry
    {
        /// <summary>产物根目录名，相对工程根；该目录被 .gitignore 忽略，属于纯产物。</summary>
        public const string OutputDirectoryName = "Build";

        /// <summary>Windows 桌面包输出目录（相对工程根）。</summary>
        public const string WindowsOutputDirectoryName = "Build/windows";

        /// <summary>Android 安装包输出目录（相对工程根）。</summary>
        public const string AndroidOutputDirectoryName = "Build/android";

        /// <summary>Windows 可执行文件后缀。</summary>
        public const string WindowsExecutableExtension = ".exe";

        /// <summary>Android 安装包后缀（本项目只出 APK，不出 AAB）。</summary>
        public const string AndroidExecutableExtension = ".apk";

        /// <summary>Windows 构建目标（保留旧名 Target，命令行与既有测试沿用）。</summary>
        public const BuildTarget Target = BuildTarget.StandaloneWindows64;

        /// <summary>Android 构建目标。</summary>
        public const BuildTarget AndroidTarget = BuildTarget.Android;

        /// <summary>
        /// 可交付平台。
        /// 为什么用枚举而不是裸 BuildTarget：输出目录、后缀、菜单文案都由平台决定，
        /// 枚举让“平台 → 产物路径”的映射在一处成表，新增平台时编译器会提示补全 switch。
        /// </summary>
        public enum Platform
        {
            /// <summary>Windows 64 位桌面版。</summary>
            Windows = 0,

            /// <summary>Android 手机/平板版。</summary>
            Android = 1,
        }

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

        /// <summary>
        /// 按平台计算输出目录的绝对路径。
        /// 输入：平台枚举；输出：&lt;工程根&gt;/Build/windows 或 &lt;工程根&gt;/Build/android 的绝对路径。
        /// 为什么单独成函数：清空目录是不可逆操作，构建前必须能独立断言“清的是哪个平台自己的目录”。
        /// </summary>
        public static string ResolveOutputDirectory(Platform platform)
        {
            return ResolveOutputDirectory(GetOutputDirectoryName(platform));
        }

        /// <summary>
        /// 按平台给出相对工程根的输出目录名。
        /// 输入：平台枚举；输出："Build/windows" 或 "Build/android"。
        /// 为什么单独成函数：菜单提示、日志与测试都要用同一份目录名，避免手写字符串写歪。
        /// </summary>
        public static string GetOutputDirectoryName(Platform platform)
        {
            switch (platform)
            {
                case Platform.Windows:
                    return WindowsOutputDirectoryName;
                case Platform.Android:
                    return AndroidOutputDirectoryName;
                default:
                    throw new ArgumentOutOfRangeException(nameof(platform), platform, "未知的打包平台");
            }
        }

        /// <summary>
        /// 按平台给出可执行文件后缀。
        /// 输入：平台枚举；输出：".exe" 或 ".apk"。
        /// </summary>
        public static string GetExecutableExtension(Platform platform)
        {
            switch (platform)
            {
                case Platform.Windows:
                    return WindowsExecutableExtension;
                case Platform.Android:
                    return AndroidExecutableExtension;
                default:
                    throw new ArgumentOutOfRangeException(nameof(platform), platform, "未知的打包平台");
            }
        }

        /// <summary>
        /// 按平台给出 Unity 构建目标。
        /// 输入：平台枚举；输出：StandaloneWindows64 或 Android。
        /// </summary>
        public static BuildTarget GetBuildTarget(Platform platform)
        {
            switch (platform)
            {
                case Platform.Windows:
                    return Target;
                case Platform.Android:
                    return AndroidTarget;
                default:
                    throw new ArgumentOutOfRangeException(nameof(platform), platform, "未知的打包平台");
            }
        }

        /// <summary>
        /// 按平台给出产物文件名（Windows 是 &lt;产品名&gt;.exe，Android 是 &lt;产品名&gt;.apk）。
        /// 输入：平台枚举；输出：文件名（不含目录）。
        /// 为什么不用固定的 "Spotlight"：产物名必须跟随 PlayerSettings.productName，
        /// 否则改名后包名与启动器显示名不一致，交付时容易发错版本。
        /// </summary>
        public static string GetExecutableFileName(Platform platform)
        {
            var productName = PlayerSettings.productName;
            if (string.IsNullOrWhiteSpace(productName)) productName = "Spotlight";
            return productName + GetExecutableExtension(platform);
        }

        // ---------------------------------------------------------------------
        // 菜单入口（只做转发，保证与命令行走同一条代码路径）
        // ---------------------------------------------------------------------

        /// <summary>菜单入口：Tools/Spotlight/打包 Windows 版本。输入：菜单点击；输出：同 <see cref="BuildWindows"/>。</summary>
        [MenuItem("Tools/Spotlight/打包 Windows 版本", priority = 60)]
        public static void BuildWindowsFromMenu()
        {
            BuildWindows();
        }

        /// <summary>菜单入口：Tools/Spotlight/打包 Android 版本。输入：菜单点击；输出：同 <see cref="BuildAndroid"/>。</summary>
        [MenuItem("Tools/Spotlight/打包 Android 版本", priority = 61)]
        public static void BuildAndroidFromMenu()
        {
            BuildAndroid();
        }

        /// <summary>
        /// 菜单入口：Tools/Spotlight/打包全部版本（Windows + Android）。
        /// 输入：菜单点击；输出：同 <see cref="BuildAll"/>。
        /// </summary>
        [MenuItem("Tools/Spotlight/打包全部版本（Windows + Android）", priority = 62)]
        public static void BuildAllFromMenu()
        {
            BuildAll();
        }

        // ---------------------------------------------------------------------
        // 打包入口（命令行也可直接调用这三个静态无参方法）
        // ---------------------------------------------------------------------

        /// <summary>
        /// 打包 Windows 64 位桌面包。
        /// 输入：无参数（命令行用法见类注释）；输出：&lt;工程根&gt;/Build/windows/&lt;产品名&gt;.exe 及同目录数据文件。
        /// 命令行用法：
        /// Unity.exe -batchmode -nographics -quit -projectPath &lt;工程根&gt;
        ///   -executeMethod Spotlight.EditorTools.BuildEntry.BuildWindows -logFile &lt;日志路径&gt;
        /// </summary>
        public static void BuildWindows()
        {
            ExitIfFailed(RunBuild(Platform.Windows));
        }

        /// <summary>
        /// 打包 Android 安装包（APK，非 AAB）。
        /// 输入：无参数；输出：&lt;工程根&gt;/Build/android/&lt;产品名&gt;.apk。
        /// 命令行用法同 <see cref="BuildWindows"/>，把方法名换成 BuildAndroid。
        /// 为什么强制 APK：AAB 不能直接 adb install / 侧载，验收与真机试玩都需要 APK。
        /// </summary>
        public static void BuildAndroid()
        {
            ExitIfFailed(RunBuild(Platform.Android));
        }

        /// <summary>
        /// 依次打包 Windows 与 Android 两个平台。
        /// 输入：无参数；输出：两个平台的产物，任一失败则整体按失败处理（批处理退出码 1）。
        /// 为什么先 Windows 后 Android：Windows 侧构建最快，能在十几秒内暴露配置门禁/场景缺失这类
        /// 与平台无关的问题，避免等十几分钟 Android IL2CPP 编译完才发现场景没配置。
        /// 为什么单个平台失败后仍继续下一个：两个平台互相独立，一次跑完能同时拿到两份诊断信息。
        /// </summary>
        public static void BuildAll()
        {
            var windowsOk = RunBuild(Platform.Windows);
            var androidOk = RunBuild(Platform.Android);
            if (windowsOk && androidOk)
            {
                Debug.Log($"全部平台打包成功：Windows → {ResolveOutputDirectory(Platform.Windows)}，" +
                          $"Android → {ResolveOutputDirectory(Platform.Android)}");
                return;
            }

            Fail($"打包未全部成功：Windows {(windowsOk ? "成功" : "失败")}，Android {(androidOk ? "成功" : "失败")}。" +
                 "详见上方日志。");
        }

        // ---------------------------------------------------------------------
        // 内部实现
        // ---------------------------------------------------------------------

        /// <summary>
        /// 真正执行一次打包。
        /// 输入：目标平台；输出：true 表示产物已生成，false 表示中途失败（原因已写入日志）。
        /// 为什么不直接抛异常：BuildAll 需要在某个平台失败后继续打另一个平台，
        /// 异常会把整轮流程打断；而单个平台的入口再用 <see cref="ExitIfFailed"/> 转成退出码。
        /// </summary>
        static bool RunBuild(Platform platform)
        {
            try
            {
                var scenes = CollectScenePaths();
                if (scenes.Length == 0)
                {
                    Debug.LogError("Build Settings 中没有启用任何场景，请先执行 Tools/Spotlight/生成演示场景。");
                    return false;
                }

                var missingScenes = scenes.Where(path => AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null).ToArray();
                if (missingScenes.Length > 0)
                {
                    Debug.LogError("Build Settings 中的场景资产不存在：" + string.Join("、", missingScenes));
                    return false;
                }

                var target = GetBuildTarget(platform);
                var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(target);
                if (!BuildPipeline.IsBuildTargetSupported(buildTargetGroup, target))
                {
                    Debug.LogError($"当前编辑器没有安装 {target} 构建模块，无法打包 {platform}。" +
                                   "请在 Unity Hub 里为该版本补装对应平台模块后重试。");
                    return false;
                }
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(buildTargetGroup, target))
                {
                    Debug.LogError($"切换构建目标到 {target} 失败。");
                    return false;
                }

                var outputDirectory = ResolveOutputDirectory(platform);
                CleanOutputDirectory(outputDirectory);
                Directory.CreateDirectory(outputDirectory);
                var executablePath = Path.Combine(outputDirectory, GetExecutableFileName(platform));

                // 发版版本覆盖：CI 通过 -buildVersion / -androidVersionCode 传入本次发版版本，
                // 与 Release 标签同源（scripts/next-release-version.sh 是唯一版本来源）。
                // 本地菜单打包不传这两个参数，结果是 null，PlayerSettings 完全不动。
                string versionError;
                var versionOverride = BuildVersionOverride.FromCommandLine(out versionError);
                if (!string.IsNullOrEmpty(versionError))
                {
                    // 版本参数非法时宁可让打包失败：带着错误的版本号发布的包无法回收。
                    Debug.LogError("命令行版本参数非法：" + versionError);
                    return false;
                }

                // BuildOptions.None = 非 Development 包。
                // 调试面板不是“只在界面隐藏”：Runtime/Debug 下的三个文件、GameBootstrap 的
                // _debugPanel 字段与两个调试按钮都由 UNITY_EDITOR || DEVELOPMENT_BUILD 挡在编译期之外，
                // 因此正式包里连类型名与按钮文案都不存在（见 docs/使用说明.md 第 5 节）。
                // 配置门禁（ConfigBuildProcessor）会在此步之前自动执行，不通过时抛出 BuildFailedException。
                BuildReport report;
                // 两层作用域：外层临时改写包体版本（构建后还原），内层临时改写平台专属设置。
                // 顺序无关但必须都包含 BuildPlayer：任何一层漏掉都会让“打完包后工程设置被改掉”。
                using (versionOverride != null ? versionOverride.Apply() : new NoopScope())
                using (ApplyPlatformOverrides(platform))
                {
                    report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                    {
                        scenes = scenes,
                        locationPathName = executablePath,
                        target = target,
                        options = BuildOptions.None,
                    });
                }

                var summary = report.summary;
                if (summary.result != BuildResult.Succeeded)
                {
                    Debug.LogError($"打包 {platform} 失败：结果 {summary.result}，错误 {summary.totalErrors} 条，详见日志。");
                    return false;
                }

                Debug.Log($"打包 {platform} 成功：{executablePath}" +
                          $"（{summary.totalSize / 1024f / 1024f:F1} MB，耗时 {summary.totalTime.TotalSeconds:F1} 秒）" +
                          $"，原始日志：{summary.outputPath}");
                return true;
            }
            catch (Exception e)
            {
                // 打包异常里最有用的是内层异常（BuildFailedException 的外层 Message 往往只有一句概括）。
                var detail = e is BuildFailedException ? e.Message : e.ToString();
                Debug.LogError($"打包 {platform} 过程中出现异常：{detail}");
                return false;
            }
        }

        /// <summary>
        /// 应用平台专属的构建期设置，并在构建结束后原样还原。
        /// 输入：目标平台；输出：可释放的作用域（Windows 为空操作）。
        /// 为什么用“临时改 + finally 还原”而不是改进 ProjectSettings：
        /// 这些设置只对某一端的包体有意义（手机锁横屏、装 64 位 ABI），写进工程会让 Windows 侧编辑器
        /// 也跟着变，且会在版本库里留下与本次打包无关的 diff；临时覆盖能让仓库保持与平台无关。
        /// 为什么必须还原：不还原会让下一次构建继承上一次的临时值，且团队里其他人打开工程会看到被改过的设置。
        /// </summary>
        static IDisposable ApplyPlatformOverrides(Platform platform)
        {
            switch (platform)
            {
                case Platform.Android:
                    return new AndroidBuildSettingsScope();
                default:
                    return new NoopScope();
            }
        }

        /// <summary>
        /// 清空输出目录内的旧产物（保留目录本身）。
        /// 输入：输出目录绝对路径；输出：无（目录不存在时直接返回）。
        /// 只删除目录内部条目，绝不删除目录自身，避免误删工程根；
        /// 也只清指定平台的子目录，不会连带删掉另一个平台的包。
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
        /// 单个平台失败时的收尾：打印错误，批处理模式下以退出码 1 结束进程。
        /// 输入：打包结果；输出：无。
        /// 为什么区分模式：编辑器内调用时关闭编辑器是不可接受的副作用，只有命令行才需要退出码。
        /// </summary>
        static void ExitIfFailed(bool succeeded)
        {
            if (succeeded) return;
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }

        /// <summary>
        /// 统一失败处理。
        /// 输入：可读错误原因；输出：打印错误，批处理模式下以退出码 1 结束进程。
        /// </summary>
        static void Fail(string message)
        {
            Debug.LogError(message);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }

        /// <summary>空作用域：用于没有平台专属设置的构建目标（Windows）。</summary>
        sealed class NoopScope : IDisposable
        {
            /// <summary>无任何需要回滚的设置。输入：无；输出：无。</summary>
            public void Dispose()
            {
            }
        }

        /// <summary>
        /// Android 构建期设置作用域：IL2CPP + 出 APK、装 ARMv7 + ARM64、锁横屏，并在 Dispose 时全部还原。
        /// 输入：无（构造时读取当前设置）；输出：无（Dispose 恢复原值）。
        /// 为什么需要这些设置：
        /// · IL2CPP：Mono 后端在 Android 上不支持 ARM64，只勾 ARM64 也会被静默降级成纯 armeabi-v7a 的包，
        ///   而 Pixel 7 / Galaxy S23 之后的纯 64 位机型根本装不上，所以必须显式切到 IL2CPP；
        /// · APK：AAB 不能直接 adb install，验收与真机试玩只认 APK；
        /// · ARMv7 + ARM64：默认工程只勾了 ARMv7，纯 64 位机型装不上；两个 ABI 都带则新老机器通吃；
        /// · 锁横屏：棋盘是 10 列宽、CanvasScaler 按 1920×1080 设计，竖屏会把棋盘压到装不下。
        /// </summary>
        sealed class AndroidBuildSettingsScope : IDisposable
        {
            readonly bool _buildAppBundle;
            readonly ScriptingImplementation _scriptingBackend;
            readonly AndroidArchitecture _targetArchitectures;
            readonly UIOrientation _defaultInterfaceOrientation;
            readonly bool _allowedAutorotateToPortrait;
            readonly bool _allowedAutorotateToPortraitUpsideDown;
            readonly bool _allowedAutorotateToLandscapeLeft;
            readonly bool _allowedAutorotateToLandscapeRight;

            /// <summary>
            /// 读取并临时改写 Android 打包设置。
            /// 输入：无；输出：无（设置已生效，等待 Dispose 还原）。
            /// </summary>
            public AndroidBuildSettingsScope()
            {
                _buildAppBundle = EditorUserBuildSettings.buildAppBundle;
                _scriptingBackend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android);
                _targetArchitectures = PlayerSettings.Android.targetArchitectures;
                _defaultInterfaceOrientation = PlayerSettings.defaultInterfaceOrientation;
                _allowedAutorotateToPortrait = PlayerSettings.allowedAutorotateToPortrait;
                _allowedAutorotateToPortraitUpsideDown = PlayerSettings.allowedAutorotateToPortraitUpsideDown;
                _allowedAutorotateToLandscapeLeft = PlayerSettings.allowedAutorotateToLandscapeLeft;
                _allowedAutorotateToLandscapeRight = PlayerSettings.allowedAutorotateToLandscapeRight;

                EditorUserBuildSettings.buildAppBundle = false;
                if (_scriptingBackend != ScriptingImplementation.IL2CPP)
                    PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARMv7 | AndroidArchitecture.ARM64;
                PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
                PlayerSettings.allowedAutorotateToPortrait = false;
                PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
                PlayerSettings.allowedAutorotateToLandscapeLeft = true;
                PlayerSettings.allowedAutorotateToLandscapeRight = true;

                Debug.Log("Android 打包设置：APK（非 AAB）、IL2CPP、ARMv7 + ARM64、仅横屏；" +
                          "构建结束后还原临时覆盖项。");
            }

            /// <summary>
            /// 还原构造时读取的全部设置。
            /// 输入：无；输出：无。
            /// </summary>
            public void Dispose()
            {
                EditorUserBuildSettings.buildAppBundle = _buildAppBundle;
                if (_scriptingBackend != ScriptingImplementation.IL2CPP)
                    PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, _scriptingBackend);
                PlayerSettings.Android.targetArchitectures = _targetArchitectures;
                PlayerSettings.defaultInterfaceOrientation = _defaultInterfaceOrientation;
                PlayerSettings.allowedAutorotateToPortrait = _allowedAutorotateToPortrait;
                PlayerSettings.allowedAutorotateToPortraitUpsideDown = _allowedAutorotateToPortraitUpsideDown;
                PlayerSettings.allowedAutorotateToLandscapeLeft = _allowedAutorotateToLandscapeLeft;
                PlayerSettings.allowedAutorotateToLandscapeRight = _allowedAutorotateToLandscapeRight;
            }
        }
    }
}
