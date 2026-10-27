# 目录阅读入口

从[根级规则](../../AGENTS.md)、[领域读取规则](domain.md)与[术语入口](../../GLOSSARY-MAP.md)进入。按本次行为选择专题，再沿公共接口、调用者与行为测试核对当前实现。

| 工作范围 | 设计与行为入口 | 实现与行为测试入口 |
| --- | --- | --- |
| 渲染提取 | [两个 World](../../wiki/architecture/rendering/GameWorld-and-RenderWorld.md)、[提取](../../wiki/architecture/rendering/Extraction.md)、[CPU 重叠](../../wiki/architecture/rendering/Frame-CPU-Overlap.md) | [RenderWorld](../../src/SomeEngine.Render/Systems/RenderWorld.cs)、[Render 测试](../../tests/SomeEngine.Render.Tests/) |
| 实例组合与上传 | [实例存储](../../wiki/architecture/rendering/Instance-Storage.md)、[当帧上传](../../wiki/architecture/render-graph/Frame-Data.md) | [实例实现](../../src/SomeEngine.Render/Instances/)、[Render 测试](../../tests/SomeEngine.Render.Tests/)、[Cluster 测试](../../tests/SomeEngine.Render.Cluster.Tests/) |
| 材质数据 | [材质](../../wiki/architecture/material/Material.md)、[材质范围](../../wiki/architecture/cluster/Material-Ranges.md)、[Bindless 扩展](../../wiki/architecture/material/MaterialBindless.md) | [Cluster 管线](../../src/SomeEngine.Render.Cluster.Pipeline/)、[材质实现](../../src/SomeEngine.Resources/Material.cs)、[Cluster 测试](../../tests/SomeEngine.Render.Cluster.Tests/) |
| RenderGraph | [持久图导航](../../wiki/architecture/Render-Graph.md)、[当帧数据](../../wiki/architecture/render-graph/Frame-Data.md)、[增量维护](../../wiki/architecture/render-graph/Incremental-Reconciliation.md)、[范围完成](../../wiki/architecture/render-graph/Range-Completions.md) | [RenderGraph](../../src/SomeEngine.RenderGraph/)、[RenderGraph 测试](../../tests/SomeEngine.RenderGraph.Tests/) |
| SomeUI 每帧更新 | [界面导航](../../wiki/architecture/SomeUI.md)、[重建顺序](../../wiki/architecture/someui/Rebuild-Schedule.md)、[布局](../../wiki/architecture/someui/Layout.md)、[渲染提取](../../wiki/architecture/someui/Render-Path.md) | [帧阶段](../../src/SomeEngine.Runtime/GameFrameSystems.cs)、[视图](../../src/SomeEngine.Views/)、[布局](../../src/SomeEngine.Layout/)、[元素提取](../../src/SomeEngine.Elements.Render/) |
| 动画阶段 | [动画导航](../../wiki/architecture/Animation.md)、[时间线](../../wiki/architecture/animation/Timeline.md)、[属性动画](../../wiki/architecture/animation/Property-Animation.md)、[来源合成](../../wiki/architecture/animation/Animation-Layers.md) | [动画核心](../../src/SomeEngine.Animation/)、[姿势](../../src/SomeEngine.Animation.Pose/)、[动画测试](../../tests/SomeEngine.Animation.Tests/)、[姿势测试](../../tests/SomeEngine.Animation.Pose.Tests/) |
| 设备资源与图形后端 | [RenderDevice](../../wiki/architecture/rendering/RenderDevice.md)、[RHI 导航](../../wiki/architecture/RHI/Index.md) | [Render](../../src/SomeEngine.Render/)、[Graphics](../../src/SomeEngine.Graphics/)、[D3D12](../../src/SomeEngine.Graphics.Direct3D12/)、[Vulkan](../../src/SomeEngine.Graphics.Vulkan/) |
| 资产与 Shader | [资产对象](../../wiki/architecture/Assets/Asset-Objects.md)、[Shader](../../wiki/architecture/Assets/Asset-Shaders.md) | [资产](../../src/SomeEngine.Assets/)、[使用对象](../../src/SomeEngine.Resources/)、[资产测试](../../tests/SomeEngine.Assets.Tests/) |
| 验证与性能 | [行为检查](../../wiki/harness/Behavior-Checks.md)、[性能测量](../../wiki/harness/Harness-Performance-Evidence.md) | [测试入口](../../tests/README.md)、[DefaultGame](../../samples/DefaultGame/)、[场景反馈](../../tools/Measure-Scenes.ps1) |

其它领域与跨领域依赖继续沿[领域读取规则](domain.md)选择。具体约束由对应专题和当前源码持有。
