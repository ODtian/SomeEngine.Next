# User Stage Interfaces

## Definition

框架只定义管线算法真正依赖的 Slang 函数签名和常量。用户类型实现接口，并在显式 Shader Block 编译边界与具体 pipeline template 组成最终入口。

## Minimum programmable stages

- Vertex
- Surface Shade
- Tessellation

Deform、Raster、Shade 的执行兼容性由各 stage 自己的 Slang 类型与属性表达，不由 Material identity 决定。编译边界可以位于内容构建，也可以位于用户机器；返回的 `Shader` 在其生命周期内保证所引用数据驻留。

## Relations

[[Shader-Composition]] · [[Conservative-Bounds]] · [[architecture/material/Stage-Compatibility]]
