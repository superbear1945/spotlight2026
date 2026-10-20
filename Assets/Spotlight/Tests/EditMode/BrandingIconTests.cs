// 品牌图标的回归测试（EditMode）。
// 背景：图标槽留空时 Unity 不会报错，只会静默打出自带 logo 的包（Android 启动器图标、Windows exe 图标），
// 属于“测试与构建都成功、装到手机上才发现不对”的问题，因此必须由测试把“源图存在”与“图标槽已写入”固定下来。
// 输入：Assets/Imported/SpotlightArt/Branding/游戏图标2.png 与 PlayerSettings；输出：NUnit 断言结果。
using System.IO;
using System.Linq;
using NUnit.Framework;
using Spotlight.EditorTools;
using UnityEditor;
using UnityEngine;

namespace Spotlight.Tests
{
    /// <summary>品牌图标的源图契约、写入行为与构建期兜底接线。输入：图标资产与 PlayerSettings；输出：断言结果。</summary>
    public sealed class BrandingIconTests
    {
        /// <summary>构建门禁源码：必须在构建前兜底写入图标，否则 CI 打出的包又回到 Unity 默认图标。</summary>
        const string BuildGateSourcePath = "Assets/Spotlight/Editor/ConfigBuildGate.cs";

        /// <summary>
        /// 源图必须是工程里可加载的方形贴图。
        /// 输入：无；输出：断言结果。
        /// 为什么断言方形与尺寸：图标槽会按正方形裁剪，非方图会被拉伸变形；过小的源图在 432px 的高密度档会被放大糊掉。
        /// </summary>
        [Test]
        public void IconAsset_ExistsAndIsSquareTexture()
        {
            var icon = BrandingIconSetup.LoadIcon();
            Assert.That(icon, Is.Not.Null, "找不到品牌图标：" + BrandingIconSetup.IconAssetPath);
            Assert.That(icon.width, Is.EqualTo(icon.height), "品牌图标必须是正方形，否则各档图标的缩放会变形");
            Assert.That(icon.width, Is.GreaterThanOrEqualTo(128), "品牌图标源图至少 128px，否则高密度档会明显模糊");
        }

        /// <summary>
        /// 写入后每个目标组的每个图标槽都指向品牌图。
        /// 输入：无；输出：断言结果。
        /// 为什么逐槽断言：SetIconsForTargetGroup 只在“所有槽都填”时才会被系统当作完整图标集，
        /// 留空的槽会让该尺寸回退成 Unity 默认图，最终表现是“有的机型图标对、有的机型还是 Unity 图标”。
        /// </summary>
        [Test]
        public void Apply_WritesBrandIconIntoEveryTargetGroupSlot()
        {
            string message;
            BrandingIconSetup.Apply(out message);

            var icon = BrandingIconSetup.LoadIcon();
            Assert.That(icon, Is.Not.Null, message);
            Assert.That(BrandingIconSetup.IsApplied(), Is.True, "PlayerSettings 中仍存在不是品牌图的图标槽：" + message);

            foreach (var group in BrandingIconSetup.TargetGroups)
            {
                var slotCount = BrandingIconSetup.GetSlotCount(group);
                Assert.That(slotCount, Is.GreaterThan(0), $"{group} 没有图标槽，图标不会进包");
                var icons = PlayerSettings.GetIconsForTargetGroup(group, BrandingIconSetup.Kind);
                Assert.That(icons, Is.Not.Null.And.Length.EqualTo(slotCount), $"{group} 的图标槽数量与引擎要求不一致");
                Assert.That(icons.All(texture => texture == icon), Is.True, $"{group} 的图标槽未全部指向品牌图");
            }
        }

        /// <summary>
        /// 重复写入必须幂等：第二次调用不再声明“已改动”。
        /// 输入：无；输出：断言结果。
        /// 为什么：构建期兜底每次打包都会调用，若每次都判定为改动就会反复重写 ProjectSettings、
        /// 让版本库出现与本次改动无关的差异。
        /// </summary>
        [Test]
        public void Apply_IsIdempotent()
        {
            BrandingIconSetup.Apply(out _);
            var changedAgain = BrandingIconSetup.Apply(out var message);
            Assert.That(changedAgain, Is.False, "图标已是品牌图时不应再次写入：" + message);
            Assert.That(BrandingIconSetup.IsApplied(), Is.True);
        }

        /// <summary>
        /// 构建前门禁必须调用图标兜底。
        /// 输入：无；输出：断言结果。
        /// 为什么做源码断言：真正的 IPreprocessBuildWithReport 回调要跑一次完整打包才能触发（耗时且属于手工验证），
        /// 但“兜底调用被删掉”这件事可以用一行文本比对守住，成本远低于等 CI 打出包再发现图标仍是 Unity 默认图。
        /// </summary>
        [Test]
        public void PreprocessBuild_AppliesBrandingIcon()
        {
            Assert.That(File.Exists(BuildGateSourcePath), Is.True, "找不到构建门禁源码：" + BuildGateSourcePath);
            var source = File.ReadAllText(BuildGateSourcePath);
            Assert.That(source, Does.Contain("BrandingIconSetup.ApplyIfNeeded()"),
                "ConfigBuildProcessor.OnPreprocessBuild 必须在构建前调用 BrandingIconSetup.ApplyIfNeeded()");
        }
    }
}
