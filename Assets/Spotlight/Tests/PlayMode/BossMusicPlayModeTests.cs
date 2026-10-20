using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Spotlight.Tests
{
    /// <summary>独立音频帧循环测试；使用内存静音片段与短计时，不写正式 SO、不依赖外部音效素材。</summary>
    public class BossMusicPlayModeTests
    {
        GameObject _host;
        BossMusicController _music;
        SoundEffectsPlayer _sound;
        AudioClip _one, _two, _three, _effect;
        float _timeScale, _listener, _master, _musicVolume, _soundVolume;
        readonly string[] _keys = { GameAudioSettings.MasterPreferenceKey, GameAudioSettings.MusicPreferenceKey, GameAudioSettings.SoundPreferenceKey };
        readonly bool[] _hadPreference = new bool[3];
        readonly float[] _preferences = new float[3];

        /// <summary>建立独立音乐/音效通道，并保存全局状态；其他场景存在监听器时不创建第二个。</summary>
        [SetUp]
        public void SetUp()
        {
            _timeScale = Time.timeScale; _listener = AudioListener.volume;
            _master = GameAudioSettings.Master; _musicVolume = GameAudioSettings.Music; _soundVolume = GameAudioSettings.Sound;
            for (var i = 0; i < _keys.Length; i++) { _hadPreference[i] = PlayerPrefs.HasKey(_keys[i]); _preferences[i] = PlayerPrefs.GetFloat(_keys[i]); }
            _host = new GameObject("MusicTest");
            if (Object.FindObjectsOfType<AudioListener>().Length == 0) _host.AddComponent<AudioListener>();
            _music = _host.AddComponent<BossMusicController>();
            var musicSource = _host.AddComponent<AudioSource>();
            var soundNode = new GameObject("SoundTest"); soundNode.transform.SetParent(_host.transform);
            _sound = soundNode.AddComponent<SoundEffectsPlayer>();
            _sound.Configure(soundNode.AddComponent<AudioSource>());
            _one = AudioClip.Create("One", 441000, 1, 44100, false);
            _two = AudioClip.Create("Two", 441000, 1, 44100, false);
            _three = AudioClip.Create("Three", 441000, 1, 44100, false);
            _effect = AudioClip.Create("IndependentSoundEffect", 44100, 1, 44100, false);
            _music.Configure(musicSource, _one, _two, _three);
            GameAudioSettings.SetMaster(.8f); GameAudioSettings.SetMusic(.6f); GameAudioSettings.SetSound(.4f);
        }

        /// <summary>失败也恢复偏好、音量和时间缩放，避免影响其他 PlayMode 用例或真实用户。</summary>
        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_host);
            Object.DestroyImmediate(_one); Object.DestroyImmediate(_two); Object.DestroyImmediate(_three);
            Object.DestroyImmediate(_effect);
            for (var i = 0; i < _keys.Length; i++)
                if (_hadPreference[i]) PlayerPrefs.SetFloat(_keys[i], _preferences[i]); else PlayerPrefs.DeleteKey(_keys[i]);
            PlayerPrefs.Save();
            GameAudioSettings.SetMaster(_master); GameAudioSettings.SetMusic(_musicVolume); GameAudioSettings.SetSound(_soundVolume);
            AudioListener.volume = _listener; Time.timeScale = _timeScale;
        }

        /// <summary>生成独立计时配置，正式 10/5/5 另有完整时长用例验证。</summary>
        static PresentationConfig Timing(float loop = .3f, float fade = .4f, float phase = .3f) =>
            new PresentationConfig(new PresentationRow { musicLoopGapSeconds = loop, musicFadeOutSeconds = fade, musicPhaseGapSeconds = phase });

        /// <summary>有限等待指定状态；使用真实时间，暂停测试中也能超时而非挂住。</summary>
        IEnumerator WaitFor(MusicPlaybackState state, float timeout = 3f)
        {
            var until = Time.realtimeSinceStartup + timeout;
            while (_music.State != state && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(_music.State, Is.EqualTo(state));
        }

        /// <summary>首次开播、重复阶段不重播，以及暂停中继续完成淡出和阶段等待。</summary>
        [UnityTest]
        public IEnumerator PhaseSwitchFadesThenWaitsEvenWhenPaused()
        {
            Assert.That(_music.State, Is.EqualTo(MusicPlaybackState.Stopped));
            _music.BeginSession(Timing()); _music.SetPhase(BossPhase.PhaseOne);
            yield return WaitFor(MusicPlaybackState.Playing);
            Assert.That(_music.Source.clip, Is.EqualTo(_one));
            _music.SetPhase(BossPhase.PhaseOne);
            Assert.That(_music.State, Is.EqualTo(MusicPlaybackState.Playing));
            Time.timeScale = 0;
            _music.SetPhase(BossPhase.PhaseTwo);
            yield return new WaitForSecondsRealtime(.12f);
            Assert.That(_music.FadeGain, Is.GreaterThan(0).And.LessThan(1));
            Assert.That(_music.Source.clip, Is.EqualTo(_one));
            GameAudioSettings.SetMusic(.25f);
            Assert.That(_music.Source.volume, Is.EqualTo(.25f * _music.FadeGain).Within(.001f));
            yield return WaitFor(MusicPlaybackState.PhaseGap);
            Assert.That(_music.Source.volume, Is.Zero);
            yield return new WaitForSecondsRealtime(.1f);
            Assert.That(_music.State, Is.EqualTo(MusicPlaybackState.PhaseGap));
            yield return WaitFor(MusicPlaybackState.Playing);
            Assert.That(_music.Source.clip, Is.EqualTo(_two));
            Assert.That(_music.Source.volume, Is.EqualTo(.25f).Within(.001f));
        }

        /// <summary>淡出中连续切换保留进度且跳过旧目标；静音等待中的新阶段会重新计算等待。</summary>
        [UnityTest]
        public IEnumerator RapidChangesKeepOnlyLatestTarget()
        {
            _music.BeginSession(Timing(phase: .5f)); _music.SetPhase(BossPhase.PhaseOne);
            yield return WaitFor(MusicPlaybackState.Playing);
            _music.SetPhase(BossPhase.PhaseTwo);
            yield return new WaitForSecondsRealtime(.2f);
            var gain = _music.FadeGain;
            _music.SetPhase(BossPhase.PhaseThree);
            Assert.That(_music.FadeGain, Is.EqualTo(gain));
            yield return WaitFor(MusicPlaybackState.PhaseGap, .35f);
            yield return new WaitForSecondsRealtime(.25f);
            // 播放器允许独立设置目标，实际规则仍只允许一→二→三；此处构造等待期间变更。
            _music.SetPhase(BossPhase.PhaseTwo);
            yield return new WaitForSecondsRealtime(.3f);
            Assert.That(_music.State, Is.EqualTo(MusicPlaybackState.PhaseGap));
            _music.SetPhase(BossPhase.PhaseThree);
            yield return WaitFor(MusicPlaybackState.Playing);
            Assert.That(_music.Source.clip, Is.EqualTo(_three));
        }

        /// <summary>真实 10/5/5：自然结束静音十秒、重播后五秒淡出、再静音五秒；边界允许一帧误差。</summary>
        [UnityTest]
        public IEnumerator ProductionDurationsAreRespected()
        {
            Object.DestroyImmediate(_one);
            _one = AudioClip.Create("ShortOne", 4410, 1, 44100, false);
            _music.Configure(_music.Source, _one, _two, _three);
            _music.BeginSession(new PresentationConfig(new PresentationRow())); _music.SetPhase(BossPhase.PhaseOne);
            yield return WaitFor(MusicPlaybackState.LoopGap);
            var ended = Time.realtimeSinceStartup;
            yield return new WaitForSecondsRealtime(9.7f);
            Assert.That(_music.State, Is.EqualTo(MusicPlaybackState.LoopGap));
            yield return WaitFor(MusicPlaybackState.Playing, 2f);
            Assert.That(Time.realtimeSinceStartup - ended, Is.GreaterThanOrEqualTo(9.98f));
            _music.SetPhase(BossPhase.PhaseTwo);
            var switched = Time.realtimeSinceStartup;
            yield return new WaitForSecondsRealtime(2f);
            Assert.That(_music.FadeGain, Is.InRange(.3f, .7f));
            yield return WaitFor(MusicPlaybackState.PhaseGap, 4f);
            Assert.That(Time.realtimeSinceStartup - switched, Is.GreaterThanOrEqualTo(4.98f));
            var silent = Time.realtimeSinceStartup;
            yield return new WaitForSecondsRealtime(4.7f);
            Assert.That(_music.State, Is.EqualTo(MusicPlaybackState.PhaseGap));
            yield return WaitFor(MusicPlaybackState.Playing, 2f);
            Assert.That(Time.realtimeSinceStartup - silent, Is.GreaterThanOrEqualTo(4.98f));
            Assert.That(_music.Source.clip, Is.EqualTo(_two));
        }

        /// <summary>自然间隔中的切阶段取消原十秒间隔，直接采用阶段静音等待。</summary>
        [UnityTest]
        public IEnumerator PhaseChangeDuringLoopGapUsesPhaseGap()
        {
            Object.DestroyImmediate(_one); _one = AudioClip.Create("Short", 4410, 1, 44100, false);
            _music.Configure(_music.Source, _one, _two, _three);
            _music.BeginSession(Timing(loop: 10)); _music.SetPhase(BossPhase.PhaseOne);
            yield return WaitFor(MusicPlaybackState.LoopGap);
            _music.SetPhase(BossPhase.PhaseTwo);
            Assert.That(_music.State, Is.EqualTo(MusicPlaybackState.PhaseGap));
            yield return WaitFor(MusicPlaybackState.Playing, 1);
            Assert.That(_music.Source.clip, Is.EqualTo(_two));
        }

        /// <summary>结算取消后续开播；重开、禁用及销毁不得留下过期任务。</summary>
        [UnityTest]
        public IEnumerator EndRestartAndDisableCancelPendingPlayback()
        {
            _music.BeginSession(Timing()); _music.SetPhase(BossPhase.PhaseOne);
            yield return WaitFor(MusicPlaybackState.Playing);
            _music.SetPhase(BossPhase.PhaseTwo); _music.EndSession();
            yield return WaitFor(MusicPlaybackState.Stopped);
            yield return new WaitForSecondsRealtime(.4f);
            Assert.That(_music.State, Is.EqualTo(MusicPlaybackState.Stopped));
            Assert.That(_music.Source.volume, Is.Zero);
            _music.BeginSession(Timing()); _music.SetPhase(BossPhase.PhaseOne);
            yield return WaitFor(MusicPlaybackState.Playing);
            _music.SetPhase(BossPhase.PhaseThree);
            yield return WaitFor(MusicPlaybackState.PhaseGap);
            _music.BeginSession(Timing()); _music.SetPhase(BossPhase.PhaseOne);
            yield return WaitFor(MusicPlaybackState.Playing);
            yield return new WaitForSecondsRealtime(.5f);
            Assert.That(_music.Source.clip, Is.EqualTo(_one));
            _music.SetPhase(BossPhase.PhaseTwo); _music.enabled = false;
            yield return new WaitForSecondsRealtime(.8f);
            _music.enabled = true;
            Assert.That(_music.State, Is.EqualTo(MusicPlaybackState.Stopped));
            Assert.That(_music.Source.clip, Is.Null);
            _music.BeginSession(Timing()); _music.SetPhase(BossPhase.PhaseOne);
            yield return WaitFor(MusicPlaybackState.Playing);
            var source = _music.Source;
            _music.SetPhase(BossPhase.PhaseTwo);
            Object.Destroy(_music); yield return null;
            Assert.That(source.isPlaying, Is.False);
            Assert.That(source.clip, Is.Null);
        }

        /// <summary>静音不停止播放时间；音乐和音效通道互不影响，保存恢复和重置实时更新音源。</summary>
        [UnityTest]
        public IEnumerator VolumesAreIndependentAndPersistent()
        {
            _music.BeginSession(Timing()); _music.SetPhase(BossPhase.PhaseOne);
            yield return WaitFor(MusicPlaybackState.Playing);
            _sound.Play(_effect);
            GameAudioSettings.SetMaster(.5f); GameAudioSettings.SetMusic(0); GameAudioSettings.SetSound(.2f);
            Assert.That(AudioListener.volume, Is.EqualTo(.5f).Within(.001f));
            Assert.That(_music.Source.volume, Is.Zero);
            Assert.That(_sound.Source.volume, Is.EqualTo(.2f).Within(.001f));
            var position = _music.Source.timeSamples;
            yield return new WaitForSecondsRealtime(.15f);
            Assert.That(_music.State, Is.EqualTo(MusicPlaybackState.Playing));
            GameAudioSettings.SetMusic(.3f);
            Assert.That(_music.Source.timeSamples, Is.GreaterThanOrEqualTo(position));
            Assert.That(_music.Source.volume, Is.EqualTo(.3f).Within(.001f));
            GameAudioSettings.Save(); GameAudioSettings.ResetVolumes();
            Assert.That(_music.Source.volume, Is.EqualTo(1));
            Assert.That(_sound.Source.volume, Is.EqualTo(1));
            GameAudioSettings.Reload();
            Assert.That(AudioListener.volume, Is.EqualTo(.5f).Within(.001f));
            Assert.That(_music.Source.volume, Is.EqualTo(.3f).Within(.001f));
            Assert.That(_sound.Source.volume, Is.EqualTo(.2f).Within(.001f));
            foreach (var key in _keys) PlayerPrefs.DeleteKey(key);
            GameAudioSettings.Reload();
            Assert.That(AudioListener.volume, Is.EqualTo(.8f).Within(.001f));
            Assert.That(_music.Source.volume, Is.EqualTo(.6f).Within(.001f));
            Assert.That(_sound.Source.volume, Is.EqualTo(.8f).Within(.001f));
        }
    }
}
