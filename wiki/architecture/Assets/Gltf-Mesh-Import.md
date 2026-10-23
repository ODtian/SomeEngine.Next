# glTF Mesh Import

## Definition
glTF 网格导入的顺序与算法，目标是符合 glTF 规范，并让从 Blender 导出的模型导入后与 Blender 中看到的一致。坐标约定与 glTF 相同，不做转换（ADR 0017）。Block 划分见 [[architecture/Assets/Block-Division|Block Division]]。

## Current design
中立网格边界与 Cluster 表示显式取得同一份已解码顶点流，压缩来源的 fallback 字节由解码生产者处理。纯线、点及全部跳过的网格通过可选 Cluster 输出表达，场景节点保留变换与层级，绘制来源按可用几何创建。

来源级依赖按需拉取，不写死顺序；实际依赖链为：纹理 → 材质 → 骨架 → 网格 → 动画 → 场景组装。

单个网格分阶段处理，每个阶段由一个 Importer 负责：

| 阶段 | 产出 | 内容 |
|---|---|---|
| 1 准备 | `VertexStreamsBlock`（仅导入进程内） | 解压 meshopt 扩展；按解码库支持情况处理 Draco；反量化（`KHR_mesh_quantization`）与展开稀疏 accessor；三角带、三角扇转为三角形列表；线、点图元单独跳过并报告；无索引时生成顺序索引；缺 NORMAL 时生成平面法线（规范要求）；材质有法线贴图且缺 TANGENT 时用 MikkTSpace 生成，UV 集取材质 `normalTexture.texCoord`；颜色统一为线性 RGBA；其余属性保持来源数值 |
| 2 几何 | `ClusterGeometryBlock` | 合并图元为材质区段；对所有存在的顶点流（含蒙皮与形变增量）逐位比较后焊接，保留硬边与 UV 接缝；按 Deform 分区（静态、蒙皮、形变、蒙皮加形变），分区后才建 cluster；LOD 简化时锁住 UV 接缝、材质边界与法线不连续处，并保护蒙皮影响与形变增量的变化 |
| 3 顶点属性 | `ClusterVertexAttributesBlock` | 全部法线、切线、UV 集、颜色与自定义属性，按 cluster 编码，记录兼容性标识 |
| 4 蒙皮 | `SkinBindingBlock` + `ClusterSkinInfluencesBlock` | 逆绑定矩阵与关节到骨架的映射归属 skin 的 `SkeletonAsset`；每个顶点的全部 `JOINTS_n`/`WEIGHTS_n`，数量与数值照搬来源；规范要求权重和为 1，只检查并报告，不修改 |
| 5 形变 | `MorphTargetSetBlock` + `ClusterMorphDeltasBlock` | 网格目标名称取 `mesh.extras.targetNames`，默认权重取 `mesh.weights`；逐目标全部属性增量与来源切线增量 |

## Invariants
- 引擎不设阈值：骨骼影响数量、UV 集、颜色集照搬来源；如何使用由着色器决定。
- 不重算来源已有的法线；不跨不同法线、UV 或任何顶点流焊接。
- 切线生成必须使用 MikkTSpace，与 Blender 烘焙法线贴图的方式一致。
- 蒙皮网格所在节点的变换按规范在蒙皮时忽略；镜像变换由运行时按行列式处理绕序。

## Evidence
Blender 导出行为与对应要求：

