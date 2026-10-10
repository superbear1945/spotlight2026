using NUnit.Framework;
using Spotlight.EditorTools;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Spotlight.Tests
{
    /// <summary>
    /// 验证 HUD 字体生成与门禁，防止 TMP 正常而 UI Toolkit 中文缺失的构建再次发布。
    /// 输入为独立面板夹具或项目字体生成入口；输出为引用链及持久化行为断言。
    /// </summary>
    public sealed class CjkFontSetupTests
    {
        /// <summary>
        /// 输入缺失的面板、文本设置与默认字体；断言门禁分别指出断链位置，避免无中文的包通过检查。
        /// 测试只创建内存对象，并在结束时释放，不改场景或正式配置。
        /// </summary>
        [Test]
        public void ValidatePanelFonts_RejectsMissingReferences()
        {
            Assert.That(CjkFontSetup.ValidatePanelFonts(null), Has.Count.EqualTo(1));
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            var textSettings = ScriptableObject.CreateInstance<PanelTextSettings>();
            try
            {
                Assert.That(CjkFontSetup.ValidatePanelFonts(panel)[0], Does.Contain("未绑定 Text Settings"));
                panel.textSettings = textSettings;
                Assert.That(CjkFontSetup.ValidatePanelFonts(panel)[0], Does.Contain("缺少默认中文字体"));
            }
            finally
            {
                Object.DestroyImmediate(panel);
                Object.DestroyImmediate(textSettings);
            }
        }

        /// <summary>
        /// 输入已有但漏绑的项目面板，通过公开字体生成入口修复两次；
        /// 断言绑定被保存、默认中文字体正确且重复执行保留 GUID，覆盖此次实际故障。
        /// finally 恢复调用前引用，以免测试改变用户的资产配置。
        /// </summary>
        [Test]
        public void GenerateCjkFont_RepairsAndPersistsExistingPanelBinding()
        {
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(CjkFontSetup.PanelSettingsPath);
            Assert.That(panel, Is.Not.Null);
            var originalSettings = panel.textSettings;
            var originalGuid = AssetDatabase.AssetPathToGUID(CjkFontSetup.PanelSettingsPath);
            try
            {
                panel.textSettings = null;
                EditorUtility.SetDirty(panel);
                AssetDatabase.SaveAssets();
                SpotlightMenu.GenerateCjkFont();
                SpotlightMenu.GenerateCjkFont();
                Assert.That(AssetDatabase.GetAssetPath(panel.textSettings), Is.EqualTo(CjkFontSetup.TextSettingsPath));
                Assert.That(AssetDatabase.GetAssetPath(panel.textSettings.defaultFontAsset), Is.EqualTo(CjkFontSetup.TextCoreFontAssetPath));
                Assert.That(CjkFontSetup.ValidatePanelFonts(panel), Is.Empty);
                Assert.That(AssetDatabase.AssetPathToGUID(CjkFontSetup.PanelSettingsPath), Is.EqualTo(originalGuid));
                var serializedPanel = new SerializedObject(panel);
                Assert.That(serializedPanel.FindProperty("textSettings").objectReferenceValue, Is.SameAs(panel.textSettings));
                var savedPanel = System.IO.File.ReadAllText(CjkFontSetup.PanelSettingsPath);
                Assert.That(savedPanel, Does.Contain(AssetDatabase.AssetPathToGUID(CjkFontSetup.TextSettingsPath)));
            }
            finally
            {
                panel.textSettings = originalSettings;
                EditorUtility.SetDirty(panel);
                AssetDatabase.SaveAssets();
            }
        }
    }
}
