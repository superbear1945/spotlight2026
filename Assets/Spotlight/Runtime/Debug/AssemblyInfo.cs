// 程序集可见性声明（仅编辑器程序集 Spotlight.Runtime.Debug）。
// 为什么需要：EditMode 测试要直接装配配置面板的界面根元素（BindTo / Open(draft)），
// 这些入口只供测试使用，用 InternalsVisibleTo 精确开放，而不是把它们变成 public API。
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Spotlight.Tests.EditMode")]
