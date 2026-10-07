// 程序集可见性声明。
// 为什么需要：EditMode 测试需要直接装配配置面板的界面根元素，
// 用 InternalsVisibleTo 精确开放测试所需的内部入口，而不是把仅供测试使用的方法公开成 public API。
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Spotlight.Tests.EditMode")]
