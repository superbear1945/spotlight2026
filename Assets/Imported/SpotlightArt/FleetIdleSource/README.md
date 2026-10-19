# 四个单位 · 机械 Idle v2

以本轮提供的四张透明单位图为母本。主体外壳、连接机构、配色和大部分活动件沿用原图拆分。钻头为实现绕轴旋转，单独重建为三维螺旋件；塔身与支脚仍使用原图。没有加入攻击演出。

| 单位 | Idle 内容 |
| --- | --- |
| 奥德修斯 | 四块盾板分别绕连接处微调，中央轮毂保持固定；保留短蓝色推进气流、灯口呼吸和盾面光泽 |
| 尖刀 | 两组完整刀臂绕共轴小幅收拢、张开；两只液压筒随端点调整方向，活塞杆随长度伸缩；保留轻微悬浮、蓝色光眼与低功率喷流 |
| Lingtu | 两根炮管的末端套筒轻微伸出、收回，保留根部搭接；红色镜头呼吸，机体轻微悬浮，炮口不发射 |
| 原油 | 三维钻头以约 20 rpm 绕轴旋转，沿轴向往复约 24 源图像素；塔身、四个支脚与固定轴套保持不动，保留观察窗和管道流光 |

四段均为 **6 秒循环、30 fps**。预览背景仅用于看清透明单位，不包含在 Unity 材质里。

## Unity 导入

1. 复制 `Unity/Assets/FleetIdle` 到项目的 `Assets` 下。
2. 等待导入与编译，选择 `Tools > Fleet Idle > Create All Prefabs`。
3. 在 `Tools > Fleet Idle > Demo` 中选择单位创建演示场景。
4. 奥德修斯、Lingtu、原油的 Game 视图设为 1:1，尖刀设为 7:5，点击 Play。

Prefab、材质和演示场景会生成在 `Assets/FleetIdle/Generated`，采用新文件名，不覆盖先前生成的对象。背景是演示相机的清屏色，单位本身仍透明。

运行时由片元 Shader 组合拆分后的部件图集，按统一机械姿态计算各活动件的位置。剪刃先映射到其透视平面再绕同轴旋转；液压筒长度保持不变，只有活塞杆伸缩。四块盾板采用独立轴点，炮管套筒沿炮管方向滑动。钻头采用局部三维距离场的圆锥轴与螺旋刃，旋转会改变可见的刃面，不是旋转一张平面钻头图。

保留的悬浮、喷流、照明和流光与机械运动共用六秒循环。地面单位的悬浮幅度为 0。

材质设计供 Built-in 与 URP 的普通 Unlit 透明对象使用，Shader Target 为 3.5，无第三方库或后处理依赖；HDRP 未适配。未在本环境内运行 Unity 编辑器验证。材质以无光照方式保留原画颜色，不随场景主光照重新着色。

## 可调参数

`FleetIdlePlayer` 提供 `speed`、`hoverPixels`、`mechanicalAmplitude`、`phaseOffset` 和播放／暂停控制。多实例可设置不同的 `phaseOffset`。`mechanicalAmplitude` 调整开合、伸缩和盾板微动的幅度，钻头转速随 `speed` 改变。所有显示运动都不会移动根 GameObject 或碰撞体。

- `PlayIdle()`：播放。
- `PauseIdle()`：暂停在当前状态。
- `RestartIdle()`：从循环起点重新播放。
- `SetIdleTime(seconds)`：设置预览时间。

`unit.png` 是居中增加透明边距后的原图。`effect-mask.png` 是线性数据纹理，R 通道控制实际灯口，G 控制盾面／装甲反光或流动管道，B/A 保存纹理坐标。不要把 Mask 按颜色贴图、透明图片或 Sprite 导入；生成工具会配置其导入选项。

`rig-atlas.png` 是各机械分层的静态颜色图集；`rig-mask-atlas.png` 是对应的线性数据图集。两者均为 2048 × 2048，不是特效序列帧。`idle-config.json` 中保存图集区域、原图区域和活动类型，生成工具会自动设置材质的部件数组。请重新生成 Prefab，旧版生成的材质没有这些新参数。

模型以 100 pixels per unit 建立，之后可统一缩放 Prefab 根对象。喷口位置、朝向与各单位不同的悬浮幅度已配置。运行时通过 MaterialPropertyBlock 更新，不逐帧复制材质。

## 预览与源文件

`Previews` 包含四段 MP4。它们由 `PreviewSource/render_idle.py` 在 OpenGL/GLSL 下离线渲染；共享效果函数直接从 Unity 的 `FleetIdleCommon.hlsl` 转译，**预览不是 Unity 编辑器录像**。

`SourceImages` 保留本轮四张原始 PNG。先运行 `PreviewSource/prepare_assets.py`，再运行 `prepare_rig.py`，可重建技术蒙版、机械分层、图集和配置。依赖 Python、NumPy、Pillow、SciPy；离线预览还依赖 Mesa/EGL、OpenGL 和 FFmpeg。

已检查：剪刃共轴、液压连接点与杆长、静止残片、钻头旋转及伸缩的遮挡、循环首尾一致、四个支脚固定、视频解码。机械连接处有少量被相邻零件遮住的原图边缘搭接。Unity ShaderLab/HLSL 与 C# 尚需在你们项目中实测；钻头的目标机批量渲染性能尚未测试。

Shader 通道标签参考：[Unity Pass Tags](https://docs.unity.cn/Manual/SL-PassTags.html)、[Unity URP Pass Tags](https://docs.unity3d.com/cn/6000.0/Manual/urp/urp-shaders/urp-shaderlab-pass-tags.html)。
