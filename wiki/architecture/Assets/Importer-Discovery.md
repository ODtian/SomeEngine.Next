# Importer Discovery

## Definition
Asset importer DI 是 Importer 的注册表，唯一用途是让一次来源导入中的编排者和 Importer 互相发现（ADR 0015）。资产导入的扩展点只到 Importer。

## Invariants
- DI 只注册三类东西：Importer、Importer 能力描述、导入组合描述（[[architecture/Assets/Importer-Sets|Importer Sets]]）。它们都由 `[AssetCapabilities]` 生成器产出；多个程序集（引擎、扩展包、项目）的能力由项目合并到同一个容器。
- 编排者（例如 glTF 来源集成）不静态依赖任何具体 Importer 类型，只按能力描述与 `.meta` 的选择取得 Importer。
- Importer 的构造函数没有参数，生成器对带参数的 Importer 报告编译错误。Importer 需要的东西按来源取得：
  - 来源对象：导入调用的参数；
  - 设置：`.meta`，经导入上下文传入；
  - 其它 Importer 的结果：`context.GetBlockAsync<T>()`；
  - 算法与编码规则：自己的代码或库函数调用；
  - 本次导入内的状态：实例字段（一次导入共用一个实例）。
- 昂贵的外部资源（编译器会话、原生库）由使用它的 Importer 自己创建与持有，不注册为可注入的服务。
- 纵向扩展（换管线）是启用另一组 Importer；横向扩展（同一管线内的正交数据）按 [[architecture/Assets/Block-Division|Block Division]] 由各自的 Importer 产出。
- 导入上下文按导入范围管理 Importer 实例：同一次导入共用，上下文关闭时释放。

## Current design
`AssetCapabilitiesGenerator` 对带参数的 Importer 构造函数报告 `SEAC004` 编译错误，检查本项目与引用程序集的声明。公开无参构造函数通过直接工厂注册；按具体类型和强类型 Importer 接口解析时共用同一作用域的实例。

`StandardGltfSceneImporter` 与默认场景构建工具的 `ImportedSceneImporter` 通过 `context.GetSettings(GltfSourceImporter.SourceImporterId, new GltfImporterSettings(), GltfSettingsJson.Default.GltfImporterSettings)` 读取来源的材质模板路径。来源集成在请求资产前通过 `context.ConfigureSettings<T>(Guid importerId, T settings)` 设置单次覆盖；该覆盖只由本次上下文持有。持久设置继续从 `.meta` 读取。Importer 持有自己的编码资源，导入作用域保持到所有 Block 保存完成。

当前代码与目标的差异：
- `GltfSourceImporter.EnsureDefaultSelections` 写死 `GltfMeshImporter`、`ClusterGltfMeshImporter`、`GltfTextureImporter`。

## Relations
[[architecture/Assets/Asset-Import-and-Storage|Import and Storage]] · [[architecture/Assets/Importer-Sets|Importer Sets]] · ADR 0015
