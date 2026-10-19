using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

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

        /// <summary>调试面板的运行时类型名（位于仅编辑器程序集，PlayMode 测试程序集不能直接引用）。</summary>
        const string DebugPanelTypeName = "Spotlight.ConfigDebugPanel, Spotlight.Runtime.Debug";

        /// <summary>
        /// 按类型名查找调试面板。
        /// 输入：无；输出：面板组件（当前场景没有时返回 null）。
        /// 为什么用反射：调试面板已移入仅编辑器程序集 `Spotlight.Runtime.Debug`，
        /// 而 PlayMode 测试程序集不限定平台、不能引用仅编辑器程序集；测试在编辑器内运行，按类型名即可拿到同一组件。
        /// </summary>
        static Component FindDebugPanel()
        {
            var type = System.Type.GetType(DebugPanelTypeName);
            Assert.That(type, Is.Not.Null, "找不到调试面板程序集，检查 Spotlight.Runtime.Debug.asmdef 是否仍为编辑器平台");
            return Object.FindObjectOfType(type) as Component;
        }

        /// <summary>
        /// 通过反射调用面板的公开方法。
        /// 输入：面板实例与方法名；输出：无。
        /// 说明：只用于“打开/关闭”这类界面状态切换，表单字段仍通过真实 UXML 按钮事件验证。
        /// </summary>
        static void InvokePanel(Component panel, string method)
        {
            Assert.That(panel, Is.Not.Null, "场景缺少调试面板");
            var info = panel.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(info, Is.Not.Null, "调试面板缺少方法：" + method);
            info.Invoke(panel, null);
        }

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
        /// </summary>
        [UnityTest]
        public IEnumerator LargeConfigurationCanOpenImportExportAndReopenWithoutVertexErrors()
        {
            yield return SceneManager.LoadSceneAsync("Assets/Spotlight/Scenes/Spotlight.unity");
            yield return null;
            var bootstrap = Object.FindObjectOfType<GameBootstrap>();
            var panel = FindDebugPanel();
            var root = Object.FindObjectOfType<HudView>().Root;
            Assert.That(panel, Is.Not.Null);
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
            InvokePanel(panel, "Open");
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
            InvokePanel(panel, "Close");
            InvokePanel(panel, "Open");
            yield return null;
            yield return null;
            Assert.That(root.Q<TextField>("debug-json"), Is.Null);
            Assert.That(GUIUtility.systemCopyBuffer, Is.EqualTo(json));
            InvokePanel(panel, "Close");
            LogAssert.NoUnexpectedReceived();
        }
    }
}
