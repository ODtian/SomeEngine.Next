# Lighting

Lighting 是独立 rendering feature，负责 light extraction、light resources、spatial grouping、cookie resources 与 lighting policy。

具体 Shade implementation 可以消费 Lighting 提供的真实资源，这些资源通过 shader reflection 映射成普通 RenderGraph accesses；Cluster core 不建立 light grid。
## 灯光编码的完成点

`RenderLightSetSystem` 集中维护各类型灯光成员与本轮值，`LightingFeatureSystem` 取得该集合后
调用原有 `LightingFeature.Record`。Feature 持有按 `MaxLights` 预留的 `GpuLight[]`；
方向光、点光、聚光灯按顺序占据连续区间，类型偏移在编码前确定。
同步范围工作各自写入唯一一行，保留现有方向归一化与 GPU 字段值。

成员集合在本轮 Record 内保持有效，Feature 保持输出数组至上传结束。
所有编码参与者完成后，原 `graph.Upload` 复制本轮有效前缀并交付图资源使用期。
空灯光集合继续上传既有的零值占位记录。后续帧复用 Feature 数组，
实际上传长度按当前成员总数确定。

系统入口沿 `WorldJobs.TryGetScheduler` 使用 Rendering world 所选 Scheduler；
公共 Record 入口沿 `JobSystem.ExecuteParallel` 使用当前 Scheduler 关系。
两者共用 `JobScheduleOptions.AutomaticBatchSize` 与同一编码工作。
源码入口为 `src/SomeEngine.Render.Features/Lighting/LightingFeature.cs` 与 `LightingFeatureSystem.cs`。
