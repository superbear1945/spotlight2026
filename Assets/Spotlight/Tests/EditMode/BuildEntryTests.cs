using System.IO;
using NUnit.Framework;
using Spotlight.EditorTools;
using UnityEditor;

namespace Spotlight.Tests
{
    /// <summary>
    /// 验证命令行打包入口的“打哪些场景、各平台产物放哪里”这几条规则。
    /// 输入：工程内已配置的 Build Settings 与生成场景；输出：路径、后缀与场景列表断言。
    /// 为什么需要：打包入口一旦取错目录或漏掉场景，命令行仍会打印成功却产出不可玩的包；
    /// 这里把这几条规则固定下来，且刻意不真的执行打包（打包耗时且属于手工验证范围）。
    /// </summary>
    public sealed class BuildEntryTests
    {
        /// <summary>
        /// 输入当前 Build Settings；断言启用场景非空、资产真实存在且包含演示场景，
        /// 避免“场景被禁用/被删但仍能打包出黑屏包”。
        /// </summary>
        [Test]
        public void CollectScenePaths_ReturnsExistingEnabledScenes()
        {
            var scenes = BuildEntry.CollectScenePaths();
            Assert.That(scenes, Is.Not.Empty, "Build Settings 中应至少有一个启用场景");
            Assert.That(scenes, Does.Contain(SpotlightMenu.DemoScenePath));
            foreach (var path in scenes)
                Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(path), Is.Not.Null, "场景资产不存在：" + path);
        }

        /// <summary>
        /// 输入默认输出目录名；断言结果是指向工程根下 Build 的绝对路径，
        /// 保证命令行打包不会把产物写到工程之外（AGENTS.md 要求的本地 Build 目录）。
        /// </summary>
        [Test]
        public void ResolveOutputDirectory_IsUnderProjectRoot()
        {
            var directory = BuildEntry.ResolveOutputDirectory();
            var expected = Path.GetFullPath(Path.Combine(ConfigBuildGate.ProjectRoot, BuildEntry.OutputDirectoryName));
            Assert.That(directory, Is.EqualTo(expected));
            Assert.That(Path.IsPathRooted(directory), Is.True);
            Assert.That(Path.GetFileName(directory), Is.EqualTo("Build"));
        }

        /// <summary>
        /// 输入自定义目录名；断言相对名会被解析到工程根之下，而不是相对编辑器进程的工作目录。
        /// </summary>
        [Test]
        public void ResolveOutputDirectory_UsesProjectRootForRelativeNames()
        {
            var directory = BuildEntry.ResolveOutputDirectory("Build/Verify");
            Assert.That(directory, Is.EqualTo(Path.GetFullPath(
                Path.Combine(ConfigBuildGate.ProjectRoot, "Build", "Verify"))));
            Assert.That(directory.StartsWith(Path.GetFullPath(ConfigBuildGate.ProjectRoot)), Is.True);
        }

        /// <summary>
        /// 输入 Windows / Android 两个平台；断言产物分别落在 Build/windows 与 Build/android，
        /// 且不再共用根目录 —— 否则打包 Windows 前的“清空输出目录”会把 Android 包一起删掉。
        /// </summary>
        [Test]
        public void GetOutputDirectoryName_SplitsPlatformsIntoOwnSubdirectories()
        {
            Assert.That(BuildEntry.GetOutputDirectoryName(BuildEntry.Platform.Windows),
                Is.EqualTo("Build/windows"));
            Assert.That(BuildEntry.GetOutputDirectoryName(BuildEntry.Platform.Android),
                Is.EqualTo("Build/android"));

            var root = Path.GetFullPath(Path.Combine(ConfigBuildGate.ProjectRoot, BuildEntry.OutputDirectoryName));
            foreach (var platform in new[] { BuildEntry.Platform.Windows, BuildEntry.Platform.Android })
            {
                var directory = BuildEntry.ResolveOutputDirectory(platform);
                Assert.That(directory, Is.EqualTo(Path.GetFullPath(
                    Path.Combine(root, platform.ToString().ToLowerInvariant()))));
                // 子目录必须在 Build/ 之下，但绝不能再等于 Build/ 本身。
                Assert.That(directory.StartsWith(root + Path.DirectorySeparatorChar), Is.True,
                    "平台产物目录必须位于 Build/ 之内：" + directory);
                Assert.That(directory, Is.Not.EqualTo(root), "平台产物不能直接落在 Build/ 根目录");
            }
        }

        /// <summary>
        /// 输入两个平台；断言可执行文件后缀分别是 .exe / .apk，且都带产品名前缀、与构建目标一致。
        /// 为什么需要：Android 若被写成 .exe，或 Windows 被写成 .apk，产物名字对但装不上/跑不起来。
        /// </summary>
        [Test]
        public void GetExecutableFileName_UsesPlatformExtensionAndProductName()
        {
            var windowsName = BuildEntry.GetExecutableFileName(BuildEntry.Platform.Windows);
            var androidName = BuildEntry.GetExecutableFileName(BuildEntry.Platform.Android);

            Assert.That(windowsName, Does.EndWith(".exe"));
            Assert.That(androidName, Does.EndWith(".apk"));
            Assert.That(windowsName, Does.StartWith(PlayerSettings.productName));
            Assert.That(androidName, Does.StartWith(PlayerSettings.productName));
            Assert.That(Path.GetFileName(windowsName), Is.EqualTo(windowsName), "文件名不得含目录");
            Assert.That(Path.GetFileName(androidName), Is.EqualTo(androidName), "文件名不得含目录");
        }

        /// <summary>
        /// 输入两个平台；断言映射到 StandaloneWindows64 与 Android 两个构建目标。
        /// 为什么需要：目标平台决定用什么工具链编译，映射错了产物后缀仍会被改对，但包一定跑不起来。
        /// </summary>
        [Test]
        public void GetBuildTarget_MapsPlatformsToExpectedTargets()
        {
            Assert.That(BuildEntry.GetBuildTarget(BuildEntry.Platform.Windows), Is.EqualTo(BuildTarget.StandaloneWindows64));
            Assert.That(BuildEntry.GetBuildTarget(BuildEntry.Platform.Android), Is.EqualTo(BuildTarget.Android));
            Assert.That(BuildEntry.Target, Is.EqualTo(BuildTarget.StandaloneWindows64), "旧常量 Target 必须保持 Windows");
            Assert.That(BuildEntry.AndroidTarget, Is.EqualTo(BuildTarget.Android));
        }
    }
}