| Blender 导出 | 导入要求 | 当前代码 |
|---|---|---|
| 逐角法线（平滑组、自定义拆分法线已按角拆分顶点） | 不重算，不跨法线焊接 | 符合（按属性逐位去重） |
| 默认不导出切线，法线贴图按 MikkTSpace 烘焙 | MikkTSpace 生成 | `GltfVertexStreamsImporter` 调用 MikkTSpace C 参考算法的 .NET 封装，按法线贴图选择 UV 集 |
| 无法线图元（规范允许） | 生成平面法线 | `GltfVertexStreamsImporter` 展开三角形角，生成逐面法线并同步复制全部属性和形变目标 |
| 顶点色已为线性值 | 按线性处理 | 顶点准备保留线性数值，RGB 补 alpha=1；形变颜色 RGB 增量补 alpha=0 |
| UV 的 V 已翻转为左上角原点 | 原样使用 | 符合 |
| 默认 4 个骨骼影响，"包含全部影响"时多出 `JOINTS_1`/`WEIGHTS_1` | 照搬全部 | 顶点准备保留所有影响集和来源数值 |
| 形状键导出为形变目标 | 支持 | 顶点准备保留逐目标的全部属性，并在逐角展开时同步复制 |
| 多个材质槽对应多个图元 | 合并为材质区段 | 符合 |
| 可选导出松散的边与点 | 跳过该图元并报告 | 顶点准备产出图元跳过诊断，继续导入三角形 |
| 多余的 UV 集 | 照搬 | 顶点准备保留来源的全部 UV 集 |

## Shared vertex streams API

`SomeEngine.Assets.Importers.GltfVertexStreamsImporter` 具有公开的无参构造函数，通过 Importer 能力声明产出 `VertexStreamsBlock`。其它 Importer 通过 `context.GetBlockAsync<VertexStreamsBlock>(output.Target, token)` 获取同一次导入中的数据；管线组合对该生产者的选择由导入组合负责。

`VertexStream`、`VertexPrimitive` 与 `VertexStreamsBlock` 的公开构造函数接受调用方数据并复制数组或集合，供替换生产者创建同一类型的 Block；发布后的数据通过只读集合与内存读取。

```csharp
[AssetBlock("SomeEngine.Assets.Importers.VertexStreamsBlock")]
public sealed partial class VertexStreamsBlock : AssetBlock
{
    public static bool Persistent => false;
    public IReadOnlyList<VertexPrimitive> Primitives { get; }
    public IReadOnlyList<VertexImportDiagnostic> Diagnostics { get; }
}
```

顶点流与蒙皮绑定共用来源 accessor 解码入口 `GltfAccessorReader.ReadComponents`。该入口保持来源模型的资源路径，读取 meshopt 与稀疏数据，调用方按自己的数据类型组装值；Importer 在读取前后检查取消。

实际名称按 AGENTS.md 的数据命名约定确定，替代票据提议中的 `PreparedVertexStreamsBlock` 与 `PreparedPrimitive`。

- `VertexPrimitive.SourcePrimitive` 为来源图元编号，`Material` 为来源材质编号（无材质时为 −1）；`Indices` 为三角形列表；`SourceVertices` 将输出顶点映射到来源 accessor 的行。
- `Attributes` 按来源语义名称索引，含位置、法线、切线、全部 UV/颜色/蒙皮影响集及自定义属性；`MorphTargets` 按来源目标顺序保存属性字典。
- `VertexStream.ComponentCount` 表示每行分量数，`Values` 是按来源分量顺序排列的 `ReadOnlyMemory<double>`。double 可精确表达来源 float32 与 uint32；整数归一化按 glTF 规则展开为 float32 数值。矩阵按列展开，并处理来源的列对齐填充。量化坐标保留来源所表达的坐标值，节点变换仍由节点数据表达。
- 缺法线时，每个三角形角获得自己的顶点；MikkTSpace 生成时同样使用逐角输出，保留切线方向与手性接缝。来源已有的法线保留原值。缺法线时规范要求忽略来源切线；原切线保存在 `SourceTangents`，使用的切线基准根据新法线生成。
- 缺来源法线时，对形变目标更新后的顶点同样生成平面法线，并记录相对基础法线的 `NORMAL` 增量。生成基础切线时，对形变目标更新后的位置、法线及指定 UV 运行 MikkTSpace，记录 XYZ 的 `TANGENT` 增量；W 按 glTF 规则来自基础切线。被替换的来源切线增量按目标编号保存在 `SourceMorphTangents`，其余来源目标属性保持数值。这样后续焊接能比较形变产生的法线与切线接缝。
- 颜色为线性 RGBA；RGB 的缺省 alpha 为 1，形变 RGB 增量的缺省 alpha 为 0。`WEIGHTS_n` 的数值直接保留。
- `GltfSourceImporter.ReadAsync` 保留来源 accessor 数值，内容检查在展开数据的 Importer 中按消费需要执行。这样可展开稀疏索引、任意影响集和压缩数据；SharpGLTF 的固定影响集内容检查与数值修复由此移到实际消费边界。meshopt 通过现有 meshoptimizer 库解码 `ATTRIBUTES`、`TRIANGLES`、`INDICES` 及对应过滤器，覆盖 `EXT_meshopt_compression` 与 `KHR_meshopt_compression`。SharpGLTF 1.0.6 提供的 Draco 能力对应跳过诊断。解码失败仅跳过引用该数据的图元。
- 图元跳过、缺少法线贴图指定 UV 和切线生成错误记录在 `Diagnostics`，同时发出 Trace warning。

