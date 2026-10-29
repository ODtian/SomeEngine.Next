# Lighting

Lighting 是独立 rendering feature，负责 light extraction、light resources、spatial grouping、cookie resources 与 lighting policy。

具体 Shade implementation 可以消费 Lighting 提供的真实资源，这些资源通过 shader reflection 映射成普通 RenderGraph accesses；Cluster core 不建立 light grid。

软件 GI 消费同一组灯光资源，发光几何体直接光由独立采样与历史重建产生。主表面材质分别合成直接光与屏幕空间探针积分；命中表面的反射辐射由 SHARC／NRC 复用。数据职责、源码入口与调用顺序见 [GI 架构](../../../docs/GI-Architecture.md)。
