// 程序集可见性声明（编辑器程序集）。
// 为什么需要：调试配置面板现在编进 Spotlight.Editor（仅编辑器编译），
// 而 EditMode 测试仍需要它的内部入口（BindTo / Open(draft) / PanelAssetPath 等）
// 来装配界面根元素、注入草稿并验证表单生成——这些入口不该公开成运行层 API。
// 因此用 InternalsVisibleTo 只对测试程序集开放。
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Spotlight.Tests.EditMode")]
