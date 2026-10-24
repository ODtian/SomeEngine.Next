# Lighting

Lighting 是独立 rendering feature，负责 light extraction、light resources、spatial grouping、cookie resources 与 lighting policy。

具体 Shade implementation 可以消费 Lighting 提供的真实资源，这些资源通过 shader reflection 映射成普通 RenderGraph accesses；Cluster core 不建立 light grid。