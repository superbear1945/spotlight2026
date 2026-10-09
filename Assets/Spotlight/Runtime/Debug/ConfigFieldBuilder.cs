// 配置面板的字段构建工具：按“标签在上、控件在下”的网页表单样式生成 UI Toolkit 控件。
// 为什么需要：配置面板有上百个逐卡种字段，如果每个字段都手写一遍 Label + 控件的查询与绑定，
// 既容易漏绑定，也无法保证“值直接写回草稿对象”的一致性；这里统一成声明式的绑定入口。
//
// 关于“写回动作”的注册：UI Toolkit 只会把 ChangeEvent 派发给已经挂到面板上的元素，
// 而未挂面板的 UXML 实例（EditMode 测试就是这样用的）不会触发回调。
// 因此把“从控件读值 → 夹紧 → 写回草稿 → 通知变更”抽成一段独立动作并登记在案，
// 事件回调与测试都调用同一段逻辑，避免出现“测试通过但运行时行为不同”的情况。
using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Spotlight
{
    /// <summary>
    /// 表单字段工厂。
    /// 输入：标签文本、取值函数与写值函数（直接绑定草稿对象）；
    /// 输出：可直接加入面板的 VisualElement，任何编辑都会立刻写回草稿并回调变更。
    /// </summary>
    public static class ConfigFieldBuilder
    {
        /// <summary>字段元素 → 写回动作。用于让无法派发事件的测试复用同一段写回逻辑。</summary>
        static readonly Dictionary<VisualElement, Action> _applyActions = new Dictionary<VisualElement, Action>();

        /// <summary>
        /// 清空写回动作登记表。
        /// 输入：无；输出：无。
        /// 面板每次重建表单前调用，避免登记表无限增长并持有已丢弃的元素引用。
        /// </summary>
        public static void ClearRegistry() => _applyActions.Clear();

        /// <summary>
        /// 执行某字段的写回动作。
        /// 输入：字段元素；输出：无。
        /// 用途：EditMode 测试在无法派发 ChangeEvent 时可以调用它验证“控件 → 草稿”的方向。
        /// </summary>
        public static void ApplyCurrentValue(VisualElement field)
        {
            if (field != null && _applyActions.TryGetValue(field, out var apply)) apply();
        }

        /// <summary>
        /// 生成两列/三列自适应的字段网格容器。
        /// 输入：若干字段元素；输出：带 grid 样式的容器。
        /// </summary>
        public static VisualElement Grid(params VisualElement[] fields) => Grid(null, fields);

        /// <summary>
        /// 生成字段网格容器（可附加样式类）。
        /// 输入：附加样式类名与字段元素；输出：带 grid 样式的容器。
        /// </summary>
        public static VisualElement Grid(string extraClass, params VisualElement[] fields)
        {
            var grid = new VisualElement();
            grid.AddToClassList("cfg-grid");
            if (!string.IsNullOrEmpty(extraClass)) grid.AddToClassList(extraClass);
            foreach (var field in fields) if (field != null) grid.Add(field);
            return grid;
        }

        /// <summary>
        /// 生成整数输入字段。
        /// 输入：标签、取值/写值函数、最小值与最大值、变更回调；输出：字段元素。
        /// 为什么带最小/最大值：工作簿校验会拒绝非法数值，
        /// 面板必须在输入阶段就夹紧，避免策划填入后才发现“应用”被禁用。
        /// </summary>
        public static VisualElement Number(string label, Func<int> get, Action<int> set, int min, int max = int.MaxValue, Action changed = null)
        {
            var field = new IntegerField();
            field.value = Clamp(get(), min, max);
            Register(field, () =>
            {
                var value = Clamp(field.value, min, max);
                if (value != field.value) field.SetValueWithoutNotify(value);
                set(value);
                changed?.Invoke();
            });
            field.RegisterValueChangedCallback(_ => ApplyCurrentValue(field));
            return Wrap(label, field);
        }

        /// <summary>
        /// 生成文本输入字段。
        /// 输入：标签、取值/写值函数与变更回调；输出：字段元素。
        /// </summary>
        public static VisualElement Text(string label, Func<string> get, Action<string> set, Action changed = null)
        {
            var field = new TextField();
            field.value = get() ?? string.Empty;
            Register(field, () => { set(field.value); changed?.Invoke(); });
            field.RegisterValueChangedCallback(_ => ApplyCurrentValue(field));
            return Wrap(label, field);
        }

        /// <summary>
        /// 生成复选框字段（控件在左、文本在右）。
        /// 输入：文本、取值/写值函数与变更回调；输出：字段元素。
        /// </summary>
        public static VisualElement Toggle(string text, Func<bool> get, Action<bool> set, Action changed = null)
        {
            var field = new Toggle();
            field.value = get();
            Register(field, () => { set(field.value); changed?.Invoke(); });
            field.RegisterValueChangedCallback(_ => ApplyCurrentValue(field));
            var label = new Label(text);
            label.AddToClassList("cfg-toggle-label");
            var row = new VisualElement();
            row.AddToClassList("cfg-toggle");
            row.Add(field);
            row.Add(label);
            return row;
        }

        /// <summary>
        /// 生成下拉选择字段。
        /// 输入：标签、选项（值 + 显示文本）、取值/写值函数与变更回调；输出：字段元素。
        /// 写回使用“值”而不是显示文本，避免改名后选项错位。
        /// </summary>
        public static VisualElement Choice(string label, IReadOnlyList<(string Value, string Label)> options, Func<string> get, Action<string> set, Action changed = null)
        {
            var field = new DropdownField();
            field.AddToClassList("cfg-input");
            var labels = new List<string>();
            var values = new List<string>();
            foreach (var option in options) { labels.Add(option.Label); values.Add(option.Value); }
            field.choices = labels;
            var current = get();
            var index = values.IndexOf(current ?? string.Empty);
            field.index = index >= 0 ? index : 0;
            Register(field, () =>
            {
                if (field.index < 0 || field.index >= values.Count) return;
                set(values[field.index]);
                changed?.Invoke();
            });
            field.RegisterValueChangedCallback(_ => ApplyCurrentValue(field));
            return Wrap(label, field);
        }

        /// <summary>
        /// 生成说明文本。
        /// 输入：文本；输出：带 note 样式的标签。
        /// </summary>
        public static Label Note(string text)
        {
            var label = new Label(text);
            label.AddToClassList("cfg-note");
            return label;
        }

        /// <summary>
        /// 生成分区容器。
        /// 输入：分区标题与可选的操作按钮；输出：带标题行的分区元素，内容子元素通过 content 返回。
        /// </summary>
        public static VisualElement Section(string title, out VisualElement content, params VisualElement[] actions)
        {
            var section = new VisualElement();
            section.AddToClassList("cfg-section");
            var head = new VisualElement();
            head.AddToClassList("cfg-section-head");
            var heading = new Label(title);
            heading.AddToClassList("cfg-section-title");
            head.Add(heading);
            foreach (var action in actions) if (action != null) head.Add(action);
            section.Add(head);
            content = new VisualElement();
            section.Add(content);
            return section;
        }

        /// <summary>登记字段的写回动作。输入：字段元素与动作；输出：无。</summary>
        static void Register(VisualElement field, Action apply) => _applyActions[field] = apply;

        /// <summary>
        /// 把控件包成“标签在上、控件在下”的字段容器。
        /// 输入：标签文本与控件；输出：字段元素。标签为空时只返回控件本身。
        /// </summary>
        static VisualElement Wrap(string label, VisualElement field)
        {
            field.AddToClassList("cfg-input");
            if (string.IsNullOrEmpty(label))
            {
                field.AddToClassList("cfg-field");
                return field;
            }
            var wrapper = new VisualElement();
            wrapper.AddToClassList("cfg-field");
            var text = new Label(label);
            text.AddToClassList("cfg-field-label");
            wrapper.Add(text);
            wrapper.Add(field);
            return wrapper;
        }

        /// <summary>把数值夹紧到区间。输入：值、最小值与最大值；输出：区间内的值。</summary>
        static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    }
}
