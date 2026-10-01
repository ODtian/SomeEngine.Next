# RHI-CORE-009 — Pipeline descriptions and identity

Graphics、Compute、Mesh、Ray Tracing 与 Work Graph 描述包含直接 Slang 所有者/反射句柄、所选入口的驻留代码视图，以及所需固定功能或能力状态。

Labels 只用于 diagnostics，不进入 compatibility 或 cache key。

同步和异步创建产生相同设备结果。异步入口在返回任务前接管所有 span-backed 描述数据、代码字节与 Slang COM 引用；原 `Shader` 随后更新或释放也不能使创建任务悬空。完成的 pipeline 已可绑定，首次使用不会打开 Asset、编译或链接 Shader。

pipeline 的原生 identity 和 cache 数据是后端派生结果，不能回写成通用 Shader 反射。

参见 [[architecture/shader/Shader-Build]] · [[architecture/shader/Shader-Block-Residency-Refactor]]。
^rhi-core-009
