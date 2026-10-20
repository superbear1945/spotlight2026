using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Spotlight.EditorTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Spotlight.Tests
{
    /// <summary>音乐配置及发布接线回归；只修改独立文档/临时工作簿，正式资产保持只读。</summary>
    public class MusicConfigurationTests
    {
        float _listenerVolume;

        /// <summary>保留全局输出音量，防止编辑器安装器首次读取用户偏好时改变后续测试的初始状态。</summary>
        [SetUp]
        public void SetUp() => _listenerVolume = AudioListener.volume;

        /// <summary>即使安装断言失败也恢复全局输出；本夹具不写玩家偏好或修改三个分类音量。</summary>
        [TearDown]
        public void TearDown() => AudioListener.volume = _listenerVolume;

        /// <summary>默认表和正式资产必须给出相同的 10 / 5 / 5，且编译往返不会丢字段。</summary>
        [Test]
        public void WorkbookAndPublishedAssetHaveMusicTimings()
        {
            var report = ConfigCompiler.Compile(publish: false);
            Assert.That(report.Issues, Is.Empty, string.Join("\n", report.Issues));
            AssertTimings(new PresentationConfig(report.Document.presentation[0]), 10, 5, 5);
            var asset = Resources.Load<SpotlightConfigAsset>(ConfigLoader.DefaultResourcePath);
            AssertTimings(asset.CreateConfiguration().Presentation, 10, 5, 5);
            var draft = new DebugConfigDraft(asset);
            Assert.That(draft.Import(draft.ExportJson(), out var error, out _), Is.True, error);
            AssertTimings(new PresentationConfig(draft.Document.presentation[0]), 10, 5, 5);
        }

        /// <summary>旧工作簿缺少新列时沿用默认值；默认工作簿始终显式写出三列。</summary>
        [Test]
        public void MissingWorkbookColumnsUseDefaults()
        {
            var path = Path.Combine(Path.GetTempPath(), "spotlight-music-" + Guid.NewGuid().ToString("N") + ".xlsx");
            try
            {
                DefaultWorkbookWriter.WriteDefault(path);
                var current = ConfigCompiler.Compile(path, false);
                Assert.That(current.Issues, Is.Empty);
                AssertTimings(new PresentationConfig(current.Document.presentation[0]), 10, 5, 5);
                var data = WorkbookReader.Read(path).Data;
                var sheets = TableSchema.RequiredSheets.Select(name =>
                {
                    var cells = data.GetSheet(name);
                    return new DefaultWorkbookWriter.SheetSpec
                    {
                        Name = name,
                        Headers = Enumerable.Range(0, cells.GetLength(1)).Select(c => cells[0, c]).ToArray(),
                        Rows = Enumerable.Range(1, cells.GetLength(0) - 1).Select(r =>
                            (System.Collections.Generic.IReadOnlyList<string>)Enumerable.Range(0, cells.GetLength(1)).Select(c => cells[r, c]).ToArray()).ToArray()
                    };
                }).ToArray();
                var presentation = sheets.Single(s => s.Name == "Presentation");
                var keep = presentation.Headers.Select((name, i) => new { name, i }).Where(v => !v.name.StartsWith("music", StringComparison.Ordinal)).Select(v => v.i).ToArray();
                presentation.Headers = keep.Select(i => presentation.Headers[i]).ToArray();
                presentation.Rows = presentation.Rows.Select(row => (System.Collections.Generic.IReadOnlyList<string>)keep.Select(i => row[i]).ToArray()).ToArray();
                DefaultWorkbookWriter.WriteWorkbook(path, sheets);
                var legacy = ConfigCompiler.Compile(path, false);
                Assert.That(legacy.Issues, Is.Empty, string.Join("\n", legacy.Issues));
                AssertTimings(new PresentationConfig(legacy.Document.presentation[0]), 10, 5, 5);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        /// <summary>旧 JSON 缺失字段显式迁移，零值保持为零；卡名中带括号和转义引号不会影响迁移。</summary>
        [Test]
        public void LegacyJsonDefaultsDoNotReplaceExplicitZero()
        {
            var d = BossPhaseTests.Document();
            d.cards[0].name = "测试 ] { \" 音乐";
            d.presentation[0].musicFadeOutSeconds = 0;
            var json = JsonUtility.ToJson(d);
            json = Regex.Replace(json, "\"musicLoopGapSeconds\":10(?:\\.0)?,?", "");
            json = Regex.Replace(json, "\"musicPhaseGapSeconds\":5(?:\\.0)?,?", "");
            var draft = new DebugConfigDraft(null);
            Assert.That(draft.Import(json, out var error, out var notices), Is.True, error);
            AssertTimings(new PresentationConfig(draft.Document.presentation[0]), 10, 0, 5);
            Assert.That(notices.Count, Is.EqualTo(2));
            Assert.That(draft.Document.cards[0].name, Is.EqualTo(d.cards[0].name));
        }

        /// <summary>三个计时字段共用同一数值约束，拒绝负数、NaN 和无穷。</summary>
        [TestCase("musicLoopGapSeconds")]
        [TestCase("musicFadeOutSeconds")]
        [TestCase("musicPhaseGapSeconds")]
        public void InvalidMusicTimingsAreRejected(string field)
        {
            foreach (var invalid in new[] { -1f, float.NaN, float.PositiveInfinity })
            {
                var d = BossPhaseTests.Document();
                typeof(PresentationRow).GetField(field).SetValue(d.presentation[0], invalid);
                Assert.That(ConfigValidation.Validate(d).Any(e => e.Contains(field)), Is.True);
            }
        }

        /// <summary>正式场景必须携带真实音乐引用、独立音源与唯一监听器；临时加载的场景检查后关闭，不保存。</summary>
        [Test]
        public void SceneReferencesAllMusicAndOneListener()
        {
            const string scenePath = "Assets/Spotlight/Scenes/Spotlight.unity";
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath);
            var alreadyLoaded = scene.IsValid() && scene.isLoaded;
            if (!alreadyLoaded) scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                var roots = scene.GetRootGameObjects();
                var music = roots.SelectMany(r => r.GetComponentsInChildren<BossMusicController>(true)).Single();
                var sound = roots.SelectMany(r => r.GetComponentsInChildren<SoundEffectsPlayer>(true)).Single();
                var bootstrap = roots.SelectMany(r => r.GetComponentsInChildren<GameBootstrap>(true)).Single();
                Assert.That(new SerializedObject(bootstrap).FindProperty("_music").objectReferenceValue, Is.EqualTo(music));
                var serialized = new SerializedObject(music);
                var fields = new[] { "_phaseOne", "_phaseTwo", "_phaseThree" };
                for (var i = 0; i < fields.Length; i++)
                {
                    var path = GameAudioInstaller.MusicFolder + "BossPhase" + (i + 1) + ".mp3";
                    Assert.That(serialized.FindProperty(fields[i]).objectReferenceValue, Is.EqualTo(AssetDatabase.LoadAssetAtPath<AudioClip>(path)));
                    Assert.That(((AudioImporter)AssetImporter.GetAtPath(path)).defaultSampleSettings.loadType, Is.EqualTo(AudioClipLoadType.Streaming));
                }
                Assert.That(music.Source, Is.Not.Null);
                Assert.That(sound.Source, Is.Not.Null.And.Not.EqualTo(music.Source));
                Assert.That(music.Source.loop || music.Source.playOnAwake, Is.False);
                Assert.That(music.Source.spatialBlend, Is.Zero);
                Assert.That(roots.SelectMany(r => r.GetComponentsInChildren<AudioListener>(true)).Count(l => l.enabled && l.gameObject.activeInHierarchy), Is.EqualTo(1));
            }
            finally { if (!alreadyLoaded) EditorSceneManager.CloseScene(scene, true); }
        }

        /// <summary>空场景首次安装及重复安装均建立完整引用，并复用禁用的监听器，不改动已有布局。</summary>
        [Test]
        public void InstallerIsIdempotentAndPreservesSceneLayout()
        {
            var previous = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var host = new GameObject("AudioInstallTest");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(host, scene);
                var bootstrap = host.AddComponent<GameBootstrap>();
                var marker = new GameObject("ExistingLayout");
                marker.transform.SetParent(host.transform);
                marker.transform.localPosition = new Vector3(7, 8, 9);
                var listener = host.AddComponent<AudioListener>(); listener.enabled = false;
                GameAudioInstaller.Install(bootstrap);
                GameAudioInstaller.Install(bootstrap);
                Assert.That(host.GetComponents<BossMusicController>().Length, Is.EqualTo(1));
                Assert.That(host.GetComponents<SoundEffectsPlayer>().Length, Is.EqualTo(1));
                Assert.That(host.GetComponentsInChildren<AudioSource>().Length, Is.EqualTo(2));
                Assert.That(host.GetComponents<AudioListener>().Length, Is.EqualTo(1));
                Assert.That(listener.enabled, Is.True);
                Assert.That(marker.transform.localPosition, Is.EqualTo(new Vector3(7, 8, 9)));
                Assert.That(new SerializedObject(bootstrap).FindProperty("_music").objectReferenceValue, Is.Not.Null);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(previous);
            }
        }

        /// <summary>集中断言只读运行配置，确保 Excel、SO 和 JSON 都经过同一组属性。</summary>
        static void AssertTimings(PresentationConfig p, float loop, float fade, float phase)
        {
            Assert.That(p.MusicLoopGapSeconds, Is.EqualTo(loop));
            Assert.That(p.MusicFadeOutSeconds, Is.EqualTo(fade));
            Assert.That(p.MusicPhaseGapSeconds, Is.EqualTo(phase));
        }
    }
}
