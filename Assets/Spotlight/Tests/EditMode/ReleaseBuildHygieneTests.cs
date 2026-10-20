// 发布包体卫生的回归测试（EditMode）。
// 背景：对外交付的 Windows 包不允许“拆包可见”开发期信息（调试配置面板的代码与界面骨架、
// Excel 路径/摘要/编译时间、Burst 调试目录）。这类问题不会让游戏跑不起来，所以只能靠静态断言守住。
// 覆盖：①调试源码整体处于 UNITY_EDITOR || DEVELOPMENT_BUILD 守卫内；②皮肤 UXML 不再含调试节点；
//      ③随包发布的配置资产不落工作簿元数据；④构建后清理器会删除 Burst 调试目录与符号文件。
// 输入：仓库内的源码/UXML/生成资产文本与临时目录；输出：NUnit 断言结果。
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Spotlight;
using Spotlight.EditorTools;

namespace Spotlight.Tests
{
    /// <summary>发布包体去调试化的静态与单元回归测试。输入：仓库文件与临时产物；输出：断言结果。</summary>
    public class ReleaseBuildHygieneTests
    {
        /// <summary>战斗 HUD 皮肤（正式包一定会带上）。</summary>
        const string SkinUxmlPath = "Assets/Spotlight/UI/Spotlight.uxml";

        /// <summary>配置面板控制器（校验它用代码搭骨架，且不依赖任何会被打包的资产）。</summary>
        const string DebugPanelSourcePath = "Assets/Spotlight/Runtime/Debug/ConfigDebugPanel.cs";

        /// <summary>演示场景（正式包唯一的场景）。</summary>
        const string DemoScenePath = "Assets/Spotlight/Scenes/Spotlight.unity";

        /// <summary>编辑器与 Development Build 才编译的调试源码。</summary>
        static readonly string[] DebugSourcePaths =
        {
            "Assets/Spotlight/Runtime/Debug/ConfigDebugPanel.cs",
            "Assets/Spotlight/Runtime/Debug/ConfigFieldBuilder.cs",
            "Assets/Spotlight/Runtime/Debug/DebugConfigDraft.cs"
        };

        /// <summary>调试守卫用的编译宏表达式，全仓库必须只有这一种写法。</summary>
        const string DebugGuard = "#if UNITY_EDITOR || DEVELOPMENT_BUILD";

        /// <summary>
        /// 皮肤 UXML 里不得再出现任何调试节点。
        /// 输入：无；输出：断言结果。
        /// 为什么：UXML 是无条件打包的资源，只要调试面板块留在里面，拆包就能读到整张表单结构。
        /// </summary>
        [Test]
        public void SkinUxmlContainsNoDebugNodes()
        {
            var skin = File.ReadAllText(SkinUxmlPath);
            Assert.That(skin, Does.Not.Contain("debug"), "Spotlight.uxml 不能再包含任何调试节点（按钮或面板）");
        }

        /// <summary>
        /// 调试面板骨架用代码搭建，且不引用任何 UXML 资产。
        /// 输入：无；输出：断言结果。
        /// 为什么这么严：实测过“把骨架拆成一个 UXML 资产”的做法——只要场景里挂着调试面板组件，
        /// 构建依赖收集就会把该 UXML 打进 sharedassets（拆包能读到整张表单）。
        /// 只有“代码搭 + 无资产引用”才能保证正式包里彻底没有它。
        /// </summary>
        [Test]
        public void DebugPanelSkeletonIsBuiltInCodeWithoutAsset()
        {
            var source = File.ReadAllText(DebugPanelSourcePath);
            var requiredNames = new[]
            {
                "debug-panel", "debug-scroll", "debug-board", "debug-home", "debug-deck",
                "debug-cards", "debug-boss", "debug-turn", "debug-toggles",
                "debug-reset-button", "debug-close-button", "debug-apply-button",
                "debug-export-button", "debug-import-button", "debug-json-status", "debug-error"
            };
            foreach (var name in requiredNames)
                Assert.That(source, Does.Contain("\"" + name + "\""), "面板骨架缺少节点：" + name);
            Assert.That(source, Does.Not.Contain("DebugPanel.uxml"),
                "面板骨架不得再依赖 UXML 资产：资产引用会把整张表单拉进正式包");
            Assert.That(source, Does.Not.Contain("VisualTreeAsset"),
                "面板控制器不得持有 VisualTreeAsset 引用（同上）");
        }

