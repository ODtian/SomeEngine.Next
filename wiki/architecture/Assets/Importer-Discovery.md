# Importer Discovery

## Definition

Asset importer DI 是一次来源导入的 Importer 注册表。项目通过 `[AssetCapabilities]` 汇合引擎、扩展包与项目的声明，来源集成按 `.meta` 的选择取得实际 Importer。

## Invariants

- DI 注册 Importer、Importer 能力描述与导入组合描述。Block 的编辑器信息保存在生成的 `BlockCapabilities` 数据中。
- Importer 使用公开无参构造。来源对象由调用参数提供，设置由 Context 从 `.meta` 与本次覆盖取得，其他 Importer 的结果通过 `GetBlockAsync<TBlock>` 请求。
- 生成的 `AssetImporterCapability<TImporter, TSource, TSettings>` 使用具体类型取得 Importer，设置经对应的静态 JSON 编码进入 `AssetImporter<TSource, TSettings>`，执行直接调用实际 Importer 的方法。
- 算法、编码规则与编译器会话由使用它们的 Importer 实现并持有。同次导入共用作用域中的实例，Context 关闭后释放作用域。

## Current design

`ConfigureAssets` 接收 `AssetSource<TSource, TAsset>`。来源描述的方法给出元素身份、实际资产和输出路径，保存方法调用该资产的固定编码入口。ImportContext 持有实际来源、设置、目标资产及每个生产者的执行数据。

`GltfSourceImporter` 接收项目组装好的 `IServiceCollection`，通过能力描述和导入组合发现生产者。几何、材质及纹理数据经这些生产者汇合到相应资产，场景 Importer 的材料和 Mesh 材料方法由实际对象直接执行。

## Relations

[[architecture/Assets/Asset-Import-and-Storage|Import and Storage]] · [[architecture/Assets/Importer-Sets|Importer Sets]] · [[architecture/Assets/Asset-Code-Generation|Code Generation]] · [ADR 0015](../../../docs/adr/0015-importer-discovery-only.md)
