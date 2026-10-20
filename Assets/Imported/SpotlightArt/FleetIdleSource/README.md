# 舰船机械 Idle v3 · 13 个视角

保留此前四套机械待机，并新增九张透明单位图对应的待机视角。新增视角全部从各自原图拆分，未重新生成舰体。主体外壳、连接机构、配色和大部分活动件沿用原图拆分。钻头为实现绕轴旋转，单独重建为三维螺旋件；塔身与支脚仍使用原图。没有加入攻击演出。

本次新增：

| 视角 ID | 内容 |
| --- | --- |
| enemy | 双尾翼微调、炮口套筒小幅滑动、低功率暖色喷流、橙色舷灯呼吸 |
| enemy-back | 双尾翼微调，正对镜头的双发动机内部热流和暖色光环脉动 |
| enemy-face | 双尾翼微调、双炮口轻微伸缩，舷灯呼吸；炮口不发射 |
| aodexiusi-face | 四块盾板独立微调，中心轮毂固定，灯光呼吸与盾面光泽 |
| aodexiusi-back | 四块盾板独立微调，中央三个喷口外壳固定，内部蓝色热流脉动 |
| jiandao-back | 两翼微调、露出的刀片轻动，刀片保持在发动机之后；双喷口内部热流 |
| jiandao-face | 刀臂小幅开合，液压筒绕固定根部转动，活塞杆联动伸缩，机翼微调 |
| lingtu-back | 两侧发动机与支架围绕连接处微调，喷口内的热流随部件一起移动 |
| lingtu-face | 两个炮管前套筒小幅伸缩，中央红色镜头呼吸；炮口不发射 |

此前四套也包含在包内：

| 单位 | Idle 内容 |
| --- | --- |
| 奥德修斯 | 四块盾板分别绕连接处微调，中央轮毂保持固定；保留短蓝色推进气流、灯口呼吸和盾面光泽 |
| 尖刀 | 两组完整刀臂绕共轴小幅收拢、张开；两只液压筒随端点调整方向，活塞杆随长度伸缩；保留轻微悬浮、蓝色光眼与低功率喷流 |
| Lingtu | 两根炮管的末端套筒轻微伸出、收回，保留根部搭接；红色镜头呼吸，机体轻微悬浮，炮口不发射 |
| 原油 | 三维钻头以约 20 rpm 绕轴旋转，沿轴向往复约 24 源图像素；塔身、四个支脚与固定轴套保持不动，保留观察窗和管道流光 |

每段均为 **6 秒循环、30 fps**。预览背景仅用于看清透明单位，不包含在 Unity 材质里。

## Unity 导入

1. 复制 `Unity/Assets/FleetIdle` 到项目的 `Assets` 下。
2. 等待导入与编译，选择 `Tools > Fleet Idle > Create All Prefabs`。
3. 在 `Tools > Fleet Idle > Demo` 中选择单位创建演示场景。
4. Game 视图宽高比使用该单位 `idle-config.json` 的 `canvas` 比例，点击 Play。演示场景建立后也会打印所需比例。

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

`rig-atlas.png` 是各机械分层的静态颜色图集；`rig-mask-atlas.png` 是对应的线性数据图集。两者均为 2048 × 2048，不是特效序列帧。`idle-config.json` 中保存图集区域、原图区域、活动类型、轴点／滑动方向和每个视角的独立喷口数据，生成工具会自动设置材质的部件数组。请重新生成 Prefab，旧版生成的材质没有这些新参数。

模型以 100 pixels per unit 建立，之后可统一缩放 Prefab 根对象。喷口位置、朝向与各单位不同的悬浮幅度已配置。运行时通过 MaterialPropertyBlock 更新，不逐帧复制材质。

## 预览与源文件

`Previews` 包含 13 段独立 MP4，以及新增九个视角依次播放的总览。它们由 `PreviewSource/render_idle.py` 在 OpenGL/GLSL 下离线渲染；共享效果函数直接从 Unity 的 `FleetIdleCommon.hlsl` 转译，**预览不是 Unity 编辑器录像**。

`SourceImages` 保留全部 13 张原始 PNG。依次运行 `PreviewSource/prepare_assets.py`、`prepare_rig.py`、`prepare_views.py` 可重建全部技术蒙版、机械分层、图集和配置。最后运行 `render_idle.py` 渲染全部预览；也可在命令后提供一个或多个视角 ID。依赖 Python、NumPy、Pillow、SciPy；离线预览还依赖 Mesa/EGL、OpenGL 和 FFmpeg。

新增九套已检查源图不透明像素的静止重组无损、0 秒与 6 秒渲染首尾一致，并逐张查看最大位移姿态。原有检查包括：剪刃共轴、液压连接点与杆长、静止残片、钻头旋转及伸缩的遮挡、循环首尾一致、四个支脚固定、视频解码。机械连接处有少量被相邻零件遮住的原图边缘搭接。Unity ShaderLab/HLSL 与 C# 尚需在你们项目中实测；钻头的目标机批量渲染性能尚未测试。

Shader 通道标签参考：[Unity Pass Tags](https://docs.unity.cn/Manual/SL-PassTags.html)、[Unity URP Pass Tags](https://docs.unity3d.com/cn/6000.0/Manual/urp/urp-shaders/urp-shaderlab-pass-tags.html)。


## 新视角实现边界

新增刀臂、盾板、尾翼、套筒使用原图的平面分层机械动画，属于 2D 资产，不是舰体三维重建。正背面分别设置了轴点、遮挡和喷口坐标。背面喷口使用局部极坐标热流与旋转亮弧，没有从正对镜头的喷口拉出平面长拖尾。半透明抗锯齿边缘允许有轻微重采样差异。

参数数组保存在 `FleetIdlePlayer` 的序列化字段中，并通过 MaterialPropertyBlock 重设，避免导入或重载后丢失。新的宽幅纹理保留原像素坐标，导入上限为 4096。首次使用 v3 请重新生成 Prefab；旧生成对象不会自动改成新配置。
