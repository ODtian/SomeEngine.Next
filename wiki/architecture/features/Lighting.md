# Lighting

Lighting 是独立 rendering feature，负责 light extraction、light resources、spatial grouping、cookie resources 与 lighting policy。

具体 Shade implementation 可以消费 Lighting 提供的真实资源，这些资源通过 shader reflection 映射成普通 RenderGraph accesses；Cluster core 不建立 light grid。

软件 GI 消费同一组灯光资源，发光几何体直接光由重要性树采样与历史重建产生。用户材质提供主表面与直接反射辐射，本帧过滤后的屏幕空间探针由接收像素积分并合成到材质颜色；命中表面的完整反射辐射由 SHARC／NRC 复用。数据职责、源码入口与调用顺序见 [GI 架构](../../../docs/GI-Architecture.md)。
