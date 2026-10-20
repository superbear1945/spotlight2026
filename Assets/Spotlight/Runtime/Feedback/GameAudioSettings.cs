using System;
using UnityEngine;

namespace Spotlight
{
    /// <summary>
    /// 玩家音量的唯一运行时入口。输入为 0～1 的音量，输出为主监听器音量及分类音量变更事件。
    /// 沿用既有 PlayerPrefs 键；设置只属于玩家偏好，不修改 Excel 生成的只读配置。
    /// </summary>
    public static class GameAudioSettings
    {
        /// <summary>现有主音量保存键。</summary>
        public const string MasterPreferenceKey = "Spotlight.MasterVolume";
        /// <summary>现有音乐音量保存键。</summary>
        public const string MusicPreferenceKey = "Spotlight.MusicVolume";
        /// <summary>现有音效音量保存键。</summary>
        public const string SoundPreferenceKey = "Spotlight.SoundVolume";
        static bool _loaded;
        static float _master, _music, _sound;
        /// <summary>分类音量变化通知；播放器必须对称订阅/取消订阅。</summary>
        public static event Action Changed;
        /// <summary>当前主音量，不重复乘入 AudioSource。</summary>
        public static float Master { get { EnsureLoaded(); return _master; } }
        /// <summary>音乐分类音量，播放器额外乘以淡出比例。</summary>
        public static float Music { get { EnsureLoaded(); return _music; } }
        /// <summary>音效分类音量，与音乐独立。</summary>
        public static float Sound { get { EnsureLoaded(); return _sound; } }

        /// <summary>每次进入运行模式清理静态事件，支持关闭 Domain Reload 的编辑器设置。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSession() { _loaded = false; Changed = null; }

        /// <summary>场景创建前加载偏好，确保第一段音乐已经使用上次保存的音量。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Initialize() => EnsureLoaded();

        /// <summary>按需读取偏好；供未经过启动回调的编辑器预览/测试使用。</summary>
        public static void EnsureLoaded() { if (!_loaded) Reload(); }

        /// <summary>从保存值恢复三个通道；没有记录时默认 80% / 60% / 80%，并通知已有播放器。</summary>
        public static void Reload()
        {
            _loaded = true;
            _master = Normalize(PlayerPrefs.GetFloat(MasterPreferenceKey, .8f), .8f);
            _music = Normalize(PlayerPrefs.GetFloat(MusicPreferenceKey, .6f), .6f);
            _sound = Normalize(PlayerPrefs.GetFloat(SoundPreferenceKey, .8f), .8f);
            Apply();
        }

        /// <summary>实时设置主音量；输入为线性 0～1 比例，不在每次拖动时写盘。</summary>
        public static void SetMaster(float value) { EnsureLoaded(); _master = Normalize(value, .8f); Apply(); }
        /// <summary>实时设置音乐音量；不改变音乐播放位置或淡出进度。</summary>
        public static void SetMusic(float value) { EnsureLoaded(); _music = Normalize(value, .6f); Apply(); }
        /// <summary>实时设置音效音量；不会改变音乐音量。</summary>
        public static void SetSound(float value) { EnsureLoaded(); _sound = Normalize(value, .8f); Apply(); }

        /// <summary>恢复设置面板原有的三个 100% 默认值，并立即通知播放器。</summary>
        public static void ResetVolumes() { _loaded = true; _master = _music = _sound = 1f; Apply(); }

        /// <summary>保存当前音量，供关闭设置或退出时统一调用。</summary>
        public static void Save()
        {
            EnsureLoaded();
            PlayerPrefs.SetFloat(MasterPreferenceKey, _master);
            PlayerPrefs.SetFloat(MusicPreferenceKey, _music);
            PlayerPrefs.SetFloat(SoundPreferenceKey, _sound);
            PlayerPrefs.Save();
        }

        /// <summary>统一处理损坏的偏好值，避免 NaN 进入 Unity 音频系统。</summary>
        static float Normalize(float value, float fallback) => float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp01(value);
        /// <summary>将主音量应用到全局监听器，分类音量通过事件应用到各自音源。</summary>
        static void Apply() { AudioListener.volume = _master; Changed?.Invoke(); }
    }
}
