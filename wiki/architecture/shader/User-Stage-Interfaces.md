# User Stage Interfaces

## Definition

框架只定义管线算法真正依赖的 Slang 函数签名和常量。用户类型实现接口，并在显式 Shader Block 编译边界与具体 pipeline template 组成最终入口。

## Minimum programmable stages

- Vertex
- Surface Shade
- Tessellation

Deform、Raster、Shade 的执行兼容性由各 stage 自己的 Slang 类型与属性表达，不由 Material identity 决定。编译边界可以位于内容构建，也可以位于用户机器；返回的 `Shader` 在其生命周期内保证所引用数据驻留。

## Vertex data

`VertexEvalContext` 提供位置字节视图、属性字节视图，以及共享的局部顶点编号和实例变换。用户 `IVertexEvaluate` 解释这些字节并定义自己的 Vertex 类型；Cluster 通过 `getPosition` 和 `readLane` 取得其算法所需的行为。

默认 Opaque 线性材质的光栅入口使用位置求值，着色入口使用完整顶点求值。用户的可见性、变形与细分函数选择自己需要的数据。默认表示见 [Vertex Encoding](../cluster/Vertex-Encoding.md)。

## Relations

[[Shader-Composition]] · [[Conservative-Bounds]] · [[architecture/material/Stage-Compatibility]]
