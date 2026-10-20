using System.Collections;
using UnityEngine;

namespace Spotlight
{
    /// <summary>音乐表现状态，与规则阶段独立；供帧循环测试和运行诊断观察。</summary>
    public enum MusicPlaybackState { Stopped, Loading, Playing, LoopGap, FadingOut, PhaseGap }

    /// <summary>
    /// Boss 三阶段音乐播放器。输入为已展示快照、只读计时配置和三首音频，输出仅为音频表现。
    /// 自己拥有唯一协程，使用真实时间；不会被 Bootstrap 的动画队列取消，也不回写规则状态。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BossMusicController : MonoBehaviour
    {
        [SerializeField] AudioSource _source;
        [SerializeField] AudioClip _phaseOne, _phaseTwo, _phaseThree;
        PresentationConfig _configuration;
        Coroutine _routine;
        BossPhase? _targetPhase;
        bool _sessionActive;
        double _deadline, _fadeStarted, _playEndsAt;
        float _fadeStartGain;
        float _fadeGain;

        /// <summary>当前播放状态，只读。</summary>
        public MusicPlaybackState State { get; private set; }
        /// <summary>最新收到的目标阶段；连续切换时不保留过期队列。</summary>
        public BossPhase? TargetPhase => _targetPhase;
        /// <summary>正在使用的音乐源，供场景接线和只读诊断。</summary>
        public AudioSource Source => _source;
        /// <summary>与用户音量独立的淡出比例。</summary>
        public float FadeGain => _fadeGain;

        /// <summary>启用时读取音量并订阅变更；禁用后的旧对局不会自动恢复。</summary>
        void OnEnable()
        {
            GameAudioSettings.EnsureLoaded();
            GameAudioSettings.Changed += ApplyVolume;
            ApplyVolume();
        }

        /// <summary>禁用时取消所有等待并停止音源，避免隐藏组件继续播放。</summary>
        void OnDisable() { GameAudioSettings.Changed -= ApplyVolume; StopImmediately(); }
        /// <summary>销毁时清理自身任务，不补完任何淡出回调。</summary>
        void OnDestroy() { GameAudioSettings.Changed -= ApplyVolume; StopImmediately(); }

        /// <summary>绑定音源和三首曲目；编辑器安装与独立测试共用，切换绑定前清理旧播放。</summary>
        public void Configure(AudioSource source, AudioClip phaseOne, AudioClip phaseTwo, AudioClip phaseThree)
        {
            StopImmediately();
            _source = source; _phaseOne = phaseOne; _phaseTwo = phaseTwo; _phaseThree = phaseThree;
            PrepareSource();
        }

        /// <summary>准备新对局；输入为只读表现配置，首个快照到达后才开播，首次选卡不播放。</summary>
        public void BeginSession(PresentationConfig configuration)
        {
            StopImmediately();
            _configuration = configuration;
            _sessionActive = configuration != null && _source != null && isActiveAndEnabled;
            PrepareSource();
        }

        /// <summary>同步当前展示快照；胜负优先于阶段，重复刷新不会重启曲目或等待。</summary>
        public void SyncSnapshot(GameSnapshot snapshot)
        {
            if (snapshot == null || !_sessionActive) return;
            if (snapshot.Phase == MatchPhase.Finished) EndSession();
            else SetPhase(snapshot.BossPhase);
        }

        /// <summary>
        /// 设置表现目标阶段。首次立即播；切换时只淡出一次，静音等待中的新目标重新计时。
        /// 不检查生命或战舰，由规则快照决定阶段；公开接口也允许独立播放器测试。
        /// </summary>
        public void SetPhase(BossPhase phase)
        {
            if (!_sessionActive || _targetPhase == phase) return;
            var first = !_targetPhase.HasValue;
            _targetPhase = phase;
            if (first) LoadTarget();
            else if (State == MusicPlaybackState.Playing) StartFade();
            else if (State != MusicPlaybackState.FadingOut) StartGap(MusicPlaybackState.PhaseGap, _configuration.MusicPhaseGapSeconds);
            if (_routine == null) _routine = StartCoroutine(PlayRoutine());
        }

        /// <summary>胜负结算：取消未来开播，已有音乐按当前增益淡出；已经淡出时沿用原进度。</summary>
        public void EndSession()
        {
            if (!_sessionActive) return;
            _sessionActive = false;
            if (State == MusicPlaybackState.Playing) StartFade();
            else if (State != MusicPlaybackState.FadingOut) StopImmediately();
        }

        /// <summary>重开/禁用/销毁的立即清理入口；停止协程、音源和所有待播放目标。</summary>
        public void StopImmediately()
        {
            if (_routine != null) StopCoroutine(_routine);
            _routine = null;
            if (_source != null) { _source.Stop(); _source.clip = null; }
            _targetPhase = null; _sessionActive = false; _fadeGain = 0;
            State = MusicPlaybackState.Stopped;
            ApplyVolume();
        }

        /// <summary>固定为二维、正常速度、手动循环；不使用全局暂停监听器，以便菜单内试听。</summary>
        void PrepareSource()
        {
            if (_source == null) return;
            _source.playOnAwake = false; _source.loop = false; _source.spatialBlend = 0;
            _source.pitch = 1; _source.ignoreListenerPause = true;
            ApplyVolume();
        }

        /// <summary>对阶段做显式曲目映射，不按卡名、血量或文件名在运行时查找。</summary>
        AudioClip TargetClip()
        {
            switch (_targetPhase)
            {
                case BossPhase.PhaseOne: return _phaseOne;
                case BossPhase.PhaseTwo: return _phaseTwo;
                case BossPhase.PhaseThree: return _phaseThree;
                default: return null;
            }
        }

        /// <summary>加载目标曲目；加载完成前保持静音，缺失/损坏素材只报告一次并停止本次播放。</summary>
        void LoadTarget()
        {
            _source.Stop();
            _source.clip = TargetClip();
            _fadeGain = 0; ApplyVolume();
            if (_source.clip == null)
            {
                Debug.LogWarning("Boss 音乐缺失，当前阶段保持静音。", this);
                State = MusicPlaybackState.Stopped;
                return;
            }
            if (_source.clip.loadState == AudioDataLoadState.Unloaded) _source.clip.LoadAudioData();
            State = MusicPlaybackState.Loading;
        }

        /// <summary>切换到真实时间静音间隔，同时取消旧曲目，保证没有声音叠播。</summary>
        void StartGap(MusicPlaybackState state, float seconds)
        {
            _source.Stop(); _fadeGain = 0; ApplyVolume();
            State = state; _deadline = Time.realtimeSinceStartupAsDouble + seconds;
        }

        /// <summary>记录淡出起点，不直接补间 AudioSource.volume，使用户调整与淡出相乘。</summary>
        void StartFade()
        {
            State = MusicPlaybackState.FadingOut;
            _fadeStarted = Time.realtimeSinceStartupAsDouble;
            _fadeStartGain = _fadeGain;
        }

        /// <summary>唯一播放循环；所有等待以真实时间推进，阶段变更只更新状态，不创建第二条协程。</summary>
        IEnumerator PlayRoutine()
        {
            // 先让 StartCoroutine 返回句柄，避免立即退出时留下过期的非空句柄。
            yield return null;
            while (State != MusicPlaybackState.Stopped)
            {
                var now = Time.realtimeSinceStartupAsDouble;
                switch (State)
                {
                    case MusicPlaybackState.Loading:
                        if (_source.clip.loadState == AudioDataLoadState.Failed)
                        {
                            Debug.LogWarning("Boss 音乐加载失败，当前阶段保持静音。", this);
                            State = MusicPlaybackState.Stopped;
                        }
                        else if (_source.clip.loadState == AudioDataLoadState.Loaded)
                        {
                            _fadeGain = 1; ApplyVolume(); _source.Play();
                            // 正常速度且不暂停：至少等到整首时长，避免音频设备初始化/虚拟音源短暂返回 false 被误判为播完。
                            _playEndsAt = now + _source.clip.length;
                            State = MusicPlaybackState.Playing;
                        }
                        break;
                    case MusicPlaybackState.Playing:
                        if (now >= _playEndsAt && !_source.isPlaying) StartGap(MusicPlaybackState.LoopGap, _configuration.MusicLoopGapSeconds);
                        break;
                    case MusicPlaybackState.LoopGap:
                    case MusicPlaybackState.PhaseGap:
                        if (now >= _deadline) LoadTarget();
                        break;
                    case MusicPlaybackState.FadingOut:
                        var duration = _configuration.MusicFadeOutSeconds;
                        var progress = duration <= 0 ? 1f : Mathf.Clamp01((float)((now - _fadeStarted) / duration));
                        _fadeGain = _fadeStartGain * (1f - progress); ApplyVolume();
                        if (progress >= 1)
                        {
                            _source.Stop();
                            if (_sessionActive) StartGap(MusicPlaybackState.PhaseGap, _configuration.MusicPhaseGapSeconds);
                            else State = MusicPlaybackState.Stopped;
                        }
                        break;
                }
                yield return null;
            }
            _routine = null;
        }

        /// <summary>分类音量乘淡出增益；主音量由监听器统一应用，避免重复相乘。</summary>
        void ApplyVolume() { if (_source != null) _source.volume = GameAudioSettings.Music * _fadeGain; }
    }
}
