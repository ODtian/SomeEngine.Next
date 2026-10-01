# Shader Composition

## Definition

用户类型实现抽象 stage 接口；显式 Shader Block 编译行为把源码模块与 Slang `IEntryPoint` compose、specialize、link 成具体 `IComponentType`。内容构建和用户机器编译都是合法的执行位置。

## Invariants

- pipeline template 消费已经满足接口的用户实现，不重新解释 Material 业务语义。
- 一个 `.shader` 中 Slang 声明的入口按 Slang 顺序组成；引擎不保存入口选择或阶段镜像。
- 目标与特化使用 Slang `TargetDesc`、`SessionDesc`、`SpecializationArg`，不定义平行请求类型。
- draw/dispatch 时不搜索 Asset、不链接代码，也不动态发现接口实现。
- Surface、Vertex、Tessellation 等用户接口不绑定 Cluster、forward 或 deferred 身份。

## Relations

[[User-Stage-Interfaces]] · [[Shader-Build]]
