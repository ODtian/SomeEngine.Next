# Job Command Production

Job-side structural work 使用 job-scoped command segments，而不是可逃逸的普通 CommandBuffer writer。

每个 producer key 对应私有 segment；producer callback 结束后 segment 封闭。CreateEntity 返回 segment 内 deferred identity，真正 Entity 在 deterministic merge/playback 时分配。

Playback 依赖所有 producers 成功结束；任一 producer 或其后代失败时整批不执行 partial playback。

`JobCommandBuffer.Schedule(producerKey, producer, accesses, ...)` 为串行 producer 持有显式组件和外部资源，复用原私有 segment、回放和释放。串行查询可在实际写声明内原位更新组件，同时记录结构命令。

异步回放在 producer 封闭后读取原命令头，汇总 Table、Sparse 与 Buffer 的 Replace 写入资源。结构写入沿原拓扑访问执行；含 Replace 的批次将实际列写声明交给同一回放适配器。普通结构批次在原工作项中直接回放，返回完成句柄覆盖列资源绑定、回放后代与最终释放。

源码入口为 [JobCommandBuffer](../../../src/SomeEngine.ECS.Systems/JobCommandBuffer.cs) 和 [CommandBuffer](../../../src/SomeEngine.ECS/Commands/CommandBuffer.cs)。物理筛选命令及实际运动输入通过 [PhysicsActivityTests](../../../tests/SomeEngine.Physics.Tests/PhysicsActivityTests.cs) 接续；原回放、失败清理和资源容量行为见 [JobCommandBufferTests](../../../tests/SomeEngine.ECS.Systems.Tests/JobCommandBufferTests.cs)。
