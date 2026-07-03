# Engine Goals

全 C# ECS 游戏引擎。

- ECS 框架（自研 `SomeEngine.ECS` + `SomeEngine.Job`）
- QVVS 坐标系统
- GPU rendering pipeline（cluster based、HiZ 剔除、软光栅、可编程光栅化、tess）
- 光照系统（compute shading、PBR、slang、VSM、megalight DI）
- 物理系统（AVBD）
- 动画系统（压缩、混合、蒙皮、physics control、IK）
- 资产管线（C#、ECS、流式加载）
- UI/编辑器（IMGUI → Avalonia）

参见 [[ECS-Architecture]]、[[Structural-Optimization]]。
