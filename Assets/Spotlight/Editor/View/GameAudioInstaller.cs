using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Spotlight.EditorTools
{
    /// <summary>编辑器音频接线工具；只补充指定对局场景的音频组件，不重建或移动任何现有 UI。</summary>
    public static class GameAudioInstaller
    {
        /// <summary>三首阶段音乐的源目录；引用存入场景，正式运行时不依赖路径查找。</summary>
        public const string MusicFolder = "Assets/Spotlight/Music/";

        /// <summary>为当前场景安装阶段音乐并保存；调用前要求非播放模式且存在唯一引导器。</summary>
        [MenuItem("Tools/Spotlight/接入阶段音乐", priority = 41)]
        public static void InstallInCurrentScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出播放模式再接入音乐。");
            var scene = SceneManager.GetActiveScene();
            var bootstraps = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<GameBootstrap>(true)).ToArray();
            if (bootstraps.Length != 1) throw new InvalidOperationException("当前场景必须有且仅有一个 GameBootstrap。");
            Install(bootstraps[0]);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("阶段音乐场景保存失败。");
            AssetDatabase.SaveAssets();
            Debug.Log("阶段音乐已接入，三首音乐、两个独立音量通道及 AudioListener 已保存。");
        }

        /// <summary>为给定引导器幂等安装音乐和音效通道；现有场景和演示场景生成器共用同一路径。</summary>
        public static void Install(GameBootstrap bootstrap)
        {
            if (bootstrap == null) throw new ArgumentNullException(nameof(bootstrap));
            var clips = new[] { LoadMusic(1), LoadMusic(2), LoadMusic(3) };
            var root = bootstrap.gameObject;
            var music = root.GetComponent<BossMusicController>();
            if (music == null) music = Undo.AddComponent<BossMusicController>(root);
            var sound = root.GetComponent<SoundEffectsPlayer>();
            if (sound == null) sound = Undo.AddComponent<SoundEffectsPlayer>(root);
            Undo.RecordObject(music, "绑定 Boss 阶段音乐");
            Undo.RecordObject(sound, "绑定独立音效通道");
            music.Configure(EnsureSource(root.transform, "BossMusic"), clips[0], clips[1], clips[2]);
            sound.Configure(EnsureSource(root.transform, "SoundEffects"));
            var serialized = new SerializedObject(bootstrap);
            serialized.FindProperty("_music").objectReferenceValue = music;
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(music); EditorUtility.SetDirty(sound);

            var scene = root.scene;
            var roots = scene.GetRootGameObjects();
            var listeners = roots.SelectMany(go => go.GetComponentsInChildren<AudioListener>(true)).ToArray();
            var listener = listeners.FirstOrDefault(item => item.isActiveAndEnabled);
            if (listener == null)
            {
                listener = listeners.FirstOrDefault(item => item.gameObject.activeInHierarchy);
            }
            if (listener == null)
            {
                var camera = roots.SelectMany(go => go.GetComponentsInChildren<Camera>()).FirstOrDefault();
                listener = Undo.AddComponent<AudioListener>(camera != null ? camera.gameObject : root);
            }
            Undo.RecordObject(listener, "启用音频监听器");
            listener.enabled = true;
            foreach (var other in listeners.Where(item => item != listener && item.enabled))
            {
                Undo.RecordObject(other, "仅保留一个音频监听器");
                other.enabled = false;
            }
        }

        /// <summary>读取指定阶段素材并设置流式加载；保留原文件和 GUID，缺失时报错，防止生成无声场景。</summary>
        static AudioClip LoadMusic(int phase)
        {
            var path = MusicFolder + "BossPhase" + phase + ".mp3";
            var importer = AssetImporter.GetAtPath(path) as AudioImporter;
            if (importer == null) throw new InvalidOperationException("缺少 Boss 音乐：" + path);
            var settings = importer.defaultSampleSettings;
            if (settings.loadType != AudioClipLoadType.Streaming || !settings.preloadAudioData || importer.loadInBackground)
            {
                settings.loadType = AudioClipLoadType.Streaming;
                settings.preloadAudioData = true;
                importer.defaultSampleSettings = settings;
                importer.loadInBackground = false;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        }

        /// <summary>创建或复用专用子节点音源，两个分类不共享 AudioSource。</summary>
        static AudioSource EnsureSource(Transform parent, string name)
        {
            var child = parent.Find(name);
            if (child == null)
            {
                var go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "创建音频通道");
                go.transform.SetParent(parent, false);
                child = go.transform;
            }
            // Unity 的缺失原生组件使用特殊空值语义，不能用 ?? 判断是否需要添加。
            var source = child.GetComponent<AudioSource>();
            if (source == null) source = Undo.AddComponent<AudioSource>(child.gameObject);
            Undo.RecordObject(source, "配置二维音源");
            source.playOnAwake = false; source.loop = false; source.spatialBlend = 0;
            return source;
        }
    }
}
