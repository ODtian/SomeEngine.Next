# Engine Goals

全 C# ECS 游戏引擎。

- ECS 框架（自研 `SomeEngine.ECS` + `SomeEngine.Job`）
- QVVS 坐标系统
- GPU rendering pipeline（cluster based、HiZ 剔除、软光栅、可编程光栅化、tess）
- 光照系统（compute shading、PBR、slang、VSM、megalight DI）
- 物理系统（AVBD）
- 动画系统（压缩、混合、蒙皮、physics control、IK）：通用核心与属性动画见 [[architecture/Animation|Animation]]，骨骼部分为姿势动画
- 资产管线（C#、ECS、流式加载）
- UI/编辑器（框架 UI 选择：SomeUI 与 ImGui；SomeUI 用于正式 UI、HUD 和未来编辑器，ImGui 用于临时调试）

参见 [[ECS-Architecture]]、[[Structural-Optimization]]。

[UI 与编辑器职责决策](../../docs/adr/0008-someui-and-imgui-roles.md)；SomeUI 设计见 [[architecture/SomeUI|SomeUI]]。