        /// <summary>
        /// 场景与界面资源里不得出现调试组件的类型名。
        /// 输入：无；输出：断言结果。
        /// 为什么：场景里只要挂过 ConfigDebugPanel，构建时 globalgamemanagers 就会留下它的 MonoScript
        /// 名字（组件在正式包里退化为 missing script，名字却还在），拆包依旧能看出有配置编辑器。
        /// </summary>
        [Test]
        public void SceneAndUiAssetsDoNotReferenceDebugComponents()
        {
            var debugTypeNames = new[] { "ConfigDebugPanel", "DebugConfigDraft", "ConfigFieldBuilder" };
            var roots = new[] { "Assets/Spotlight/Scenes", "Assets/Spotlight/UI", "Assets/Spotlight/Generated", "Assets/Spotlight/Runtime/View" };
            // 只看序列化资产与界面文件：图片等二进制不参与“类型名引用”的判断，也避免大文件读入。
            var textExtensions = new[] { ".unity", ".prefab", ".asset", ".uxml", ".uss" };
            var inspected = 0;
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (!textExtensions.Any(extension => file.EndsWith(extension, System.StringComparison.OrdinalIgnoreCase))) continue;
                    var text = File.ReadAllText(file);
                    inspected++;
                    foreach (var name in debugTypeNames)
                        Assert.That(text, Does.Not.Contain(name), file + " 引用了调试类型：" + name + "（会让类名随包发布）");
                }
            }
            Assert.That(inspected, Is.GreaterThan(0), "没有检查到任何场景/界面资源");
            Assert.That(File.Exists(DemoScenePath), Is.True, "缺少演示场景 " + DemoScenePath);
        }

        /// <summary>
        /// 三个调试源文件必须整文件处于调试守卫内（而不是“只隐藏界面”）。
        /// 输入：无；输出：断言结果。
        /// 为什么：正式包对 Managed/*.dll 的字符串扫描会命中类型名与中文提示，
        /// 只有条件编译才能真正让它们不出现在程序集里。
        /// </summary>
        [Test]
        public void DebugSourcesAreEntirelyInsideDebugGuard()
        {
            foreach (var path in DebugSourcePaths)
            {
                var text = File.ReadAllText(path);
                var guardIndex = text.IndexOf(DebugGuard, System.StringComparison.Ordinal);
                Assert.That(guardIndex, Is.GreaterThanOrEqualTo(0), path + " 缺少调试守卫 " + DebugGuard);
                Assert.That(text.IndexOf(DebugGuard, guardIndex + 1, System.StringComparison.Ordinal), Is.EqualTo(-1),
                    path + " 出现了多段调试守卫，必须整文件包住");
                Assert.That(guardIndex, Is.LessThan(text.IndexOf("namespace ", System.StringComparison.Ordinal)),
                    path + " 的调试守卫必须写在 namespace 之前");
                Assert.That(text.TrimEnd(), Does.EndWith("#endif // UNITY_EDITOR || DEVELOPMENT_BUILD"),
                    path + " 的调试守卫必须闭合在文件末尾（否则只包住了半截类型）");
            }
        }

        /// <summary>
        /// 运行层对调试类型的引用必须都落在守卫里。
        /// 输入：无；输出：断言结果。
        /// 为什么：只要漏一处（例如 GameBootstrap 的序列化字段），正式包编译就会失败或把类型强行带进包。
        /// </summary>
        [Test]
        public void RuntimeReferencesToDebugTypesStayInsideDebugGuard()
        {
            AssertEveryOccurrenceGuarded("Assets/Spotlight/Runtime/Game/GameBootstrap.cs", "ConfigDebugPanel");
            AssertEveryOccurrenceGuarded("Assets/Spotlight/Runtime/UI/HudView.cs", "配置/调试");
            AssertEveryOccurrenceGuarded("Assets/Spotlight/Runtime/UI/HudView.cs", "配置 / 调试");
            AssertEveryOccurrenceGuarded("Assets/Spotlight/Runtime/UI/HudView.cs", "quick-debug-button");
        }

        /// <summary>
        /// 随包发布的配置资产不得保存工作簿路径、摘要与编译时间。
        /// 输入：无；输出：断言结果。
        /// 为什么：主配置在 Resources/ 下、卡种资产被它引用，都会进包；
        /// 这三项会把开发期路径与编译时间直接写进 sharedassets。
        /// </summary>
        [Test]
        public void ShippedConfigAssetsDoNotPersistWorkbookMetadata()
        {
            var forbiddenFieldNames = new[] { "sourceworkbook", "sourcesheet", "sourcehash", "compiledatutc" };
            foreach (var type in new[] { typeof(SpotlightConfigAsset), typeof(CardDefinitionSO) })
            {
                var serializedFields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Where(field => field.IsDefined(typeof(UnityEngine.SerializeField), false))
                    .Select(field => field.Name.ToLowerInvariant())
                    .ToArray();
                foreach (var forbidden in forbiddenFieldNames)
                    Assert.That(serializedFields.Any(name => name.Contains(forbidden)), Is.False,
                        type.Name + " 又出现了会进包的溯源字段：" + forbidden);
            }

            Assert.That(typeof(GeneratedConfigAttribute).GetProperties().Select(p => p.Name), Does.Not.Contain("SourcePath"),
                "GeneratedConfigAttribute 不得再携带源文件路径：特性参数会被编译进正式包程序集");

            var generatedRoot = "Assets/Spotlight/Generated";
            var assets = Directory.GetFiles(generatedRoot, "*.asset", SearchOption.AllDirectories)
                // ConfigManifest 是唯一允许保留溯源信息的地方：它不在 Resources/ 下、运行时不被引用，
                // 因此不进包；门禁要靠它判断“工作簿改过但没重新编译”。
                .Where(path => !path.EndsWith("ConfigManifest.asset"))
                .ToArray();
            Assert.That(assets.Length, Is.GreaterThan(0), "找不到生成资产，请先执行 Tools/Spotlight/编译全部配置");
            foreach (var asset in assets)
            {
                var text = File.ReadAllText(asset);
                Assert.That(text, Does.Not.Contain(".xlsx"), asset + " 仍记录了工作簿路径");
                Assert.That(text, Does.Not.Contain("sourceHash"), asset + " 仍记录了工作簿摘要");
            }
        }

        /// <summary>
        /// 构建后清理器删除 Burst 调试目录与符号文件，同时保留可运行产物。
        /// 输入：临时输出目录夹具；输出：断言结果。
        /// 为什么：调试目录名里的 DoNotShip 是 Unity 自己写的，但打包脚本会把它一起压缩上传，
        /// 里面是构建机的绝对路径（Temp/Burst、Library/Burst、编辑器安装位置）。
        /// </summary>
        [Test]
        public void BuildCleanupRemovesBurstDebugArtifactsAndKeepsPlayerFiles()
        {
            var root = Path.Combine(Path.GetTempPath(), "spotlight-build-cleanup-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var debugDirectory = Path.Combine(root, "ProductName" + BuildArtifactCleanup.BurstDebugDirectorySuffix, "Data", "Plugins", "x86_64");
                var pluginDirectory = Path.Combine(root, "ProductName_Data", "Plugins", "x86_64");
                Directory.CreateDirectory(debugDirectory);
                Directory.CreateDirectory(pluginDirectory);
                File.WriteAllText(Path.Combine(debugDirectory, "lib_burst_generated.txt"), "--temp-folder=D:\\build\\Temp\\Burst");
                var requiredDll = Path.Combine(pluginDirectory, "lib_burst_generated.dll");
                File.WriteAllBytes(requiredDll, new byte[] { 0x4D, 0x5A });
                var strayPdb = Path.Combine(pluginDirectory, BuildArtifactCleanup.BurstDebugPdbFileName);
                File.WriteAllBytes(strayPdb, new byte[] { 0x4D, 0x53 });
                var executable = Path.Combine(root, "ProductName.exe");
                File.WriteAllBytes(executable, new byte[] { 0x4D, 0x5A });

                var removed = BuildArtifactCleanup.CleanBuildArtifacts(root);

                Assert.That(Directory.Exists(Path.Combine(root, "ProductName" + BuildArtifactCleanup.BurstDebugDirectorySuffix)), Is.False,
                    "Burst 调试目录必须被删除");
                Assert.That(File.Exists(strayPdb), Is.False, "Burst 符号文件必须被删除");
                Assert.That(File.Exists(requiredDll), Is.True, "lib_burst_generated.dll 是运行时必需的，不能删");
                Assert.That(File.Exists(executable), Is.True, "可执行文件不能删");
                Assert.That(removed, Has.Count.EqualTo(2), "应报告两处清理结果：" + string.Join("、", removed));
                Assert.That(removed, Has.Some.Contains(BuildArtifactCleanup.BurstDebugDirectorySuffix));
                Assert.That(removed, Has.Some.Contains(BuildArtifactCleanup.BurstDebugPdbFileName));

                Assert.That(BuildArtifactCleanup.CleanBuildArtifacts(root), Is.Empty, "重复清理必须是幂等的");
                Assert.That(BuildArtifactCleanup.CleanBuildArtifacts(null), Is.Empty, "空路径不能抛异常");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        /// <summary>
        /// 断言某个文件里 needle 的每次出现都位于调试守卫内部。
        /// 输入：相对路径与目标文本；输出：无（未守卫时断言失败）。
        /// 为什么用“逐行维护条件编译栈”而不是正则：守卫可能嵌套在其它 #if 里，
        /// 只看相邻行会把“外层已经 #endif 掉了”的引用误判为安全。
        /// </summary>
        static void AssertEveryOccurrenceGuarded(string relativePath, string needle)
        {
            var stack = new Stack<bool>();
            var lineNumber = 0;
            foreach (var line in File.ReadAllLines(relativePath))
            {
                lineNumber++;
                var trimmed = line.Trim();
                if (trimmed.StartsWith("#if", System.StringComparison.Ordinal))
                {
                    stack.Push(string.Equals(trimmed, DebugGuard, System.StringComparison.Ordinal));
                    continue;
                }
                if (trimmed.StartsWith("#endif", System.StringComparison.Ordinal))
                {
                    if (stack.Count > 0) stack.Pop();
                    continue;
                }
                if (!line.Contains(needle)) continue;
                Assert.That(stack.Contains(true), Is.True,
                    $"{relativePath}:{lineNumber} 引用了 {needle}，但没有包在 {DebugGuard} 里");
            }
        }
    }
}
