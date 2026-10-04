# Importer Sets

## Definition
导入组合是包声明的一组 Importer，用于选择与默认值：例如 Cluster 管线组合包含几何、顶点属性、蒙皮影响、形变增量四个 Importer。它只是描述，不参与执行。

## Invariants
- 组合描述由 `[AssetCapabilities]` 生成器产出并注册进 Asset importer DI，与 Importer 能力描述同级（[[architecture/Assets/Importer-Discovery|Importer Discovery]]）。
- 中立数据的 Importer 构成中立组合，始终启用；管线组合由用户选择，多条管线的组合可以同时启用（产出不同类型的 Block，不冲突）。
- 用户在来源 `.meta` 中选择组合；来源集成把组合展开为逐个 Importer 写入 `.meta`，导入结果可复现、可审阅。
- 替换组合中的一员（例如只换顶点属性 Importer）就是替换 `.meta` 中产出该 Block 的那一项。同一 Block 只允许一个生产者，所以替换不会形成两个都启用。
- 来源集成不写死具体 Importer；默认值完全来自组合描述。

## Current design
包在公开顶层类上使用可重复的 `[AssetImporterSet(string id, string name, params Type[] importers)]` 声明组合，`Neutral = true` 表示中立组合。生成器检查组合 ID、名称与成员声明，并跨引用程序集生成 `AssetImporterSetCapability(Guid Id, string Name, IReadOnlyList<Guid> Importers, bool Neutral)`。项目在 `[AssetCapabilities(params string[] defaultImporterSets)]` 中填写默认管线组合的持久 ID；生成的 `ImporterSetCapabilities` 与 DI 注册描述通过 `Default` 属性记录项目默认值。多个注册入口按组合 ID 合并成员与默认值。

```csharp
[AssetCapabilities(GltfImporterSets.ClusterId)]
public static partial class ProjectAssetCapabilities;
```

`SourceMeta.ImporterSets` 保存来源选择的组合 ID。初始值 `null` 按 `UseDefaultImporters` 初始化项目默认组合；显式空列表启用中立组合。之后每次导入使用保存的组合选择。glTF 来源集成按实际支持的来源类型展开中立组合与选中的管线组合，并将逐项选择写入 `SourceMeta.Importers`。组合中产出 `MaterialBlock` 的成员负责材质解释；其它材质沿用场景 Importer 的项目解释，正交材质 Block 的生产者共同参与该材质资产。所选组合或成员缺失时报告包含该 ID 的错误。

展开项的 `SourceImporterSettings.SetMemberId` 保存原始成员 Importer ID；该成员在多条组合中共用同一项。通过 `with { ImporterId = replacementId, Settings = replacementSettings }` 替换这一项后，其成员关系与其它项的设置保留。已有逐项选择按来源类型和产出 Block 匹配组合成员。切换组合会移除已退出组合的展开项，并添加新成员；共享成员及其设置继续使用。同一来源类型、同一 Block 的两个组合生产者由 Context 的生产者检查明确报错。

单次 `GltfImportOverrides.Importers` 覆盖管线成员，同时沿用中立成员和场景选择；显式替换或关闭某一中立成员时，以该次逐项选择为准。保存的组合与逐项参数参与输入版本，来源复制保留组合选择、成员替换与参数。

引擎包的 `GltfImporterSets` 声明 glTF 中立组合（`GltfMeshImporter`、`GltfTextureImporter`、`GltfVertexStreamsImporter`、`MorphTargetSetImporter`）与 Cluster 组合（`ClusterGeometryImporter`、`ClusterVertexAttributesImporter`、扩展阶段保留的 `ClusterGltfMeshImporter`）。两份新表示及既有组合表示在同一次导入中产出，准备流保存为导入进程内 Block。动画包的 `GltfSkeletonImporter` 另行声明中立骨架组合（`GltfSkeletonImporter`、`SkinBindingImporter`）；项目引用该包后按 skin 来源展开。绑定与形变集合的数据归属见 [Block Division](Block-Division.md)。默认项目通过 `[AssetCapabilities(GltfImporterSets.ClusterId)]` 选择 Cluster。

执行仍由导入上下文按需完成：来源集成为每种元素启用 `.meta` 中的 Importer；请求资产时，运行该元素启用的全部 Importer；Importer 调用 `GetBlockAsync` 时先运行对应的生产者，每个生产者在一次导入中只运行一次，并检查依赖环。

glTF 来源集成声明 mesh、material、texture、scene、skin、animation 六种元素。skin 对应 `SkeletonAsset`，animation 对应 `AnimationClipAsset`；两种资产只提供身份、引用与 Block 目录，内容 Block 由动画程序集定义。来源集成按 `.meta` 启用 `Skin` 与 `Animation` 的 Importer，选中生产者后请求来源中的全部 skin 与 animation，包括场景节点未使用的元素。

| 元素 | 持久元素键 | 资产文件后缀 |
|---|---|---|
| skin | `skin:id:<extras.someengine_id>` | `.skeleton.asset` |
| animation | `animation:id:<extras.someengine_id>` | `.animation-clip.asset` |

元素键沿用来源设置中的持久 ID 属性；显式关闭 `RequirePersistentElementIds` 后才采用 `skin:index:<index>`、`animation:index:<index>`。显示名称不参与身份。输出文件名包含资产 GUID，重命名元素与调整带持久 ID 的元素顺序保留身份及路径。

选中 skin 生产者时，场景中带 skin 的网格节点同时具有 `MeshSource` 与 `SkeletonSource`，骨架引用对应其 skin 的资产；多个节点共用同一 skin 时共用骨架身份，同一 mesh 可由不同节点引用不同骨架。`SceneSerialization` 在真实 ECS 内容中写入骨架外部引用，WorldAsset 的依赖来自这些引用。skin 或 animation 的生产者全部关闭时，对应输出随重新导入移除；骨架组件随 skin 选择生成。

公开行为验收见 [GltfImporterSetBehaviorTests](../../../tests/SomeEngine.Assets.ImporterSets.Tests/GltfImporterSetBehaviorTests.cs)：测试项目只注册测试 Importer，核对保存重开后的 Block，覆盖默认与中立选择、切换、替换、多管线、单次覆盖、来源类型、失败与来源复制。跨程序集发现、默认值合并和声明诊断见 [AssetCapabilityGeneratorBehaviorTests](../../../tests/SomeEngine.Assets.Consumer.Tests/AssetCapabilityGeneratorBehaviorTests.cs)；原生 AOT 验收通过资产冒烟工具执行。

## Relations
[[architecture/Assets/Asset-Import-and-Storage|Import and Storage]] · [[architecture/Assets/Block-Division|Block Division]] · ADR 0015
