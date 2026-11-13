# Material Entries

材质 shader 定义的 `SourceNames` 列出材质模块。用于绘制的定义包含一个实现 `IMaterial` 的具体类型，并用 associatedtype 指定自己的 Vertex、Domain 和 Visibility；材质保留完整的 `evaluateSurface<TOutput>` 与调试求值。

具体材质类型声明 `VertexEncoding` 和 `ConservativeBounds` 属性。前者标明材质几何使用的顶点编码，后者声明 `finite` 或 `unbounded` 的包围支持。导入器把这两项复制到生成入口；既有 Cluster 消费者继续按入口属性核对几何兼容性。

`RasterFaces` 可在材质类型声明屏幕与阴影目标的 `front` 或 `both`；默认沿用前面光栅。生成器将其传给内部光栅配置并保留到 pixel entry。`VisibilityDepth` 由材质选择的 Visibility 类型声明，生成模块的 `VisibilityEvaluate` 别名继续交付该类型的原有属性。

公开材质通过 `Shader`、具体属性和 `Layer` 表达行为。原 `Material.State`、`MaterialState`、`SurfaceMode` 已退役；覆盖和深度来自用户 Visibility，层路由来自 `Material.Layer`。同一着色实现需要前面与双面两种用途时，用两个声明了相应 `RasterFaces` 的材质组合烘焙入口，完整 `evaluateSurface` 继续复用同一材质库。DefaultGame 的 `standard`、`unlit`、`deformed-phong` 保留前面入口，对应的 `*-both` 组合供双面层样例使用。

材质位移的 aggregate 包围仍由 `Material.BoundsExpansion` virtual 属性和实例半径进入原提取、BVH 与 cluster 裁剪链；Domain 的 `bounds`、`patchBounds` 和 `errorBounds` 描述已取得原始三角形后的曲面与子域。两级包围的空间含义见 [Conservative-Bounds](Conservative-Bounds.md#材质位移范围)。

`MaterialShaderEntries` 在导入时先编译材质的 Slang IR，通过模块声明反射和 `IsSubType` 找到 `IMaterial` 类型。随后在内存中生成 `<定义名>.entries.slang`，把生成模块的 IR 与材质 IR 一起交回原有 `ShaderAsset.CompileAsync`，保存到 `ShaderSourceBlock` 和 `ShaderBlock`。输出资产包含生成模块的名称与 IR，源 `.shader` 继续列出用户材质模块。

`ClusterShadersBlock.MaterialEntries` 声明两个着色目标与调试特化表：

| 入口 | 输入 | 输出 | 特化参数 |
|---|---|---|---|
| `ClusterShadeMode` | `VisibilityBufferInput` | `OpaqueOutput` | `0`、`8`、`9`、`10`、`11` |
| `LayerShadeMode` | `LayerFragmentInput` | `LayerPayloadOutput` | `0`、`8`、`9`、`10`、`11` |
| `ClusterForwardShade` | `VisibilityBufferInput` | `OpaqueOutput` | 深度模式 `0`、`1`、`2` 与五种调试模式的组合 |
| `LayerRasterSoftware` | 材质的几何组合 | tile 片元存储与页请求 | `LayerNoPageRequests`、`LayerShadowPageRequests<32>` |

生成 wrapper 内部封闭具体材质类型，调用同一个 `ShadeSurface` 泛型。调用方以 `RequireSpecialization("LayerShadeMode", [mode])` 取得已加载的 native 代码，再按同名入口创建管线。材质类型与输入、输出类型的组合在烘焙时确定。

屏幕与阴影目标的普通光栅入口复用 `cluster_material_entries.slang`；带细分标记时再复用 `cluster_tessellation_entries.slang`。展开在引擎内部命名空间中，保留既有 SW、VS、MS、PS、target 与细分角色的入口名、参数和属性。`SurfaceEvaluate` 组合转发同一个材质实现，服务既有的基础 `ClusterShade` 入口；两个目标的调试特化直接调用 `BakedMaterial`。内部模板的 `CLUSTER_RASTER_BOUNDS` 与细分包围设置由材质显式属性填写。

层硬件入口 `LayerRasterVertex` / `LayerRasterPixel` 同时为普通与细分 Domain 生成。细分 VS 直接复用 Core 的 final-patch 顶点求值、原始三角形身份和 varying 传递，使用引擎自己的 patch 索引流。绘制接入仍需复用对应的索引缓冲与间接 indexed draw。DefaultGame 的 standard、unlit、deformed-phong 以各自的单个 `IMaterial` 包装现有材质库，资源继续按叶名绑定。

两个着色输入均以 Vertex 和 Domain 为类型参数，从原始三角形身份和光栅深度恢复三角形与目标位置，调用用户 `Domain.reconstruct` 反算重心坐标与导数。Domain 带 `TessellatedSurface` 标记时，tile 光栅入口调用 `RasterizeLayerTileSurface` 并传入 Domain；普通 Domain 调用 `RasterizeLayerTile`。`VertexEvaluate`、`DomainEvaluate`、`VisibilityEvaluate`、`SurfaceEvaluate` 别名由生成模块提供，供现有反射消费路径使用。

`LayerRasterSoftware` 使用 32 线程工作组，wave 宽度由设备选择。线性路径通过工作组共享数组缓存 cluster 的 64 个 clip 坐标和完整用户 varying，在填充后与下一 cluster 复用前同步；三角形按局部顶点下标读取这些数组。细分路径的顶点求值与像素分发复用引擎按当前 wave 宽度工作的传输方法。每个工作组继续负责一个 tile 和一个几何阶段。

`ShaderImports` 在 `BuildAllAsync` 的依赖发现阶段加入共享输入、着色入口、写出目标与 tile 光栅库。细分库作为可共享的潜在目标依赖参与构建；实际生成代码由 Domain 标记选择。项目外的共享库由其内容目录提供，项目内的库在材质编译之前构建。

生成模块读到的模板与 include 路径从 Slang 模块依赖列表合入原有 `ImportTrace`。修改引擎光栅模板会触发相应材质重新烘焙，生成源码通过内存处理和 IR 序列化交付。

`MaterialEntryImportTests` 验证双后端 native 特化保存与恢复、生成目标库的构建顺序，以及带细分标记时 tile 光栅入口使用细分资源、两个着色入口直接由 Domain 反算。临时材质源由检查自行清理，生成入口通过内存 IR 交付。
