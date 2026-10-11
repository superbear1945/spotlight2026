// 中文字体与 UI Toolkit 文本设置生成工具。
// 为什么需要：Unity 内置字体与 TMP 默认字体都不包含中文字形，
// 若不显式配置，UGUI 文本与 UI Toolkit 标签都会显示为方框，无法满足“中文显示完整”的验收要求。
// 注意两套字体类型：UGUI/TMP 使用 TMPro.TMP_FontAsset；UI Toolkit 使用 UnityEngine.TextCore.Text.FontAsset。
// 本工具同时生成两者，避免出现“棋盘中文正常、HUD 中文是方框”的不一致。
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// 中文字体资产生成器。
    /// 输入：工程内 `Assets/Spotlight/Fonts/SimHei.ttf`；
    /// 输出：TMP 字体资产（UGUI 用）、TextCore 字体资产（UI Toolkit 用）、
    ///       PanelTextSettings 与 PanelSettings（UIDocument 用）。
    /// </summary>
    public static class CjkFontSetup
    {
        /// <summary>随工程携带的字体文件路径。</summary>
        public const string FontPath = "Assets/Spotlight/Fonts/SimHei.ttf";
        /// <summary>生成的 TMP 字体资产路径（UGUI 文本使用）。</summary>
        public const string FontAssetPath = "Assets/Spotlight/Fonts/SimHei SDF.asset";
        /// <summary>生成的 TextCore 字体资产路径（UI Toolkit 文本使用）。</summary>
        public const string TextCoreFontAssetPath = "Assets/Spotlight/Fonts/SimHei TextCore.asset";
        /// <summary>生成的 UI Toolkit 文本设置资产路径。</summary>
        public const string TextSettingsPath = "Assets/Spotlight/UI/SpotlightTextSettings.asset";
        /// <summary>生成的 UIDocument 面板设置资产路径。</summary>
        public const string PanelSettingsPath = "Assets/Spotlight/UI/SpotlightPanelSettings.asset";

        /// <summary>TMP 基础资源包的包内路径（包含默认着色器与 TMP Settings）。</summary>
        const string TmpEssentialPackage = "Packages/com.unity.textmeshpro/Package Resources/TMP Essential Resources.unitypackage";

        /// <summary>
        /// 确保 TMP 基础资源已导入。
        /// 输入：无；输出：是否已就绪。
        /// 为什么需要：TMP_FontAsset.CreateFontAsset 依赖 TextMeshPro/Distance Field 着色器，
        /// 未导入基础资源时会抛 ArgumentNullException(shader)，导致字体创建与配置发布全部失败。
        /// </summary>
        public static bool EnsureTmpEssentials()
        {
            if (Shader.Find("TextMeshPro/Distance Field") != null) return true;
            if (!File.Exists(Path.GetFullPath(TmpEssentialPackage)))
            {
                Debug.LogError($"找不到 TMP 基础资源包：{TmpEssentialPackage}，请在 Window/TextMeshPro 中手动导入。");
                return false;
            }
            AssetDatabase.ImportPackage(TmpEssentialPackage, false);
            AssetDatabase.Refresh();
            var ready = Shader.Find("TextMeshPro/Distance Field") != null;
            if (!ready) Debug.LogError("TMP 基础资源导入后仍未找到默认着色器，请重启编辑器后重试。");
            return ready;
        }

        /// <summary>
        /// 确保全部字体相关资产存在。
        /// 输入：无；输出：TMP 字体资产（失败时返回 null）。
        /// 已存在的资产不会被覆盖，避免销毁手工调整过的图集参数。
        /// 菜单入口统一放在 SpotlightMenu，避免同一个菜单名被注册两次。
        /// </summary>
        public static TMP_FontAsset EnsureFontAssets()
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            if (font == null)
            {
                Debug.LogError($"未找到字体文件 {FontPath}，请先放入一个包含中文字形的 TrueType 字体。");
                return null;
            }
            EnsureTmpEssentials();
            var fontAsset = EnsureTmpFontAsset(font);
            var textCoreAsset = EnsureTextCoreFontAsset(font);
            if (textCoreAsset != null) EnsureTextSettings(textCoreAsset);
            EnsurePanelSettings();
            AssetDatabase.Refresh();
            return fontAsset;
        }

        /// <summary>
        /// 生成或复用 TMP 字体资产。
        /// 输入：TrueType 字体；输出：TMP_FontAsset（UGUI 文本与卡面使用）。
        /// </summary>
        static TMP_FontAsset EnsureTmpFontAsset(Font font)
        {
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
            if (existing != null) return existing;
            // 动态字体资产按需生成字形，避免一次性烘焙全部中文字符造成巨大图集。
            TMP_FontAsset created;
            try
            {
                created = TMP_FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024, TMPro.AtlasPopulationMode.Dynamic);
            }
            catch (System.Exception e)
            {
                Debug.LogError("创建 TMP 字体资产失败（通常是缺少 TMP 基础资源）：" + e.Message);
                return null;
            }
            if (created == null)
            {
                Debug.LogError("创建 TMP 字体资产失败。");
                return null;
            }
            EnsureDirectory(FontAssetPath);
            AssetDatabase.CreateAsset(created, FontAssetPath);
            SaveFontSubAssets(created, created.atlasTextures, created.material);
            AssetDatabase.SaveAssets();
            return created;
        }

        /// <summary>
        /// 把字体资产的图集纹理与材质保存为子资产。
        /// 输入：宿主资产、图集纹理数组与材质；输出：无。
        /// 为什么必需：CreateFontAsset 返回的纹理/材质默认只是内存对象，不随资产落盘，
        /// 重新加载后会出现“Font Atlas Texture ... is missing”，导致中文全部不显示。
        /// </summary>
        static void SaveFontSubAssets(UnityEngine.Object host, Texture2D[] atlasTextures, Material material)
        {
            if (atlasTextures != null)
            {
                for (var i = 0; i < atlasTextures.Length; i++)
                {
                    var texture = atlasTextures[i];
                    if (texture == null || AssetDatabase.Contains(texture)) continue;
                    texture.name = host.name + " Atlas " + i;
                    AssetDatabase.AddObjectToAsset(texture, host);
                }
            }
            if (material != null && !AssetDatabase.Contains(material))
            {
                material.name = host.name + " Material";
                AssetDatabase.AddObjectToAsset(material, host);
            }
        }

        /// <summary>
        /// 生成或复用 TextCore 字体资产。
        /// 输入：TrueType 字体；输出：FontAsset（UI Toolkit 文本使用）。
        /// 为什么不能复用 TMP 字体资产：Turnip/TextCore 在 2022.3 中与 TMP 是两套类型，互不兼容。
        /// </summary>
        static FontAsset EnsureTextCoreFontAsset(Font font)
        {
            var existing = AssetDatabase.LoadAssetAtPath<FontAsset>(TextCoreFontAssetPath);
            if (existing != null) return existing;
            FontAsset created;
            try
            {
                created = FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024, UnityEngine.TextCore.Text.AtlasPopulationMode.Dynamic, true);
            }
            catch (System.Exception e)
            {
                Debug.LogError("创建 TextCore 字体资产失败，UI Toolkit 中文可能显示为方框：" + e.Message);
                return null;
            }
            if (created == null)
            {
                Debug.LogError("创建 TextCore 字体资产失败，UI Toolkit 中文可能显示为方框。");
                return null;
            }
            EnsureDirectory(TextCoreFontAssetPath);
            AssetDatabase.CreateAsset(created, TextCoreFontAssetPath);
            SaveFontSubAssets(created, created.atlasTextures, created.material);
            AssetDatabase.SaveAssets();
            return created;
        }

        /// <summary>
        /// 确保 UI Toolkit 文本设置存在并把默认字体指向中文字体。
        /// 输入：TextCore 字体资产；输出：无。
        /// 用反射写属性而不是直接赋值：TextSettings 的 setter 可见性在不同版本间有差异。
        /// </summary>
        static void EnsureTextSettings(FontAsset textCoreAsset)
        {
            var settings = AssetDatabase.LoadAssetAtPath<PanelTextSettings>(TextSettingsPath);
            if (settings == null)
            {
                EnsureDirectory(TextSettingsPath);
                settings = ScriptableObject.CreateInstance<PanelTextSettings>();
                AssetDatabase.CreateAsset(settings, TextSettingsPath);
            }
            var property = typeof(PanelTextSettings).GetProperty("defaultFontAsset", BindingFlags.Public | BindingFlags.Instance);
            var setter = property?.GetSetMethod(true);
            if (setter == null)
            {
                Debug.LogWarning("无法把中文字体写入 PanelTextSettings.defaultFontAsset，UI Toolkit 中文可能显示为方框。");
                return;
            }
            if (!ReferenceEquals(property.GetValue(settings), textCoreAsset)) setter.Invoke(settings, new object[] { textCoreAsset });
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// 确保 UIDocument 使用的 PanelSettings 存在，并绑定中文文本设置。
        /// 输入：无；输出：无。
        /// 参考分辨率取玩法文档的 1920×1080，与表现配置默认值一致。
        /// </summary>
        static void EnsurePanelSettings()
        {
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (panel == null)
            {
                EnsureDirectory(PanelSettingsPath);
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                panel.referenceResolution = new Vector2Int(1920, 1080);
                panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                AssetDatabase.CreateAsset(panel, PanelSettingsPath);
            }
            var settings = AssetDatabase.LoadAssetAtPath<PanelTextSettings>(TextSettingsPath);
            if (settings == null) return;
            // Unity 2022.3 的 textSettings 是公开字段，GetProperty 会返回 null 并静默漏绑。
            panel.textSettings = settings;
            EditorUtility.SetDirty(panel);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// 检查 HUD 的中文字体引用链，供运行/构建门禁与回归测试复用。
        /// 输入：场景使用的面板设置；输出：全部字体引用问题，空列表表示引用完整。
        /// 为什么需要：TMP 卡牌字体正常并不代表 UI Toolkit 已绑定中文字体，漏绑也可能没有运行时日志。
        /// </summary>
        public static List<string> ValidatePanelFonts(PanelSettings panel)
        {
            var problems = new List<string>();
            const string remedy = "，请执行 Tools/Spotlight/生成中文字体资产";
            if (panel == null) problems.Add("缺少 HUD PanelSettings" + remedy);
            else if (panel.textSettings == null) problems.Add("HUD PanelSettings 未绑定 Text Settings" + remedy);
            else
            {
                var font = panel.textSettings.defaultFontAsset;
                if (font == null) problems.Add("HUD Text Settings 缺少默认中文字体" + remedy);
                else if (font.atlasPopulationMode == UnityEngine.TextCore.Text.AtlasPopulationMode.Dynamic && font.sourceFontFile == null)
                    problems.Add("HUD 动态中文字体缺少源字体文件" + remedy);
            }
            return problems;
        }

        /// <summary>确保资产所在目录存在。输入：Assets 相对路径；输出：无。</summary>
        static void EnsureDirectory(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
        }
    }
}
