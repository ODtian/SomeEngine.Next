# Importer Sets

## Definition

导入组合是包声明的一组 Importer，表达中立数据与管线选择。组合描述包含持久身份、名称、Importer 身份、中立标记和默认标记。

## Invariants

- 包通过 `[AssetImporterSet(id, typeof(Importer), ...)]` 声明组合，项目生成器将它们汇合到 `ImporterSets` 并注册组合描述。
- `.meta` 的 `ImporterSets` 保存组合选择，来源集成将组合展开为逐个 `Importers` 项，保存实际生产者与参数。
- 中立组合始终进入来源选择；管线组合由来源选择，初始默认组合来自包的 `Default` 声明。
- 已保存的逐个 Importer 项表达人工替换与启用状态。已有生产者占据其产出 Block 时，组合展开保留该项。

## Current design

glTF 的中立组合包含 `GltfMeshImporter` 与 `GltfTextureImporter`，默认管线组合包含 `ClusterGltfMeshImporter`，默认场景组合包含 `StandardGltfSceneImporter`。具体场景 Importer 也可以由项目明确选择。

执行由 ImportContext 按需完成。来源集成为每种元素启用实际 Importer，资产请求执行这些生产者；Block 依赖请求先运行对应生产者，每个生产者在本次导入中执行一次，Context 管理依赖关系与关闭。

## Relations

[[architecture/Assets/Importer-Discovery|Importer Discovery]] · [[architecture/Assets/Asset-Import-and-Storage|Import and Storage]]
