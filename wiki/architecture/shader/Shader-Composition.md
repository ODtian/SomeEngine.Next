# Shader Composition

## Definition

用户类型实现抽象 stage 接口；显式 Shader Block 编译行为把源码模块与 Slang `IEntryPoint` compose、specialize、link 成具体 `IComponentType`。内容构建和用户机器编译都是合法的执行位置。

## Invariants

- pipeline template 消费已经满足接口的用户实现，不重新解释 Material 业务语义。
- 一个 `.shader` 中 Slang 声明的入口按 Slang 顺序组成；引擎不保存入口选择或阶段镜像。
- 目标与特化使用 Slang `TargetDesc`、`SessionDesc`、`SpecializationArg`，不定义平行请求类型。
- draw/dispatch 时不搜索 Asset、不链接代码，也不动态发现接口实现。
- Surface、Vertex、Tessellation 等用户接口不绑定 Cluster、forward 或 deferred 身份。

## Source composition

项目维护 `.shader` 定义和顶层 `.slang` 组合入口。顶层入口选择通用阶段实现、材质实现与管线入口。引擎 Shader 库提供 `standard_material.slang`、`unlit_material.slang` 以及对应的通用阶段实现。

Cluster 的默认管线入口位于 `assets/Shaders/Pipeline/cluster_material.slang`，负责引入生成的实例布局、选择 Cluster 光栅变体，并包含实际管线入口。Cluster 的视图、深度和执行代码由对应的管线实现读取。项目的材质组合入口通过一条 include 选择该管线。

`ClusterShaderSources.BuildInstanceLayout` 根据已组合的实例属性布局生成 Slang；开发资产构建把结果写入项目 Shader 搜索路径的 `generated/render_instance_layout.slang`。管线入口从项目搜索路径取得生成布局。材质求值所需的公共输入见 [User Stage Interfaces](User-Stage-Interfaces.md)。

行为验证通过 `ShaderSourceBlock` 和 `SlangShaderImporter` 编译：同一份 Standard、Unlit 材质分别连接独立着色入口与实际 Cluster 入口，取得编译后的 DXIL 和 SPIR-V 代码。

## Relations

[[User-Stage-Interfaces]] · [[Shader-Build]]
