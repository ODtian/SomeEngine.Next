# Lighting

Lighting 是独立 rendering feature，负责 light extraction、light resources、spatial grouping、cookie resources 与 lighting policy。

具体 Shade implementation 可以消费 Lighting 提供的真实资源，这些资源通过 shader reflection 映射成普通 RenderGraph accesses；Cluster core 不建立 light grid。
## 局部光列表

- `MaxLightsPerCell` 是全局列表预算的每 cell 平均值，默认仍为 64；总容量为 cell 数 × view 容量 × 此预算。它不再限制单个 cell 的有效灯光数。
- 分配 pass 从有界共享 link pool 保留相交灯光，compact pass 发布每 cell 的 offset/count。Standard shading、像素与 froxel 页请求、局部光投影均消费相同的完整列表。
- 全局预算耗尽时，饱和分配器阻止越界，`LightingFeatureResources.AllocationStatus` 的一个 uint 置为非零，表示本帧有未保留的灯光分配。它可由任意 Lighting 消费者读取，不依赖 VSM。没有隐式全灯回退或成功降质。
- 现有可选 `VirtualShadowFrameQuality.LightAssignmentOverflow` 捕获同一状态；普通帧不新增 CPU readback，也不因容量不足引入新的 fatal exception。

机制对照固定 UE ref `396c9f059903aed5fec78ecd3d437a40c6415368`：
[默认 linked-list 模式与全局预算](https://github.com/EpicGames/UnrealEngine/blob/396c9f059903aed5fec78ecd3d437a40c6415368/Engine/Source/Runtime/Renderer/Private/LightGridInjection.cpp#L123-L136)，
[有界 link 分配与 compact offset/count](https://github.com/EpicGames/UnrealEngine/blob/396c9f059903aed5fec78ecd3d437a40c6415368/Engine/Shaders/Private/LightGridInjection.usf#L453-L517)。UE thread-group 路径的 16M packing 上限不移植为本引擎的额外容量限制。
