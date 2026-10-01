# Vulkan Binding ABI

Vulkan pipeline layout 创建直接消费驻留 SPIR-V、`IComponentType`、`ShaderReflection` 与所选 `EntryPointReflection`。Bounded parameters 形成 pipeline-private descriptor-set layouts；unbounded declarations 使用 device-global descriptor indices。

SPIR-V decoration 到原生 set/binding 的映射只属于 Vulkan pipeline 创建结果。后端可以保存该原生对象所需的紧凑映射，但不得公开或持久化一棵通用 Shader ABI。command 热路径使用已经计算的 native placement。

同步创建当场消费代码；异步创建持有代码副本和 Slang COM 身份直到完成。pipeline 保留直接反射句柄所需的所有者。

`WriteDescriptor` 只改变 CPU staging；`PublishDescriptors` 统一提交更新。已录制命令保留它实际使用的 descriptor generation，后续 publish/dispose 不改变已接受 GPU work。

参见 [[architecture/shader/Shader-Block-Residency-Refactor]]。
