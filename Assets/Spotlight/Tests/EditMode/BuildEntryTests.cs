using System.IO;
using NUnit.Framework;
using Spotlight.EditorTools;
using UnityEditor;

namespace Spotlight.Tests
{
    /// <summary>
    /// 验证命令行打包入口的“打哪些场景、产物放哪里”两条规则。
    /// 输入：工程内已配置的 Build Settings 与生成场景；输出：路径与场景列表断言。
    /// 为什么需要：打包入口一旦取错目录或漏掉场景，命令行仍会打印成功却产出不可玩的包；
    /// 这里把两条规则固定下来，且刻意不真的执行打包（打包耗时且属于手工验证范围）。
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
    }
}
