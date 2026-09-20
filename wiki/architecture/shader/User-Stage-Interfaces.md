# User Stage Interfaces

## Definition
框架只定义管线算法真正依赖的函数签名和常量；用户类型实现接口并在 build 阶段与具体 pipeline template 组成最终 entry。

## Minimum programmable stages
- Vertex
- Surface Shade
- Tessellation

Deform、Raster、Shade 的执行兼容性由各 stage 自己定义，不由 Material identity 决定。

## Relations
[[Shader-Composition]] · [[Conservative-Bounds]] · [[architecture/material/Stage-Compatibility]]