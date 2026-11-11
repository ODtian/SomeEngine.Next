# 保存用途

ECS 的版本 5 容器区分同构建检查点与长期存档。`SavePurpose.RawCheckpoint` 绑定当前构建和值表示，`SavePurpose.DurableSave` 使用当前显式 schema 与字段 codec。类型登记同时确定保存的值投影，World 的实体身份完整进入存档。

`WorldSerializer.WriteWorld`、`ReadWorld` 和独立组件入口沿 SG 随 `ComponentType` 建立的默认描述编码。`ComponentCodec.Create<T,TCodec>(codec)` 使用该类型的默认格式，`Create<T,TCodec>(format,codec)` 使用调用方给出的 `SerializationTypeKey`；两者要求 `T : struct`、`TCodec : struct, IComponentCodec<T>`。`SerializeOptions`、`SerializationReadOptions` 与 `WorldLoadOptions` 的 `ImmutableArray<ComponentCodec> Codecs` 为本次操作选择格式与带状态的 codec，其余类型继续使用默认描述。带 `IBufferElement` 的元素类型对应实际 `Buffer<T>` 描述。文件类型目录在操作开始时绑定一次，后续列直接执行已选 codec 和对应的 Entity 引用修补。

显式 `SerializationRegistry` 入口继续使用调用方登记的投影，并接受同次 `Codecs` 覆盖。Hierarchy 和关系端点通过同一组件描述取得具体泛型的默认格式与维护服务；SG 已知的闭合族在初始化时登记，实际使用的族沿原生 Hierarchy/Relations 初始化登记，各族缓存一份具体描述供后续操作复用。

文件类型目录与表列按格式 GUID 排列，检查点登记身份按同一格式顺序计算。读进程根据格式描述绑定本地组件编号，允许两个进程以不同顺序登记同一组类型。

同步保存持有结构和值读取范围。需要同时修改源 World 时，先显式 Capture 再编码；普通 managed 引用浅复制，buffer 存储深复制。`Rebuild` 与 `Preserve` 分别表达完成维护后的保存，以及包含读取和观察进度的继续执行。

`Save(world, entity/members/query, stream, options)` 保留 World 容器与 `ReadWorld` 恢复语义。它在源读取范围内绑定所选成员的已登记投影列，直接调用该列的 codec；Shared 目录只记录所用值，Sparse 与 typed 拓扑按所选成员输出。拥有值的克隆与释放仍由实际 World／显式 Capture 的生命周期负责，见 [选择保存](../../../src/SomeEngine.ECS.Serialization/WorldSerializer.Selection.cs)。

`WorldSerializer.WriteWorld` / `ReadWorld` 保存和恢复整个 World；`WriteEntities(Stream, World, ReadOnlySpan<Entity>, SerializeOptions)` 编码选中成员，`ReadEntities(ReadOnlyMemory<byte>, World, SerializationReadOptions)` 与对应 Stream 重载向已有 World 追加整组并返回本次 `Entity[]`。调用沿组件的生成描述绑定类型，`options.Codecs` 为本次操作选择带状态的值 codec 与格式，显式 registry 入口保留其类型投影。

实体集合使用版本 5 容器的独立种类。目录先记录成员身份、最终组件组合及各列的成员位置和字节长度。读取持有目标 World 的结构写入权，按最终组件组合批量预留身份和列空间，建立完整组内映射，再经 Job 按独立列段直接解码和修补引用。共享值和 buffer 池在分区执行前绑定；同类型的列段共享该类型的解码工作。完成现有共享成员、索引、分组、typed 拓扑与成员变化维护后，调用方即可消费返回成员。

Parent 随子实体编码，关系实体作为普通成员编码；Children 与邻接由 typed 初始化生成。组外引用由 `SerializationReadOptions.MissingReferenceMode` 选择保留、清空或失败；同一字节集合再次追加会取得另一组实体身份。通过 `World.Destroy(members.AsSpan())` 卸载本次成员，批量销毁沿现有拓扑生命周期处理连带结束的关系实体。实现入口见 [实体集合编码](../../../src/SomeEngine.ECS.Serialization/WorldSerializer.Entities.cs)、[类型绑定](../../../src/SomeEngine.ECS.Serialization/SerializationRegistry.Codecs.cs) 和 [批量实体与列预留](../../../src/SomeEngine.ECS/Copy/World.CopyBatch.cs)。

格式、复制与文件交接的决定由 [ADR 0005](../../../docs/adr/0005-ecs-save-contracts.md) 持有。通用事件与步结果的交付见 [ADR 0016](../../../docs/adr/0016-events-and-step-results.md)。公共读档失败分类与 codec 登记数据见 [BinarySerialization](../../../docs/BinarySerialization.md#read-failures)。

含界面数据的 World 使用 Views 的生成登记与读回后重建入口，见 [[architecture/someui/Assemblies|视图序列化]]。
