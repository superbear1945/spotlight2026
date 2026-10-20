// 品牌图标写入工具：把随工程携带的游戏图标写进 Player Settings 的各平台图标槽。
// 为什么需要：PlayerSettings 的图标槽初始为空，空槽会让 Unity 打出带自家 logo 的包
// （Windows 的 exe 图标、Android 启动器图标都会是 Unity 默认图），与品牌外观不符；
// 图标属于“每次打包都必须成立”的工程状态，因此除了菜单与命令行入口之外，
// 还由 ConfigBuildProcessor 在构建前兜底应用一次，避免 ProjectSettings 被重置后无人察觉。
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// 品牌图标设置器。
    /// 输入：<see cref="IconAssetPath"/> 指向的方形 PNG；
    /// 输出：<see cref="TargetGroups"/> 里每个图标目标组的 Application 图标槽都指向该图。
    /// 为什么按 GetIconSizesForTargetGroup 给出的尺寸个数来填槽，而不是写死尺寸表或只传一张图：
    /// · 只传一张图会被引擎拒绝（实测“Standalone: 8 expected while 1 were specified”，且只打警告不报错，
    ///   结果是 Windows 包仍然带 Unity 默认图标，属于最难发现的失败方式）；
    /// · 写死尺寸表会随引擎升级失效，而 GetIconSizesForTargetGroup 直接问引擎要当前平台的槽位数量。
    /// 为什么只填 Application 一种图标类型：Android 的 Adaptive 图标由前景/背景两层组成，
    /// 把同一张方图硬塞进两层只会得到一个被裁成圆形的重复图层；本项目用传统启动器图标
    /// （Unity 在缺 Adaptive 时会用它生成 mipmap-*/ic_launcher.png），外观与包体积都更可控。
    /// </summary>
    public static class BrandingIconSetup
    {
        /// <summary>品牌图标源图路径（方形 PNG，随工程提交）。</summary>
        public const string IconAssetPath = "Assets/Imported/SpotlightArt/Branding/游戏图标2.png";

        /// <summary>写入的图标类型：传统应用图标（启动器图标 / exe 图标）。</summary>
        public const IconKind Kind = IconKind.Application;

        /// <summary>
        /// 需要写入图标的图标目标组。
        /// 本项目只交付 Windows（Standalone）与 Android 两个平台，因此只覆盖这两组；
        /// 新增平台时在这里补一项即可，<see cref="Apply"/> 不需要改动。
        /// </summary>
        public static readonly BuildTargetGroup[] TargetGroups =
        {
            BuildTargetGroup.Standalone,
            BuildTargetGroup.Android,
        };

        /// <summary>
        /// 读取品牌图标。
        /// 输入：无；输出：图标 Texture2D（资产缺失或类型不对时为 null，调用方据此报错而不是静默跳过）。
        /// </summary>
        public static Texture2D LoadIcon()
        {
            return AssetDatabase.LoadAssetAtPath<Texture2D>(IconAssetPath);
        }

        /// <summary>
        /// 读取某个目标组需要的图标槽数量。
        /// 输入：图标目标组；输出：槽位数量（引擎未给出尺寸时为 0）。
        /// 单独成方法是为了让 <see cref="Apply"/> 与测试共用同一份“到底要几个槽”的答案。
        /// </summary>
        public static int GetSlotCount(BuildTargetGroup group)
        {
            var sizes = PlayerSettings.GetIconSizesForTargetGroup(group, Kind);
            return sizes == null ? 0 : sizes.Length;
        }

        /// <summary>
        /// 判断当前 PlayerSettings 是否已经把每个图标槽都指向品牌图标。
        /// 输入：无；输出：true 表示全部槽位已是该图（可用于跳过重复写入与测试断言）。
        /// </summary>
        public static bool IsApplied()
        {
            var icon = LoadIcon();
            if (icon == null) return false;
            foreach (var group in TargetGroups)
            {
                var slotCount = GetSlotCount(group);
                if (slotCount == 0) return false;
                var current = PlayerSettings.GetIconsForTargetGroup(group, Kind);
                if (current == null || current.Length != slotCount) return false;
                if (current.Any(texture => texture != icon)) return false;
            }
            return true;
        }

        /// <summary>
        /// 把品牌图标写入全部目标组的图标槽。
        /// 输入：无；输出：true 表示本次确实改动了设置，false 表示无需改动或图标缺失（<paramref name="message"/> 给出原因）。
        /// 为什么返回“是否改动”：构建前兜底调用时只在真的改动时打印日志，避免每次打包都刷一条无意义的提示；
        /// 已经是品牌图时直接返回 false，保证幂等（重复调用不会产生重复的 ProjectSettings 差异）。
        /// </summary>
        public static bool Apply(out string message)
        {
            var icon = LoadIcon();
            if (icon == null)
            {
                message = $"找不到品牌图标 {IconAssetPath}，请确认该 PNG 已随工程提交并完成导入。";
                return false;
            }

            var changedGroups = new List<string>();
            foreach (var group in TargetGroups)
            {
                var slotCount = GetSlotCount(group);
                if (slotCount == 0)
                {
                    // 引擎没给出该平台的图标尺寸说明这个构建模块没装好，属于环境问题，交给调用方打印后继续。
                    message = $"引擎未给出 {group} 的图标尺寸，无法写入品牌图标（请确认已安装对应平台构建模块）。";
                    return false;
                }

                var current = PlayerSettings.GetIconsForTargetGroup(group, Kind);
                if (current != null && current.Length == slotCount && current.All(texture => texture == icon)) continue;

                var icons = new Texture2D[slotCount];
                for (var i = 0; i < icons.Length; i++) icons[i] = icon;
                PlayerSettings.SetIconsForTargetGroup(group, icons, Kind);
                changedGroups.Add($"{group}（{slotCount} 个槽位）");
            }

            if (changedGroups.Count == 0)
            {
                message = $"品牌图标已是最新：{IconAssetPath}";
                return false;
            }

            AssetDatabase.SaveAssets();
            message = $"已把品牌图标 {IconAssetPath} 写入：" + string.Join("、", changedGroups) + "。";
            return true;
        }

        /// <summary>
        /// 构建前的兜底应用：只在图标缺失或不一致时写入，并把结论打印到 Console。
        /// 输入：无；输出：true 表示本次写入了图标。
        /// 为什么兜底不抛异常：图标不对不会让包体不可玩，把打包卡死反而更难排查；
        /// 缺图时打印错误、继续打包，便于在 CI 日志里直接看到原因。
        /// </summary>
        public static bool ApplyIfNeeded()
        {
            string message;
            var changed = Apply(out message);
            if (LoadIcon() == null) Debug.LogError(message);
            else if (changed) Debug.Log(message);
            return changed;
        }

        /// <summary>
        /// 命令行入口：批处理模式下应用图标后退出。
        /// 输入：无；输出：无（结论打印到 Console）。
        /// 为什么单独提供一个无参静态方法：Unity 的 <c>-executeMethod</c> 只能调用静态无参方法，
        /// 供本地/CI 脚本在不打包的情况下刷一次 ProjectSettings 图标。
        /// </summary>
        public static void ApplyFromCommandLine()
        {
            string message;
            var changed = Apply(out message);
            if (changed) Debug.Log(message);
            else if (LoadIcon() == null) Debug.LogError(message);
            else Debug.Log(message);
        }
    }
}
