# 资产架构导航

2026-09-16：Asset 重构的 A00–A33、B00–B08 共 43 项任务已按原范围完成实现与验收。重新核对原对话后，又补齐进程内 Block 的保存依赖及 Context 并发关闭等待两项公开行为，最终完整回归为 2625 项通过、0 失败、3 项原有条件跳过。新导入架构已同步原主题页；历史记录保留其当时范围，不再作为当前 API 示例。

## 当前模型

Asset 是身份、引用和生命周期单位；Block 是 Asset 内开放、强类型的内容单位。Asset 按 Block 的持久类型身份直接索引，不保证全部数据驻留。Block 自己负责数据表达和取得，现有页管理负责底层分页、读取、预算与驻留；具体系统取得需要的 Block 后创建可直接使用的业务对象。

项目引用决定可用 Block 与 Importer。SG 生成 Microsoft DI 注册与能力描述，来源设置决定本次启用项，Context 缓存依赖执行；完整来源读取、遍历、目标身份、保存登记、失败处理及重新导入由工程流程统一负责。Scene 引用语义 MeshAsset，而不是某个管线表示。

## 设计、任务和证据

[重构原则](Assets/Asset-Rebuild-Principles.md) 包含既有边界和新导入架构；[统一任务](Assets/Asset-Rebuild-Tasks.md) 记录 43 项交付；[执行记录](Assets/Asset-Rebuild-Execution.md) 保留实施过程；[2026-09-16 验证](Assets/Asset-Verification-2026-09-16.md) 记录最终完整回归、额外 18 项图形测试、三套原生 AOT、独立来源构建、默认项目及六种真实渲染场景，并区分历史结果与平台边界。

## 主题

[对象模型](Assets/Asset-Objects.md) · [职责](Assets/Asset-Responsibilities.md) · [编译期生成](Assets/Asset-Code-Generation.md) · [导入与工程存储](Assets/Asset-Import-and-Storage.md) · [身份与引用](Assets/Asset-Identity-and-Dependencies.md)

[文件读写](Assets/Asset-File-IO.md) · [取得和生命周期](Assets/Asset-Loading-and-Lifetime.md) · [共享与写入](Assets/Asset-Sharing-and-Writes.md) · [验证](Assets/Asset-Validation.md) · [已解决设计与边界](Assets/Asset-Open-Questions.md)

[Shader](Assets/Asset-Shaders.md) · [Texture](Assets/Asset-Textures.md) · [Mesh](Assets/Asset-Meshes.md) · [Material](Assets/Asset-Materials.md) · [World](Assets/Asset-Worlds.md) · [项目与渲染接入](Assets/Asset-Application-and-Rendering.md)

## 与其他系统的关系

沿用既有静态编码、文档、范围 I/O、调度、预算、包、签名、差分、原生布局与 ECS。使用对象不反向依赖资产设施。Game world 到 Rendering world 的数据通路和用户 Shader 解释权保持，未增加几何提交旁路或另一套页、GPU 生命周期。

Vulkan 非呈现回归通过，但现有呈现限制未改变；最新真实应用多帧验证覆盖硬件 D3D12 的独立项目和六种场景，未完成的 WARP 尝试不计成功。多个独立文件的导入回退不等于断电时整体原子性。具体边界与性能参数以当前验证记录为准。
