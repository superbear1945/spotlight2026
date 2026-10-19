// 打包后清理的 EditMode 测试。
// 覆盖：删除 Burst 调试目录、递归删除符号文件、保留运行时必需的 lib_burst_generated.dll 与普通资源。
// 为什么用临时目录：清理规则一旦误删运行时文件就会产出起不来的包，必须在不真正打包的前提下验证删除范围。
using System.IO;
using NUnit.Framework;
using Spotlight.EditorTools;

namespace Spotlight.Tests
{
    /// <summary>验证构建产物清理只删调试信息。输入：临时目录结构；输出：NUnit 断言结果。</summary>
    public sealed class BuildArtifactCleanupTests
    {
        /// <summary>测试用的临时构建目录。</summary>
        string _root;

        /// <summary>创建临时构建目录。输入：无；输出：无。</summary>
        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "SpotlightBuildCleanup-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        /// <summary>删除临时目录。输入：无；输出：无。</summary>
        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        /// <summary>在目录中创建一个带内容的小文件，保证删除的是真实文件而不是空占位。</summary>
        static string WriteFile(string path, string content = "x")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>
        /// 输入：含 Burst 调试目录、顶层与嵌套符号文件、运行时必需文件的标准产物结构；
        /// 输出：调试信息被删、运行时文件保留、返回值为删除条目数。
        /// </summary>
        [Test]
        public void Clean_RemovesBurstDebugDirectoryAndSymbolsOnly()
        {
            var burstDirectory = Path.Combine(_root, "2026spotligt" + BuildArtifactCleanup.BurstDebugDirectorySuffix);
            var burstLog = WriteFile(Path.Combine(burstDirectory, "Data", "Plugins", "x86_64", "lib_burst_generated.txt"));
            var topPdb = WriteFile(Path.Combine(_root, "2026spotligt.pdb"));
            var nestedMdb = WriteFile(Path.Combine(_root, "2026spotligt_Data", "Managed", "Spotlight.Runtime.mdb"));
            var burstDll = WriteFile(Path.Combine(_root, "2026spotligt_Data", "Plugins", "x86_64", "lib_burst_generated.dll"));
            var exe = WriteFile(Path.Combine(_root, "2026spotligt.exe"));

            var removed = BuildArtifactCleanup.Clean(_root);

            Assert.That(removed, Is.EqualTo(3), "应当删除 1 个调试目录 + 2 个符号文件");
            Assert.That(Directory.Exists(burstDirectory), Is.False, "Burst 调试目录必须整体删除");
            Assert.That(File.Exists(burstLog), Is.False);
            Assert.That(File.Exists(topPdb), Is.False, "顶层 .pdb 必须删除");
            Assert.That(File.Exists(nestedMdb), Is.False, "嵌套 .mdb 必须删除");
            Assert.That(File.Exists(burstDll), Is.True, "lib_burst_generated.dll 是运行时必需文件，不能删");
            Assert.That(File.Exists(exe), Is.True, "可执行文件不能删");
        }

        /// <summary>
        /// 输入：不存在的目录或空路径；输出：返回 0 且不抛异常。
        /// 为什么需要：构建后处理在异常的产物布局下也必须安静失败，不能把一次成功的打包变成失败。
        /// </summary>
        [Test]
        public void Clean_ReturnsZeroForMissingDirectory()
        {
            Assert.That(BuildArtifactCleanup.Clean(Path.Combine(_root, "missing")), Is.EqualTo(0));
            Assert.That(BuildArtifactCleanup.Clean(null), Is.EqualTo(0));
            Assert.That(BuildArtifactCleanup.Clean(string.Empty), Is.EqualTo(0));
        }
    }
}
