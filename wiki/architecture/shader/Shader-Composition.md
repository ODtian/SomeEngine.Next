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

公共代码以 Slang `import` 组合，模块 IR 由 `ShaderLibrary` 按名称加载一次。逐 shader 宏配置与生成实例布局通过 `#include` 注入，清单由 [Shader Build](Shader-Build.md) 维护。模块依赖作为 asset 引用保存在 `SourceBuildRecord`。Slang 对象提供全部反射数据。

项目维护 `.shader` 定义和顶层 `.slang` 组合入口。顶层入口选择通用阶段实现、材质实现与管线入口。引擎 Shader 库提供 `standard_material.slang`、`unlit_material.slang` 以及对应的通用阶段实现。

Cluster 的入口模板位于 `cluster_material_entries.slang`、`cluster_tessellation_entries.slang`，通用入口位于 `cluster_draw.slang`、`sw_raster.slang` 和 `cluster_tessellation_phong.slang`。通用入口以 `IVertexEvaluate` 以及相应的可见性、曲面接口为泛型参数。材质组合通过类型别名选择自身的解释方式。

标准材质组合的深度和阴影入口选择 `StaticPositionEval`；着色入口选择 `StaticVertexEval`。自定义材质在组合源码中选择自己的顶点求值。`DefaultVisibilityEvaluate` 位于标准材质诊断 shader。`standard_material.slang`、`unlit_material.slang` 与接口输入均通过独立 `.shader` 资产提供共享 IR，具体组合保存对应的资产引用。

派生材质沿用模板的 `IMaterial` 类型声明，具体类型选择自身的 `Vertex`、`Domain`、`Visibility` 与曲面实现。`MaterialShaderEntries.Add` 从 Slang 取得该类型及其属性，保留材质 IR，再生成使用该类型的入口 IR；`ShaderSourceBlock.SourceNames` 保存这两个单元，入口与全部特化由同一导入行为完成。

`ClusterShaderSources.BuildInstanceLayout` 根据已组合的实例属性布局生成 Slang；内容构建把结果写入引擎与项目 Shader 搜索路径的 `generated/render_instance_layout.slang`。依赖这些常量的实例读取与入口配置头文件在调用者编译单元展开。材质求值所需的公共输入见 [User Stage Interfaces](User-Stage-Interfaces.md)。

行为验证通过 `ShaderLibrary` 加载独立公共输入资产，再组合两个类型化入口 Block；保存的资产使用公共输入 GUID，恢复阶段在空源码文件系统中取得相同的入口与代码。

Cluster 管线创建时通过 `ShaderParameterResources.Create(shader, target, entry...)` 取得完整的 Slang 字段顺序与入口使用信息。图构造期按字段名从已有 `GraphResourceBinder` 表连接使用中的资源；其余字段保留对应类型的空绑定位置。资源表在创建 pass 前就绪，计算、光栅、层级初始化与剔除共用这套绑定过程。draw/dispatch 使用 RG 已缓存的绑定，名称解析与反射集中在创建和图构造阶段。

## Relations

[[User-Stage-Interfaces]] · [[Shader-Build]]
