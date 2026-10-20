using UnityEngine;

namespace Spotlight
{
    /// <summary>独立二维音效入口；调用方提供 AudioClip，播放音量受主音量和音效滑条共同控制。</summary>
    [DisallowMultipleComponent]
    public sealed class SoundEffectsPlayer : MonoBehaviour
    {
        [SerializeField] AudioSource _source;
        /// <summary>只读音源，供安装器和测试验证通道隔离。</summary>
        public AudioSource Source => _source;
        /// <summary>启用时订阅音量并立即应用保存值。</summary>
        void OnEnable() { GameAudioSettings.EnsureLoaded(); GameAudioSettings.Changed += ApplyVolume; ApplyVolume(); }
        /// <summary>禁用时停止包括 OneShot 在内的所有音效，并取消订阅。</summary>
        void OnDisable() { GameAudioSettings.Changed -= ApplyVolume; if (_source != null) _source.Stop(); }
        /// <summary>绑定专用音效源，避免音乐与音效共享音量或播放位置。</summary>
        public void Configure(AudioSource source)
        {
            if (_source != null) _source.Stop();
            _source = source;
            if (_source == null) return;
            _source.playOnAwake = false; _source.loop = false; _source.spatialBlend = 0;
            ApplyVolume();
        }
        /// <summary>播放调用方提供的独立音效；空素材/禁用状态不播放，本次不附带任何正式音效素材。</summary>
        public void Play(AudioClip clip) { if (isActiveAndEnabled && _source != null && clip != null) _source.PlayOneShot(clip); }
        /// <summary>应用音效分类音量，保持音乐通道不变。</summary>
        void ApplyVolume() { if (_source != null) _source.volume = GameAudioSettings.Sound; }
    }
}
