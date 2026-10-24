# Slang Authority

Shader 的语言语义、模块、入口、阶段、特化、类型、布局、属性与资源使用事实只来自当前有效的 Slang 对象。

实现与完整不变量见 [[Shader-Block-Residency-Refactor]]。本页只保留导航，避免重复维护另一份规则。

引擎消费者可以从 Slang 直接派生自身需要的冷路径数据，但不得序列化或公开一棵平行 Shader 反射树。官方 `SlangShaderSharp` NuGet 是绑定来源；引擎不维护本地 Shader 语义 fork。

## Relations

[[Shader-Build]] · [[Shader-Block-Residency-Refactor]]
