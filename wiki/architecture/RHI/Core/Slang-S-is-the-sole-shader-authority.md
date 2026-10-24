# RHI-CORE-004 — Slang is the sole shader authority

RHI pipeline 描述直接接收 `IComponentType`、`ShaderReflection`、`EntryPointReflection`、`VariableReflection`、`VariableLayoutReflection` 等 Slang 类型，以及驻留代码的只读字节序列。

同步创建在调用期间消费这些对象；异步创建必须先持有 Slang COM 身份并复制或保留本次需要的代码存储，直到原生创建结束。pipeline 在其生命周期内保留使参数布局句柄有效的 Slang 所有者。

D3D12/Vulkan 只在 pipeline 冷路径从 Slang 派生原生布局。RHI 不保存通用 Shader 类型树、register map 或可独立变化的反射副本，也不从 DXIL/SPIR-V 反推 Slang 已提供的事实。

源码编译属于 Shader Block 的显式异步行为，可以发生在用户机器；RHI 创建入口本身不打开 Asset 或隐式编译。

参见 [[architecture/shader/Shader-Block-Residency-Refactor]] · [[architecture/shader/Slang-Authority]]。
^rhi-core-004
