# Shader Build

## Definition

Shader 构建是一次显式异步 Block 行为。它可以发生在内容导入、应用启动、用户机器或运行中的资源更新边界；这些位置调用同一套 Slang compose、specialize、link、反射与 codegen 行为。

## Invariants

- `.shader` 是人工维护的 `ShaderAsset` 来源；二进制 `ShaderSourceBlock` 保存可由 Slang 重新加载的序列化模块，不依赖编辑机器路径。
- `ShaderSourceBlock.CreateAsync` 使用调用方提供的 `IGlobalSession`、`SessionDesc` 与 `SpecializationArg` 编译并返回驻留完成的 `Shader`。
- `ShaderBlock.CreateAsync` 从同一来源恢复直接 Slang 对象，将所需保存代码物化为最终 GC 数组；`Shader` 通过普通引用持有这些数组，不接收页释放责任。
- 保存的整 target 代码是可选的；逐入口代码可以独立存在。缺少适用代码时，调用方可显式走 `ShaderSourceBlock` 编译路径。
- pipeline 创建只消费已驻留 `Shader` 的直接 Slang 对象与代码，不隐式打开 Asset 或启动编译。
- 名称查找和反射发生在创建材质/pipeline 等冷路径；命令热路径只使用消费者已经派生的数据。

## 当前实现差距

以上是共同编译边界的要求。当前原生源码编译仍位于 Importer，`ShaderSourceBlock.CreateAsync` 只恢复并编译已有 IR；保存代码读取仍经过页再复制为数组。两项分别见 [2026-09-29 审查](../../../reviews/AssetSpecReview-2026-09-29.md) I06、I11，不能作为已经完成统一取得和存储的证据。

## Relations

[[Slang-Authority]] · [[Shader-Composition]] · [[Shader-Block-Residency-Refactor]]
