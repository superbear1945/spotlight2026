# Spotlight 美术素材导入说明

本目录于 2026-10-19 从 `D:\Desktop\spotlight` 整理导入，并补充了 ChatGPT 项目“涌现”中本地素材库缺失的附件。

## 目录映射

- `Branding/`：宣传图、游戏库背景、图标与 Logo。
- `Backgrounds/`：棋盘与异星场景背景。
- `Cards/`：卡牌属性图形素材。
- `MotionReferences/`：单位动作参考图与 MP4 预览。
- `Units/`：四个单位的透明 PNG 母图，以及 `Player/`、`Boss/` 中的正面、背面和敌舰视图。
- `UI/Loose/`：早期回合 UI 素材，以及操作条、单位信息、暂停和设置界面图。
- `UI/Components/`：完整外围 UI 套件、四态按钮、面板、装饰和配套清单。
- `FleetIdleSource/`：FleetIdle 的单位贴图、配置与 Shader 源素材。

## 有意未导入的内容

- `原油战线_UI全套_v1.zip`：与已解压的 `UI/Components/` 重复，避免 Unity 导入无用压缩包。
- FleetIdle 的离线预览、Python 重建工具和重复源图：这些不是 Unity 运行资源。
- `FleetIdlePlayer.cs` 与 `FleetIdleSetup.cs`：原包没有 asmdef，命名空间与字段风格也不符合 Spotlight 的程序集和编码约定。正式接入动画时，需要将运行代码放入 `Spotlight.Runtime`、编辑器代码放入 `Spotlight.Editor`，补充注释和测试后再启用。

## 使用约定

- `References/` 仅用于选稿和视觉参考，不应直接绑定到运行界面。
- 正式使用的 PNG 应配置为 Sprite，并关闭 Mipmap；数据蒙版必须保持线性颜色，不能按普通透明 UI 图导入。
- 所有 PNG、JPG 和 MP4 均由 Git LFS 管理。
- 原始来源目录 `D:\Desktop\spotlight` 保持不变，可作为本机备份。
