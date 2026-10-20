// 命令行版本覆盖：把发版流程传进来的版本号写进包体，并在构建结束后原样还原。
// 为什么需要：CI（.github/workflows/release-build.yml）里的 Release 标签、包内 bundleVersion
// 与 Android versionCode 必须同源，否则“Release 上写 v0.2.1、装上去却是 0.1.0”。
// 而 Unity 自身并不认识 -buildVersion / -androidVersionCode（那是 game-ci 构建脚本的约定参数），
// 本工程又用自己的 BuildEntry 当构建入口，所以由这里读取这两个参数并落到 PlayerSettings。
// 参数名沿用 game-ci 的既有约定，不另造一套，避免 CI 里出现两套命名。
using System;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// 命令行传入的包体版本覆盖项。
    /// 输入：Unity 命令行参数数组（如 <c>-buildVersion 0.2.1 -androidVersionCode 201</c>）。
    /// 输出：<see cref="BundleVersion"/>（写入 PlayerSettings.bundleVersion）与
    /// <see cref="AndroidVersionCode"/>（写入 PlayerSettings.Android.bundleVersionCode）；
    /// 两者都可以缺省——缺省的项保持工程原有设置不变，因此本地菜单打包完全不传参数也不受影响。
    /// 为什么不做成“只读配置”而必须临时写 PlayerSettings：包体版本是 Unity 序列化进产物里的，
    /// 只能在构建前改、构建后还原；直接写进 ProjectSettings 会让版本号跟随每次发版在版本库里抖动。
    /// </summary>
    public sealed class BuildVersionOverride
    {
        /// <summary>包体版本参数名（game-ci 构建脚本传的就是这个）。</summary>
        public const string BundleVersionArgument = "-buildVersion";

        /// <summary>Android versionCode 参数名（game-ci 构建脚本传的就是这个）。</summary>
        public const string AndroidVersionCodeArgument = "-androidVersionCode";

        /// <summary>
        /// 版本格式：严格三段数字。
        /// 为什么严格三段：同一份数字还要给 Android versionName 与 Release 标签用，
        /// 缺段（0.2）或带后缀（0.2.1-rc1）会让“三处版本一致”这个前提当场失效。
        /// </summary>
        static readonly Regex VersionPattern = new Regex(@"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant);

        /// <summary>要写入 PlayerSettings.bundleVersion 的版本；空字符串表示本次不覆盖。</summary>
        public string BundleVersion { get; private set; }

        /// <summary>要写入 PlayerSettings.Android.bundleVersionCode 的版本号；0 表示本次不覆盖。</summary>
        public int AndroidVersionCode { get; private set; }

        /// <summary>是否请求了 bundleVersion 覆盖。</summary>
        public bool HasBundleVersion
        {
            get { return !string.IsNullOrEmpty(BundleVersion); }
        }

        /// <summary>是否请求了 Android versionCode 覆盖。</summary>
        public bool HasAndroidVersionCode
        {
            get { return AndroidVersionCode > 0; }
        }

        /// <summary>
        /// 构造覆盖项。输入：已校验的版本与 versionCode；输出：不可变实例。
        /// 为什么构造器私有：唯一合法入口是 <see cref="TryParse"/>，避免绕过校验造出非法版本。
        /// </summary>
        BuildVersionOverride(string bundleVersion, int androidVersionCode)
        {
            BundleVersion = bundleVersion ?? string.Empty;
            AndroidVersionCode = androidVersionCode;
        }

        /// <summary>
        /// 解析命令行参数。
        /// 输入：Unity 命令行参数数组（可含 null/空串，会被跳过）。
        /// 输出：true 表示参数合法（<paramref name="result"/> 为 null 表示“没有请求覆盖”），
        /// false 表示参数存在但非法（<paramref name="error"/> 为可读原因）。
        /// 为什么不抛异常：调用方是打包流程，需要把原因写进日志并以退出码 1 结束，
        /// 而不是靠异常栈让 CI 里只剩一句 “Build failed”。
        /// </summary>
        public static bool TryParse(string[] arguments, out BuildVersionOverride result, out string error)
        {
            result = null;
            error = null;
            if (arguments == null || arguments.Length == 0) return true;

            var bundleVersion = string.Empty;
            var androidVersionCode = 0;

            string rawVersion;
            if (TryGetValue(arguments, BundleVersionArgument, out rawVersion))
            {
                var trimmed = rawVersion.Trim();
                if (!VersionPattern.IsMatch(trimmed))
                {
                    error = $"{BundleVersionArgument} 必须是 x.y.z 形式的三段数字，实际是 \"{rawVersion}\"。";
                    return false;
                }

                bundleVersion = trimmed;
            }

            string rawCode;
            if (TryGetValue(arguments, AndroidVersionCodeArgument, out rawCode))
            {
                var trimmed = rawCode.Trim();
                if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out androidVersionCode)
                    || androidVersionCode <= 0)
                {
                    error = $"{AndroidVersionCodeArgument} 必须是正整数，实际是 \"{rawCode}\"。" +
                            "Android 会拒绝 versionCode 相同或更小的包覆盖安装。";
                    return false;
                }
            }

            // 两个参数都没出现：属于本地菜单打包的常规情况，不产生覆盖项也不算错误。
            if (string.IsNullOrEmpty(bundleVersion) && androidVersionCode <= 0) return true;

            result = new BuildVersionOverride(bundleVersion, androidVersionCode);
            return true;
        }

        /// <summary>
        /// 从当前进程的命令行取覆盖项。
        /// 输入：无（内部读 Environment.GetCommandLineArgs()）。
        /// 输出：覆盖项；没有请求时为 null。参数非法时同样返回 null，原因写到 <paramref name="error"/>。
        /// </summary>
        public static BuildVersionOverride FromCommandLine(out string error)
        {
            BuildVersionOverride parsed;
            if (TryParse(Environment.GetCommandLineArgs(), out parsed, out error)) return parsed;
            return null;
        }

        /// <summary>
        /// 把覆盖项写进 PlayerSettings。
        /// 输入：无；输出：可释放作用域，Dispose 时把 bundleVersion 与 versionCode 还原成构建前的值。
        /// 为什么必须还原：不还原会让 CI 的 ProjectSettings.asset 变脏（第二次平台构建的
        /// “分支不干净”检查会拦住它），也会让本机编辑器在打包后残留一个 release 版本号。
        /// </summary>
        public IDisposable Apply()
        {
            var restore = new RestoreScope(PlayerSettings.bundleVersion, PlayerSettings.Android.bundleVersionCode);
            if (HasBundleVersion) PlayerSettings.bundleVersion = BundleVersion;
            if (HasAndroidVersionCode) PlayerSettings.Android.bundleVersionCode = AndroidVersionCode;
            Debug.Log($"包体版本覆盖：bundleVersion={PlayerSettings.bundleVersion}、" +
                      $"Android versionCode={PlayerSettings.Android.bundleVersionCode}（构建结束后还原）。");
            return restore;
        }

        /// <summary>
        /// 在命令行参数里找一个“开关 + 取值”或“开关=取值”形式的参数。
        /// 输入：参数数组、参数名（带不带前导 - 都可以）；输出：找到时返回 true 并把取值写进 value。
        /// 为什么同时支持两种写法：Unity 官方与 game-ci 各自用不同风格传参，
        /// 只认一种会在换一个构建入口后静默失效（版本覆盖悄悄不生效，包还是老版本号）。
        /// </summary>
        static bool TryGetValue(string[] arguments, string name, out string value)
        {
            value = string.Empty;
            var key = NormalizeKey(name);
            if (key.Length == 0) return false;

            for (var i = 0; i < arguments.Length; i++)
            {
                var argument = arguments[i];
                if (string.IsNullOrEmpty(argument)) continue;

                var normalized = NormalizeKey(argument);
                if (normalized == key)
                {
                    if (i + 1 >= arguments.Length) return false;
                    value = arguments[i + 1] ?? string.Empty;
                    return !string.IsNullOrWhiteSpace(value);
                }

                var prefix = key + "=";
                if (normalized.StartsWith(prefix, StringComparison.Ordinal))
                {
                    value = normalized.Substring(prefix.Length);
                    return !string.IsNullOrWhiteSpace(value);
                }
            }

            return false;
        }

        /// <summary>
        /// 去掉参数的前导横线，使 -x / --x 视为同一个键。
        /// 输入：原始参数或参数名；输出：不含前导 - 的小写敏感原样字符串。
        /// </summary>
        static string NormalizeKey(string argument)
        {
            return argument == null ? string.Empty : argument.TrimStart('-');
        }

        /// <summary>
        /// 还原作用域：记录构建前的版本设置，Dispose 时写回。
        /// 输入：构造时传入的原值；输出：无（Dispose 恢复）。
        /// </summary>
        sealed class RestoreScope : IDisposable
        {
            readonly string _bundleVersion;
            readonly int _androidVersionCode;

            /// <summary>记录原值。输入：构建前的 bundleVersion 与 Android versionCode；输出：无。</summary>
            public RestoreScope(string bundleVersion, int androidVersionCode)
            {
                _bundleVersion = bundleVersion;
                _androidVersionCode = androidVersionCode;
            }

            /// <summary>写回构造时记录的原值。输入：无；输出：无。</summary>
            public void Dispose()
            {
                PlayerSettings.bundleVersion = _bundleVersion;
                PlayerSettings.Android.bundleVersionCode = _androidVersionCode;
            }
        }
    }
}
