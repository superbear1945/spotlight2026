// 运行时配置加载入口。
// 为什么需要单独一层：场景、调试面板与测试需要同一套“先取资产、再建配置、失败时给出可读原因”的流程，
// 避免各处自行 Resources.Load 或忽略校验异常。
using System;
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 配置加载工具。
    /// 输入：可选的显式配置资产引用（通常由 Bootstrap 在 Inspector 上指定）；
    /// 输出：可用的 GameConfiguration，或失败原因文本。
    /// 查找顺序：显式引用 → Resources/SpotlightConfig。
    /// </summary>
    public static class ConfigLoader
    {
        /// <summary>Resources 目录下的默认配置资产名（不含扩展名）。</summary>
        public const string DefaultResourcePath = "SpotlightConfig";

        /// <summary>
        /// 获取配置资产。
        /// 输入：显式引用，可为 null；输出：配置资产，找不到时返回 null。
        /// </summary>
        public static SpotlightConfigAsset LoadAsset(SpotlightConfigAsset explicitAsset = null)
        {
            if (explicitAsset != null) return explicitAsset;
            return Resources.Load<SpotlightConfigAsset>(DefaultResourcePath);
        }

        /// <summary>
        /// 建立本局配置并汇总失败原因。
        /// 输入：显式引用，可为 null；输出：成功标志、配置对象与错误文本。
        /// 为什么返回错误文本而不是抛出：Bootstrap 需要把失败原因显示在界面上并阻止开局，
        /// 而不是让场景在异常中断中停在半初始化状态。
        /// </summary>
        public static bool TryCreateConfiguration(out GameConfiguration configuration, out string error, SpotlightConfigAsset explicitAsset = null)
        {
            configuration = null;
            error = null;
            var asset = LoadAsset(explicitAsset);
            if (asset == null)
            {
                error = "未找到配置资产，请先执行 Spotlight/编译全部配置，并确认 SpotlightConfig 位于 Resources 目录";
                return false;
            }
            try
            {
                configuration = asset.CreateConfiguration();
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }
    }
}
