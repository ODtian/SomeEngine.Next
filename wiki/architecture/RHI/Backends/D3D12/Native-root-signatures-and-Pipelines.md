# RHI-D3D12-003 — Native root signatures and Pipelines

D3D12 root-signature construction consumes cooked shader code、Slang# detached reflection/layout、static samplers、Pipeline kind 与 Device capability facts，在 cold path 建立 backend-private native placement。

不要求 shipping 进程保留 Slang compiler session 或 live reflection handle。Serialized root signatures 可以按 bytes 去重；每个 Pipeline 只保留实际执行所需的 native objects、cooked shader data 与 binding placement。

Graphics/Compute/Mesh 建立 native PSO；DXR/Work Graph 建立相应 native state object。同步和异步创建共享同一核心与 cache key；异步成功即表示 native objects 已可直接绑定。

参见 [[architecture/shader/Detached-Reflection]]。

同一不可变链接 shader 数据的内容哈希与 entry bytecode 在管线创建时共享。
内容哈希覆盖原完整序列化数据，entry 哈希覆盖原 bytecode；管线缓存 key 继续
包含相同内容字段。数据通过弱引用关联原链接 shader，首次读取受同一数据锁保护，
后续管线沿既有布局、固定状态与原生创建路径建立。
^rhi-d3d12-003
