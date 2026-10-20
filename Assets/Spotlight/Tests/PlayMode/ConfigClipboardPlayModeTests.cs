using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Spotlight.Tests
{
    /// <summary>
    /// 配置 JSON 剪贴板通道的真实渲染回归。输入：演示场景及正式资产的独立草稿；
    /// 输出：打开、导入、导出、滚动与重新打开后的无渲染错误断言，防止超长 JSON 再进入单个文本元素。
    /// </summary>
    public class ConfigClipboardPlayModeTests
    {
        /// <summary>测试前的系统剪贴板；结束时恢复，避免影响用户。</summary>
        string _previousClipboard;
        /// <summary>测试前的开局开关；结束时恢复，避免影响其它夹具。</summary>
        bool _previousSuppressSelection;
        /// <summary>正式资产，用于断言调试导入不污染正式配置。</summary>
        SpotlightConfigAsset _sourceAsset;
        /// <summary>测试前的完整正式配置文本，用于清理时比较。</summary>
        string _sourceJson;

        /// <summary>保存进程外剪贴板和开局开关。输入：无；输出：隔离测试所需的清理备份。</summary>
        [SetUp]
        public void SetUp()
        {
            _previousClipboard = GUIUtility.systemCopyBuffer;
            _previousSuppressSelection = GameBootstrap.SuppressDeckSelectionOnStart;
            GameBootstrap.SuppressDeckSelectionOnStart = true;
        }

        /// <summary>还原剪贴板及开局开关并检查正式资产。输入：备份；输出：无，污染时断言失败。</summary>
        [TearDown]
        public void TearDown()
        {
            GUIUtility.systemCopyBuffer = _previousClipboard;
            GameBootstrap.SuppressDeckSelectionOnStart = _previousSuppressSelection;
            if (_sourceAsset != null)
                Assert.That(JsonUtility.ToJson(_sourceAsset.ToDocument()), Is.EqualTo(_sourceJson));
            _sourceAsset = null;
        }

        /// <summary>
        /// 通过真实按钮的键盘提交事件触发回调。输入：已挂载的界面根和按钮名；输出：无。
        /// 使用 UI 事件验证 UXML 事件绑定，避免直接调用私有导入导出函数遗漏界面接线错误。
        /// </summary>
        static void SubmitButton(VisualElement root, string name)
        {
            var button = root.Q<Button>(name);
            Assert.That(button, Is.Not.Null);
            using (var evt = NavigationSubmitEvent.GetPooled())
            {
                evt.target = button;
                button.SendEvent(evt);
            }
        }

        /// <summary>
        /// 大配置在真实帧循环中完整往返，滚动至导入导出区也不绘制 JSON 全文。
        /// 输入：正式配置的副本与额外独立 Boss 卡种；输出：剪贴板、对局隔离、重开面板及渲染日志断言。
        /// 说明：面板类型在 Spotlight.Editor 程序集里（PlayMode 测试程序集不引用它），
        /// 因此这里完全通过界面入口（齿轮菜单里的“配置/调试”、面板里的“关闭”）驱动，
        /// 反而顺带验证了“入口按钮 → 打开面板”这条真实接线。
        /// </summary>
        [UnityTest]
        public IEnumerator LargeConfigurationCanOpenImportExportAndReopenWithoutVertexErrors()
        {
            yield return SceneManager.LoadSceneAsync("Assets/Spotlight/Scenes/Spotlight.unity");
            yield return null;
            var bootstrap = Object.FindObjectOfType<GameBootstrap>();
            var root = Object.FindObjectOfType<HudView>().Root;
            Assert.That(root.Q<VisualElement>("debug-panel"), Is.Not.Null,
                "编辑器里应由 ConfigPanelInstaller 创建调试面板骨架");
            _sourceAsset = bootstrap.CurrentAsset;
            _sourceJson = JsonUtility.ToJson(_sourceAsset.ToDocument());
            var document = _sourceAsset.ToDocument();
            var cards = document.cards.ToList();
            for (var i = 0; i < 24; i++)
            {
                var card = JsonUtility.FromJson<CardRow>(JsonUtility.ToJson(document.cards.First(c => c.tier == "none" && c.category == "attack")));
                card.id = "clipboard_test_" + i;
                card.name = "剪贴板回归卡" + i;
                cards.Add(card);
            }
            document.cards = cards.ToArray();
            var json = JsonUtility.ToJson(document, true);
            Assert.That(json.Count(c => !char.IsWhiteSpace(c)), Is.GreaterThan(20000));
            var session = bootstrap.Session;
            GUIUtility.systemCopyBuffer = json;
            // 入口用顶部工具条的快捷按钮（设置页里的“开发者配置”走的是同一个 DebugRequested）；
            // 它由 HudView 在编辑器里用代码插入，所以这里能点到就是接线正常。
            SubmitButton(root, "quick-debug-button");
            yield return null;
            yield return null;
            Assert.That(root.Q<TextField>("debug-json"), Is.Null);
            Assert.That(GUIUtility.systemCopyBuffer, Is.EqualTo(json));
            SubmitButton(root, "debug-import-button");
            yield return null;
            Assert.That(root.Q<Label>("debug-json-status").text, Does.Contain("导入成功"));
            var scroll = root.Q<ScrollView>("debug-scroll");
            scroll.ScrollTo(root.Q<Button>("debug-export-button"));
            yield return null;
            yield return null;
            SubmitButton(root, "debug-export-button");
            Assert.That(GUIUtility.systemCopyBuffer, Is.EqualTo(json));
            Assert.That(bootstrap.Session, Is.SameAs(session), "导入草稿不得直接重开对局");
            SubmitButton(root, "debug-close-button");
            SubmitButton(root, "quick-debug-button");
            yield return null;
            yield return null;
            Assert.That(root.Q<TextField>("debug-json"), Is.Null);
            Assert.That(GUIUtility.systemCopyBuffer, Is.EqualTo(json));
            SubmitButton(root, "debug-close-button");
            LogAssert.NoUnexpectedReceived();
        }
    }
}
