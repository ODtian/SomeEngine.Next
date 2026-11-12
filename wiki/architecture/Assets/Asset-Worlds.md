# 世界目录与实体资产

`WorldAsset` 提供世界目录身份；`WorldBlock.Contents` 保存 `ImmutableArray<WorldContent>`。每条 `WorldContent` 引用一个 `AssetId<Asset>`，并以六个 double 构成的 `WorldBounds` 记录绝对世界覆盖范围。

`EntityAsset` 的 `EntityBlock` 保存一组实体的组件、缓冲区、拓扑与外部资产引用。调用方选择成员及本次 codec，沿 ECS 实体集合格式编码：

```csharp
using var entities = new EntityAsset(Guid.NewGuid()) { Name = "district" };
entities.GetBlock<EntityBlock>().WriteEntities(world, members, new(Codecs: codecs));
AssetEntry entityFile = await entities.SaveAsync(entityPath);

using var directory = new WorldAsset(Guid.NewGuid()) { Name = "level" };
directory.GetBlock<WorldBlock>().Contents =
[
    new(new(entityFile.AssetGuid), bounds),
];
AssetEntry worldFile = await directory.SaveAsync(worldPath);
```

保存写出当前 Block 数据。`EntityBlock.ExternalReferences` 保存 codec 写出的直接资产引用；世界目录的依赖来自各内容条目。资产 ID 描述内容来源，目录索引描述一次 WorldStream 中的条目。

## 实体集合追加

`WorldSerializer.ReadEntities` 接收字节或 Stream、目标 World 与 `SerializationReadOptions`，返回该次追加的原生 `Entity[]`。类型绑定、最终列分配、整组实体映射、列解码、引用修补与拓扑维护全部完成后，调用方取得可用成员。组外实体引用按 `MissingReferenceMode` 处理。

同一 EntityAsset 可以被多条目录项引用，每次装入得到独立成员。目标 World 保留原有业务数据；卸载使用本次返回的成员集合。完整 World 保存与独立恢复继续由 ECS 序列化器承担，见 [World 编码](../ECS/World-Encoding.md)。

## 资产流送与使用对象

[WorldStream](World-Streaming.md) 从目录推进请求、字节读取、实体组提交与卸载。应用在同一个 Game World 的实体上解析 Mesh、Material 等引用，再通过既有提取进入 Rendering World，见 [项目启动与渲染接入](Asset-Application-and-Rendering.md)。

SceneSerialization.Codecs 是第一方场景的调用级 codec 集合。用户的组件默认描述与调用级 ComponentCodec 沿同一 ECS 编码入口绑定。

实现：[WorldAsset](../../../src/SomeEngine.Assets/WorldAsset.cs)、[WorldBlock](../../../src/SomeEngine.Assets/WorldBlock.cs)、[EntityAsset](../../../src/SomeEngine.Assets/EntityAsset.cs)、[EntityBlock](../../../src/SomeEngine.Assets/EntityBlock.cs)。公开行为：[WorldStreamTests](../../../tests/SomeEngine.Assets.Tests/WorldStreamTests.cs)、[实体组往返](../../../tests/SomeEngine.ECS.Serialization.Tests/EntityGroupSerializationTests.cs)、[第一方场景往返](../../../tests/SomeEngine.Runtime.Tests/WorldAssetTests.cs)。
