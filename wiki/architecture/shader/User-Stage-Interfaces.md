# User Stage Interfaces

## Definition

框架只定义管线算法真正依赖的 Slang 函数签名和常量。用户类型实现接口，并在显式 Shader Block 编译边界与具体 pipeline template 组成最终入口。

## Minimum programmable stages

- Vertex
- Surface Shade
- Tessellation

Deform、Raster、Shade 的执行兼容性由各 stage 自己的 Slang 类型与属性表达，不由 Material identity 决定。编译边界可以位于内容构建，也可以位于用户机器；返回的 `Shader` 在其生命周期内保证所引用数据驻留。

## Surface input

`ISurfaceEvaluate<TSurface, TTriangle>` 接收用户定义的曲面、三角形和 `SurfacePixelInput`。后者包含输出像素坐标、实例编号、材质编号、相机世界坐标，以及相邻屏幕像素对应的世界坐标差 `PositionDx` / `PositionDy`。材质通过这些显式输入进行着色和调试求值。

管线负责从自己的视图和深度资源取得这些输入。Cluster 的着色实现从深度图重建屏幕邻域，选择临近前景深度处理轮廓，再把坐标差传入材质。Standard 用自己的曲面位置加上该坐标差计算阴影接收点，并自行解释纹理、BRDF、共享灯光网格与阴影资源。管线继续通过泛型接口传递用户曲面和三角形。

`IVertexEvaluate` 使用通用变换数据和用户顶点表示；Cluster 的实例布局及资源读取由 Cluster 实现持有。

`IDomainEvaluate<TVertex>.makeTriangle(first, second, third, materialIndex)` 接收三个用户顶点和当前三角形对应的材质标量地址。Shade 与 Tessellation 都按实例材质绑定起点加上 Cluster 三角形的局部材质编号取得该地址。用户 `TriangleData` 可保存它，再通过 `LoadMaterialScalars<T>` 解释自己的参数结构；两个阶段共用 `MaterialScalarRegion` 和 `MaterialScalarOffsets`。曲面继续提供 `position`、`eval`、`reconstruct`、`bounds`、`patchBounds` 与 `errorBounds`，细分与着色使用同一数学定义。

公共 Cluster 材质入口和细分入口都接受 `CLUSTER_VERTEX_ENCODING`，值与调用方编码器的身份一致。细分入口通过 `CLUSTER_TESSELLATION_BOUNDS` 声明有限包围或对应的裁剪配置。实现分别见 [domain_interfaces](../../../assets/Shaders/domain_interfaces.slang)、[cluster_tessellation_entries](../../../assets/Shaders/cluster_tessellation_entries.slang) 与 [ClusterPipelineSystem.Materials](../../../src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Materials.cs)。

## Relations

[[Shader-Composition]] · [[Conservative-Bounds]] · [[architecture/material/Stage-Compatibility]]
