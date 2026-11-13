# Lighting

Lighting 是独立 rendering feature，负责 light extraction、light resources、spatial grouping、cookie resources 与 lighting policy。

具体 Shade implementation 可以消费 Lighting 提供的真实资源，这些资源通过 shader reflection 映射成普通 RenderGraph accesses；Cluster core 不建立 light grid。

`LightingResources` 集中声明对外的资源名、绑定类型、资源身份、同步阶段与访问模式。`LightingFeature` 提供材质和 VSM 所需的灯光、cell 列表及 cookie 资源。

[Clustered-Light-Binning](../rendering/Clustered-Light-Binning.md) 由 `LightingFeature` 持有，使用 `LightListCapacity` 控制全局引用容量、`MaxLightsPerCell` 控制单 cell 上限。准备阶段按灯光优先级建立上传顺序，VSM 与材质共同使用该下标。分箱与容量读回由共享 ScreenBinning 链路执行。

透明功能持有自己的 tile 尺寸与深度切片参数。透明研究的行为检查由工具自己的测试项目持有。
