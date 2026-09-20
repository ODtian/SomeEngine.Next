# RHI-D3D12-003 — Native root signatures and Pipelines

D3D12 root-signature construction consumes cooked shader code、Slang# detached reflection/layout、static samplers、Pipeline kind 与 Device capability facts，在 cold path 建立 backend-private native placement。

不要求 shipping 进程保留 Slang compiler session 或 live reflection handle。Serialized root signatures 可以按 bytes 去重；每个 Pipeline 只保留实际执行所需的 native objects、cooked shader data 与 binding placement。

Graphics/Compute/Mesh 建立 native PSO；DXR/Work Graph 建立相应 native state object。同步和异步创建共享同一核心与 cache key；异步成功即表示 native objects 已可直接绑定。

参见 [[architecture/shader/Detached-Reflection]]。
^rhi-d3d12-003
