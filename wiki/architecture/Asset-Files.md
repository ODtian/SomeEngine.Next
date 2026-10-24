# 资产架构导航

2026-10-01：当前规则由 [Asset 重构原则](Assets/Asset-Rebuild-Principles.md)、[数据共享与修改](Assets/Asset-Sharing-and-Writes.md)、[统一 CPU 字节缓存](Assets/Asset-Byte-Cache.md) 和 [Shader 总规范](shader/Shader-Block-Residency-Refactor.md) 共同确定，Shader 专项边界以后一份为准。实现审查及后续修复范围见 [审查记录](../../reviews/AssetSpecReview-2026-09-29.md)，当前缓存与规范对齐的执行结果见 [2026-10-01 验证](Assets/Asset-Verification-2026-10-01.md)。

## 当前模型

Asset 是身份、引用和生命周期单位；Block 是 Asset 内开放、强类型的内容单位。Asset 按 Block 的持久类型身份直接索引，不保证全部数据驻留。Block 自己负责数据表达和取得；普通字段使用 C# 值与集合，发布字节使用托管数组。Texture tile、mip 和 Mesh 几何页定义业务取得粒度，不附带通用 CPU 页存储。具体系统取得需要的 Block 后创建可直接使用的业务对象。

项目引用决定可用 Block 与 Importer。SG 生成 Microsoft DI 注册与能力描述，来源设置决定本次启用项，Context 缓存依赖执行；完整来源读取、遍历、目标身份、保存登记、失败处理及重新导入由工程流程统一负责。Scene 引用语义 MeshAsset，而不是某个管线表示。

## 设计、任务和证据

[重构原则](Assets/Asset-Rebuild-Principles.md) 和主题页定义行为；API 文档说明具体调用方式；[统一任务](Assets/Asset-Rebuild-Tasks.md) 保存有日期的交付范围；[执行记录](Assets/Asset-Rebuild-Execution.md) 提供当前结果导航。[2026-09-16 验证](Assets/Asset-Verification-2026-09-16.md) 保存原 43 项任务的测试、AOT 和应用证据，[2026-09-20 验证](Assets/Asset-Verification-2026-09-20.md) 保存后续 Block/Importer 范围，[2026-09-30 验证](Assets/Asset-Verification-2026-09-30.md) 保存业务数据共享与托管存储重构的范围。审查问题是否关闭以具名修复及对应证据为准。

被替代的架构规则从现行页面删除，由 Git 历史保存。按日期记录的验证事实和审查证据保留其执行范围，不作为当前 API 示例，也不把旧阶段通过结果合并为当前验收通过。

## 主题

[对象模型](Assets/Asset-Objects.md) · [职责](Assets/Asset-Responsibilities.md) · [编译期生成](Assets/Asset-Code-Generation.md) · [导入与工程存储](Assets/Asset-Import-and-Storage.md) · [身份与引用](Assets/Asset-Identity-and-Dependencies.md)

[文件读写](Assets/Asset-File-IO.md) · [取得和生命周期](Assets/Asset-Loading-and-Lifetime.md) · [共享与写入](Assets/Asset-Sharing-and-Writes.md) · [验证](Assets/Asset-Validation.md) · [已解决设计与边界](Assets/Asset-Open-Questions.md)

[Shader](Assets/Asset-Shaders.md) · [Texture](Assets/Asset-Textures.md) · [Mesh](Assets/Asset-Meshes.md) · [Material](Assets/Asset-Materials.md) · [World](Assets/Asset-Worlds.md) · [项目与渲染接入](Assets/Asset-Application-and-Rendering.md)

## 与其他系统的关系

沿用既有静态编码、文档、范围 I/O、包、签名、差分、原生布局与 ECS。读取并发和在途工作由实际操作约束，不保存每文件长期解码缓存。使用对象不反向依赖资产设施。Game world 到 Rendering world 的数据通路和用户 Shader 解释权保持，未增加几何提交旁路或另一套 GPU 生命周期。

验证结果按日期、平台与范围分别记录：2026-09-16 包括 Vulkan 非呈现测试、硬件 D3D12 独立项目及六种场景，没有完成的 WARP 完整应用尝试不计成功；2026-09-29 的 Shader 记录另有三场景结果，同时存在后续审查发现的组合测试崩溃。多个独立文件的导入回退不等于断电时整体原子性。