公共行为验收见 `tests/SomeEngine.Assets.Tests/Assets/GltfVertexStreamsTests.cs`。MikkTSpace 对照资产与固定参考数据见 `Content/Gltf/mikk-reference.json`，由独立的原始 C 参考实现生成，包含镜像 UV、平滑接缝和退化 UV。

## Cluster geometry and attributes

中立组合包含 `GltfVertexStreamsImporter`；Cluster 组合包含 `ClusterGeometryImporter`、`ClusterVertexAttributesImporter`、`ClusterSkinInfluencesImporter` 与 `ClusterMorphDeltasImporter`。Importer 通过无参构造与生成的能力描述注册，算法由 Importer 的实现持有。

几何焊接逐位比较 double 中保存的全部基础属性、来源切线、各形变目标及来源形变切线，同时区分材质、属性名称与分量数、形变目标数量和 Deform 类别。只为被三角形引用的行建立顶点身份。三角形的材质与区域来源独立于焊接顶点持有；现有 Cluster/LOD 流程使用区域对应的工作拓扑保护区段边界，持久顶点身份继续共享。`Vertices` 中每一项包含来源图元号、准备流的行号和原 accessor 行号，所有逐顶点 Importer 可使用这份映射。

cluster 构建按四种 Deform 类别分区。复用现有 meshoptimizer cluster 与 LOD DAG 构建流程，局部索引引用 cluster 顶点，逐三角形保留来源材质编号和网格区域编号，渲染按区域索引实例的材质绑定；不同分区的 LOD 组编号彼此独立。重合但身份不同的顶点受到简化锁保护，覆盖全部 UV 接缝、法线不连续和材质边界。蒙皮或形变流在边两端发生变化时，两端同样锁住：以变化为零的保守误差约束保留所有影响集和目标，适用于任意来源数量。新路径采用受锁约束的简化；静态及变形数据恒定区域可生成更粗 LOD。

位置按 cluster 以 uint16 XYZ 编码，使用与标准组合编码一致的二次幂量化步长；包围半径包含量化误差。属性按同一 cluster 顶点顺序以 little-endian float64 编码，完整保留来源数值（包括 uint32 精度与符号零）。每个属性保存语义名称、最大分量数和逐顶点的实际分量数，0 表示该来源图元省略该属性。基础位置与蒙皮影响分别属于几何及后续影响 Block；其余基础属性全部进入属性 Block。属性数值与材质 Shader 的编码由同一个属性 Importer 交付，具体表示见 [Vertex Encoding](../cluster/Vertex-Encoding.md)。渲染页组合仅复制属性字节，材质 Shader 解释其布局。

公开行为验收见 `ClusterGeometryImportTests`：位置解码与来源对照、全部属性与保存重读、逐流位比较焊接、四种分区、LOD 接缝及材质、混合属性形状、包组合交付两份独立的 Cluster Block。

蒙皮影响与形变增量通过同一几何映射保存所有来源分量，并提供可由材质 Importer 选择的编码字节；公开读取、来源切线和保存行为见 [Deformation Import](../cluster/Deformation-Import.md)。

## Relations
[[architecture/Assets/Block-Division|Block Division]] · [[architecture/Assets/Asset-Meshes|Mesh]] · [[architecture/ECS/Coordinate-System|Coordinate System]] · ADR 0016 · ADR 0017
