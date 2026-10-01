# RHI-D3D12-003 — Native root signatures and pipelines

D3D12 pipeline 创建直接消费驻留 DXIL、`IComponentType`、`ShaderReflection`、所选 `EntryPointReflection`、static samplers、pipeline kind 与设备能力。在冷路径遍历 Slang 布局并建立 backend-private root parameters、descriptor ranges 与参数块放置。

pipeline 保留 Slang COM 身份，使 `VariableLayoutReflection` 等非拥有句柄在参数绑定期间有效。异步创建在调度前持有本次代码和 Slang 所有者；调用方随后释放或更新 `Shader` 不影响任务。

Serialized root signatures 可以按 bytes 去重。Graphics/Compute/Mesh 建立 native PSO；DXR/Work Graph 建立相应 native state object。完成的对象只公开 RHI 行为，不公开另一棵 Shader 反射树。

参见 [[architecture/shader/Shader-Block-Residency-Refactor]]。
^rhi-d3d12-003
