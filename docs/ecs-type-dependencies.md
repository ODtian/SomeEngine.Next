# SomeEngine ECS 完整类型依赖与持久状态图索引

本文件同时读取当前工作树的 Roslyn semantic source 和内存编译结果。边方向为 `source type → referenced type`；权威边集取两者并集，因此既保留会在编译时擦除的 enum/constant 等语义依赖，也保留源码没有显式写出但在签名或 IL 中出现的推断/隐式依赖。每个源/目标类型对只保留一次，同时保存 `signature`、`inheritance`、`creation`、`body-use`、`containment`、`value-state`、`state` 类明确关系。`value-state` 是内联值，没有独立释放生命周期；引用类型 `state` 只表示持久保存，所有权必须继续根据构造、逃逸、发布、替换与清理路径推导。rank 先压缩强连通分量，再按依赖叶子计算，仅用于依赖顺序和审计，不是人为架构层级。

- 节点：524
- 去重依赖对：2624
- 带类别边：4720
  - `body-use`：1720
  - `containment`：108
  - `creation`：535
  - `inheritance`：114
  - `signature`：1699
  - `state`：315
  - `value-state`：229
- 强连通分量：343
- 多节点强连通分量：16
- 最大 rank：16
- 完整数据：[`ecs-type-dependencies.json`](ecs-type-dependencies.json)
- Graphviz：[`ecs-type-dependencies.dot`](ecs-type-dependencies.dot)
- 可直接打开的完整图：[`ecs-type-dependencies.svg`](ecs-type-dependencies.svg)
- 重新生成：`dotnet run --project tools/RhiTypeGraph/RhiTypeGraph.csproj -p:DefineConstants=ECS_GRAPH -- <repository-root>`
- 所有权判断台账：[`ecs-ownership-audit.md`](ecs-ownership-audit.md)

## 边口径对账

源码扫描覆盖显式语义依赖和实例字段 `state` 候选；编译扫描覆盖签名与 IL 中推断或隐式出现的依赖。两者各自可见范围不同，因此当前权威图取并集；完整逐对差异保存在 JSON 的 `edgeMethodReconciliation` 中。

| 集合 | 类型对 |
| --- | ---: |
| 两种方法共有 | 2245 |
| 仅编译图有 | 243 |
| 仅显式语法图有 | 136 |
| 历史净差 | +107 |
| 当前并集 | 2624 |

仅编译图的类别成员数（同一类型对可属于多个类别）：

- `contains`：11
- `creates`：11
- `implements`：21
- `signature`：40
- `uses`：161

## 包装、描述符与物化边界审计

下表是穷尽式语法候选，不把“只保留一个成员”直接等同于错误包装。命令、强类型身份、内联存储、迭代器、适配器和生命周期 scope 仍须根据构造、逃逸、不变量与释放路径判断。完整机器可读记录位于 JSON 的 `audit`。

### 单成员包装候选（32）

| 类型 | 保留成员 | 普通方法 | 构造器 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.Collections.SmallInlineStorage<T>` | `_element0: T` | 0 | 0 | `src/SomeEngine.ECS/Collections/SmallList.cs` |
| `SomeEngine.ECS.Commands.CommandBuffer.JobProducerPlaybackBatch` | `_world: SomeEngine.ECS.World?` | 3 | 1 | `src/SomeEngine.ECS/Commands/CommandBuffer.cs` |
| `SomeEngine.ECS.Commands.CommandBuffer.RecordAccessScope` | `_gate: object?` | 1 | 1 | `src/SomeEngine.ECS/Commands/CommandBuffer.cs` |
| `SomeEngine.ECS.Commands.DeferredEntity` | `_cell: SomeEngine.ECS.Commands.DeferredEntityCell?` | 6 | 1 | `src/SomeEngine.ECS/Commands/CommandBuffer.cs` |
| `SomeEngine.ECS.Commands.DeferredRelationEdge<T>` | `_cell: SomeEngine.ECS.Commands.DeferredRelationEdgeCell<T>?` | 6 | 1 | `src/SomeEngine.ECS/Commands/CommandBuffer.Relations.cs` |
| `SomeEngine.ECS.Commands.DestroyRelationCommand<T>` | `_edge: SomeEngine.ECS.Commands.RelationCommandEdge<T>` | 1 | 1 | `src/SomeEngine.ECS/Commands/CommandBuffer.Relations.cs` |
| `SomeEngine.ECS.Commands.DestroySubtreeCommand<TDomain>` | `_root: SomeEngine.ECS.Commands.CommandEntity` | 1 | 1 | `src/SomeEngine.ECS/Commands/CommandBuffer.Hierarchy.cs` |
| `SomeEngine.ECS.Components.BufferCapacityAttribute` | `Capacity: int` | 0 | 1 | `src/SomeEngine.ECS/Components/InternalBufferCapacityAttribute.cs` |
| `SomeEngine.ECS.Components.DynamicBufferInline<T>` | `_element0: T` | 0 | 0 | `src/SomeEngine.ECS/Components/DynamicBufferComponents.cs` |
| `SomeEngine.ECS.Hierarchy.Parent<TDomain>` | `Value: SomeEngine.ECS.Entities.Entity` | 0 | 1 | `src/SomeEngine.ECS/Hierarchy/HierarchyComponents.cs` |
| `SomeEngine.ECS.Indexing.ComponentIndex<TComponent, TKey>.Builder` | `_buckets: System.Collections.Generic.Dictionary<TKey, SomeEngine.ECS.Indexing.ComponentIndex<TComponent, TKey>.Bucket>?` | 2 | 0 | `src/SomeEngine.ECS/Indexing/ComponentIndex.cs` |
| `SomeEngine.ECS.Owners.Clock` | `_tick: int` | 2 | 0 | `src/SomeEngine.ECS/Owners.Clock.cs` |
| `SomeEngine.ECS.Owners.ExceptionAccumulator` | `_exceptions: System.Collections.Generic.List<System.Exception>?` | 3 | 0 | `src/SomeEngine.ECS/Owners.ExceptionAccumulator.cs` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.HierarchyDomainGeneration` | `_shared: int` | 1 | 0 | `src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs` |
| `SomeEngine.ECS.Queries.ChunkRowEnumerator` | `_rows: SomeEngine.ECS.Queries.QueryRowCursor` | 3 | 1 | `src/SomeEngine.ECS/Queries/QueryRowEnumerator.cs` |
| `SomeEngine.ECS.Queries.QueryDefinitionBuilder` | `_terms: System.Collections.Generic.List<SomeEngine.ECS.Queries.QueryTerm>` | 22 | 0 | `src/SomeEngine.ECS/Queries/QueryDefinitionBuilder.cs` |
| `SomeEngine.ECS.Relations.RelationDirtyEdgeBucket` | `_entities: SomeEngine.ECS.Entities.Entity[]?` | 2 | 1 | `src/SomeEngine.ECS/Relations/RelationEntityMap.cs` |
| `SomeEngine.ECS.Relations.RelationEdge<T>` | `Entity: SomeEngine.ECS.Entities.Entity` | 4 | 1 | `src/SomeEngine.ECS/Relations/RelationEdge.cs` |
| `SomeEngine.ECS.Relations.RelationEntityMap<TValue>` | `_storage: SomeEngine.ECS.Relations.RelationEntityMap<TValue>.Storage` | 11 | 2 | `src/SomeEngine.ECS/Relations/RelationEntityMap.cs` |
| `SomeEngine.ECS.SharedStores` | `_stores: SomeEngine.ECS.ISharedComponentStore?[]` | 4 | 2 | `src/SomeEngine.ECS/SharedComponentStore.cs` |
| `SomeEngine.ECS.Serialization.DataWriter` | `_writer: System.IO.BinaryWriter` | 17 | 1 | `src/SomeEngine.ECS.Serialization/DataWriter.cs` |
| `SomeEngine.ECS.Serialization.ExternalReferenceKey` | `Value: System.Guid` | 0 | 1 | `src/SomeEngine.ECS.Serialization/ExternalReferences/ExternalReferenceContracts.cs` |
| `SomeEngine.ECS.Serialization.RelationTopologySerializationRuntime<T>` | `_payloadEntry: SomeEngine.ECS.Serialization.SerializationTypeEntry` | 4 | 1 | `src/SomeEngine.ECS.Serialization/TopologySerializationRegistry.cs` |
| `SomeEngine.ECS.Serialization.SerializedFieldAttribute` | `StableId: string` | 0 | 1 | `src/SomeEngine.ECS.Serialization/SerializableComponentAttribute.cs` |
| `SomeEngine.ECS.Systems.HierarchyMaintenanceEvidence` | `_revision: long` | 2 | 0 | `src/SomeEngine.ECS.Systems/HierarchyMaintenanceSystem.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagationState` | `_proof: SomeEngine.ECS.Systems.HierarchyPropagationPartitionProof?` | 2 | 0 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs` |
| `SomeEngine.ECS.Systems.ImmediateSystemDriver` | `_world: SomeEngine.ECS.World` | 2 | 1 | `src/SomeEngine.ECS.Systems/ImmediateSystemDriver.cs` |
| `SomeEngine.ECS.Systems.JobCommandBuffer.PublicationAdapter` | `_owner: SomeEngine.ECS.Systems.JobCommandBuffer` | 1 | 1 | `src/SomeEngine.ECS.Systems/JobCommandBuffer.cs` |
| `SomeEngine.ECS.Systems.JobCommandWriter` | `_segment: SomeEngine.ECS.Commands.CommandBuffer` | 22 | 1 | `src/SomeEngine.ECS.Systems/JobCommandBuffer.cs` |
| `SomeEngine.ECS.Systems.RelationMaintenanceSystem<T>.MaintenanceJob` | `_world: SomeEngine.ECS.World` | 1 | 1 | `src/SomeEngine.ECS.Systems/RelationMaintenanceSystem.cs` |
| `SomeEngine.ECS.Systems.SystemNode<TSystem, TContext>` | `_system: TSystem` | 3 | 1 | `src/SomeEngine.ECS.Systems/SystemNode.cs` |
| `SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>.ParentFinalizerJob` | `_stage: SomeEngine.ECS.Systems.ParentTopologyStage<TDomain>` | 3 | 1 | `src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs` |

### 包装命名候选（62）

名称后缀为 `Wrapper / Box / Adapter / View / Handle / Scope / Facade / Proxy / Access / Borrow / Lease / Token / Cursor / Enumerator` 的类型也全部进入审计，避免带额外 token、generation 或 capability 字段的多成员包装逃过单成员扫描。

| 类型 | 持久成员 | 普通方法 | 构造器 | 源文件 |
| --- | ---: | ---: | ---: | --- |
| `SomeEngine.ECS.BufferView<T>` | 4 | 3 | 1 | `src/SomeEngine.ECS/BufferView.cs` |
| `SomeEngine.ECS.BundleWriteView` | 3 | 4 | 1 | `src/SomeEngine.ECS/BundleWriteView.cs` |
| `SomeEngine.ECS.Collections.SmallList<T>.Enumerator` | 2 | 1 | 1 | `src/SomeEngine.ECS/Collections/SmallList.cs` |
| `SomeEngine.ECS.Commands.CommandBuffer.RecordAccessScope` | 1 | 1 | 1 | `src/SomeEngine.ECS/Commands/CommandBuffer.cs` |
| `SomeEngine.ECS.Hierarchy.HierarchyChildrenView<TDomain>` | 2 | 2 | 1 | `src/SomeEngine.ECS/Hierarchy/HierarchyComponents.cs` |
| `SomeEngine.ECS.Hierarchy.HierarchyChildrenView<TDomain>.Enumerator` | 2 | 3 | 1 | `src/SomeEngine.ECS/Hierarchy/HierarchyComponents.cs` |
| `SomeEngine.ECS.Hooks.HookCommandToken` | 2 | 0 | 1 | `src/SomeEngine.ECS/Hooks/DeferredWorld.cs` |
| `SomeEngine.ECS.JobCommandProducerScope` | 3 | 1 | 1 | `src/SomeEngine.ECS/World.JobAdmission.cs` |
| `SomeEngine.ECS.Owners.Hooks.HookExecutionScope` | 3 | 1 | 1 | `src/SomeEngine.ECS/Owners.Hooks.cs` |
| `SomeEngine.ECS.Queries.ChunkRowEnumerator` | 1 | 3 | 1 | `src/SomeEngine.ECS/Queries/QueryRowEnumerator.cs` |
| `SomeEngine.ECS.Queries.ChunkRowIndexEnumerator` | 5 | 3 | 1 | `src/SomeEngine.ECS/Queries/QueryChunkView.cs` |
| `SomeEngine.ECS.Queries.QueryAccess` | 0 | 0 | 0 | `src/SomeEngine.ECS/Queries/QueryAccess.cs` |
| `SomeEngine.ECS.Queries.QueryChunkEnumerator<TFilter>` | 11 | 4 | 1 | `src/SomeEngine.ECS/Queries/QueryChunkEnumerator.cs` |
| `SomeEngine.ECS.Queries.QueryChunkView` | 5 | 22 | 1 | `src/SomeEngine.ECS/Queries/QueryChunkView.cs` |
| `SomeEngine.ECS.Queries.QueryColumnAccess` | 3 | 0 | 1 | `src/SomeEngine.ECS/Queries/QueryState.cs` |
| `SomeEngine.ECS.Queries.QueryCursor` | 4 | 5 | 1 | `src/SomeEngine.ECS/Queries/QueryCursor.cs` |
| `SomeEngine.ECS.Queries.QueryHandle` | 2 | 4 | 1 | `src/SomeEngine.ECS/Queries/QueryHandle.cs` |
| `SomeEngine.ECS.Queries.QueryPairEnumerator<TWrite, TRead>` | 15 | 6 | 1 | `src/SomeEngine.ECS/Queries/QueryPairEnumerator.cs` |
| `SomeEngine.ECS.Queries.QueryRowCursor` | 7 | 3 | 1 | `src/SomeEngine.ECS/Queries/QueryRowEnumerator.cs` |
| `SomeEngine.ECS.Queries.QueryRowEnumerator<TFilter>` | 2 | 3 | 1 | `src/SomeEngine.ECS/Queries/QueryRowEnumerator.cs` |
| `SomeEngine.ECS.Relations.RelationComponentSlotTable<TValue>.Enumerator` | 3 | 1 | 1 | `src/SomeEngine.ECS/Relations/RelationTypeSlotTable.cs` |
| `SomeEngine.ECS.Relations.RelationEdgeQuery<T>.Enumerator` | 4 | 1 | 1 | `src/SomeEngine.ECS/Relations/RelationAdjacency.cs` |
| `SomeEngine.ECS.Relations.RelationEndpointAccess` | 0 | 0 | 0 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.Relations.RelationEntityMap<TValue>.Enumerator` | 4 | 1 | 1 | `src/SomeEngine.ECS/Relations/RelationEntityMap.cs` |
| `SomeEngine.ECS.Relations.RelationTypeSlotTable.Enumerator` | 3 | 1 | 1 | `src/SomeEngine.ECS/Relations/RelationTypeSlotTable.cs` |
| `SomeEngine.ECS.RestrictedWorldApiScope` | 3 | 1 | 1 | `src/SomeEngine.ECS/World.JobAdmission.cs` |
| `SomeEngine.ECS.Serialization.HierarchyTopologyWriteAccess<TDomain>` | 4 | 2 | 1 | `src/SomeEngine.ECS/Serialization/WorldTopologySerializationAccess.cs` |
| `SomeEngine.ECS.Serialization.RelationTopologyWriteAccess<T>` | 7 | 3 | 1 | `src/SomeEngine.ECS/Serialization/WorldTopologySerializationAccess.cs` |
| `SomeEngine.ECS.SerializationValidationScope` | 2 | 1 | 1 | `src/SomeEngine.ECS/World.SerializationWriteAdmission.cs` |
| `SomeEngine.ECS.StructuralMutationScope` | 8 | 3 | 1 | `src/SomeEngine.ECS/StructuralMutationScope.cs` |
| `SomeEngine.ECS.World.ReadSnapshotCallbackScope` | 2 | 1 | 1 | `src/SomeEngine.ECS/World.ReadSnapshotAdmission.cs` |
| `SomeEngine.ECS.World.SerializationReadRootScope` | 4 | 1 | 1 | `src/SomeEngine.ECS/World.SerializationReadRoot.cs` |
| `SomeEngine.ECS.World.SerializationWriteLifetimeScope` | 2 | 1 | 1 | `src/SomeEngine.ECS/World.SerializationWriteAdmission.cs` |
| `SomeEngine.ECS.World.StructuralCandidateScope` | 3 | 1 | 1 | `src/SomeEngine.ECS/World.cs` |
| `SomeEngine.ECS.World.StructuralTransactionScope` | 2 | 1 | 1 | `src/SomeEngine.ECS/World.cs` |
| `SomeEngine.ECS.WorldJobAdmissionScope` | 5 | 1 | 2 | `src/SomeEngine.ECS/World.JobAdmission.cs` |
| `SomeEngine.ECS.WorldJobStorageAccess` | 3 | 0 | 1 | `src/SomeEngine.ECS/World.JobAdmission.cs` |
| `SomeEngine.ECS.WorldStorageAccess` | 0 | 0 | 0 | `src/SomeEngine.ECS/World.JobAdmission.cs` |
| `SomeEngine.ECS.WorldTopologyAccess` | 0 | 0 | 0 | `src/SomeEngine.ECS/World.JobAdmission.cs` |
| `SomeEngine.ECS.Serialization.DurableSaveStore.OperationLease` | 2 | 1 | 1 | `src/SomeEngine.ECS.Serialization/DurableSaveStore.cs` |
| `SomeEngine.ECS.Systems.BufferJobAccess<T>` | 0 | 0 | 0 | `src/SomeEngine.ECS.Systems/BufferJobAccess.cs` |
| `SomeEngine.ECS.Systems.ComponentJobAccess<T>` | 0 | 0 | 0 | `src/SomeEngine.ECS.Systems/ComponentJobAccess.cs` |
| `SomeEngine.ECS.Systems.GeneratedQueryAccess` | 9 | 2 | 1 | `src/SomeEngine.ECS.Systems/JobEntity.cs` |
| `SomeEngine.ECS.Systems.GeneratedQueryAccessDescriptor.LogicalQueryAccess` | 3 | 0 | 1 | `src/SomeEngine.ECS.Systems/JobEntity.cs` |
| `SomeEngine.ECS.Systems.HierarchyJobAccess<TDomain>` | 0 | 0 | 0 | `src/SomeEngine.ECS.Systems/HierarchyJobAccess.cs` |
| `SomeEngine.ECS.Systems.HierarchyJobAccess<TDomain>.ParentChunkJobAdapter<TJob>` | 3 | 1 | 1 | `src/SomeEngine.ECS.Systems/HierarchyJobAccess.cs` |
| `SomeEngine.ECS.Systems.HierarchyJobAccess<TDomain>.ParentReadChunkJobAdapter<TJob>` | 3 | 1 | 1 | `src/SomeEngine.ECS.Systems/HierarchyJobAccess.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>` | 0 | 0 | 0 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs`<br>`src/SomeEngine.ECS.Systems/HierarchyPropagationCapture.cs` |
| `SomeEngine.ECS.Systems.IGeneratedJobEntityAdapter<TJob>` | 0 | 1 | 0 | `src/SomeEngine.ECS.Systems/JobEntity.cs` |
| `SomeEngine.ECS.Systems.JobCommandBuffer.CompletionAdapter` | 2 | 1 | 1 | `src/SomeEngine.ECS.Systems/JobCommandBuffer.cs` |
| `SomeEngine.ECS.Systems.JobCommandBuffer.ParallelProducerAdapter<TProducer>` | 2 | 1 | 1 | `src/SomeEngine.ECS.Systems/JobCommandBuffer.cs` |
| `SomeEngine.ECS.Systems.JobCommandBuffer.PublicationAdapter` | 1 | 1 | 1 | `src/SomeEngine.ECS.Systems/JobCommandBuffer.cs` |
| `SomeEngine.ECS.Systems.JobCommandBuffer.SerialProducerAdapter<TProducer>` | 3 | 1 | 1 | `src/SomeEngine.ECS.Systems/JobCommandBuffer.cs` |
| `SomeEngine.ECS.Systems.RelationJobAccess<T>` | 0 | 0 | 0 | `src/SomeEngine.ECS.Systems/RelationJobAccess.cs` |
| `SomeEngine.ECS.Systems.RelationJobAccess<T>.DirectedEndpointChunkJobAdapter<TJob>` | 3 | 1 | 1 | `src/SomeEngine.ECS.Systems/RelationJobAccess.cs` |
| `SomeEngine.ECS.Systems.RelationJobAccess<T>.DirectedEndpointReadChunkJobAdapter<TJob>` | 3 | 1 | 1 | `src/SomeEngine.ECS.Systems/RelationJobAccess.cs` |
| `SomeEngine.ECS.Systems.RelationJobAccess<T>.UndirectedEndpointChunkJobAdapter<TJob>` | 3 | 1 | 1 | `src/SomeEngine.ECS.Systems/RelationJobAccess.cs` |
| `SomeEngine.ECS.Systems.RelationJobAccess<T>.UndirectedEndpointReadChunkJobAdapter<TJob>` | 3 | 1 | 1 | `src/SomeEngine.ECS.Systems/RelationJobAccess.cs` |
| `SomeEngine.ECS.Systems.RelationshipJobAccess` | 0 | 0 | 0 | `src/SomeEngine.ECS.Systems/RelationshipJobAccess.cs` |
| `SomeEngine.ECS.Systems.SharedJobAccess<T>` | 0 | 0 | 0 | `src/SomeEngine.ECS.Systems/SharedJobAccess.cs` |
| `SomeEngine.ECS.Systems.SparseJobAccess<T>` | 0 | 0 | 0 | `src/SomeEngine.ECS.Systems/SparseJobAccess.cs` |
| `SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>.TopologyCompletionAdapter` | 2 | 1 | 1 | `src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs` |

### 描述符命名候选（8）

后缀 `Descriptor / Desc / Metadata / Info / Definition / Schema / Manifest` 全部进入审计；名称只决定候选集合，不替代语义判断。

| 类型 | 持久成员 | 普通方法 | 构造器 | 源文件 |
| --- | ---: | ---: | ---: | --- |
| `SomeEngine.ECS.Queries.QueryDefinition` | 6 | 0 | 1 | `src/SomeEngine.ECS/Queries/QueryDefinition.cs` |
| `SomeEngine.ECS.Queries.QueryableTypeInfo` | 4 | 1 | 1 | `src/SomeEngine.ECS/Queries/QueryableCapabilities.cs` |
| `SomeEngine.ECS.Registry.ComponentInfo` | 17 | 0 | 0 | `src/SomeEngine.ECS/Registry/ComponentInfo.cs` |
| `SomeEngine.ECS.Registry.ComponentMetadata<T>` | 0 | 0 | 1 | `src/SomeEngine.ECS/Registry/ComponentMetadata.cs` |
| `SomeEngine.ECS.Registry.JobStorageTypeMetadata<T>` | 0 | 0 | 0 | `src/SomeEngine.ECS/Registry/JobStorageTypeMetadata.cs` |
| `SomeEngine.ECS.Relations.RelationSchema` | 3 | 1 | 1 | `src/SomeEngine.ECS/Relations/RelationSchema.cs` |
| `SomeEngine.ECS.Serialization.WorldCheckpointInfo` | 3 | 0 | 1 | `src/SomeEngine.ECS.Serialization/WorldCheckpointCodec.cs` |
| `SomeEngine.ECS.Systems.GeneratedQueryAccessDescriptor` | 7 | 5 | 1 | `src/SomeEngine.ECS.Systems/JobEntity.cs` |

### 可保留数组/集合边界候选（42）

表中只列有效可见的数组或可保留集合签名；`ref`、`Span<T>`、`ReadOnlySpan<T>`、`Memory<T>` 和 `ReadOnlyMemory<T>` 不在候选中。候选必须是显式所有权转移/快照，不能伪装成同步借用。

| 类型 | 成员 | 角色 | 边界类型 | 判定 | 可见性 |
| --- | --- | --- | --- | --- | --- |
| `SomeEngine.ECS.Serialization.SparseSerializationPresence` | `AddPresentRuntimesTo` | `parameter:destination` | `System.Collections.Generic.HashSet<SomeEngine.ECS.Serialization.SerializationTypeRuntime>` | `serialization-destination` | `Internal` |
| `SomeEngine.ECS.Systems.HierarchyPropagationPartitionProof` | `.ctor` | `parameter:ownedRanges` | `SomeEngine.ECS.Systems.HierarchyPropagationPacketRange[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Systems.HierarchyPropagationPartitionProof` | `.ctor` | `parameter:ownedRoots` | `SomeEngine.ECS.Entities.Entity[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Systems.ParentTopologyStage<TDomain>` | `.ctor` | `parameter:ownedCapturedParents` | `SomeEngine.ECS.Hierarchy.Parent<TDomain>[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Systems.ParentTopologyStage<TDomain>` | `.ctor` | `parameter:ownedEntities` | `SomeEngine.ECS.Entities.Entity[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Systems.ParentTopologyStage<TDomain>` | `PublishPacketEdits` | `parameter:ownedEdits` | `SomeEngine.ECS.Systems.ParentTopologyEdit[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Systems.ReadOnlyQueryPacketPlan` | `.ctor` | `parameter:ownedPackets` | `SomeEngine.ECS.Systems.ReadOnlyPacketRange[]?` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Systems.StableQueryPacketSet` | `.ctor` | `parameter:ownedPackets` | `SomeEngine.ECS.Systems.QueryPacket[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Systems.StableQueryPartitionProof` | `.ctor` | `parameter:ownedRanges` | `SomeEngine.ECS.Systems.StableQueryPacketRange[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Archetypes.ArchetypeRegistry` | `InsertSorted` | `return` | `int[]` | `owner-construction` | `Internal` |
| `SomeEngine.ECS.Archetypes.ArchetypeRegistry` | `RemoveSorted` | `return` | `int[]` | `owner-construction` | `Internal` |
| `SomeEngine.ECS.Archetypes.ArchetypeRegistry` | `SharedMap` | `return` | `SomeEngine.ECS.Archetypes.SharedColumnMapping[]` | `owner-construction` | `Internal` |
| `SomeEngine.ECS.Archetypes.Chunk` | `CaptureComponentValue` | `return` | `System.Array` | `one-row-owner-snapshot` | `Internal` |
| `SomeEngine.ECS.Archetypes.Chunk` | `SetOwnedBufferOverflow` | `parameter:ownedOverflow` | `T[]?` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Archetypes.StructuralTransition` | `.ctor` | `parameter:ownedSharedColumns` | `SomeEngine.ECS.Archetypes.SharedColumnMapping[]` | `ownership-transfer` | `Public` |
| `SomeEngine.ECS.Collections.ArrayGrowthExtensions` | `EnsureCapacity` | `parameter:array` | `T[]?` | `owner-growth-by-ref` | `Public` |
| `SomeEngine.ECS.Commands.SetOrderPolicyCommand<TDomain>` | `.ctor` | `parameter:ownedPermutation` | `SomeEngine.ECS.Commands.CommandEntity[]?` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Components.DynamicBufferHeader<T>` | `SetOwnedOverflow` | `parameter:ownedOverflow` | `T[]?` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Hierarchy.HierarchyChildrenView<TDomain>` | `ToArray` | `return` | `SomeEngine.ECS.Entities.Entity[]` | `explicit-owner-copy` | `Public` |
| `SomeEngine.ECS.Owners.Components` | `CommitRemove` | `parameter:ownedOldValueSnapshot` | `System.Array` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Owners.Components` | `CommitReplace` | `parameter:ownedOldValueSnapshot` | `System.Array` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.TopologyImport` | `AddOrderedSequence` | `parameter:ownedChildren` | `SomeEngine.ECS.Entities.Entity[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Owners.PublishedChildren` | `.ctor` | `parameter:ownedItems` | `SomeEngine.ECS.Entities.Entity[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Queries.QueryArchetypeMatch` | `.ctor` | `parameter:ownedAccessColumns` | `SomeEngine.ECS.Queries.QueryColumnAccess[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Queries.QueryArchetypeMatch` | `.ctor` | `parameter:ownedChunkColumns` | `int[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Queries.QueryArchetypeMatch` | `.ctor` | `parameter:ownedDisabledMasks` | `int[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Queries.QueryArchetypeMatch` | `.ctor` | `parameter:ownedEnabledMasks` | `int[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Queries.QueryArchetypeMatch` | `.ctor` | `parameter:ownedExactTerms` | `SomeEngine.ECS.Queries.ChangeTerm[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Queries.QueryKey` | `.ctor` | `parameter:ownedTerms` | `SomeEngine.ECS.Queries.QueryTerm[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Relations.OrderedRelationAdjacencyShard<T>` | `.ctor` | `parameter:ownedEntries` | `SomeEngine.ECS.Relations.RelationAdjacencyEntry<T>[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Relations.PreparedRelationState<T>` | `.ctor` | `parameter:ownedAffectedShards` | `SomeEngine.ECS.Relations.RelationAffectedShard[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Relations.RelationAdjacencyShard<T>` | `.ctor` | `parameter:ownedEntries` | `SomeEngine.ECS.Relations.RelationAdjacencyEntry<T>[]` | `ownership-transfer` | `Protected` |
| `SomeEngine.ECS.Relations.RelationEdgeQuery<T>` | `ToArray` | `return` | `SomeEngine.ECS.Relations.RelationEdge<T>[]` | `explicit-owner-copy` | `Public` |
| `SomeEngine.ECS.Relations.RelationEntityMap<TValue>` | `ToEntityArray` | `return` | `SomeEngine.ECS.Entities.Entity[]` | `stable-snapshot` | `Internal` |
| `SomeEngine.ECS.Relations.RelationEntityMap<TValue>.Storage` | `.ctor` | `parameter:ownedPages` | `SomeEngine.ECS.Relations.RelationEntityMap<TValue>.Page?[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Relations.RelationGeneration<T>` | `ImportShard` | `parameter:ownedEntries` | `SomeEngine.ECS.Relations.RelationAdjacencyEntry<T>[]` | `ownership-transfer` | `Internal` |
| `SomeEngine.ECS.Relations.RelationGeneration<T>` | `OrderedShardKeysStable` | `return` | `(SomeEngine.ECS.Entities.Entity Endpoint, SomeEngine.ECS.Relations.RelationAdjacencyRole Role)[]` | `stable-mutation-plan` | `Internal` |
| `SomeEngine.ECS.Relations.RelationTypeSlotTable` | `SnapshotValues` | `return` | `SomeEngine.ECS.Relations.IRelationTypeState[]` | `stable-snapshot` | `Internal` |
| `SomeEngine.ECS.Relations.RelationTypeState<T>` | `CommandBatchEdgesAt` | `return` | `SomeEngine.ECS.Relations.RelationEdge<T>[]` | `stable-mutation-plan` | `Internal` |
| `SomeEngine.ECS.Relations.RelationTypeState<T>` | `CommandBatchEdgesBetween` | `return` | `SomeEngine.ECS.Relations.RelationEdge<T>[]` | `stable-mutation-plan` | `Internal` |
| `SomeEngine.ECS.Relations.RelationTypeState<T>` | `DirtyEdgesStable` | `return` | `SomeEngine.ECS.Relations.RelationEdge<T>[]` | `stable-mutation-plan` | `Internal` |
| `SomeEngine.ECS.Relations.UnorderedRelationAdjacencyShard<T>` | `.ctor` | `parameter:ownedEntries` | `SomeEngine.ECS.Relations.RelationAdjacencyEntry<T>[]` | `ownership-transfer` | `Internal` |

多来源 `state` 仅是重复所有权候选：引用类型可能被多个对象合法共享；只有构造、替换、发布和清理路径同时声称生命周期时才是重复 owner。

### 多来源持久引用目标（51）

| 被保留类型 | 来源数 | 来源类型 |
| --- | ---: | --- |
| `SomeEngine.ECS.World` | 51 | `SomeEngine.ECS.Serialization.AdmittedWorldWrite`<br>`SomeEngine.ECS.Systems.HierarchyJobAccess<TDomain>.ParentChunkJobAdapter<TJob>`<br>`SomeEngine.ECS.Systems.HierarchyJobAccess<TDomain>.ParentReadChunkJobAdapter<TJob>`<br>`SomeEngine.ECS.Systems.HierarchyMaintenanceDependency<TDomain>`<br>`SomeEngine.ECS.Systems.HierarchyMaintenanceSystem<TDomain>.MaintenanceJob`<br>`SomeEngine.ECS.Systems.HierarchyPropagationAccessSet<TDomain>`<br>`SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>.PropagationOwnerJob<TJob>`<br>`SomeEngine.ECS.Systems.ImmediateSystemContext`<br>`SomeEngine.ECS.Systems.ImmediateSystemDriver`<br>`SomeEngine.ECS.Systems.JobCommandBuffer`<br>`SomeEngine.ECS.Systems.JobEntityRow`<br>`SomeEngine.ECS.Systems.JobEntityRuntime.PacketCaptureJob<TJob, TAdapter>`<br>`SomeEngine.ECS.Systems.JobEntityRuntime.SerialJob<TJob, TAdapter>`<br>`SomeEngine.ECS.Systems.JobEntityRuntime.SerialQueryCaptureJob<TJob, TAdapter>`<br>`SomeEngine.ECS.Systems.JobEntityRuntime.SerialState<TJob, TAdapter>`<br>`SomeEngine.ECS.Systems.ParentTopologyStage<TDomain>`<br>`SomeEngine.ECS.Systems.ReadOnlyQueryPacketPlan`<br>`SomeEngine.ECS.Systems.RelationJobAccess<T>.DirectedEndpointChunkJobAdapter<TJob>`<br>`SomeEngine.ECS.Systems.RelationJobAccess<T>.DirectedEndpointReadChunkJobAdapter<TJob>`<br>`SomeEngine.ECS.Systems.RelationJobAccess<T>.UndirectedEndpointChunkJobAdapter<TJob>`<br>`SomeEngine.ECS.Systems.RelationJobAccess<T>.UndirectedEndpointReadChunkJobAdapter<TJob>`<br>`SomeEngine.ECS.Systems.RelationMaintenanceSystem<T>.MaintenanceJob`<br>`SomeEngine.ECS.Systems.StableQueryPacketSet`<br>`SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>.CaptureAndScheduleJob<TJob>`<br>`SomeEngine.ECS.Commands.CommandBuffer`<br>`SomeEngine.ECS.Commands.CommandBuffer.JobProducerPlaybackBatch`<br>`SomeEngine.ECS.Commands.CommandPlaybackContext`<br>`SomeEngine.ECS.Commands.DeferredEntityCell`<br>`SomeEngine.ECS.Commands.DeferredRelationEdgeCell<T>`<br>`SomeEngine.ECS.Hooks.DeferredWorld`<br>`SomeEngine.ECS.Owners.Components`<br>`SomeEngine.ECS.Owners.Entities`<br>`SomeEngine.ECS.Owners.Hooks`<br>`SomeEngine.ECS.Queries.QueryChunkEnumerator<TFilter>`<br>`SomeEngine.ECS.Queries.QueryChunkView`<br>`SomeEngine.ECS.Queries.QueryCursor`<br>`SomeEngine.ECS.Queries.QueryPairEnumerator<TWrite, TRead>`<br>`SomeEngine.ECS.Queries.QueryRow`<br>`SomeEngine.ECS.Queries.QueryRowCursor`<br>`SomeEngine.ECS.Queries.QuerySharedFilter`<br>`SomeEngine.ECS.Serialization.RelationTopologyImport<T>`<br>`SomeEngine.ECS.Serialization.RelationTopologyWriteAccess<T>`<br>`SomeEngine.ECS.StructuralMutationScope`<br>`SomeEngine.ECS.World.ReadSnapshotCallbackScope`<br>`SomeEngine.ECS.World.SerializationReadRootContext`<br>`SomeEngine.ECS.World.SerializationReadRootScope`<br>`SomeEngine.ECS.World.SerializationWriteLifetimeScope`<br>`SomeEngine.ECS.World.StructuralCandidateContext`<br>`SomeEngine.ECS.World.StructuralCandidateScope`<br>`SomeEngine.ECS.World.StructuralTransactionScope`<br>`SomeEngine.ECS.WorldJobAdmissionScope` |
| `SomeEngine.ECS.Archetypes.Chunk` | 17 | `SomeEngine.ECS.Systems.JobEntityRow`<br>`SomeEngine.ECS.Systems.QueryPacket`<br>`SomeEngine.ECS.Systems.ReadOnlyQueryPacket`<br>`SomeEngine.ECS.Archetypes.Archetype`<br>`SomeEngine.ECS.Archetypes.DetachedTableMap`<br>`SomeEngine.ECS.Archetypes.SharedChunkBucket`<br>`SomeEngine.ECS.BufferView<T>`<br>`SomeEngine.ECS.BundleMaterializedRow`<br>`SomeEngine.ECS.BundleWriteRuntime`<br>`SomeEngine.ECS.DynamicBuffer<T>`<br>`SomeEngine.ECS.Entities.EntityRecord`<br>`SomeEngine.ECS.Entities.EntityStore`<br>`SomeEngine.ECS.Queries.ChunkRowIndexEnumerator`<br>`SomeEngine.ECS.Queries.QueryChunkEnumerator<TFilter>`<br>`SomeEngine.ECS.Queries.QueryChunkView`<br>`SomeEngine.ECS.Queries.QueryRow`<br>`SomeEngine.ECS.Queries.QueryRowCursor` |
| `SomeEngine.ECS.Archetypes.Archetype` | 13 | `SomeEngine.ECS.Serialization.WorldWritePlan`<br>`SomeEngine.ECS.Archetypes.ArchetypeRegistry`<br>`SomeEngine.ECS.Archetypes.DetachedTableMap`<br>`SomeEngine.ECS.Archetypes.StructuralTransition`<br>`SomeEngine.ECS.BundleMaterializedRow`<br>`SomeEngine.ECS.BundleSpawnMap`<br>`SomeEngine.ECS.Entities.EntityRecord`<br>`SomeEngine.ECS.Entities.EntityStore`<br>`SomeEngine.ECS.Owners.Tables`<br>`SomeEngine.ECS.Queries.QueryArchetypeMatch`<br>`SomeEngine.ECS.Queries.QueryState`<br>`SomeEngine.ECS.Queries.QueryState.QueryMatchBuilder`<br>`SomeEngine.ECS.Queries.ReadWriteMatch` |
| `SomeEngine.ECS.Queries.QueryArchetypeMatch` | 11 | `SomeEngine.ECS.Systems.JobEntityRow`<br>`SomeEngine.ECS.Systems.QueryPacket`<br>`SomeEngine.ECS.Systems.ReadOnlyQueryPacket`<br>`SomeEngine.ECS.Queries.ChunkRowIndexEnumerator`<br>`SomeEngine.ECS.Queries.QueryChunkEnumerator<TFilter>`<br>`SomeEngine.ECS.Queries.QueryChunkView`<br>`SomeEngine.ECS.Queries.QueryRow`<br>`SomeEngine.ECS.Queries.QueryRowCursor`<br>`SomeEngine.ECS.Queries.QueryState`<br>`SomeEngine.ECS.Queries.ReadWriteMatch`<br>`SomeEngine.ECS.Queries.SingleSharedFilter` |
| `SomeEngine.ECS.Commands.CommandBuffer` | 9 | `SomeEngine.ECS.Systems.JobCommandBuffer.ProducerSegment`<br>`SomeEngine.ECS.Systems.JobCommandWriter`<br>`SomeEngine.ECS.Commands.DeferredEntityCell`<br>`SomeEngine.ECS.Commands.DeferredRelationEdgeCell<T>`<br>`SomeEngine.ECS.Commands.HierarchyCommandWriter<TDomain>`<br>`SomeEngine.ECS.Commands.RelationCommandWriter<T>`<br>`SomeEngine.ECS.Hooks.DeferredCommandWriter`<br>`SomeEngine.ECS.JobCommandProducerScope`<br>`SomeEngine.ECS.Owners.Commands` |
| `SomeEngine.ECS.Owners.Entities` | 9 | `SomeEngine.ECS.Owners.Buffers`<br>`SomeEngine.ECS.Owners.Bundles`<br>`SomeEngine.ECS.Owners.Components`<br>`SomeEngine.ECS.Owners.Copy`<br>`SomeEngine.ECS.Owners.Hierarchy`<br>`SomeEngine.ECS.Owners.Shared`<br>`SomeEngine.ECS.Owners.Sparse`<br>`SomeEngine.ECS.Owners.Tables`<br>`SomeEngine.ECS.WorldStructureRoot` |
| `SomeEngine.ECS.Owners.Iteration` | 8 | `SomeEngine.ECS.Owners.Buffers`<br>`SomeEngine.ECS.Owners.Bundles`<br>`SomeEngine.ECS.Owners.Components`<br>`SomeEngine.ECS.Owners.Copy`<br>`SomeEngine.ECS.Owners.Entities`<br>`SomeEngine.ECS.Owners.Shared`<br>`SomeEngine.ECS.Owners.Sparse`<br>`SomeEngine.ECS.WorldStructureRoot` |
| `SomeEngine.ECS.Owners.Tables` | 7 | `SomeEngine.ECS.Owners.Bundles`<br>`SomeEngine.ECS.Owners.Components`<br>`SomeEngine.ECS.Owners.Copy`<br>`SomeEngine.ECS.Owners.Entities`<br>`SomeEngine.ECS.Owners.Hierarchy`<br>`SomeEngine.ECS.Owners.Shared`<br>`SomeEngine.ECS.WorldStructureRoot` |
| `SomeEngine.ECS.Systems.GeneratedQueryAccessDescriptor` | 6 | `SomeEngine.ECS.Systems.JobEntityRow`<br>`SomeEngine.ECS.Systems.JobEntityRuntime.PacketCaptureJob<TJob, TAdapter>`<br>`SomeEngine.ECS.Systems.JobEntityRuntime.ParallelJob<TJob, TAdapter>`<br>`SomeEngine.ECS.Systems.JobEntityRuntime.SerialJob<TJob, TAdapter>`<br>`SomeEngine.ECS.Systems.JobEntityRuntime.SerialQueryCaptureJob<TJob, TAdapter>`<br>`SomeEngine.ECS.Systems.JobEntityRuntime.SerialState<TJob, TAdapter>` |
| `SomeEngine.ECS.Owners.Clock` | 6 | `SomeEngine.ECS.Owners.Buffers`<br>`SomeEngine.ECS.Owners.Bundles`<br>`SomeEngine.ECS.Owners.Components`<br>`SomeEngine.ECS.Owners.Copy`<br>`SomeEngine.ECS.Owners.Hierarchy`<br>`SomeEngine.ECS.WorldStructureRoot` |
| `SomeEngine.ECS.Owners.Components` | 6 | `SomeEngine.ECS.Owners.Buffers`<br>`SomeEngine.ECS.Owners.Bundles`<br>`SomeEngine.ECS.Owners.Copy`<br>`SomeEngine.ECS.Owners.Entities`<br>`SomeEngine.ECS.Owners.Hierarchy`<br>`SomeEngine.ECS.WorldStructureRoot` |
| `SomeEngine.ECS.Owners.Hierarchy` | 6 | `SomeEngine.ECS.Owners.Bundles`<br>`SomeEngine.ECS.Owners.Components`<br>`SomeEngine.ECS.Owners.Copy`<br>`SomeEngine.ECS.Owners.Entities`<br>`SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>`<br>`SomeEngine.ECS.WorldStructureRoot` |
| `SomeEngine.ECS.Owners.RelationGraph` | 5 | `SomeEngine.ECS.Owners.Components`<br>`SomeEngine.ECS.Owners.Entities`<br>`SomeEngine.ECS.Owners.RelationGraph.RelationEndpointTracker<T>`<br>`SomeEngine.ECS.Serialization.RelationTopologyImport<T>`<br>`SomeEngine.ECS.WorldStructureRoot` |
| `SomeEngine.ECS.TopologyOrderDiagnosticCounter` | 5 | `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>`<br>`SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.OrderedChildShard`<br>`SomeEngine.ECS.Relations.MutableRelationAdjacencyShard<T>`<br>`SomeEngine.ECS.Relations.RelationGeneration<T>`<br>`SomeEngine.ECS.Relations.RelationTypeState<T>` |
| `SomeEngine.ECS.WorldStructureRoot` | 5 | `SomeEngine.ECS.Serialization.AdmittedWorldWrite`<br>`SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>.AdmittedHierarchyReader`<br>`SomeEngine.ECS.World.SerializationReadRootContext`<br>`SomeEngine.ECS.World.StructuralCandidateContext`<br>`SomeEngine.ECS.WorldStructurePublication` |
| `SomeEngine.ECS.Systems.JobCommandBuffer` | 4 | `SomeEngine.ECS.Systems.JobCommandBuffer.CompletionAdapter`<br>`SomeEngine.ECS.Systems.JobCommandBuffer.ParallelProducerAdapter<TProducer>`<br>`SomeEngine.ECS.Systems.JobCommandBuffer.PublicationAdapter`<br>`SomeEngine.ECS.Systems.JobCommandBuffer.SerialProducerAdapter<TProducer>` |
| `SomeEngine.ECS.Commands.DeferredEntityCell` | 4 | `SomeEngine.ECS.Commands.CommandBuffer`<br>`SomeEngine.ECS.Commands.CommandEntity`<br>`SomeEngine.ECS.Commands.CommandPlaybackContext`<br>`SomeEngine.ECS.Commands.DeferredEntity` |
| `SomeEngine.ECS.Owners.Buffers` | 4 | `SomeEngine.ECS.DynamicBuffer<T>`<br>`SomeEngine.ECS.Owners.Bundles`<br>`SomeEngine.ECS.Owners.Copy`<br>`SomeEngine.ECS.WorldStructureRoot` |
| `SomeEngine.ECS.Owners.Hooks` | 4 | `SomeEngine.ECS.Owners.Bundles`<br>`SomeEngine.ECS.Owners.Components`<br>`SomeEngine.ECS.Owners.Hooks.HookExecutionScope`<br>`SomeEngine.ECS.World` |
| `SomeEngine.ECS.Owners.Indices` | 4 | `SomeEngine.ECS.Owners.Bundles`<br>`SomeEngine.ECS.Owners.Components`<br>`SomeEngine.ECS.Owners.Copy`<br>`SomeEngine.ECS.WorldStructureRoot` |
| `SomeEngine.ECS.Owners.Sparse` | 4 | `SomeEngine.ECS.Owners.Bundles`<br>`SomeEngine.ECS.Owners.Copy`<br>`SomeEngine.ECS.Owners.Entities`<br>`SomeEngine.ECS.WorldStructureRoot` |
| `SomeEngine.ECS.Queries.QueryDefinition` | 4 | `SomeEngine.ECS.Systems.GeneratedQueryAccessDescriptor`<br>`SomeEngine.ECS.Systems.JobEntityScheduleOptions`<br>`SomeEngine.ECS.Queries.QueryRecord`<br>`SomeEngine.ECS.Queries.QueryState` |
| `SomeEngine.ECS.Serialization.SerializationReadBudget` | 3 | `SomeEngine.ECS.Serialization.BufferSerializationRuntime<T>.BufferApplyState`<br>`SomeEngine.ECS.Serialization.DataReader`<br>`SomeEngine.ECS.Serialization.WorldSerializer.PayloadFrame` |
| `SomeEngine.ECS.Serialization.SerializationTypeRuntime` | 3 | `SomeEngine.ECS.Serialization.SerializationRegistry`<br>`SomeEngine.ECS.Serialization.SparseSerializationPresence`<br>`SomeEngine.ECS.Serialization.WorldWritePlan` |
| `SomeEngine.ECS.Systems.ParentTopologyStage<TDomain>` | 3 | `SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>.FinalizerLauncherJob`<br>`SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>.ParentFinalizerJob`<br>`SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>.ParentPacketJob<TJob>` |
| `SomeEngine.ECS.Systems.StableQueryPartitionProof` | 3 | `SomeEngine.ECS.Systems.ParentTopologyStage<TDomain>`<br>`SomeEngine.ECS.Systems.StableQueryPacketSet`<br>`SomeEngine.ECS.Systems.TopologyOperationState` |
| `SomeEngine.ECS.Systems.TopologyOperationState` | 3 | `SomeEngine.ECS.Systems.TopologyFinalization`<br>`SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>.CaptureAndScheduleJob<TJob>`<br>`SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>.TopologyCompletionAdapter` |
| `SomeEngine.ECS.Archetypes.SharedComponentTuple` | 3 | `SomeEngine.ECS.Archetypes.Archetype`<br>`SomeEngine.ECS.Archetypes.Chunk`<br>`SomeEngine.ECS.Archetypes.SharedChunkBucket` |
| `SomeEngine.ECS.BundleSpawnMap` | 3 | `SomeEngine.ECS.BundleMaterializedRow`<br>`SomeEngine.ECS.BundleWriteRuntime`<br>`SomeEngine.ECS.Owners.Bundles` |
| `SomeEngine.ECS.Commands.DeferredRelationEdgeCell<T>` | 3 | `SomeEngine.ECS.Commands.CreateRelationCommand<T>`<br>`SomeEngine.ECS.Commands.DeferredRelationEdge<T>`<br>`SomeEngine.ECS.Commands.RelationCommandEdge<T>` |
| `SomeEngine.ECS.Owners.Bundles` | 3 | `SomeEngine.ECS.BundleWriteRuntime`<br>`SomeEngine.ECS.Owners.Buffers`<br>`SomeEngine.ECS.WorldStructureRoot` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>` | 3 | `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>.AdmittedHierarchyReader`<br>`SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.TopologyImport`<br>`SomeEngine.ECS.Serialization.HierarchyTopologyWriteAccess<TDomain>` |
| `SomeEngine.ECS.Queries.QueryState` | 3 | `SomeEngine.ECS.Queries.QueryChunkEnumerator<TFilter>`<br>`SomeEngine.ECS.Queries.QueryCursor`<br>`SomeEngine.ECS.Queries.QueryRecord` |
| `SomeEngine.ECS.Relations.RelationGeneration<T>` | 3 | `SomeEngine.ECS.Relations.PreparedRelationState<T>`<br>`SomeEngine.ECS.Relations.RelationTypeState<T>`<br>`SomeEngine.ECS.Serialization.RelationTopologyImport<T>` |
| `SomeEngine.ECS.Relations.RelationTypeState<T>` | 3 | `SomeEngine.ECS.Owners.RelationGraph.RelationEndpointTracker<T>`<br>`SomeEngine.ECS.Serialization.RelationTopologyImport<T>`<br>`SomeEngine.ECS.Serialization.RelationTopologyWriteAccess<T>` |
| `SomeEngine.ECS.Systems.HierarchyMaintenanceEvidence` | 2 | `SomeEngine.ECS.Systems.HierarchyMaintenanceDependency<TDomain>`<br>`SomeEngine.ECS.Systems.HierarchyMaintenanceSystem<TDomain>.MaintenanceJob` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAccessSet<TDomain>` | 2 | `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>.PropagationPacketJob<TJob>`<br>`SomeEngine.ECS.Systems.HierarchyPropagationContext<TDomain>` |
| `SomeEngine.ECS.Systems.HierarchyPropagationState` | 2 | `SomeEngine.ECS.Systems.HierarchyPropagation`<br>`SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>.PropagationOwnerJob<TJob>` |
| `SomeEngine.ECS.Archetypes.ArchetypeRegistry` | 2 | `SomeEngine.ECS.Entities.EntityStore`<br>`SomeEngine.ECS.Owners.Tables` |
| `SomeEngine.ECS.BundleWriteRuntime` | 2 | `SomeEngine.ECS.BundleWriteView`<br>`SomeEngine.ECS.Owners.Bundles` |
| `SomeEngine.ECS.Entities.EntityStore` | 2 | `SomeEngine.ECS.Entities.EntityRecordWriter`<br>`SomeEngine.ECS.Owners.Entities` |
| `SomeEngine.ECS.IWorldJobAdmission` | 2 | `SomeEngine.ECS.World`<br>`SomeEngine.ECS.WorldJobAdmissionScope` |
| `SomeEngine.ECS.Indexing.ComponentIndex<TComponent, TKey>.Bucket` | 2 | `SomeEngine.ECS.Indexing.ComponentIndex<TComponent, TKey>.Builder`<br>`SomeEngine.ECS.Indexing.ComponentIndex<TComponent, TKey>.Generation` |
| `SomeEngine.ECS.Owners.Shared` | 2 | `SomeEngine.ECS.Owners.Bundles`<br>`SomeEngine.ECS.WorldStructureRoot` |
| `SomeEngine.ECS.Relations.IRelationTypeState` | 2 | `SomeEngine.ECS.Relations.RelationTypeSlotTable`<br>`SomeEngine.ECS.Relations.RelationTypeSlotTable.Enumerator` |
| `SomeEngine.ECS.Relations.RelationAdjacencyBatchDiagnosticCounter` | 2 | `SomeEngine.ECS.Relations.RelationGeneration<T>`<br>`SomeEngine.ECS.Relations.RelationTypeState<T>` |
| `SomeEngine.ECS.Relations.RelationComponentSlotTable<TValue>` | 2 | `SomeEngine.ECS.Owners.RelationGraph`<br>`SomeEngine.ECS.World` |
| `SomeEngine.ECS.Relations.RelationEntityMap<TValue>` | 2 | `SomeEngine.ECS.Relations.RelationGeneration<T>`<br>`SomeEngine.ECS.Relations.RelationTypeState<T>` |
| `SomeEngine.ECS.Relations.RelationEntityMap<TValue>.Storage` | 2 | `SomeEngine.ECS.Relations.RelationEntityMap<TValue>`<br>`SomeEngine.ECS.Relations.RelationEntityMap<TValue>.Enumerator` |
| `SomeEngine.ECS.Serialization.RelationTopologyImport<T>.MembershipPlan` | 2 | `SomeEngine.ECS.Serialization.RelationTopologyImport<T>`<br>`SomeEngine.ECS.Serialization.RelationTopologyImport<T>.OrderedSequence` |
| `SomeEngine.ECS.WorldStructurePublication` | 2 | `SomeEngine.ECS.StructuralMutationScope`<br>`SomeEngine.ECS.World` |

`ToArray` 记录用于边界复核：允许位置是 owner 构造、显式 Snapshot/发布、序列化边界或异步任务所有权转移；查询、逐实体修改与写入热路径不得物化。

### `ToArray` 位置（61）

| 类型 | 成员 | 已审边界 | 位置 |
| --- | --- | --- | --- |
| `SomeEngine.ECS.Serialization.WorldWritePlan` | `Build` | `serialization-boundary` | `src/SomeEngine.ECS.Serialization/AdmittedWorldWrite.cs:497` |
| `SomeEngine.ECS.Serialization.WorldWritePlan` | `Build` | `serialization-boundary` | `src/SomeEngine.ECS.Serialization/AdmittedWorldWrite.cs:500` |
| `SomeEngine.ECS.Serialization.WorldSerializer` | `SortManifest` | `serialization-boundary` | `src/SomeEngine.ECS.Serialization/WorldSerializer.ManifestValidation.cs:72` |
| `SomeEngine.ECS.SourceGen.BundleGenerator` | `GenerateSource` | `source-generation` | `src/SomeEngine.ECS.SourceGen/BundleGenerator.cs:325` |
| `SomeEngine.ECS.SourceGen.BundleGenerator` | `GenerateSource` | `source-generation` | `src/SomeEngine.ECS.SourceGen/BundleGenerator.cs:330` |
| `SomeEngine.ECS.SourceGen.BundleGenerator` | `GenerateSource` | `source-generation` | `src/SomeEngine.ECS.SourceGen/BundleGenerator.cs:335` |
| `SomeEngine.ECS.SourceGen.BundleGenerator` | `GenerateSource` | `source-generation` | `src/SomeEngine.ECS.SourceGen/BundleGenerator.cs:341` |
| `SomeEngine.ECS.SourceGen.JobEntityGenerator` | `BuildModel` | `source-generation` | `src/SomeEngine.ECS.SourceGen/JobEntityGenerator.cs:142` |
| `SomeEngine.ECS.SourceGen.JobEntityGenerator` | `Generate` | `source-generation` | `src/SomeEngine.ECS.SourceGen/JobEntityGenerator.cs:358` |
| `SomeEngine.ECS.SourceGen.SerializationGenerator` | `AddEnumSchema` | `source-generation` | `src/SomeEngine.ECS.SourceGen/SerializationGenerator.cs:734` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>` | `Schedule` | `async-transfer` | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs:323` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>` | `Schedule` | `async-transfer` | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs:325` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>` | `NormalizeRoots` | `async-transfer` | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs:477` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>` | `NormalizeRoots` | `async-transfer` | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs:508` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>` | `CaptureTraversal` | `async-transfer` | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs:597` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>` | `CaptureTraversal` | `async-transfer` | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs:638` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>` | `CaptureTraversal` | `async-transfer` | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs:640` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>` | `BuildDataAccesses` | `async-transfer` | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs:818` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>` | `BuildDataAccesses` | `async-transfer` | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs:843` |
| `SomeEngine.ECS.Systems.GeneratedQueryAccessDescriptor` | `NormalizeAndValidate` | `owner-construction` | `src/SomeEngine.ECS.Systems/JobEntity.cs:465` |
| `SomeEngine.ECS.Systems.JobEntityRuntime` | `CapturePackets` | `async-transfer` | `src/SomeEngine.ECS.Systems/JobEntityRuntime.cs:315` |
| `SomeEngine.ECS.Systems.JobEntityRuntime` | `CapturePackets` | `async-transfer` | `src/SomeEngine.ECS.Systems/JobEntityRuntime.cs:320` |
| `SomeEngine.ECS.Systems.JobEntityRuntime` | `BuildPacketAccesses` | `async-transfer` | `src/SomeEngine.ECS.Systems/JobEntityRuntime.cs:385` |
| `SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>` | `BuildCaptureAccesses` | `async-transfer` | `src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs:264` |
| `SomeEngine.ECS.Archetypes.Archetype` | `.ctor` | `owner-construction` | `src/SomeEngine.ECS/Archetypes/Archetype.cs:181` |
| `SomeEngine.ECS.Archetypes.Archetype` | `.ctor` | `owner-construction` | `src/SomeEngine.ECS/Archetypes/Archetype.cs:216` |
| `SomeEngine.ECS.Archetypes.Archetype` | `.ctor` | `owner-construction` | `src/SomeEngine.ECS/Archetypes/Archetype.cs:217` |
| `SomeEngine.ECS.Archetypes.Archetype` | `.ctor` | `owner-construction` | `src/SomeEngine.ECS/Archetypes/Archetype.cs:218` |
| `SomeEngine.ECS.Archetypes.Archetype` | `.ctor` | `owner-construction` | `src/SomeEngine.ECS/Archetypes/Archetype.cs:219` |
| `SomeEngine.ECS.Archetypes.Archetype` | `.ctor` | `owner-construction` | `src/SomeEngine.ECS/Archetypes/Archetype.cs:220` |
| `SomeEngine.ECS.Archetypes.Archetype` | `.ctor` | `owner-construction` | `src/SomeEngine.ECS/Archetypes/Archetype.cs:222` |
| `SomeEngine.ECS.Archetypes.SharedComponentTuple` | `.ctor` | `owner-construction` | `src/SomeEngine.ECS/Archetypes/SharedComponentTuple.cs:21` |
| `SomeEngine.ECS.BundleSpawnMap` | `.ctor` | `owner-construction` | `src/SomeEngine.ECS/BundleSpawnMap.cs:15` |
| `SomeEngine.ECS.Hierarchy.HierarchyChildrenView<TDomain>` | `ToArray` | `explicit-owner-copy` | `src/SomeEngine.ECS/Hierarchy/HierarchyComponents.cs:88` |
| `SomeEngine.ECS.Indexing.ComponentIndex<TComponent, TKey>.Bucket` | `Publish` | `cow-publication` | `src/SomeEngine.ECS/Indexing/ComponentIndex.cs:263` |
| `SomeEngine.ECS.Owners.Copy.CopyShape` | `CopyIds` | `stable-mutation-plan` | `src/SomeEngine.ECS/Owners.Copy.cs:343` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>` | `StableEntities` | `stable-mutation-plan` | `src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs:106` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.UnorderedChildShard` | `.ctor` | `cow-clone` | `src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs:201` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.UnorderedChildShard` | `PublishSnapshot` | `cow-publication` | `src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs:267` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.OrderedChildShard` | `.ctor` | `cow-clone` | `src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs:321` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.OrderedChildShard` | `PublishSnapshot` | `cow-publication` | `src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs:413` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>` | `SetOrderPolicy` | `cow-publication` | `src/SomeEngine.ECS/Owners.Hierarchy.cs:992` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>` | `PrepareMaintenance` | `stable-mutation-plan` | `src/SomeEngine.ECS/Owners.Hierarchy.cs:1192` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>` | `BeginTerminalDestroy` | `stable-mutation-plan` | `src/SomeEngine.ECS/Owners.Hierarchy.cs:1424` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>` | `RollbackPreimages` | `rollback-snapshot` | `src/SomeEngine.ECS/Owners.Hierarchy.cs:1838` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>` | `CollectCandidates` | `stable-mutation-plan` | `src/SomeEngine.ECS/Owners.Hierarchy.cs:1881` |
| `SomeEngine.ECS.Owners.RelationGraph.RelationEndpointTracker<T>` | `Rollback` | `rollback-snapshot` | `src/SomeEngine.ECS/Owners.RelationGraph.EndpointTracking.cs:165` |
| `SomeEngine.ECS.Queries.QueryDefinition` | `.ctor` | `owner-construction` | `src/SomeEngine.ECS/Queries/QueryDefinition.cs:30` |
| `SomeEngine.ECS.Queries.QueryDefinition` | `CreateNormalized` | `owner-construction` | `src/SomeEngine.ECS/Queries/QueryDefinition.cs:125` |
| `SomeEngine.ECS.Queries.QueryDefinition` | `CompileJobStorageAccesses` | `owner-construction` | `src/SomeEngine.ECS/Queries/QueryDefinition.cs:219` |
| `SomeEngine.ECS.Queries.QueryState.QueryMatchBuilder` | `TryCreate` | `owner-construction` | `src/SomeEngine.ECS/Queries/QueryState.cs:219` |
| `SomeEngine.ECS.Queries.QueryState.QueryMatchBuilder` | `TryCreate` | `owner-construction` | `src/SomeEngine.ECS/Queries/QueryState.cs:220` |
| `SomeEngine.ECS.Queries.QueryState.QueryMatchBuilder` | `TryCreate` | `owner-construction` | `src/SomeEngine.ECS/Queries/QueryState.cs:221` |
| `SomeEngine.ECS.Queries.QueryState.QueryMatchBuilder` | `TryCreate` | `owner-construction` | `src/SomeEngine.ECS/Queries/QueryState.cs:222` |
| `SomeEngine.ECS.Queries.QueryState.QueryMatchBuilder` | `TryCreate` | `owner-construction` | `src/SomeEngine.ECS/Queries/QueryState.cs:223` |
| `SomeEngine.ECS.Relations.RelationEdgeQuery<T>` | `ToArray` | `explicit-owner-copy` | `src/SomeEngine.ECS/Relations/RelationAdjacency.cs:104` |
| `SomeEngine.ECS.Relations.RelationGeneration<T>` | `SetOrderPolicy` | `cow-publication` | `src/SomeEngine.ECS/Relations/RelationGeneration.Mutation.cs:220` |
| `SomeEngine.ECS.Relations.RelationGeneration<T>` | `Reorder` | `cow-publication` | `src/SomeEngine.ECS/Relations/RelationGeneration.Mutation.cs:337` |
| `SomeEngine.ECS.Relations.RelationTypeState<T>` | `StableAffected` | `stable-mutation-plan` | `src/SomeEngine.ECS/Relations/RelationTypeState.Queries.cs:139` |
| `SomeEngine.ECS.Relations.MutableRelationAdjacencyShard<T>` | `Freeze` | `cow-publication` | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs:286` |
| `SomeEngine.ECS.Relations.RelationTypeState<T>` | `StableLiveEdges` | `stable-mutation-plan` | `src/SomeEngine.ECS/Relations/RelationTypeState.Tracking.cs:289` |

## 程序集统计

| 程序集 | 节点 | 跨程序集出边 | 跨程序集入边 |
| --- | ---: | ---: | ---: |
| `SomeEngine.ECS` | 318 | 0 | 485 |
| `SomeEngine.ECS.Serialization` | 78 | 117 | 0 |
| `SomeEngine.ECS.SourceGen` | 14 | 0 | 0 |
| `SomeEngine.ECS.Systems` | 114 | 368 | 0 |

## Rank 统计

| Rank | 节点 |
| ---: | ---: |
| 0 | 108 |
| 1 | 81 |
| 2 | 38 |
| 3 | 22 |
| 4 | 12 |
| 5 | 10 |
| 6 | 3 |
| 7 | 1 |
| 8 | 144 |
| 9 | 24 |
| 10 | 14 |
| 11 | 30 |
| 12 | 11 |
| 13 | 7 |
| 14 | 8 |
| 15 | 9 |
| 16 | 2 |

## 全部节点

### Rank 0

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.Entities.Entity` | `SomeEngine.ECS` | 159 | 0 | `src/SomeEngine.ECS/Entities/Entity.cs` |
| `SomeEngine.ECS.IComponent` | `SomeEngine.ECS` | 98 | 0 | `src/SomeEngine.ECS/RootComponentContracts.cs` |
| `SomeEngine.ECS.Hierarchy.IHierarchyDomain` | `SomeEngine.ECS` | 58 | 0 | `src/SomeEngine.ECS/Hierarchy/HierarchyComponents.cs` |
| `SomeEngine.ECS.Components.IBufferElement` | `SomeEngine.ECS` | 34 | 0 | `src/SomeEngine.ECS/Components/IBufferElementData.cs` |
| `SomeEngine.ECS.Queries.QueryHandle` | `SomeEngine.ECS` | 23 | 0 | `src/SomeEngine.ECS/Queries/QueryHandle.cs` |
| `SomeEngine.ECS.Registry.StoragePath` | `SomeEngine.ECS` | 20 | 0 | `src/SomeEngine.ECS/Registry/StoragePath.cs` |
| `SomeEngine.ECS.Serialization.SerializationTypeKey` | `SomeEngine.ECS.Serialization` | 19 | 0 | `src/SomeEngine.ECS.Serialization/SerializationTypeKey.cs` |
| `SomeEngine.ECS.Components.ISparseComponent` | `SomeEngine.ECS` | 15 | 0 | `src/SomeEngine.ECS/Components/IComponent.cs` |
| `SomeEngine.ECS.WorldStorageKind` | `SomeEngine.ECS` | 15 | 0 | `src/SomeEngine.ECS/World.JobAdmission.cs` |
| `SomeEngine.ECS.Queries.QueryAccess` | `SomeEngine.ECS` | 13 | 0 | `src/SomeEngine.ECS/Queries/QueryAccess.cs` |
| `SomeEngine.ECS.Queries.QueryTermFilter` | `SomeEngine.ECS` | 13 | 0 | `src/SomeEngine.ECS/Queries/QueryTerm.cs` |
| `SomeEngine.ECS.Relations.RelationAdjacencyOrderPolicy` | `SomeEngine.ECS` | 13 | 0 | `src/SomeEngine.ECS/Relations/RelationAdjacency.cs` |
| `SomeEngine.ECS.Relations.RelationAdjacencyRole` | `SomeEngine.ECS` | 13 | 0 | `src/SomeEngine.ECS/Relations/RelationAdjacency.cs` |
| `SomeEngine.ECS.Relations.RelationDirection` | `SomeEngine.ECS` | 12 | 0 | `src/SomeEngine.ECS/Relations/RelationSchema.cs` |
| `SomeEngine.ECS.Components.ISharedComponent` | `SomeEngine.ECS` | 11 | 0 | `src/SomeEngine.ECS/Components/ISharedComponent.cs` |
| `SomeEngine.ECS.Registry.ComponentOperations` | `SomeEngine.ECS` | 11 | 0 | `src/SomeEngine.ECS/Registry/ComponentOperations.cs` |
| `SomeEngine.ECS.Serialization.ComponentCodecKind` | `SomeEngine.ECS.Serialization` | 11 | 0 | `src/SomeEngine.ECS.Serialization/SerializationTypeEntry.cs` |
| `SomeEngine.ECS.Serialization.SerializationContract` | `SomeEngine.ECS.Serialization` | 11 | 0 | `src/SomeEngine.ECS.Serialization/Options/SerializationOptions.cs` |
| `SomeEngine.ECS.Systems.StableQueryPacketRange` | `SomeEngine.ECS.Systems` | 11 | 0 | `src/SomeEngine.ECS.Systems/StableQueryPackets.cs` |
| `SomeEngine.ECS.Collections.ArrayGrowthExtensions` | `SomeEngine.ECS` | 10 | 0 | `src/SomeEngine.ECS/Collections/ArrayGrowthExtensions.cs` |
| `SomeEngine.ECS.Owners.Clock` | `SomeEngine.ECS` | 10 | 0 | `src/SomeEngine.ECS/Owners.Clock.cs` |
| `SomeEngine.ECS.Owners.Iteration` | `SomeEngine.ECS` | 10 | 0 | `src/SomeEngine.ECS/Owners.Iteration.cs` |
| `SomeEngine.ECS.Serialization.SerializationSchemaSource` | `SomeEngine.ECS.Serialization` | 10 | 0 | `src/SomeEngine.ECS.Serialization/SerializationTypeEntry.cs` |
| `SomeEngine.ECS.Serialization.SerializationValueKind` | `SomeEngine.ECS.Serialization` | 10 | 0 | `src/SomeEngine.ECS.Serialization/SerializationTypeEntry.cs` |
| `SomeEngine.ECS.Hooks.HookCommandToken` | `SomeEngine.ECS` | 9 | 0 | `src/SomeEngine.ECS/Hooks/DeferredWorld.cs` |
| `SomeEngine.ECS.Components.ITag` | `SomeEngine.ECS` | 8 | 0 | `src/SomeEngine.ECS/Components/IComponent.cs` |
| `SomeEngine.ECS.WorldStorageAccess` | `SomeEngine.ECS` | 8 | 0 | `src/SomeEngine.ECS/World.JobAdmission.cs` |
| `SomeEngine.ECS.Queries.QueryTermKind` | `SomeEngine.ECS` | 7 | 0 | `src/SomeEngine.ECS/Queries/QueryTerm.cs` |
| `SomeEngine.ECS.Serialization.SerializationReadLimits` | `SomeEngine.ECS.Serialization` | 7 | 0 | `src/SomeEngine.ECS.Serialization/Options/SerializationReadLimits.cs` |
| `SomeEngine.ECS.Systems.IJobEntity` | `SomeEngine.ECS.Systems` | 7 | 0 | `src/SomeEngine.ECS.Systems/JobEntity.cs` |
| `SomeEngine.ECS.EntityCopyOptions` | `SomeEngine.ECS` | 6 | 0 | `src/SomeEngine.ECS/EntityCopyOptions.cs` |
| `SomeEngine.ECS.Hierarchy.ChildOrderPolicy` | `SomeEngine.ECS` | 6 | 0 | `src/SomeEngine.ECS/Hierarchy/HierarchyComponents.cs` |
| `SomeEngine.ECS.Relations.RelationCardinality` | `SomeEngine.ECS` | 6 | 0 | `src/SomeEngine.ECS/Relations/RelationSchema.cs` |
| `SomeEngine.ECS.Collections.StableHash` | `SomeEngine.ECS` | 5 | 0 | `src/SomeEngine.ECS/Collections/StableHash.cs` |
| `SomeEngine.ECS.Commands.ICommandPayloadList` | `SomeEngine.ECS` | 5 | 0 | `src/SomeEngine.ECS/Commands/CommandBuffer.Payloads.cs` |
| `SomeEngine.ECS.Owners.ExceptionAccumulator` | `SomeEngine.ECS` | 5 | 0 | `src/SomeEngine.ECS/Owners.ExceptionAccumulator.cs` |
| `SomeEngine.ECS.Relations.DirectedRelationPlacement` | `SomeEngine.ECS` | 5 | 0 | `src/SomeEngine.ECS/Relations/RelationAdjacency.cs` |
| `SomeEngine.ECS.Relations.RelationMaintenanceTiming` | `SomeEngine.ECS` | 5 | 0 | `src/SomeEngine.ECS/Relations/RelationSchema.cs` |
| `SomeEngine.ECS.Relations.UndirectedRelationPlacement` | `SomeEngine.ECS` | 5 | 0 | `src/SomeEngine.ECS/Relations/RelationAdjacency.cs` |
| `SomeEngine.ECS.Serialization.TopologySerializationKind` | `SomeEngine.ECS.Serialization` | 5 | 0 | `src/SomeEngine.ECS.Serialization/TopologySerializationRegistry.cs` |
| `SomeEngine.ECS.Systems.ReadOnlyQueryPacketContext` | `SomeEngine.ECS.Systems` | 5 | 0 | `src/SomeEngine.ECS.Systems/ReadOnlyQueryPacketJobs.cs` |
| `SomeEngine.ECS.Relations.RelationPendingPlacement` | `SomeEngine.ECS` | 4 | 0 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.RestrictedWorldApiContext` | `SomeEngine.ECS` | 4 | 0 | `src/SomeEngine.ECS/World.JobAdmission.cs` |
| `SomeEngine.ECS.Serialization.TopologyCaptureBudget` | `SomeEngine.ECS` | 4 | 0 | `src/SomeEngine.ECS/Serialization/TopologyCaptureBudget.cs` |
| `SomeEngine.ECS.TopologyOrderDiagnostics` | `SomeEngine.ECS` | 4 | 0 | `src/SomeEngine.ECS/TopologyOrderDiagnostics.cs` |
| `SomeEngine.ECS.VersionClock` | `SomeEngine.ECS` | 4 | 0 | `src/SomeEngine.ECS/VersionClock.cs` |
| `SomeEngine.ECS.WorldTopologyAccess` | `SomeEngine.ECS` | 4 | 0 | `src/SomeEngine.ECS/World.JobAdmission.cs` |
| `SomeEngine.ECS.Serialization.MissingReferenceMode` | `SomeEngine.ECS.Serialization` | 4 | 0 | `src/SomeEngine.ECS.Serialization/Options/SerializationOptions.cs` |
| `SomeEngine.ECS.Serialization.SnapshotPayloadKind` | `SomeEngine.ECS.Serialization` | 4 | 0 | `src/SomeEngine.ECS.Serialization/Options/SerializationOptions.cs` |
| `SomeEngine.ECS.Systems.GeneratedQueryStorage` | `SomeEngine.ECS.Systems` | 4 | 0 | `src/SomeEngine.ECS.Systems/JobEntity.cs` |
| `SomeEngine.ECS.Commands.CommandBuffer.RecordAccessScope` | `SomeEngine.ECS` | 3 | 0 | `src/SomeEngine.ECS/Commands/CommandBuffer.cs` |
| `SomeEngine.ECS.Commands.HierarchyMaintenanceTiming` | `SomeEngine.ECS` | 3 | 0 | `src/SomeEngine.ECS/Commands/CommandBuffer.Hierarchy.cs` |
| `SomeEngine.ECS.Commands.RelationCreatePlacement` | `SomeEngine.ECS` | 3 | 0 | `src/SomeEngine.ECS/Commands/CommandBuffer.Relations.cs` |
| `SomeEngine.ECS.Components.IBufferStorageComponent` | `SomeEngine.ECS` | 3 | 0 | `src/SomeEngine.ECS/Components/DynamicBufferComponents.cs` |
| `SomeEngine.ECS.Entities.PersistentEntityRecord` | `SomeEngine.ECS` | 3 | 0 | `src/SomeEngine.ECS/Entities/EntityRecord.cs` |
| `SomeEngine.ECS.Indexing.IResettableIndex` | `SomeEngine.ECS` | 3 | 0 | `src/SomeEngine.ECS/Indexing/ComponentIndex.cs` |
| `SomeEngine.ECS.Relations.RelationAdjacencyBatchDiagnostics` | `SomeEngine.ECS` | 3 | 0 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.Relations.RelationEntityMap<TValue>.Page` | `SomeEngine.ECS` | 3 | 0 | `src/SomeEngine.ECS/Relations/RelationEntityMap.cs` |
| `SomeEngine.ECS.WorldStructureCloneMetrics` | `SomeEngine.ECS` | 3 | 0 | `src/SomeEngine.ECS/WorldStructureRoot.cs` |
| `SomeEngine.ECS.Serialization.EntityIdentityMode` | `SomeEngine.ECS.Serialization` | 3 | 0 | `src/SomeEngine.ECS.Serialization/Options/SerializationOptions.cs` |
| `SomeEngine.ECS.Serialization.ExternalReferenceKey` | `SomeEngine.ECS.Serialization` | 3 | 0 | `src/SomeEngine.ECS.Serialization/ExternalReferences/ExternalReferenceContracts.cs` |
| `SomeEngine.ECS.SourceGen.JobEntityGenerator.ParameterKind` | `SomeEngine.ECS.SourceGen` | 3 | 0 | `src/SomeEngine.ECS.SourceGen/JobEntityGenerator.cs` |
| `SomeEngine.ECS.Systems.GeneratedQueryMode` | `SomeEngine.ECS.Systems` | 3 | 0 | `src/SomeEngine.ECS.Systems/JobEntity.cs` |
| `SomeEngine.ECS.Systems.HierarchyMaintenanceEvidence` | `SomeEngine.ECS.Systems` | 3 | 0 | `src/SomeEngine.ECS.Systems/HierarchyMaintenanceSystem.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagationComponentCapability` | `SomeEngine.ECS.Systems` | 3 | 0 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagationPacketRange` | `SomeEngine.ECS.Systems` | 3 | 0 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs` |
| `SomeEngine.ECS.Systems.SystemSlot` | `SomeEngine.ECS.Systems` | 3 | 0 | `src/SomeEngine.ECS.Systems/SystemSlot.cs` |
| `SomeEngine.ECS.BundleComponents` | `SomeEngine.ECS` | 2 | 0 | `src/SomeEngine.ECS/BundleComponents.cs` |
| `SomeEngine.ECS.BundleSharedAssignment` | `SomeEngine.ECS` | 2 | 0 | `src/SomeEngine.ECS/BundleWriteView.cs` |
| `SomeEngine.ECS.BundleWriteMode` | `SomeEngine.ECS` | 2 | 0 | `src/SomeEngine.ECS/BundleWriteView.cs` |
| `SomeEngine.ECS.Commands.CommandType` | `SomeEngine.ECS` | 2 | 0 | `src/SomeEngine.ECS/Commands/CommandBuffer.cs` |
| `SomeEngine.ECS.Commands.RelationBulkDestroy` | `SomeEngine.ECS` | 2 | 0 | `src/SomeEngine.ECS/Commands/CommandBuffer.Relations.cs` |
| `SomeEngine.ECS.Components.DynamicBufferConstants` | `SomeEngine.ECS` | 2 | 0 | `src/SomeEngine.ECS/Components/DynamicBufferComponents.cs` |
| `SomeEngine.ECS.ISharedComponentStore` | `SomeEngine.ECS` | 2 | 0 | `src/SomeEngine.ECS/SharedComponentStore.cs` |
| `SomeEngine.ECS.Owners.Copy.ComponentChanges.OldComponent` | `SomeEngine.ECS` | 2 | 0 | `src/SomeEngine.ECS/Owners.Copy.cs` |
| `SomeEngine.ECS.Queries.QueryState.TermMatchState` | `SomeEngine.ECS` | 2 | 0 | `src/SomeEngine.ECS/Queries/QueryState.cs` |
| `SomeEngine.ECS.RelationTopologyWriteDiagnostics` | `SomeEngine.ECS` | 2 | 0 | `src/SomeEngine.ECS/Serialization/WorldTopologySerializationAccess.cs` |
| `SomeEngine.ECS.Relations.RelationCanonicalLookupDiagnostics` | `SomeEngine.ECS` | 2 | 0 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.Relations.RelationCommandBatchValidationDiagnostics` | `SomeEngine.ECS` | 2 | 0 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.Relations.RelationComponentSlotTable<TValue>.Enumerator` | `SomeEngine.ECS` | 2 | 0 | `src/SomeEngine.ECS/Relations/RelationTypeSlotTable.cs` |
| `SomeEngine.ECS.Serialization.EntitySlotSnapshot` | `SomeEngine.ECS` | 2 | 0 | `src/SomeEngine.ECS/Serialization/EntitySlotSnapshot.cs` |
| `SomeEngine.ECS.WorldStructuralMetrics` | `SomeEngine.ECS` | 2 | 0 | `src/SomeEngine.ECS/WorldStructuralMetrics.cs` |
| `SomeEngine.ECS.Serialization.DurableSaveStore.CandidateReadStatus` | `SomeEngine.ECS.Serialization` | 2 | 0 | `src/SomeEngine.ECS.Serialization/DurableSaveStore.cs` |
| `SomeEngine.ECS.Serialization.DurableSaveStore.EnvelopeAuthenticationKind` | `SomeEngine.ECS.Serialization` | 2 | 0 | `src/SomeEngine.ECS.Serialization/DurableSaveStore.Envelope.cs` |
| `SomeEngine.ECS.Serialization.DurableSaveStore.SlotInspection` | `SomeEngine.ECS.Serialization` | 2 | 0 | `src/SomeEngine.ECS.Serialization/DurableSaveStore.cs` |
| `SomeEngine.ECS.Serialization.DurableSaveWriteStage` | `SomeEngine.ECS.Serialization` | 2 | 0 | `src/SomeEngine.ECS.Serialization/DurableSaveStore.cs` |
| `SomeEngine.ECS.SourceGen.BundleGenerator.BundleMemberKind` | `SomeEngine.ECS.SourceGen` | 2 | 0 | `src/SomeEngine.ECS.SourceGen/BundleGenerator.cs` |
| `SomeEngine.ECS.SourceGen.SerializationGenerator.FieldKind` | `SomeEngine.ECS.SourceGen` | 2 | 0 | `src/SomeEngine.ECS.SourceGen/SerializationGenerator.cs` |
| `SomeEngine.ECS.SourceGen.SerializationGenerator.SerializableKind` | `SomeEngine.ECS.SourceGen` | 2 | 0 | `src/SomeEngine.ECS.SourceGen/SerializationGenerator.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagationScheduleOptions` | `SomeEngine.ECS.Systems` | 2 | 0 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs` |
| `SomeEngine.ECS.Systems.ISystem<TContext>` | `SomeEngine.ECS.Systems` | 2 | 0 | `src/SomeEngine.ECS.Systems/ISystem.cs` |
| `SomeEngine.ECS.Systems.SystemNode<TContext>` | `SomeEngine.ECS.Systems` | 2 | 0 | `src/SomeEngine.ECS.Systems/SystemNode.cs` |
| `SomeEngine.ECS.Systems.TopologyPacketScheduleOptions` | `SomeEngine.ECS.Systems` | 2 | 0 | `src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs` |
| `SomeEngine.ECS.Collections.SmallInlineStorage<T>` | `SomeEngine.ECS` | 1 | 0 | `src/SomeEngine.ECS/Collections/SmallList.cs` |
| `SomeEngine.ECS.Components.BufferCapacityAttribute` | `SomeEngine.ECS` | 1 | 0 | `src/SomeEngine.ECS/Components/InternalBufferCapacityAttribute.cs` |
| `SomeEngine.ECS.Queries.QueryableCapabilities` | `SomeEngine.ECS` | 1 | 0 | `src/SomeEngine.ECS/Queries/QueryableCapabilities.cs` |
| `SomeEngine.ECS.Registry.ComponentTypeCounter` | `SomeEngine.ECS` | 1 | 0 | `src/SomeEngine.ECS/Registry/ComponentTypeCounter.cs` |
| `SomeEngine.ECS.Registry.JobStorageTypeShape` | `SomeEngine.ECS` | 1 | 0 | `src/SomeEngine.ECS/Registry/JobStorageTypeMetadata.cs` |
| `SomeEngine.ECS.Relations.RelationSerializationValidationDiagnostics` | `SomeEngine.ECS` | 1 | 0 | `src/SomeEngine.ECS/Relations/RelationTypeState.cs` |
| `SomeEngine.ECS.SharedComponentStore<T>.Generation` | `SomeEngine.ECS` | 1 | 0 | `src/SomeEngine.ECS/SharedComponentStore.cs` |
| `SomeEngine.ECS.Serialization.DurableSaveCommit` | `SomeEngine.ECS.Serialization` | 1 | 0 | `src/SomeEngine.ECS.Serialization/DurableSaveStore.cs` |
| `SomeEngine.ECS.Serialization.RawCanonicalLayout.FieldLayout` | `SomeEngine.ECS.Serialization` | 1 | 0 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Serialization.SerializableComponentAttribute` | `SomeEngine.ECS.Serialization` | 1 | 0 | `src/SomeEngine.ECS.Serialization/SerializableComponentAttribute.cs` |
| `SomeEngine.ECS.Serialization.TopologyCodec.TopologyWriteBudget` | `SomeEngine.ECS.Serialization` | 1 | 0 | `src/SomeEngine.ECS.Serialization/TopologyCodec.cs` |
| `SomeEngine.ECS.Serialization.WorldCheckpointCodec.Header` | `SomeEngine.ECS.Serialization` | 1 | 0 | `src/SomeEngine.ECS.Serialization/WorldCheckpointCodec.cs` |
| `SomeEngine.ECS.Serialization.WorldCheckpointInfo` | `SomeEngine.ECS.Serialization` | 1 | 0 | `src/SomeEngine.ECS.Serialization/WorldCheckpointCodec.cs` |
| `SomeEngine.ECS.Components.IComponentBundle` | `SomeEngine.ECS` | 0 | 0 | `src/SomeEngine.ECS/Components/IComponentBundle.cs` |
| `SomeEngine.ECS.Serialization.SerializedFieldAttribute` | `SomeEngine.ECS.Serialization` | 0 | 0 | `src/SomeEngine.ECS.Serialization/SerializableComponentAttribute.cs` |

### Rank 1

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.Relations.RelationEdge<T>` | `SomeEngine.ECS` | 21 | 2 | `src/SomeEngine.ECS/Relations/RelationEdge.cs` |
| `SomeEngine.ECS.Serialization.IReferenceRemapper` | `SomeEngine.ECS.Serialization` | 19 | 1 | `src/SomeEngine.ECS.Serialization/IReferencePatcher.cs` |
| `SomeEngine.ECS.Systems.WorldStorageResourceKey` | `SomeEngine.ECS.Systems` | 16 | 1 | `src/SomeEngine.ECS.Systems/BufferJobAccess.cs` |
| `SomeEngine.ECS.Components.IComponent` | `SomeEngine.ECS` | 14 | 1 | `src/SomeEngine.ECS/Components/IComponent.cs` |
| `SomeEngine.ECS.Relations.RelationEndpointPair` | `SomeEngine.ECS` | 14 | 1 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.Serialization.SerializationReadBudget` | `SomeEngine.ECS.Serialization` | 13 | 1 | `src/SomeEngine.ECS.Serialization/Options/SerializationReadLimits.cs` |
| `SomeEngine.ECS.Serialization.SerializationTypeEntry` | `SomeEngine.ECS.Serialization` | 10 | 5 | `src/SomeEngine.ECS.Serialization/SerializationTypeEntry.cs` |
| `SomeEngine.ECS.Systems.StableQueryPartitionProof` | `SomeEngine.ECS.Systems` | 10 | 1 | `src/SomeEngine.ECS.Systems/StableQueryPackets.cs` |
| `SomeEngine.ECS.Archetypes.SharedComponentTuple` | `SomeEngine.ECS` | 8 | 1 | `src/SomeEngine.ECS/Archetypes/SharedComponentTuple.cs` |
| `SomeEngine.ECS.Components.DynamicBufferInline<T>` | `SomeEngine.ECS` | 7 | 4 | `src/SomeEngine.ECS/Components/DynamicBufferComponents.cs` |
| `SomeEngine.ECS.IEnableableComponent` | `SomeEngine.ECS` | 7 | 1 | `src/SomeEngine.ECS/RootComponentContracts.cs` |
| `SomeEngine.ECS.Queries.QueryAccessExtensions` | `SomeEngine.ECS` | 7 | 1 | `src/SomeEngine.ECS/Queries/QueryAccess.cs` |
| `SomeEngine.ECS.Queries.QueryTerm` | `SomeEngine.ECS` | 7 | 3 | `src/SomeEngine.ECS/Queries/QueryTerm.cs` |
| `SomeEngine.ECS.Queries.QueryColumnAccess` | `SomeEngine.ECS` | 6 | 1 | `src/SomeEngine.ECS/Queries/QueryState.cs` |
| `SomeEngine.ECS.Registry.JobStorageTypeMetadata<T>` | `SomeEngine.ECS` | 6 | 1 | `src/SomeEngine.ECS/Registry/JobStorageTypeMetadata.cs` |
| `SomeEngine.ECS.TopologyOrderDiagnosticCounter` | `SomeEngine.ECS` | 5 | 1 | `src/SomeEngine.ECS/TopologyOrderDiagnostics.cs` |
| `SomeEngine.ECS.Archetypes.SharedColumnMapping` | `SomeEngine.ECS` | 4 | 1 | `src/SomeEngine.ECS/Archetypes/ArchetypeEdge.cs` |
| `SomeEngine.ECS.Collections.SortedValueKey` | `SomeEngine.ECS` | 4 | 1 | `src/SomeEngine.ECS/Collections/SortedValueKey.cs` |
| `SomeEngine.ECS.Hierarchy.DefaultHierarchyDomain` | `SomeEngine.ECS` | 4 | 1 | `src/SomeEngine.ECS/Hierarchy/HierarchyComponents.cs` |
| `SomeEngine.ECS.WorldJobStorageAccess` | `SomeEngine.ECS` | 4 | 2 | `src/SomeEngine.ECS/World.JobAdmission.cs` |
| `SomeEngine.ECS.Collections.SmallList<T>` | `SomeEngine.ECS` | 3 | 3 | `src/SomeEngine.ECS/Collections/SmallList.cs` |
| `SomeEngine.ECS.ICleanupComponent` | `SomeEngine.ECS` | 3 | 1 | `src/SomeEngine.ECS/RootComponentContracts.cs` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.ChildShard` | `SomeEngine.ECS` | 3 | 2 | `src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs` |
| `SomeEngine.ECS.Relations.RelationAffectedShard` | `SomeEngine.ECS` | 3 | 2 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.Serialization.SerializeOptions` | `SomeEngine.ECS.Serialization` | 3 | 1 | `src/SomeEngine.ECS.Serialization/Options/SerializationOptions.cs` |
| `SomeEngine.ECS.Serialization.WorldLoadOptions` | `SomeEngine.ECS.Serialization` | 3 | 4 | `src/SomeEngine.ECS.Serialization/Options/SerializationOptions.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>.TraversalNode` | `SomeEngine.ECS.Systems` | 3 | 2 | `src/SomeEngine.ECS.Systems/HierarchyPropagationCapture.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagationEntityAddress` | `SomeEngine.ECS.Systems` | 3 | 1 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagationPartitionProof` | `SomeEngine.ECS.Systems` | 3 | 2 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs` |
| `SomeEngine.ECS.Systems.ParentTopologyEdit` | `SomeEngine.ECS.Systems` | 3 | 1 | `src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs` |
| `SomeEngine.ECS.Systems.StableQueryPacketAddress` | `SomeEngine.ECS.Systems` | 3 | 1 | `src/SomeEngine.ECS.Systems/StableQueryPackets.cs` |
| `SomeEngine.ECS.Components.DynamicBufferLayout<T>` | `SomeEngine.ECS` | 2 | 3 | `src/SomeEngine.ECS/Components/DynamicBufferComponents.cs` |
| `SomeEngine.ECS.Indexing.IIndex<T>` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/Indexing/ComponentIndex.cs` |
| `SomeEngine.ECS.Indexing.IIndexStore` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/Indexing/ComponentIndex.cs` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.CanonicalParent` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.HierarchyDomainGeneration` | `SomeEngine.ECS` | 2 | 1 | `src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs` |
| `SomeEngine.ECS.Owners.PublishedChildren` | `SomeEngine.ECS` | 2 | 1 | `src/SomeEngine.ECS/Owners.Hierarchy.cs` |
| `SomeEngine.ECS.Queries.ChangeTerm` | `SomeEngine.ECS` | 2 | 1 | `src/SomeEngine.ECS/Queries/QueryState.cs` |
| `SomeEngine.ECS.Queries.QueryAccessEntry` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/Queries/QueryTerm.cs` |
| `SomeEngine.ECS.Relations.AppliedRelationEndpoints<T>` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/Relations/RelationEndpoints.cs` |
| `SomeEngine.ECS.Relations.RelationAdjacencyBatchDiagnosticCounter` | `SomeEngine.ECS` | 2 | 1 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.Relations.RelationCanonicalEndpointKey` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.Relations.RelationComponentSlotTable<TValue>` | `SomeEngine.ECS` | 2 | 1 | `src/SomeEngine.ECS/Relations/RelationTypeSlotTable.cs` |
| `SomeEngine.ECS.Relations.RelationEntityMap<TValue>.Storage` | `SomeEngine.ECS` | 2 | 1 | `src/SomeEngine.ECS/Relations/RelationEntityMap.cs` |
| `SomeEngine.ECS.Serialization.RelationTopologyImport<T>.MembershipPlan` | `SomeEngine.ECS` | 2 | 1 | `src/SomeEngine.ECS/Serialization/WorldTopologySerializationAccess.cs` |
| `SomeEngine.ECS.SharedComponentStore<T>` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/SharedComponentStore.cs` |
| `SomeEngine.ECS.Sparse.ISparseSet` | `SomeEngine.ECS` | 2 | 1 | `src/SomeEngine.ECS/Sparse/SparseSet.cs` |
| `SomeEngine.ECS.WorldStructuralMetricsState` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/WorldStructuralMetrics.cs` |
| `SomeEngine.ECS.SourceGen.BundleGenerator.BundleMember` | `SomeEngine.ECS.SourceGen` | 2 | 1 | `src/SomeEngine.ECS.SourceGen/BundleGenerator.cs` |
| `SomeEngine.ECS.SourceGen.JobEntityGenerator.ParameterModel` | `SomeEngine.ECS.SourceGen` | 2 | 1 | `src/SomeEngine.ECS.SourceGen/JobEntityGenerator.cs` |
| `SomeEngine.ECS.SourceGen.SerializationGenerator.FieldModel` | `SomeEngine.ECS.SourceGen` | 2 | 2 | `src/SomeEngine.ECS.SourceGen/SerializationGenerator.cs` |
| `SomeEngine.ECS.SourceGen.SerializationGenerator.SerializableModel` | `SomeEngine.ECS.SourceGen` | 2 | 2 | `src/SomeEngine.ECS.SourceGen/SerializationGenerator.cs` |
| `SomeEngine.ECS.Systems.ISystemDriver<TContext>` | `SomeEngine.ECS.Systems` | 2 | 1 | `src/SomeEngine.ECS.Systems/ISystemDriver.cs` |
| `SomeEngine.ECS.Systems.TopologyPacketContext` | `SomeEngine.ECS.Systems` | 2 | 1 | `src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs` |
| `SomeEngine.ECS.Systems.WorldJobAdmission.StorageFrame` | `SomeEngine.ECS.Systems` | 2 | 1 | `src/SomeEngine.ECS.Systems/WorldJobAdmission.cs` |
| `SomeEngine.ECS.Archetypes.Chunk.ChunkStorage` | `SomeEngine.ECS` | 1 | 1 | `src/SomeEngine.ECS/Archetypes/Chunk.cs` |
| `SomeEngine.ECS.Collections.SmallList<T>.Enumerator` | `SomeEngine.ECS` | 1 | 1 | `src/SomeEngine.ECS/Collections/SmallList.cs` |
| `SomeEngine.ECS.Entities.EntityStore.EntityRecordPage` | `SomeEngine.ECS` | 1 | 1 | `src/SomeEngine.ECS/Entities/EntityStore.RecordPage.cs` |
| `SomeEngine.ECS.Hierarchy.HierarchyChildrenView<TDomain>.Enumerator` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/Hierarchy/HierarchyComponents.cs` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.EntityComparer` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.ParentPreimage` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.PendingChildPlacement` | `SomeEngine.ECS` | 1 | 1 | `src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs` |
| `SomeEngine.ECS.Queries.QueryChunkPair<TWrite, TRead>` | `SomeEngine.ECS` | 1 | 1 | `src/SomeEngine.ECS/Queries/QueryPairEnumerator.cs` |
| `SomeEngine.ECS.Queries.QueryDefinition.TermState` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/Queries/QueryDefinition.cs` |
| `SomeEngine.ECS.RelationTopologyWriteCounter` | `SomeEngine.ECS` | 1 | 1 | `src/SomeEngine.ECS/Serialization/WorldTopologySerializationAccess.cs` |
| `SomeEngine.ECS.Relations.RelationDirtyEdgeBucket` | `SomeEngine.ECS` | 1 | 1 | `src/SomeEngine.ECS/Relations/RelationEntityMap.cs` |
| `SomeEngine.ECS.Relations.RelationSchemaAttribute` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/Relations/RelationSchema.cs` |
| `SomeEngine.ECS.Sparse.SparseSet<T>.Storage` | `SomeEngine.ECS` | 1 | 1 | `src/SomeEngine.ECS/Sparse/SparseSet.cs` |
| `SomeEngine.ECS.SparseReadExecution<T, TState>` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/SparseExecution.cs` |
| `SomeEngine.ECS.SparseReadExecution<T>` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/SparseExecution.cs` |
| `SomeEngine.ECS.SparseWriteExecution<T, TState>` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/SparseExecution.cs` |
| `SomeEngine.ECS.SparseWriteExecution<T>` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/SparseExecution.cs` |
| `SomeEngine.ECS.Serialization.DurableSaveStore.EnvelopeHeader` | `SomeEngine.ECS.Serialization` | 1 | 1 | `src/SomeEngine.ECS.Serialization/DurableSaveStore.Envelope.cs` |
| `SomeEngine.ECS.Serialization.DurableSaveStoreOptions` | `SomeEngine.ECS.Serialization` | 1 | 1 | `src/SomeEngine.ECS.Serialization/DurableSaveStore.cs` |
| `SomeEngine.ECS.Serialization.RawCanonicalLayout` | `SomeEngine.ECS.Serialization` | 1 | 1 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Serialization.SerializationReadOptions` | `SomeEngine.ECS.Serialization` | 1 | 2 | `src/SomeEngine.ECS.Serialization/Options/SerializationOptions.cs` |
| `SomeEngine.ECS.SourceGen.JobEntityGenerator.ParameterAliasComparer` | `SomeEngine.ECS.SourceGen` | 1 | 1 | `src/SomeEngine.ECS.SourceGen/JobEntityGenerator.cs` |
| `SomeEngine.ECS.Systems.GeneratedQueryAccessDescriptor.LogicalQueryAccess` | `SomeEngine.ECS.Systems` | 1 | 2 | `src/SomeEngine.ECS.Systems/JobEntity.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>.EntityIdentityComparer` | `SomeEngine.ECS.Systems` | 1 | 2 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs` |
| `SomeEngine.ECS.Systems.SystemNode<TSystem, TContext>` | `SomeEngine.ECS.Systems` | 1 | 2 | `src/SomeEngine.ECS.Systems/SystemNode.cs` |
| `SomeEngine.ECS.Serialization.IExternalResolver` | `SomeEngine.ECS.Serialization` | 0 | 1 | `src/SomeEngine.ECS.Serialization/ExternalReferences/ExternalReferenceContracts.cs` |

### Rank 2

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.Relations.RelationAdjacencyEntry<T>` | `SomeEngine.ECS` | 14 | 3 | `src/SomeEngine.ECS/Relations/RelationAdjacency.cs` |
| `SomeEngine.ECS.Relations.RelationSchema` | `SomeEngine.ECS` | 12 | 4 | `src/SomeEngine.ECS/Relations/RelationSchema.cs` |
| `SomeEngine.ECS.Components.DynamicBufferHeader<T>` | `SomeEngine.ECS` | 9 | 4 | `src/SomeEngine.ECS/Components/DynamicBufferComponents.cs` |
| `SomeEngine.ECS.Serialization.SerializationBinary` | `SomeEngine.ECS.Serialization` | 9 | 1 | `src/SomeEngine.ECS.Serialization/DataReader.cs` |
| `SomeEngine.ECS.Hierarchy.HierarchyChildrenView<TDomain>` | `SomeEngine.ECS` | 7 | 3 | `src/SomeEngine.ECS/Hierarchy/HierarchyComponents.cs` |
| `SomeEngine.ECS.Components.IRelationshipTarget` | `SomeEngine.ECS` | 6 | 2 | `src/SomeEngine.ECS/Components/IComponent.cs` |
| `SomeEngine.ECS.Serialization.IValuePatcher<T>` | `SomeEngine.ECS.Serialization` | 6 | 1 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Relations.RelationEntityMap<TValue>.Enumerator` | `SomeEngine.ECS` | 5 | 3 | `src/SomeEngine.ECS/Relations/RelationEntityMap.cs` |
| `SomeEngine.ECS.Components.IRelationshipSource` | `SomeEngine.ECS` | 4 | 2 | `src/SomeEngine.ECS/Components/IComponent.cs` |
| `SomeEngine.ECS.Relations.RelationAppliedEndpointImage` | `SomeEngine.ECS` | 4 | 1 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.Sparse.SparseSet<T>` | `SomeEngine.ECS` | 4 | 4 | `src/SomeEngine.ECS/Sparse/SparseSet.cs` |
| `SomeEngine.ECS.WorldJobAdmissionRequest` | `SomeEngine.ECS` | 4 | 4 | `src/SomeEngine.ECS/World.JobAdmission.cs` |
| `SomeEngine.ECS.Systems.TopologyOperationState` | `SomeEngine.ECS.Systems` | 4 | 1 | `src/SomeEngine.ECS.Systems/TopologyOperationState.cs` |
| `SomeEngine.ECS.Collections.SortedValueComparer` | `SomeEngine.ECS` | 3 | 2 | `src/SomeEngine.ECS/Collections/SortedValueKey.cs` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.UnorderedChildShard` | `SomeEngine.ECS` | 3 | 3 | `src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagationState` | `SomeEngine.ECS.Systems` | 3 | 1 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs` |
| `SomeEngine.ECS.Components.IIndexedComponent` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/Components/IComponent.cs` |
| `SomeEngine.ECS.Queries.QueryKey` | `SomeEngine.ECS` | 2 | 1 | `src/SomeEngine.ECS/Queries/QueryKey.cs` |
| `SomeEngine.ECS.Relations.RelationEndpointTransition<T>` | `SomeEngine.ECS` | 2 | 3 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.Relations.RelationGeneration<T>.PendingCardinalityClaim` | `SomeEngine.ECS` | 2 | 3 | `src/SomeEngine.ECS/Relations/RelationGeneration.Cardinality.cs` |
| `SomeEngine.ECS.Serialization.IReferencePatcher<T>` | `SomeEngine.ECS.Serialization` | 2 | 1 | `src/SomeEngine.ECS.Serialization/IReferencePatcher.cs` |
| `SomeEngine.ECS.Serialization.TopologySerializationHelpers` | `SomeEngine.ECS.Serialization` | 2 | 3 | `src/SomeEngine.ECS.Serialization/TopologySerializationRegistry.cs` |
| `SomeEngine.ECS.Serialization.WorldSerializer.PolicyReferenceRemapper` | `SomeEngine.ECS.Serialization` | 2 | 3 | `src/SomeEngine.ECS.Serialization/WorldSerializer.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>.HierarchyTraversalCapture` | `SomeEngine.ECS.Systems` | 2 | 3 | `src/SomeEngine.ECS.Systems/HierarchyPropagationCapture.cs` |
| `SomeEngine.ECS.Archetypes.SharedComponentTupleComparer` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/Archetypes/SharedComponentTuple.cs` |
| `SomeEngine.ECS.Collections.SmallListExtensions` | `SomeEngine.ECS` | 1 | 1 | `src/SomeEngine.ECS/Collections/SmallListExtensions.cs` |
| `SomeEngine.ECS.Components.ICleanupComponent` | `SomeEngine.ECS` | 1 | 3 | `src/SomeEngine.ECS/Components/IComponent.cs` |
| `SomeEngine.ECS.Owners.RelationGraph.RelationEndpointTracker<T>.EndpointPreimage` | `SomeEngine.ECS` | 1 | 3 | `src/SomeEngine.ECS/Owners.RelationGraph.EndpointTracking.cs` |
| `SomeEngine.ECS.Relations.RelationSchemaCache<T>` | `SomeEngine.ECS` | 1 | 3 | `src/SomeEngine.ECS/Relations/RelationSchema.cs` |
| `SomeEngine.ECS.SharedStores` | `SomeEngine.ECS` | 1 | 3 | `src/SomeEngine.ECS/SharedComponentStore.cs` |
| `SomeEngine.ECS.SourceGen.BundleGenerator.BundleModel` | `SomeEngine.ECS.SourceGen` | 1 | 1 | `src/SomeEngine.ECS.SourceGen/BundleGenerator.cs` |
| `SomeEngine.ECS.SourceGen.JobEntityGenerator.JobModel` | `SomeEngine.ECS.SourceGen` | 1 | 1 | `src/SomeEngine.ECS.SourceGen/JobEntityGenerator.cs` |
| `SomeEngine.ECS.Systems.WorldJobAdmission.AdmissionFrame` | `SomeEngine.ECS.Systems` | 1 | 3 | `src/SomeEngine.ECS.Systems/WorldJobAdmission.cs` |
| `SomeEngine.ECS.Systems.WorldJobAdmission.RequestedStorage` | `SomeEngine.ECS.Systems` | 1 | 2 | `src/SomeEngine.ECS.Systems/WorldJobAdmission.cs` |
| `SomeEngine.ECS.Systems.WorldStorageJobResources.WorldResources` | `SomeEngine.ECS.Systems` | 1 | 1 | `src/SomeEngine.ECS.Systems/BufferJobAccess.cs` |
| `SomeEngine.ECS.Components.IEnableableComponent` | `SomeEngine.ECS` | 0 | 3 | `src/SomeEngine.ECS/Components/IComponent.cs` |
| `SomeEngine.ECS.Relations.RelationPairKey` | `SomeEngine.ECS` | 0 | 3 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.SourceGen.SerializationGenerator` | `SomeEngine.ECS.SourceGen` | 0 | 4 | `src/SomeEngine.ECS.SourceGen/SerializationGenerator.cs` |

### Rank 3

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.Archetypes.Chunk` | `SomeEngine.ECS` | 45 | 7 | `src/SomeEngine.ECS/Archetypes/Chunk.cs` |
| `SomeEngine.ECS.Serialization.DataReader` | `SomeEngine.ECS.Serialization` | 19 | 4 | `src/SomeEngine.ECS.Serialization/DataReader.cs` |
| `SomeEngine.ECS.Serialization.DataWriter` | `SomeEngine.ECS.Serialization` | 19 | 3 | `src/SomeEngine.ECS.Serialization/DataWriter.cs` |
| `SomeEngine.ECS.Relations.DirectedRelationEndpoints<T>` | `SomeEngine.ECS` | 8 | 4 | `src/SomeEngine.ECS/Relations/RelationEndpoints.cs` |
| `SomeEngine.ECS.Relations.UndirectedRelationEndpoints<T>` | `SomeEngine.ECS` | 8 | 4 | `src/SomeEngine.ECS/Relations/RelationEndpoints.cs` |
| `SomeEngine.ECS.Components.IIndexedComponent<TKey>` | `SomeEngine.ECS` | 7 | 3 | `src/SomeEngine.ECS/Components/IComponent.cs` |
| `SomeEngine.ECS.Relations.RelationAdjacencyShard<T>` | `SomeEngine.ECS` | 7 | 3 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.Relations.RelationAdjacencySnapshot<T>` | `SomeEngine.ECS` | 4 | 3 | `src/SomeEngine.ECS/Relations/RelationAdjacency.cs` |
| `SomeEngine.ECS.Relations.RelationEntityMap<TValue>` | `SomeEngine.ECS` | 4 | 4 | `src/SomeEngine.ECS/Relations/RelationEntityMap.cs` |
| `SomeEngine.ECS.Components.Removed<T>` | `SomeEngine.ECS` | 3 | 4 | `src/SomeEngine.ECS/Components/IComponent.cs` |
| `SomeEngine.ECS.Relations.Incident<T>` | `SomeEngine.ECS` | 1 | 3 | `src/SomeEngine.ECS/Relations/RelationEndpoints.cs` |
| `SomeEngine.ECS.Relations.Incoming<T>` | `SomeEngine.ECS` | 1 | 3 | `src/SomeEngine.ECS/Relations/RelationEndpoints.cs` |
| `SomeEngine.ECS.Relations.Outgoing<T>` | `SomeEngine.ECS` | 1 | 3 | `src/SomeEngine.ECS/Relations/RelationEndpoints.cs` |
| `SomeEngine.ECS.Relations.RelationEdgeQuery<T>.Enumerator` | `SomeEngine.ECS` | 1 | 4 | `src/SomeEngine.ECS/Relations/RelationAdjacency.cs` |
| `SomeEngine.ECS.Relations.RelationGeneration<T>.CardinalityWorkspace` | `SomeEngine.ECS` | 1 | 4 | `src/SomeEngine.ECS/Relations/RelationGeneration.Cardinality.cs` |
| `SomeEngine.ECS.Serialization.CustomReferencePatcher<T, TPatcher>` | `SomeEngine.ECS.Serialization` | 1 | 3 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Serialization.RawAbiTypeKey` | `SomeEngine.ECS.Serialization` | 1 | 5 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagation` | `SomeEngine.ECS.Systems` | 1 | 2 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs` |
| `SomeEngine.ECS.Systems.TopologyFinalization` | `SomeEngine.ECS.Systems` | 1 | 2 | `src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs` |
| `SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>.TopologyCompletionAdapter` | `SomeEngine.ECS.Systems` | 1 | 2 | `src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs` |
| `SomeEngine.ECS.SourceGen.BundleGenerator` | `SomeEngine.ECS.SourceGen` | 0 | 3 | `src/SomeEngine.ECS.SourceGen/BundleGenerator.cs` |
| `SomeEngine.ECS.SourceGen.JobEntityGenerator` | `SomeEngine.ECS.SourceGen` | 0 | 4 | `src/SomeEngine.ECS.SourceGen/JobEntityGenerator.cs` |

### Rank 4

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.Serialization.IValueCodec<T>` | `SomeEngine.ECS.Serialization` | 11 | 3 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.BufferView<T>` | `SomeEngine.ECS` | 9 | 4 | `src/SomeEngine.ECS/BufferView.cs` |
| `SomeEngine.ECS.Serialization.IComponentCodec<T>` | `SomeEngine.ECS.Serialization` | 4 | 2 | `src/SomeEngine.ECS.Serialization/IComponentCodec.cs` |
| `SomeEngine.ECS.Archetypes.SharedChunkBucket` | `SomeEngine.ECS` | 3 | 2 | `src/SomeEngine.ECS/Archetypes/SharedComponentTuple.cs` |
| `SomeEngine.ECS.Indexing.ComponentIndex<TComponent, TKey>.Bucket` | `SomeEngine.ECS` | 3 | 4 | `src/SomeEngine.ECS/Indexing/ComponentIndex.cs` |
| `SomeEngine.ECS.Relations.RelationEdgeQuery<T>` | `SomeEngine.ECS` | 3 | 5 | `src/SomeEngine.ECS/Relations/RelationAdjacency.cs` |
| `SomeEngine.ECS.Relations.OrderedRelationAdjacencyShard<T>` | `SomeEngine.ECS` | 2 | 4 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.Relations.UnorderedRelationAdjacencyShard<T>` | `SomeEngine.ECS` | 2 | 4 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.Systems.IDirectedRelationEndpointsReadChunkJob<T>` | `SomeEngine.ECS.Systems` | 2 | 3 | `src/SomeEngine.ECS.Systems/RelationshipChunkJobs.cs` |
| `SomeEngine.ECS.Systems.IDirectedRelationEndpointsWriteChunkJob<T>` | `SomeEngine.ECS.Systems` | 2 | 3 | `src/SomeEngine.ECS.Systems/RelationshipChunkJobs.cs` |
| `SomeEngine.ECS.Systems.IUndirectedRelationEndpointsReadChunkJob<T>` | `SomeEngine.ECS.Systems` | 2 | 3 | `src/SomeEngine.ECS.Systems/RelationshipChunkJobs.cs` |
| `SomeEngine.ECS.Systems.IUndirectedRelationEndpointsWriteChunkJob<T>` | `SomeEngine.ECS.Systems` | 2 | 3 | `src/SomeEngine.ECS.Systems/RelationshipChunkJobs.cs` |

### Rank 5

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.BufferReadExecution<T, TState>` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/BufferExecution.cs` |
| `SomeEngine.ECS.BufferReadExecution<T>` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/BufferExecution.cs` |
| `SomeEngine.ECS.Indexing.ComponentIndex<TComponent, TKey>.Generation` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/Indexing/ComponentIndex.cs` |
| `SomeEngine.ECS.Relations.MutableRelationAdjacencyShard<T>` | `SomeEngine.ECS` | 1 | 9 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.Serialization.CanonicalValueCodec<T, TCodec>` | `SomeEngine.ECS.Serialization` | 1 | 5 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Serialization.CustomValueCodec<T, TCodec>` | `SomeEngine.ECS.Serialization` | 1 | 5 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Serialization.ICanonicalComponentCodec<T>` | `SomeEngine.ECS.Serialization` | 1 | 1 | `src/SomeEngine.ECS.Serialization/IComponentCodec.cs` |
| `SomeEngine.ECS.Serialization.MissingValueCodec<T>` | `SomeEngine.ECS.Serialization` | 1 | 4 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Serialization.RawCanonicalValueCodec<T>` | `SomeEngine.ECS.Serialization` | 1 | 4 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Serialization.RawValueCodec<T>` | `SomeEngine.ECS.Serialization` | 1 | 4 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |

### Rank 6

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.Relations.RelationGeneration<T>` | `SomeEngine.ECS` | 6 | 20 | `src/SomeEngine.ECS/Relations/RelationGeneration.Cardinality.cs`<br>`src/SomeEngine.ECS/Relations/RelationGeneration.cs`<br>`src/SomeEngine.ECS/Relations/RelationGeneration.Mutation.cs` |
| `SomeEngine.ECS.Indexing.ComponentIndex<TComponent, TKey>` | `SomeEngine.ECS` | 2 | 8 | `src/SomeEngine.ECS/Indexing/ComponentIndex.cs` |
| `SomeEngine.ECS.Indexing.ComponentIndex<TComponent, TKey>.Builder` | `SomeEngine.ECS` | 2 | 4 | `src/SomeEngine.ECS/Indexing/ComponentIndex.cs` |

### Rank 7

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.Relations.PreparedRelationState<T>` | `SomeEngine.ECS` | 2 | 3 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |

### Rank 8

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.World` | `SomeEngine.ECS` | 132 | 106 | `src/SomeEngine.ECS/Hierarchy/World.Hierarchy.cs`<br>`src/SomeEngine.ECS/Serialization/WorldSerializationAccess.cs`<br>`src/SomeEngine.ECS/Serialization/WorldTopologySerializationAccess.cs`<br>`src/SomeEngine.ECS/World.Bundle.cs`<br>`src/SomeEngine.ECS/World.CommandBufferComponents.cs`<br>`src/SomeEngine.ECS/World.Components.cs`<br>`src/SomeEngine.ECS/World.cs`<br>`src/SomeEngine.ECS/World.DynamicBuffer.cs`<br>`src/SomeEngine.ECS/World.Entities.cs`<br>`src/SomeEngine.ECS/World.EntityCopy.cs`<br>`src/SomeEngine.ECS/World.EntityGuards.cs`<br>`src/SomeEngine.ECS/World.Hooks.cs`<br>`src/SomeEngine.ECS/World.Indexing.cs`<br>`src/SomeEngine.ECS/World.Iteration.cs`<br>`src/SomeEngine.ECS/World.JobAdmission.cs`<br>`src/SomeEngine.ECS/World.JobAdmission.Lifetime.cs`<br>`src/SomeEngine.ECS/World.Lifetime.cs`<br>`src/SomeEngine.ECS/World.Queries.cs`<br>`src/SomeEngine.ECS/World.ReadSnapshotAdmission.cs`<br>`src/SomeEngine.ECS/World.RelationEdges.cs`<br>`src/SomeEngine.ECS/World.SerializationReadRoot.cs`<br>`src/SomeEngine.ECS/World.SerializationWriteAdmission.cs`<br>`src/SomeEngine.ECS/World.SharedComponent.cs`<br>`src/SomeEngine.ECS/World.Sparse.cs`<br>`src/SomeEngine.ECS/World.StructuralTransaction.cs` |
| `SomeEngine.ECS.Archetypes.Archetype` | `SomeEngine.ECS` | 43 | 14 | `src/SomeEngine.ECS/Archetypes/Archetype.cs` |
| `SomeEngine.ECS.Registry.ComponentMetadata<T>` | `SomeEngine.ECS` | 38 | 18 | `src/SomeEngine.ECS/Registry/ComponentMetadata.cs` |
| `SomeEngine.ECS.Queries.QueryArchetypeMatch` | `SomeEngine.ECS` | 24 | 7 | `src/SomeEngine.ECS/Queries/QueryState.cs` |
| `SomeEngine.ECS.Owners.Hierarchy` | `SomeEngine.ECS` | 18 | 17 | `src/SomeEngine.ECS/Owners.Hierarchy.cs`<br>`src/SomeEngine.ECS/Owners.Hierarchy.MutationTracking.cs` |
| `SomeEngine.ECS.Registry.ComponentRegistry` | `SomeEngine.ECS` | 18 | 2 | `src/SomeEngine.ECS/Registry/ComponentRegistry.cs` |
| `SomeEngine.ECS.Entities.EntityStore` | `SomeEngine.ECS` | 17 | 9 | `src/SomeEngine.ECS/Entities/EntityStore.cs`<br>`src/SomeEngine.ECS/Entities/EntityStore.RecordPage.cs`<br>`src/SomeEngine.ECS/Entities/EntityStore.Serialization.cs` |
| `SomeEngine.ECS.Registry.ComponentInfo` | `SomeEngine.ECS` | 17 | 3 | `src/SomeEngine.ECS/Registry/ComponentInfo.cs` |
| `SomeEngine.ECS.WorldStructureRoot` | `SomeEngine.ECS` | 17 | 21 | `src/SomeEngine.ECS/WorldStructureRoot.cs` |
| `SomeEngine.ECS.Commands.CommandPlaybackContext` | `SomeEngine.ECS` | 16 | 6 | `src/SomeEngine.ECS/Commands/CommandBuffer.cs` |
| `SomeEngine.ECS.Owners.Components` | `SomeEngine.ECS` | 16 | 27 | `src/SomeEngine.ECS/Owners.Components.cs` |
| `SomeEngine.ECS.Owners.Entities` | `SomeEngine.ECS` | 16 | 18 | `src/SomeEngine.ECS/Owners.Entities.cs` |
| `SomeEngine.ECS.Commands.CommandEntity` | `SomeEngine.ECS` | 15 | 3 | `src/SomeEngine.ECS/Commands/CommandBuffer.cs` |
| `SomeEngine.ECS.Hierarchy.Parent<TDomain>` | `SomeEngine.ECS` | 14 | 8 | `src/SomeEngine.ECS/Hierarchy/HierarchyComponents.cs` |
| `SomeEngine.ECS.Commands.CommandBuffer` | `SomeEngine.ECS` | 13 | 31 | `src/SomeEngine.ECS/Commands/CommandBuffer.Buffers.cs`<br>`src/SomeEngine.ECS/Commands/CommandBuffer.cs`<br>`src/SomeEngine.ECS/Commands/CommandBuffer.Hierarchy.cs`<br>`src/SomeEngine.ECS/Commands/CommandBuffer.Relations.cs` |
| `SomeEngine.ECS.Components.BufferComponents` | `SomeEngine.ECS` | 13 | 5 | `src/SomeEngine.ECS/Components/DynamicBufferComponents.cs` |
| `SomeEngine.ECS.Entities.EntityRecord` | `SomeEngine.ECS` | 12 | 2 | `src/SomeEngine.ECS/Entities/EntityRecord.cs` |
| `SomeEngine.ECS.Owners.RelationGraph` | `SomeEngine.ECS` | 12 | 47 | `src/SomeEngine.ECS/Owners.RelationGraph.CommandBatch.cs`<br>`src/SomeEngine.ECS/Owners.RelationGraph.cs`<br>`src/SomeEngine.ECS/Owners.RelationGraph.EndpointTracking.cs`<br>`src/SomeEngine.ECS/Serialization/WorldTopologySerializationAccess.cs` |
| `SomeEngine.ECS.Queries.QueryCursor` | `SomeEngine.ECS` | 12 | 10 | `src/SomeEngine.ECS/Queries/QueryCursor.cs` |
| `SomeEngine.ECS.Commands.TypedRelationshipCommand` | `SomeEngine.ECS` | 11 | 3 | `src/SomeEngine.ECS/Commands/CommandBuffer.Hierarchy.cs` |
| `SomeEngine.ECS.Owners.Buffers` | `SomeEngine.ECS` | 10 | 19 | `src/SomeEngine.ECS/Owners.Buffers.cs` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>` | `SomeEngine.ECS` | 10 | 25 | `src/SomeEngine.ECS/Owners.Hierarchy.cs`<br>`src/SomeEngine.ECS/Owners.Hierarchy.SerializationValidation.cs`<br>`src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs` |
| `SomeEngine.ECS.Owners.Tables` | `SomeEngine.ECS` | 10 | 14 | `src/SomeEngine.ECS/Owners.Tables.cs` |
| `SomeEngine.ECS.Queries.QueryDefinition` | `SomeEngine.ECS` | 10 | 16 | `src/SomeEngine.ECS/Queries/QueryDefinition.cs` |
| `SomeEngine.ECS.Archetypes.ArchetypeRegistry` | `SomeEngine.ECS` | 9 | 8 | `src/SomeEngine.ECS/Archetypes/ArchetypeRegistry.cs` |
| `SomeEngine.ECS.DynamicBuffer<T>` | `SomeEngine.ECS` | 9 | 5 | `src/SomeEngine.ECS/DynamicBuffer.cs` |
| `SomeEngine.ECS.Entities.EntityRecordWriter` | `SomeEngine.ECS` | 9 | 5 | `src/SomeEngine.ECS/Entities/EntityRecord.cs` |
| `SomeEngine.ECS.Queries.QueryState` | `SomeEngine.ECS` | 9 | 10 | `src/SomeEngine.ECS/Queries/QueryState.cs` |
| `SomeEngine.ECS.Hierarchy.Hierarchy<TDomain>` | `SomeEngine.ECS` | 8 | 10 | `src/SomeEngine.ECS/Hierarchy/Hierarchy.cs` |
| `SomeEngine.ECS.Queries.QueryChunkView` | `SomeEngine.ECS` | 8 | 16 | `src/SomeEngine.ECS/Queries/QueryChunkView.cs` |
| `SomeEngine.ECS.Queries.QueryExecution<TState>` | `SomeEngine.ECS` | 8 | 1 | `src/SomeEngine.ECS/Queries/QueryCursor.cs` |
| `SomeEngine.ECS.Archetypes.StructuralTransition` | `SomeEngine.ECS` | 7 | 2 | `src/SomeEngine.ECS/Archetypes/ArchetypeEdge.cs` |
| `SomeEngine.ECS.Owners.Indices` | `SomeEngine.ECS` | 7 | 14 | `src/SomeEngine.ECS/Owners.Indices.cs` |
| `SomeEngine.ECS.Owners.Shared` | `SomeEngine.ECS` | 7 | 17 | `src/SomeEngine.ECS/Owners.Shared.cs` |
| `SomeEngine.ECS.Owners.Sparse` | `SomeEngine.ECS` | 7 | 9 | `src/SomeEngine.ECS/Owners.Sparse.cs` |
| `SomeEngine.ECS.Queries.QueryRecord` | `SomeEngine.ECS` | 7 | 3 | `src/SomeEngine.ECS/Queries/QueryRecord.cs` |
| `SomeEngine.ECS.Queries.QueryRegistry` | `SomeEngine.ECS` | 7 | 8 | `src/SomeEngine.ECS/Queries/QueryRegistry.cs` |
| `SomeEngine.ECS.WorldJobAdmissionScope` | `SomeEngine.ECS` | 7 | 3 | `src/SomeEngine.ECS/World.JobAdmission.cs` |
| `SomeEngine.ECS.Archetypes.DetachedTableMap` | `SomeEngine.ECS` | 6 | 3 | `src/SomeEngine.ECS/Archetypes/DetachedTableMap.cs` |
| `SomeEngine.ECS.Owners.Hooks` | `SomeEngine.ECS` | 6 | 12 | `src/SomeEngine.ECS/Owners.Hooks.cs` |
| `SomeEngine.ECS.Relations.IRelationTypeState` | `SomeEngine.ECS` | 6 | 4 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.Relations.RelationEndpointAccess` | `SomeEngine.ECS` | 6 | 11 | `src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs` |
| `SomeEngine.ECS.BundleWriteView` | `SomeEngine.ECS` | 5 | 6 | `src/SomeEngine.ECS/BundleWriteView.cs` |
| `SomeEngine.ECS.Commands.DeferredEntity` | `SomeEngine.ECS` | 5 | 4 | `src/SomeEngine.ECS/Commands/CommandBuffer.cs` |
| `SomeEngine.ECS.Commands.DeferredRelationEdgeCell<T>` | `SomeEngine.ECS` | 5 | 4 | `src/SomeEngine.ECS/Commands/CommandBuffer.Relations.cs` |
| `SomeEngine.ECS.Commands.RelationCommandEdge<T>` | `SomeEngine.ECS` | 5 | 4 | `src/SomeEngine.ECS/Commands/CommandBuffer.Relations.cs` |
| `SomeEngine.ECS.Components.IBufferCopier` | `SomeEngine.ECS` | 5 | 2 | `src/SomeEngine.ECS/Components/BufferRegistry.cs` |
| `SomeEngine.ECS.Owners.Copy` | `SomeEngine.ECS` | 5 | 25 | `src/SomeEngine.ECS/Owners.Copy.cs` |
| `SomeEngine.ECS.Owners.Copy.CopyRules` | `SomeEngine.ECS` | 5 | 7 | `src/SomeEngine.ECS/Owners.Copy.cs` |
| `SomeEngine.ECS.Owners.IHierarchyComponentRegistration` | `SomeEngine.ECS` | 5 | 2 | `src/SomeEngine.ECS/Owners.Hierarchy.cs` |
| `SomeEngine.ECS.Owners.IHierarchyDomainStore` | `SomeEngine.ECS` | 5 | 2 | `src/SomeEngine.ECS/Owners.Hierarchy.cs` |
| `SomeEngine.ECS.Relations.RelationTypeState<T>` | `SomeEngine.ECS` | 5 | 34 | `src/SomeEngine.ECS/Relations/RelationTypeState.cs`<br>`src/SomeEngine.ECS/Relations/RelationTypeState.Queries.cs`<br>`src/SomeEngine.ECS/Relations/RelationTypeState.Tracking.cs`<br>`src/SomeEngine.ECS/Relations/RelationTypeState.Transitions.cs` |
| `SomeEngine.ECS.Commands.DeferredEntityCell` | `SomeEngine.ECS` | 4 | 3 | `src/SomeEngine.ECS/Commands/CommandBuffer.cs` |
| `SomeEngine.ECS.Commands.ITypedRelationshipCommand` | `SomeEngine.ECS` | 4 | 2 | `src/SomeEngine.ECS/Commands/CommandBuffer.Hierarchy.cs` |
| `SomeEngine.ECS.Components.BufferRegistry` | `SomeEngine.ECS` | 4 | 7 | `src/SomeEngine.ECS/Components/BufferRegistry.cs` |
| `SomeEngine.ECS.Hooks.DeferredWorld` | `SomeEngine.ECS` | 4 | 6 | `src/SomeEngine.ECS/Hooks/DeferredWorld.cs` |
| `SomeEngine.ECS.Owners.Bundles` | `SomeEngine.ECS` | 4 | 43 | `src/SomeEngine.ECS/Owners.Bundles.cs` |
| `SomeEngine.ECS.Queries.IChunkFilter` | `SomeEngine.ECS` | 4 | 2 | `src/SomeEngine.ECS/Queries/QueryChunkEnumerator.cs` |
| `SomeEngine.ECS.Queries.QueryChunkEnumerator<TFilter>` | `SomeEngine.ECS` | 4 | 7 | `src/SomeEngine.ECS/Queries/QueryChunkEnumerator.cs` |
| `SomeEngine.ECS.Queries.QueryRow` | `SomeEngine.ECS` | 4 | 12 | `src/SomeEngine.ECS/Queries/QueryRow.cs` |
| `SomeEngine.ECS.Registry.PublicComponentMutationGuard` | `SomeEngine.ECS` | 4 | 4 | `src/SomeEngine.ECS/Registry/PublicComponentMutationGuard.cs` |
| `SomeEngine.ECS.StructuralMutationScope` | `SomeEngine.ECS` | 4 | 7 | `src/SomeEngine.ECS/StructuralMutationScope.cs` |
| `SomeEngine.ECS.BundleSpawnMap` | `SomeEngine.ECS` | 3 | 4 | `src/SomeEngine.ECS/BundleSpawnMap.cs` |
| `SomeEngine.ECS.BundleWriteAction<TState>` | `SomeEngine.ECS` | 3 | 1 | `src/SomeEngine.ECS/BundleWriteView.cs` |
| `SomeEngine.ECS.Commands.HierarchyCommandWriter<TDomain>` | `SomeEngine.ECS` | 3 | 15 | `src/SomeEngine.ECS/Commands/CommandBuffer.Hierarchy.cs` |
| `SomeEngine.ECS.Commands.RelationCommandWriter<T>` | `SomeEngine.ECS` | 3 | 27 | `src/SomeEngine.ECS/Commands/CommandBuffer.Relations.cs` |
| `SomeEngine.ECS.Hooks.DeferredCommandWriter` | `SomeEngine.ECS` | 3 | 10 | `src/SomeEngine.ECS/Hooks/DeferredWorld.cs` |
| `SomeEngine.ECS.IWorldJobAdmission` | `SomeEngine.ECS` | 3 | 2 | `src/SomeEngine.ECS/World.JobAdmission.cs` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.OrderedChildShard` | `SomeEngine.ECS` | 3 | 5 | `src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.TopologyImport` | `SomeEngine.ECS` | 3 | 12 | `src/SomeEngine.ECS/Owners.Hierarchy.cs`<br>`src/SomeEngine.ECS/Owners.Hierarchy.SerializationValidation.cs` |
| `SomeEngine.ECS.Queries.NoSharedFilter` | `SomeEngine.ECS` | 3 | 3 | `src/SomeEngine.ECS/Queries/QueryChunkEnumerator.cs` |
| `SomeEngine.ECS.Queries.QueryAccessGuards` | `SomeEngine.ECS` | 3 | 6 | `src/SomeEngine.ECS/Queries/QueryAccessGuards.cs` |
| `SomeEngine.ECS.Queries.QueryPairEnumerator<TWrite, TRead>` | `SomeEngine.ECS` | 3 | 10 | `src/SomeEngine.ECS/Queries/QueryPairEnumerator.cs` |
| `SomeEngine.ECS.Queries.ReadWriteMatch` | `SomeEngine.ECS` | 3 | 2 | `src/SomeEngine.ECS/Queries/QueryState.cs` |
| `SomeEngine.ECS.RestrictedWorldApiScope` | `SomeEngine.ECS` | 3 | 2 | `src/SomeEngine.ECS/World.JobAdmission.cs` |
| `SomeEngine.ECS.Serialization.RelationTopologyImport<T>` | `SomeEngine.ECS` | 3 | 21 | `src/SomeEngine.ECS/Serialization/WorldTopologySerializationAccess.cs` |
| `SomeEngine.ECS.SerializationValidationScope` | `SomeEngine.ECS` | 3 | 1 | `src/SomeEngine.ECS/World.SerializationWriteAdmission.cs` |
| `SomeEngine.ECS.BufferWriteExecution<T, TState>` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/BufferExecution.cs` |
| `SomeEngine.ECS.BundleMaterializedRow` | `SomeEngine.ECS` | 2 | 4 | `src/SomeEngine.ECS/BundleWriteView.cs` |
| `SomeEngine.ECS.BundleWriteAction` | `SomeEngine.ECS` | 2 | 1 | `src/SomeEngine.ECS/BundleWriteView.cs` |
| `SomeEngine.ECS.BundleWriteRuntime` | `SomeEngine.ECS` | 2 | 20 | `src/SomeEngine.ECS/BundleWriteView.cs` |
| `SomeEngine.ECS.Commands.CommandBuffer.JobProducerPlaybackBatch` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/Commands/CommandBuffer.cs` |
| `SomeEngine.ECS.Commands.IBufferCommandList` | `SomeEngine.ECS` | 2 | 3 | `src/SomeEngine.ECS/Commands/CommandBuffer.Payloads.cs` |
| `SomeEngine.ECS.Commands.IComponentCommandList` | `SomeEngine.ECS` | 2 | 3 | `src/SomeEngine.ECS/Commands/CommandBuffer.Payloads.cs` |
| `SomeEngine.ECS.Hierarchy.Children<TDomain>` | `SomeEngine.ECS` | 2 | 7 | `src/SomeEngine.ECS/Hierarchy/HierarchyComponents.cs` |
| `SomeEngine.ECS.Hooks.ComponentHooks<T>` | `SomeEngine.ECS` | 2 | 3 | `src/SomeEngine.ECS/Hooks/ComponentHooks.cs` |
| `SomeEngine.ECS.Hooks.HookAction<T>` | `SomeEngine.ECS` | 2 | 3 | `src/SomeEngine.ECS/Hooks/ComponentHooks.cs` |
| `SomeEngine.ECS.Hooks.HookStore<T>` | `SomeEngine.ECS` | 2 | 5 | `src/SomeEngine.ECS/Hooks/HookStore.cs` |
| `SomeEngine.ECS.Hooks.IHookStore` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/Hooks/HookStore.cs` |
| `SomeEngine.ECS.JobCommandProducerScope` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/World.JobAdmission.cs` |
| `SomeEngine.ECS.Owners.Copy.ComponentChanges` | `SomeEngine.ECS` | 2 | 11 | `src/SomeEngine.ECS/Owners.Copy.cs` |
| `SomeEngine.ECS.Owners.RelationGraph.IRelationEndpointTracker` | `SomeEngine.ECS` | 2 | 4 | `src/SomeEngine.ECS/Owners.RelationGraph.EndpointTracking.cs` |
| `SomeEngine.ECS.Queries.ChunkRowIndexEnumerator` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/Queries/QueryChunkView.cs` |
| `SomeEngine.ECS.Queries.QueryExecution` | `SomeEngine.ECS` | 2 | 1 | `src/SomeEngine.ECS/Queries/QueryCursor.cs` |
| `SomeEngine.ECS.Queries.QueryRowCursor` | `SomeEngine.ECS` | 2 | 4 | `src/SomeEngine.ECS/Queries/QueryRowEnumerator.cs` |
| `SomeEngine.ECS.Queries.QuerySharedFilter` | `SomeEngine.ECS` | 2 | 1 | `src/SomeEngine.ECS/Queries/QueryTerm.cs` |
| `SomeEngine.ECS.Queries.QueryableTypeInfo` | `SomeEngine.ECS` | 2 | 10 | `src/SomeEngine.ECS/Queries/QueryableCapabilities.cs` |
| `SomeEngine.ECS.Relations.RelationTypeSlotTable.Enumerator` | `SomeEngine.ECS` | 2 | 1 | `src/SomeEngine.ECS/Relations/RelationTypeSlotTable.cs` |
| `SomeEngine.ECS.Serialization.HierarchyTopologyWriteAccess<TDomain>` | `SomeEngine.ECS` | 2 | 3 | `src/SomeEngine.ECS/Serialization/WorldTopologySerializationAccess.cs` |
| `SomeEngine.ECS.Serialization.RelationTopologyImport<T>.OrderedSequence` | `SomeEngine.ECS` | 2 | 12 | `src/SomeEngine.ECS/Serialization/WorldTopologySerializationAccess.cs` |
| `SomeEngine.ECS.Serialization.RelationTopologyWriteAccess<T>` | `SomeEngine.ECS` | 2 | 14 | `src/SomeEngine.ECS/Serialization/WorldTopologySerializationAccess.cs` |
| `SomeEngine.ECS.World.SerializationReadRootContext` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/World.SerializationReadRoot.cs` |
| `SomeEngine.ECS.World.SerializationReadRootScope` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/World.SerializationReadRoot.cs` |
| `SomeEngine.ECS.World.SerializationWriteLifetimeScope` | `SomeEngine.ECS` | 2 | 1 | `src/SomeEngine.ECS/World.SerializationWriteAdmission.cs` |
| `SomeEngine.ECS.World.StructuralCandidateContext` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/World.cs` |
| `SomeEngine.ECS.World.StructuralCandidateScope` | `SomeEngine.ECS` | 2 | 2 | `src/SomeEngine.ECS/World.cs` |
| `SomeEngine.ECS.World.StructuralTransactionScope` | `SomeEngine.ECS` | 2 | 1 | `src/SomeEngine.ECS/World.cs` |
| `SomeEngine.ECS.WorldStructurePublication` | `SomeEngine.ECS` | 2 | 1 | `src/SomeEngine.ECS/WorldStructureRoot.cs` |
| `SomeEngine.ECS.BufferWriteExecution<T>` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/BufferExecution.cs` |
| `SomeEngine.ECS.Commands.BufferCommandDataList<T>` | `SomeEngine.ECS` | 1 | 8 | `src/SomeEngine.ECS/Commands/CommandBuffer.Payloads.cs` |
| `SomeEngine.ECS.Commands.BulkDestroyRelationCommand<T>` | `SomeEngine.ECS` | 1 | 7 | `src/SomeEngine.ECS/Commands/CommandBuffer.Relations.cs` |
| `SomeEngine.ECS.Commands.CommandDataList<T>` | `SomeEngine.ECS` | 1 | 6 | `src/SomeEngine.ECS/Commands/CommandBuffer.Payloads.cs` |
| `SomeEngine.ECS.Commands.CommandHeader` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/Commands/CommandBuffer.cs` |
| `SomeEngine.ECS.Commands.CreateRelationCommand<T>` | `SomeEngine.ECS` | 1 | 12 | `src/SomeEngine.ECS/Commands/CommandBuffer.Relations.cs` |
| `SomeEngine.ECS.Commands.DeferredRelationEdge<T>` | `SomeEngine.ECS` | 1 | 5 | `src/SomeEngine.ECS/Commands/CommandBuffer.Relations.cs` |
| `SomeEngine.ECS.Commands.DestroyRelationCommand<T>` | `SomeEngine.ECS` | 1 | 5 | `src/SomeEngine.ECS/Commands/CommandBuffer.Relations.cs` |
| `SomeEngine.ECS.Commands.DestroySubtreeCommand<TDomain>` | `SomeEngine.ECS` | 1 | 6 | `src/SomeEngine.ECS/Commands/CommandBuffer.Hierarchy.cs` |
| `SomeEngine.ECS.Commands.DetachCommand<TDomain>` | `SomeEngine.ECS` | 1 | 8 | `src/SomeEngine.ECS/Commands/CommandBuffer.Hierarchy.cs` |
| `SomeEngine.ECS.Commands.ReorderCommand<TDomain>` | `SomeEngine.ECS` | 1 | 6 | `src/SomeEngine.ECS/Commands/CommandBuffer.Hierarchy.cs` |
| `SomeEngine.ECS.Commands.ReorderRelationCommand<T>` | `SomeEngine.ECS` | 1 | 7 | `src/SomeEngine.ECS/Commands/CommandBuffer.Relations.cs` |
| `SomeEngine.ECS.Commands.RetargetRelationCommand<T>` | `SomeEngine.ECS` | 1 | 12 | `src/SomeEngine.ECS/Commands/CommandBuffer.Relations.cs` |
| `SomeEngine.ECS.Commands.SetOrderPolicyCommand<TDomain>` | `SomeEngine.ECS` | 1 | 8 | `src/SomeEngine.ECS/Commands/CommandBuffer.Hierarchy.cs` |
| `SomeEngine.ECS.Commands.SetParentCommand<TDomain>` | `SomeEngine.ECS` | 1 | 8 | `src/SomeEngine.ECS/Commands/CommandBuffer.Hierarchy.cs` |
| `SomeEngine.ECS.Commands.SetRelationAdjacencyOrderCommand<T>` | `SomeEngine.ECS` | 1 | 7 | `src/SomeEngine.ECS/Commands/CommandBuffer.Relations.cs` |
| `SomeEngine.ECS.Components.BufferCopier<T>` | `SomeEngine.ECS` | 1 | 4 | `src/SomeEngine.ECS/Components/BufferRegistry.cs` |
| `SomeEngine.ECS.Components.BufferRegistry.RegistryState` | `SomeEngine.ECS` | 1 | 1 | `src/SomeEngine.ECS/Components/BufferRegistry.cs` |
| `SomeEngine.ECS.Owners.Commands` | `SomeEngine.ECS` | 1 | 5 | `src/SomeEngine.ECS/Owners.Commands.cs` |
| `SomeEngine.ECS.Owners.Copy.CopyGuard` | `SomeEngine.ECS` | 1 | 4 | `src/SomeEngine.ECS/Owners.Copy.cs` |
| `SomeEngine.ECS.Owners.Copy.CopyShape` | `SomeEngine.ECS` | 1 | 5 | `src/SomeEngine.ECS/Owners.Copy.cs` |
| `SomeEngine.ECS.Owners.Copy.ExtraSurface` | `SomeEngine.ECS` | 1 | 5 | `src/SomeEngine.ECS/Owners.Copy.cs` |
| `SomeEngine.ECS.Owners.Copy.TableSurface` | `SomeEngine.ECS` | 1 | 16 | `src/SomeEngine.ECS/Owners.Copy.cs` |
| `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.PreparedMaintenance` | `SomeEngine.ECS` | 1 | 4 | `src/SomeEngine.ECS/Owners.Hierarchy.cs` |
| `SomeEngine.ECS.Owners.Hooks.HookExecutionScope` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/Owners.Hooks.cs` |
| `SomeEngine.ECS.Owners.RelationGraph.RelationEndpointTracker<T>` | `SomeEngine.ECS` | 1 | 13 | `src/SomeEngine.ECS/Owners.RelationGraph.EndpointTracking.cs` |
| `SomeEngine.ECS.Owners.Tables.ChunkCapacity` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/Owners.Tables.cs` |
| `SomeEngine.ECS.Queries.ChunkRowEnumerator` | `SomeEngine.ECS` | 1 | 5 | `src/SomeEngine.ECS/Queries/QueryRowEnumerator.cs` |
| `SomeEngine.ECS.Queries.QueryDefinitionBuilder` | `SomeEngine.ECS` | 1 | 15 | `src/SomeEngine.ECS/Queries/QueryDefinitionBuilder.cs` |
| `SomeEngine.ECS.Queries.QueryPairExecution<TWrite, TRead, TState>` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/Queries/QueryPairEnumerator.cs` |
| `SomeEngine.ECS.Queries.QueryPairExecution<TWrite, TRead>` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/Queries/QueryPairEnumerator.cs` |
| `SomeEngine.ECS.Queries.QueryRowEnumerator<TFilter>` | `SomeEngine.ECS` | 1 | 6 | `src/SomeEngine.ECS/Queries/QueryRowEnumerator.cs` |
| `SomeEngine.ECS.Queries.QueryState.QueryMatchBuilder` | `SomeEngine.ECS` | 1 | 9 | `src/SomeEngine.ECS/Queries/QueryState.cs` |
| `SomeEngine.ECS.Queries.SingleSharedFilter` | `SomeEngine.ECS` | 1 | 5 | `src/SomeEngine.ECS/Queries/QueryChunkEnumerator.cs` |
| `SomeEngine.ECS.Relations.RelationTypeSlotTable` | `SomeEngine.ECS` | 1 | 2 | `src/SomeEngine.ECS/Relations/RelationTypeSlotTable.cs` |
| `SomeEngine.ECS.World.ReadSnapshotCallbackScope` | `SomeEngine.ECS` | 1 | 1 | `src/SomeEngine.ECS/World.ReadSnapshotAdmission.cs` |

### Rank 9

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.Systems.RelationshipJobAccess` | `SomeEngine.ECS.Systems` | 13 | 4 | `src/SomeEngine.ECS.Systems/RelationshipJobAccess.cs` |
| `SomeEngine.ECS.Systems.WorldStorageJobResources` | `SomeEngine.ECS.Systems` | 12 | 4 | `src/SomeEngine.ECS.Systems/BufferJobAccess.cs` |
| `SomeEngine.ECS.Systems.ParentTopologyStage<TDomain>` | `SomeEngine.ECS.Systems` | 6 | 8 | `src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs` |
| `SomeEngine.ECS.Serialization.TopologySerializationRuntime` | `SomeEngine.ECS.Serialization` | 5 | 7 | `src/SomeEngine.ECS.Serialization/TopologySerializationRegistry.cs` |
| `SomeEngine.ECS.Systems.JobCommandWriter` | `SomeEngine.ECS.Systems` | 5 | 10 | `src/SomeEngine.ECS.Systems/JobCommandBuffer.cs` |
| `SomeEngine.ECS.Systems.ReadOnlyQueryPacket` | `SomeEngine.ECS.Systems` | 5 | 9 | `src/SomeEngine.ECS.Systems/ReadOnlyQueryPacketJobs.cs` |
| `SomeEngine.ECS.Systems.QueryPacket` | `SomeEngine.ECS.Systems` | 4 | 3 | `src/SomeEngine.ECS.Systems/StableQueryPackets.cs` |
| `SomeEngine.ECS.Systems.ReadOnlyPacketRange` | `SomeEngine.ECS.Systems` | 4 | 2 | `src/SomeEngine.ECS.Systems/ReadOnlyQueryPacketJobs.cs` |
| `SomeEngine.ECS.Serialization.AdmittedWorldWrite` | `SomeEngine.ECS.Serialization` | 3 | 14 | `src/SomeEngine.ECS.Serialization/AdmittedWorldWrite.cs` |
| `SomeEngine.ECS.Systems.HierarchyMaintenanceDependency<TDomain>` | `SomeEngine.ECS.Systems` | 3 | 6 | `src/SomeEngine.ECS.Systems/HierarchyMaintenanceSystem.cs` |
| `SomeEngine.ECS.Systems.IParentTopologyPacketJob<TDomain>` | `SomeEngine.ECS.Systems` | 3 | 4 | `src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs` |
| `SomeEngine.ECS.Systems.JobEntityRuntime.JobEntityExecutionVersion` | `SomeEngine.ECS.Systems` | 3 | 1 | `src/SomeEngine.ECS.Systems/JobEntityRuntime.cs` |
| `SomeEngine.ECS.Systems.JobEntityScheduleOptions` | `SomeEngine.ECS.Systems` | 3 | 1 | `src/SomeEngine.ECS.Systems/JobEntity.cs` |
| `SomeEngine.ECS.Systems.RelationshipChunkQueryGuards` | `SomeEngine.ECS.Systems` | 3 | 11 | `src/SomeEngine.ECS.Systems/RelationshipChunkJobs.cs` |
| `SomeEngine.ECS.Systems.GeneratedQueryAccess` | `SomeEngine.ECS.Systems` | 2 | 10 | `src/SomeEngine.ECS.Systems/JobEntity.cs` |
| `SomeEngine.ECS.Systems.IParentReadChunkJob<TDomain>` | `SomeEngine.ECS.Systems` | 2 | 3 | `src/SomeEngine.ECS.Systems/RelationshipChunkJobs.cs` |
| `SomeEngine.ECS.Systems.IParentWriteChunkJob<TDomain>` | `SomeEngine.ECS.Systems` | 2 | 3 | `src/SomeEngine.ECS.Systems/RelationshipChunkJobs.cs` |
| `SomeEngine.ECS.Systems.WorldJobAdmission` | `SomeEngine.ECS.Systems` | 2 | 12 | `src/SomeEngine.ECS.Systems/WorldJobAdmission.cs` |
| `SomeEngine.ECS.Serialization.DurableSaveStore.CandidateWorldRead` | `SomeEngine.ECS.Serialization` | 1 | 2 | `src/SomeEngine.ECS.Serialization/DurableSaveStore.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagationExecutionVersion` | `SomeEngine.ECS.Systems` | 1 | 1 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs` |
| `SomeEngine.ECS.Systems.ImmediateSystemContext` | `SomeEngine.ECS.Systems` | 1 | 1 | `src/SomeEngine.ECS.Systems/ImmediateSystemContext.cs` |
| `SomeEngine.ECS.Systems.JobCommandBuffer.ProducerSegment` | `SomeEngine.ECS.Systems` | 1 | 1 | `src/SomeEngine.ECS.Systems/JobCommandBuffer.cs` |
| `SomeEngine.ECS.Hierarchy.Hierarchy` | `SomeEngine.ECS` | 0 | 6 | `src/SomeEngine.ECS/Hierarchy/Hierarchy.cs` |
| `SomeEngine.ECS.Systems.SystemGroup<TContext>` | `SomeEngine.ECS.Systems` | 0 | 6 | `src/SomeEngine.ECS.Systems/SystemGroup.cs` |

### Rank 10

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.Systems.WorldStorageJobSchedule` | `SomeEngine.ECS.Systems` | 9 | 2 | `src/SomeEngine.ECS.Systems/WorldStorageJobSchedule.cs` |
| `SomeEngine.ECS.Systems.GeneratedQueryAccessDescriptor` | `SomeEngine.ECS.Systems` | 7 | 16 | `src/SomeEngine.ECS.Systems/JobEntity.cs` |
| `SomeEngine.ECS.Systems.IJobParallelCommandProducer` | `SomeEngine.ECS.Systems` | 3 | 1 | `src/SomeEngine.ECS.Systems/JobCommandBuffer.cs` |
| `SomeEngine.ECS.Systems.IReadOnlyQueryPacketCommandJob` | `SomeEngine.ECS.Systems` | 3 | 3 | `src/SomeEngine.ECS.Systems/ReadOnlyQueryPacketJobs.cs` |
| `SomeEngine.ECS.Systems.IReadOnlyQueryPacketJob` | `SomeEngine.ECS.Systems` | 3 | 2 | `src/SomeEngine.ECS.Systems/ReadOnlyQueryPacketJobs.cs` |
| `SomeEngine.ECS.Systems.StableQueryPacketSet` | `SomeEngine.ECS.Systems` | 3 | 3 | `src/SomeEngine.ECS.Systems/StableQueryPackets.cs` |
| `SomeEngine.ECS.Systems.IJobCommandProducer` | `SomeEngine.ECS.Systems` | 2 | 1 | `src/SomeEngine.ECS.Systems/JobCommandBuffer.cs` |
| `SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>.ParentFinalizerJob` | `SomeEngine.ECS.Systems` | 2 | 16 | `src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs` |
| `SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>.ParentPacketJob<TJob>` | `SomeEngine.ECS.Systems` | 2 | 9 | `src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs` |
| `SomeEngine.ECS.Serialization.HierarchyTopologySerializationRuntime<TDomain>` | `SomeEngine.ECS.Serialization` | 1 | 14 | `src/SomeEngine.ECS.Serialization/TopologySerializationRegistry.cs` |
| `SomeEngine.ECS.Serialization.RelationTopologySerializationRuntime<T>` | `SomeEngine.ECS.Serialization` | 1 | 25 | `src/SomeEngine.ECS.Serialization/TopologySerializationRegistry.cs` |
| `SomeEngine.ECS.Systems.TopologyStablePacketCapture` | `SomeEngine.ECS.Systems` | 1 | 19 | `src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs` |
| `SomeEngine.ECS.Systems.ImmediateSystemDriver` | `SomeEngine.ECS.Systems` | 0 | 4 | `src/SomeEngine.ECS.Systems/ImmediateSystemDriver.cs` |
| `SomeEngine.ECS.Systems.WorldJobAdmissionModule` | `SomeEngine.ECS.Systems` | 0 | 2 | `src/SomeEngine.ECS.Systems/WorldJobAdmissionModule.cs` |

### Rank 11

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.Serialization.SerializationTypeRuntime` | `SomeEngine.ECS.Serialization` | 13 | 7 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Serialization.SerializationRegistry` | `SomeEngine.ECS.Serialization` | 9 | 35 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs`<br>`src/SomeEngine.ECS.Serialization/SerializationRegistry.SnapshotValidation.cs`<br>`src/SomeEngine.ECS.Serialization/TopologySerializationRegistry.cs` |
| `SomeEngine.ECS.Systems.HierarchyJobAccess<TDomain>` | `SomeEngine.ECS.Systems` | 8 | 18 | `src/SomeEngine.ECS.Systems/HierarchyJobAccess.cs` |
| `SomeEngine.ECS.Serialization.ValueSerializationRuntime<T>` | `SomeEngine.ECS.Serialization` | 7 | 12 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Systems.JobCommandBuffer` | `SomeEngine.ECS.Systems` | 6 | 15 | `src/SomeEngine.ECS.Systems/JobCommandBuffer.cs` |
| `SomeEngine.ECS.Systems.RelationJobAccess<T>` | `SomeEngine.ECS.Systems` | 6 | 27 | `src/SomeEngine.ECS.Systems/RelationJobAccess.cs` |
| `SomeEngine.ECS.Serialization.SparseSerializationPresence` | `SomeEngine.ECS.Serialization` | 3 | 3 | `src/SomeEngine.ECS.Serialization/AdmittedWorldWrite.cs` |
| `SomeEngine.ECS.Systems.JobEntityRow` | `SomeEngine.ECS.Systems` | 3 | 19 | `src/SomeEngine.ECS.Systems/JobEntityRuntime.cs` |
| `SomeEngine.ECS.Serialization.BufferSerializationRuntime<T>` | `SomeEngine.ECS.Serialization` | 2 | 18 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Serialization.BufferSerializationRuntime<T>.BufferApplyState` | `SomeEngine.ECS.Serialization` | 1 | 4 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Serialization.ComponentSerializationRuntime<T>` | `SomeEngine.ECS.Serialization` | 1 | 14 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Serialization.SharedSerializationRuntime<T>` | `SomeEngine.ECS.Serialization` | 1 | 14 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Serialization.SparseSerializationRuntime<T>` | `SomeEngine.ECS.Serialization` | 1 | 17 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Serialization.TagSerializationRuntime<T>` | `SomeEngine.ECS.Serialization` | 1 | 14 | `src/SomeEngine.ECS.Serialization/SerializationRegistry.cs` |
| `SomeEngine.ECS.Systems.ComponentJobAccess<T>` | `SomeEngine.ECS.Systems` | 1 | 9 | `src/SomeEngine.ECS.Systems/ComponentJobAccess.cs` |
| `SomeEngine.ECS.Systems.HierarchyJobAccess<TDomain>.ParentChunkJobAdapter<TJob>` | `SomeEngine.ECS.Systems` | 1 | 10 | `src/SomeEngine.ECS.Systems/HierarchyJobAccess.cs` |
| `SomeEngine.ECS.Systems.HierarchyJobAccess<TDomain>.ParentReadChunkJobAdapter<TJob>` | `SomeEngine.ECS.Systems` | 1 | 10 | `src/SomeEngine.ECS.Systems/HierarchyJobAccess.cs` |
| `SomeEngine.ECS.Systems.JobCommandBuffer.CompletionAdapter` | `SomeEngine.ECS.Systems` | 1 | 1 | `src/SomeEngine.ECS.Systems/JobCommandBuffer.cs` |
| `SomeEngine.ECS.Systems.JobCommandBuffer.ParallelProducerAdapter<TProducer>` | `SomeEngine.ECS.Systems` | 1 | 2 | `src/SomeEngine.ECS.Systems/JobCommandBuffer.cs` |
| `SomeEngine.ECS.Systems.JobCommandBuffer.PublicationAdapter` | `SomeEngine.ECS.Systems` | 1 | 1 | `src/SomeEngine.ECS.Systems/JobCommandBuffer.cs` |
| `SomeEngine.ECS.Systems.JobCommandBuffer.SerialProducerAdapter<TProducer>` | `SomeEngine.ECS.Systems` | 1 | 2 | `src/SomeEngine.ECS.Systems/JobCommandBuffer.cs` |
| `SomeEngine.ECS.Systems.ReadOnlyQueryPacketJobs.PacketCommandProducer<TJob>` | `SomeEngine.ECS.Systems` | 1 | 6 | `src/SomeEngine.ECS.Systems/ReadOnlyQueryPacketJobs.cs` |
| `SomeEngine.ECS.Systems.ReadOnlyQueryPacketJobs.PacketJob<TJob>` | `SomeEngine.ECS.Systems` | 1 | 4 | `src/SomeEngine.ECS.Systems/ReadOnlyQueryPacketJobs.cs` |
| `SomeEngine.ECS.Systems.RelationJobAccess<T>.DirectedEndpointChunkJobAdapter<TJob>` | `SomeEngine.ECS.Systems` | 1 | 10 | `src/SomeEngine.ECS.Systems/RelationJobAccess.cs` |
| `SomeEngine.ECS.Systems.RelationJobAccess<T>.DirectedEndpointReadChunkJobAdapter<TJob>` | `SomeEngine.ECS.Systems` | 1 | 10 | `src/SomeEngine.ECS.Systems/RelationJobAccess.cs` |
| `SomeEngine.ECS.Systems.RelationJobAccess<T>.UndirectedEndpointChunkJobAdapter<TJob>` | `SomeEngine.ECS.Systems` | 1 | 10 | `src/SomeEngine.ECS.Systems/RelationJobAccess.cs` |
| `SomeEngine.ECS.Systems.RelationJobAccess<T>.UndirectedEndpointReadChunkJobAdapter<TJob>` | `SomeEngine.ECS.Systems` | 1 | 10 | `src/SomeEngine.ECS.Systems/RelationJobAccess.cs` |
| `SomeEngine.ECS.Systems.BufferJobAccess<T>` | `SomeEngine.ECS.Systems` | 0 | 8 | `src/SomeEngine.ECS.Systems/BufferJobAccess.cs` |
| `SomeEngine.ECS.Systems.SharedJobAccess<T>` | `SomeEngine.ECS.Systems` | 0 | 8 | `src/SomeEngine.ECS.Systems/SharedJobAccess.cs` |
| `SomeEngine.ECS.Systems.SparseJobAccess<T>` | `SomeEngine.ECS.Systems` | 0 | 8 | `src/SomeEngine.ECS.Systems/SparseJobAccess.cs` |

### Rank 12

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.Systems.IGeneratedJobEntityAdapter<TJob>` | `SomeEngine.ECS.Systems` | 6 | 2 | `src/SomeEngine.ECS.Systems/JobEntity.cs` |
| `SomeEngine.ECS.Serialization.WorldSerializer.PayloadFrame` | `SomeEngine.ECS.Serialization` | 3 | 9 | `src/SomeEngine.ECS.Serialization/WorldSerializer.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAccessSet<TDomain>` | `SomeEngine.ECS.Systems` | 3 | 19 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs` |
| `SomeEngine.ECS.Serialization.WorldSerializer.PayloadBytes` | `SomeEngine.ECS.Serialization` | 2 | 6 | `src/SomeEngine.ECS.Serialization/WorldSerializer.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>.AdmittedHierarchyReader` | `SomeEngine.ECS.Systems` | 2 | 15 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs` |
| `SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>.FinalizerLauncherJob` | `SomeEngine.ECS.Systems` | 2 | 5 | `src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs` |
| `SomeEngine.ECS.Serialization.WorldWritePlan` | `SomeEngine.ECS.Serialization` | 1 | 9 | `src/SomeEngine.ECS.Serialization/AdmittedWorldWrite.cs` |
| `SomeEngine.ECS.Systems.HierarchyMaintenanceSystem<TDomain>.MaintenanceJob` | `SomeEngine.ECS.Systems` | 1 | 7 | `src/SomeEngine.ECS.Systems/HierarchyMaintenanceSystem.cs` |
| `SomeEngine.ECS.Systems.ReadOnlyQueryPacketJobs` | `SomeEngine.ECS.Systems` | 1 | 15 | `src/SomeEngine.ECS.Systems/ReadOnlyQueryPacketJobs.cs` |
| `SomeEngine.ECS.Systems.ReadOnlyQueryPacketPlan` | `SomeEngine.ECS.Systems` | 1 | 8 | `src/SomeEngine.ECS.Systems/ReadOnlyQueryPacketJobs.cs` |
| `SomeEngine.ECS.Systems.RelationMaintenanceSystem<T>.MaintenanceJob` | `SomeEngine.ECS.Systems` | 1 | 3 | `src/SomeEngine.ECS.Systems/RelationMaintenanceSystem.cs` |

### Rank 13

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.Systems.HierarchyPropagationContext<TDomain>` | `SomeEngine.ECS.Systems` | 2 | 4 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs` |
| `SomeEngine.ECS.Systems.JobEntityRuntime.SerialState<TJob, TAdapter>` | `SomeEngine.ECS.Systems` | 2 | 4 | `src/SomeEngine.ECS.Systems/JobEntityRuntime.cs` |
| `SomeEngine.ECS.Serialization.WorldSerializer.EntityCodec` | `SomeEngine.ECS.Serialization` | 1 | 7 | `src/SomeEngine.ECS.Serialization/WorldSerializer.cs` |
| `SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>` | `SomeEngine.ECS.Systems` | 1 | 25 | `src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs` |
| `SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>.CaptureAndScheduleJob<TJob>` | `SomeEngine.ECS.Systems` | 1 | 11 | `src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs` |
| `SomeEngine.ECS.Systems.HierarchyMaintenanceSystem<TDomain>` | `SomeEngine.ECS.Systems` | 0 | 7 | `src/SomeEngine.ECS.Systems/HierarchyMaintenanceSystem.cs` |
| `SomeEngine.ECS.Systems.RelationMaintenanceSystem<T>` | `SomeEngine.ECS.Systems` | 0 | 5 | `src/SomeEngine.ECS.Systems/RelationMaintenanceSystem.cs` |

### Rank 14

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.Serialization.WorldSerializer` | `SomeEngine.ECS.Serialization` | 5 | 35 | `src/SomeEngine.ECS.Serialization/WorldSerializer.Contracts.cs`<br>`src/SomeEngine.ECS.Serialization/WorldSerializer.cs`<br>`src/SomeEngine.ECS.Serialization/WorldSerializer.ManifestValidation.cs`<br>`src/SomeEngine.ECS.Serialization/WorldStateHash.cs` |
| `SomeEngine.ECS.Serialization.PayloadFormat` | `SomeEngine.ECS.Serialization` | 4 | 11 | `src/SomeEngine.ECS.Serialization/Format/PayloadFormat.cs` |
| `SomeEngine.ECS.Serialization.TopologyCodec` | `SomeEngine.ECS.Serialization` | 3 | 14 | `src/SomeEngine.ECS.Serialization/TopologyCodec.cs` |
| `SomeEngine.ECS.Systems.IHierarchyPropagationJob<TDomain>` | `SomeEngine.ECS.Systems` | 3 | 2 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs` |
| `SomeEngine.ECS.Systems.JobEntityRuntime.SerialJob<TJob, TAdapter>` | `SomeEngine.ECS.Systems` | 2 | 8 | `src/SomeEngine.ECS.Systems/JobEntityRuntime.cs` |
| `SomeEngine.ECS.Serialization.SerializationEnvironment` | `SomeEngine.ECS.Serialization` | 1 | 2 | `src/SomeEngine.ECS.Serialization/Format/PayloadFormat.cs` |
| `SomeEngine.ECS.Serialization.WorldSerializer.WorldImporter` | `SomeEngine.ECS.Serialization` | 1 | 17 | `src/SomeEngine.ECS.Serialization/WorldSerializer.cs` |
| `SomeEngine.ECS.Serialization.WorldSerializer.WorldRestorer` | `SomeEngine.ECS.Serialization` | 1 | 14 | `src/SomeEngine.ECS.Serialization/WorldSerializer.cs` |

### Rank 15

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.Systems.JobEntityRuntime` | `SomeEngine.ECS.Systems` | 3 | 39 | `src/SomeEngine.ECS.Systems/JobEntityRuntime.cs` |
| `SomeEngine.ECS.Serialization.DurableSaveStore` | `SomeEngine.ECS.Serialization` | 2 | 15 | `src/SomeEngine.ECS.Serialization/DurableSaveStore.cs`<br>`src/SomeEngine.ECS.Serialization/DurableSaveStore.Envelope.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>.PropagationPacketJob<TJob>` | `SomeEngine.ECS.Systems` | 2 | 8 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs` |
| `SomeEngine.ECS.Systems.JobEntityRuntime.ParallelJob<TJob, TAdapter>` | `SomeEngine.ECS.Systems` | 2 | 15 | `src/SomeEngine.ECS.Systems/JobEntityRuntime.cs` |
| `SomeEngine.ECS.Serialization.DurableSaveStore.OperationLease` | `SomeEngine.ECS.Serialization` | 1 | 1 | `src/SomeEngine.ECS.Serialization/DurableSaveStore.cs` |
| `SomeEngine.ECS.Serialization.DurableSaveStore.SlotSet` | `SomeEngine.ECS.Serialization` | 1 | 2 | `src/SomeEngine.ECS.Serialization/DurableSaveStore.cs` |
| `SomeEngine.ECS.Systems.JobEntityRuntime.PacketCaptureJob<TJob, TAdapter>` | `SomeEngine.ECS.Systems` | 1 | 12 | `src/SomeEngine.ECS.Systems/JobEntityRuntime.cs` |
| `SomeEngine.ECS.Systems.JobEntityRuntime.SerialQueryCaptureJob<TJob, TAdapter>` | `SomeEngine.ECS.Systems` | 1 | 9 | `src/SomeEngine.ECS.Systems/JobEntityRuntime.cs` |
| `SomeEngine.ECS.Serialization.WorldCheckpointCodec` | `SomeEngine.ECS.Serialization` | 0 | 16 | `src/SomeEngine.ECS.Serialization/WorldCheckpointCodec.cs` |

### Rank 16

| 节点 | 程序集 | 入度 | 出度 | 源文件 |
| --- | --- | ---: | ---: | --- |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>` | `SomeEngine.ECS.Systems` | 1 | 25 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs`<br>`src/SomeEngine.ECS.Systems/HierarchyPropagationCapture.cs` |
| `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>.PropagationOwnerJob<TJob>` | `SomeEngine.ECS.Systems` | 1 | 15 | `src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs` |

## 多节点强连通分量

- SCC 47: `SomeEngine.ECS.Collections.SmallList<T>`, `SomeEngine.ECS.Collections.SmallList<T>.Enumerator`
- SCC 51: `SomeEngine.ECS.Indexing.ComponentIndex<TComponent, TKey>`, `SomeEngine.ECS.Indexing.ComponentIndex<TComponent, TKey>.Builder`
- SCC 77: `SomeEngine.ECS.Relations.RelationSchema`, `SomeEngine.ECS.Relations.RelationSchemaCache<T>`
- SCC 165: `SomeEngine.ECS.Archetypes.Archetype`, `SomeEngine.ECS.Archetypes.ArchetypeRegistry`, `SomeEngine.ECS.Archetypes.DetachedTableMap`, `SomeEngine.ECS.Archetypes.StructuralTransition`, `SomeEngine.ECS.BufferWriteExecution<T, TState>`, `SomeEngine.ECS.BufferWriteExecution<T>`, `SomeEngine.ECS.BundleMaterializedRow`, `SomeEngine.ECS.BundleSpawnMap`, `SomeEngine.ECS.BundleWriteAction`, `SomeEngine.ECS.BundleWriteAction<TState>`, `SomeEngine.ECS.BundleWriteRuntime`, `SomeEngine.ECS.BundleWriteView`, `SomeEngine.ECS.Commands.BufferCommandDataList<T>`, `SomeEngine.ECS.Commands.BulkDestroyRelationCommand<T>`, `SomeEngine.ECS.Commands.CommandBuffer`, `SomeEngine.ECS.Commands.CommandBuffer.JobProducerPlaybackBatch`, `SomeEngine.ECS.Commands.CommandDataList<T>`, `SomeEngine.ECS.Commands.CommandEntity`, `SomeEngine.ECS.Commands.CommandHeader`, `SomeEngine.ECS.Commands.CommandPlaybackContext`, `SomeEngine.ECS.Commands.CreateRelationCommand<T>`, `SomeEngine.ECS.Commands.DeferredEntity`, `SomeEngine.ECS.Commands.DeferredEntityCell`, `SomeEngine.ECS.Commands.DeferredRelationEdge<T>`, `SomeEngine.ECS.Commands.DeferredRelationEdgeCell<T>`, `SomeEngine.ECS.Commands.DestroyRelationCommand<T>`, `SomeEngine.ECS.Commands.DestroySubtreeCommand<TDomain>`, `SomeEngine.ECS.Commands.DetachCommand<TDomain>`, `SomeEngine.ECS.Commands.HierarchyCommandWriter<TDomain>`, `SomeEngine.ECS.Commands.IBufferCommandList`, `SomeEngine.ECS.Commands.IComponentCommandList`, `SomeEngine.ECS.Commands.ITypedRelationshipCommand`, `SomeEngine.ECS.Commands.RelationCommandEdge<T>`, `SomeEngine.ECS.Commands.RelationCommandWriter<T>`, `SomeEngine.ECS.Commands.ReorderCommand<TDomain>`, `SomeEngine.ECS.Commands.ReorderRelationCommand<T>`, `SomeEngine.ECS.Commands.RetargetRelationCommand<T>`, `SomeEngine.ECS.Commands.SetOrderPolicyCommand<TDomain>`, `SomeEngine.ECS.Commands.SetParentCommand<TDomain>`, `SomeEngine.ECS.Commands.SetRelationAdjacencyOrderCommand<T>`, `SomeEngine.ECS.Commands.TypedRelationshipCommand`, `SomeEngine.ECS.Components.BufferComponents`, `SomeEngine.ECS.Components.BufferCopier<T>`, `SomeEngine.ECS.Components.BufferRegistry`, `SomeEngine.ECS.Components.BufferRegistry.RegistryState`, `SomeEngine.ECS.Components.IBufferCopier`, `SomeEngine.ECS.DynamicBuffer<T>`, `SomeEngine.ECS.Entities.EntityRecord`, `SomeEngine.ECS.Entities.EntityRecordWriter`, `SomeEngine.ECS.Entities.EntityStore`, `SomeEngine.ECS.Hierarchy.Children<TDomain>`, `SomeEngine.ECS.Hierarchy.Hierarchy<TDomain>`, `SomeEngine.ECS.Hierarchy.Parent<TDomain>`, `SomeEngine.ECS.Hooks.ComponentHooks<T>`, `SomeEngine.ECS.Hooks.DeferredCommandWriter`, `SomeEngine.ECS.Hooks.DeferredWorld`, `SomeEngine.ECS.Hooks.HookAction<T>`, `SomeEngine.ECS.Hooks.HookStore<T>`, `SomeEngine.ECS.Hooks.IHookStore`, `SomeEngine.ECS.IWorldJobAdmission`, `SomeEngine.ECS.JobCommandProducerScope`, `SomeEngine.ECS.Owners.Buffers`, `SomeEngine.ECS.Owners.Bundles`, `SomeEngine.ECS.Owners.Commands`, `SomeEngine.ECS.Owners.Components`, `SomeEngine.ECS.Owners.Copy`, `SomeEngine.ECS.Owners.Copy.ComponentChanges`, `SomeEngine.ECS.Owners.Copy.CopyGuard`, `SomeEngine.ECS.Owners.Copy.CopyRules`, `SomeEngine.ECS.Owners.Copy.CopyShape`, `SomeEngine.ECS.Owners.Copy.ExtraSurface`, `SomeEngine.ECS.Owners.Copy.TableSurface`, `SomeEngine.ECS.Owners.Entities`, `SomeEngine.ECS.Owners.Hierarchy`, `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>`, `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.OrderedChildShard`, `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.PreparedMaintenance`, `SomeEngine.ECS.Owners.HierarchyDomainStore<TDomain>.TopologyImport`, `SomeEngine.ECS.Owners.Hooks`, `SomeEngine.ECS.Owners.Hooks.HookExecutionScope`, `SomeEngine.ECS.Owners.IHierarchyComponentRegistration`, `SomeEngine.ECS.Owners.IHierarchyDomainStore`, `SomeEngine.ECS.Owners.Indices`, `SomeEngine.ECS.Owners.RelationGraph`, `SomeEngine.ECS.Owners.RelationGraph.IRelationEndpointTracker`, `SomeEngine.ECS.Owners.RelationGraph.RelationEndpointTracker<T>`, `SomeEngine.ECS.Owners.Shared`, `SomeEngine.ECS.Owners.Sparse`, `SomeEngine.ECS.Owners.Tables`, `SomeEngine.ECS.Owners.Tables.ChunkCapacity`, `SomeEngine.ECS.Queries.ChunkRowEnumerator`, `SomeEngine.ECS.Queries.ChunkRowIndexEnumerator`, `SomeEngine.ECS.Queries.IChunkFilter`, `SomeEngine.ECS.Queries.NoSharedFilter`, `SomeEngine.ECS.Queries.QueryAccessGuards`, `SomeEngine.ECS.Queries.QueryArchetypeMatch`, `SomeEngine.ECS.Queries.QueryChunkEnumerator<TFilter>`, `SomeEngine.ECS.Queries.QueryChunkView`, `SomeEngine.ECS.Queries.QueryCursor`, `SomeEngine.ECS.Queries.QueryDefinition`, `SomeEngine.ECS.Queries.QueryDefinitionBuilder`, `SomeEngine.ECS.Queries.QueryExecution`, `SomeEngine.ECS.Queries.QueryExecution<TState>`, `SomeEngine.ECS.Queries.QueryPairEnumerator<TWrite, TRead>`, `SomeEngine.ECS.Queries.QueryPairExecution<TWrite, TRead, TState>`, `SomeEngine.ECS.Queries.QueryPairExecution<TWrite, TRead>`, `SomeEngine.ECS.Queries.QueryRecord`, `SomeEngine.ECS.Queries.QueryRegistry`, `SomeEngine.ECS.Queries.QueryRow`, `SomeEngine.ECS.Queries.QueryRowCursor`, `SomeEngine.ECS.Queries.QueryRowEnumerator<TFilter>`, `SomeEngine.ECS.Queries.QuerySharedFilter`, `SomeEngine.ECS.Queries.QueryState`, `SomeEngine.ECS.Queries.QueryState.QueryMatchBuilder`, `SomeEngine.ECS.Queries.QueryableTypeInfo`, `SomeEngine.ECS.Queries.ReadWriteMatch`, `SomeEngine.ECS.Queries.SingleSharedFilter`, `SomeEngine.ECS.Registry.ComponentInfo`, `SomeEngine.ECS.Registry.ComponentMetadata<T>`, `SomeEngine.ECS.Registry.ComponentRegistry`, `SomeEngine.ECS.Registry.PublicComponentMutationGuard`, `SomeEngine.ECS.Relations.IRelationTypeState`, `SomeEngine.ECS.Relations.RelationEndpointAccess`, `SomeEngine.ECS.Relations.RelationTypeSlotTable`, `SomeEngine.ECS.Relations.RelationTypeSlotTable.Enumerator`, `SomeEngine.ECS.Relations.RelationTypeState<T>`, `SomeEngine.ECS.RestrictedWorldApiScope`, `SomeEngine.ECS.Serialization.HierarchyTopologyWriteAccess<TDomain>`, `SomeEngine.ECS.Serialization.RelationTopologyImport<T>`, `SomeEngine.ECS.Serialization.RelationTopologyImport<T>.OrderedSequence`, `SomeEngine.ECS.Serialization.RelationTopologyWriteAccess<T>`, `SomeEngine.ECS.SerializationValidationScope`, `SomeEngine.ECS.StructuralMutationScope`, `SomeEngine.ECS.World`, `SomeEngine.ECS.World.ReadSnapshotCallbackScope`, `SomeEngine.ECS.World.SerializationReadRootContext`, `SomeEngine.ECS.World.SerializationReadRootScope`, `SomeEngine.ECS.World.SerializationWriteLifetimeScope`, `SomeEngine.ECS.World.StructuralCandidateContext`, `SomeEngine.ECS.World.StructuralCandidateScope`, `SomeEngine.ECS.World.StructuralTransactionScope`, `SomeEngine.ECS.WorldJobAdmissionScope`, `SomeEngine.ECS.WorldStructurePublication`, `SomeEngine.ECS.WorldStructureRoot`
- SCC 202: `SomeEngine.ECS.Serialization.BufferSerializationRuntime<T>`, `SomeEngine.ECS.Serialization.BufferSerializationRuntime<T>.BufferApplyState`, `SomeEngine.ECS.Serialization.ComponentSerializationRuntime<T>`, `SomeEngine.ECS.Serialization.SerializationRegistry`, `SomeEngine.ECS.Serialization.SerializationTypeRuntime`, `SomeEngine.ECS.Serialization.SharedSerializationRuntime<T>`, `SomeEngine.ECS.Serialization.SparseSerializationPresence`, `SomeEngine.ECS.Serialization.SparseSerializationRuntime<T>`, `SomeEngine.ECS.Serialization.TagSerializationRuntime<T>`, `SomeEngine.ECS.Serialization.ValueSerializationRuntime<T>`
- SCC 223: `SomeEngine.ECS.Serialization.PayloadFormat`, `SomeEngine.ECS.Serialization.SerializationEnvironment`, `SomeEngine.ECS.Serialization.TopologyCodec`, `SomeEngine.ECS.Serialization.WorldSerializer`, `SomeEngine.ECS.Serialization.WorldSerializer.WorldImporter`, `SomeEngine.ECS.Serialization.WorldSerializer.WorldRestorer`
- SCC 224: `SomeEngine.ECS.Serialization.DurableSaveStore`, `SomeEngine.ECS.Serialization.DurableSaveStore.OperationLease`, `SomeEngine.ECS.Serialization.DurableSaveStore.SlotSet`
- SCC 241: `SomeEngine.ECS.SourceGen.SerializationGenerator.FieldModel`, `SomeEngine.ECS.SourceGen.SerializationGenerator.SerializableModel`
- SCC 248: `SomeEngine.ECS.Systems.RelationshipJobAccess`, `SomeEngine.ECS.Systems.WorldJobAdmission`, `SomeEngine.ECS.Systems.WorldStorageJobResources`
- SCC 260: `SomeEngine.ECS.Systems.HierarchyJobAccess<TDomain>`, `SomeEngine.ECS.Systems.HierarchyJobAccess<TDomain>.ParentChunkJobAdapter<TJob>`, `SomeEngine.ECS.Systems.HierarchyJobAccess<TDomain>.ParentReadChunkJobAdapter<TJob>`
- SCC 283: `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>`, `SomeEngine.ECS.Systems.HierarchyPropagationAdapter<TDomain>.PropagationOwnerJob<TJob>`
- SCC 306: `SomeEngine.ECS.Systems.JobCommandBuffer`, `SomeEngine.ECS.Systems.JobCommandBuffer.CompletionAdapter`, `SomeEngine.ECS.Systems.JobCommandBuffer.ParallelProducerAdapter<TProducer>`, `SomeEngine.ECS.Systems.JobCommandBuffer.PublicationAdapter`, `SomeEngine.ECS.Systems.JobCommandBuffer.SerialProducerAdapter<TProducer>`
- SCC 314: `SomeEngine.ECS.Systems.JobEntityRuntime`, `SomeEngine.ECS.Systems.JobEntityRuntime.PacketCaptureJob<TJob, TAdapter>`, `SomeEngine.ECS.Systems.JobEntityRuntime.ParallelJob<TJob, TAdapter>`, `SomeEngine.ECS.Systems.JobEntityRuntime.SerialQueryCaptureJob<TJob, TAdapter>`
- SCC 320: `SomeEngine.ECS.Systems.ReadOnlyQueryPacketJobs`, `SomeEngine.ECS.Systems.ReadOnlyQueryPacketPlan`
- SCC 321: `SomeEngine.ECS.Systems.RelationJobAccess<T>`, `SomeEngine.ECS.Systems.RelationJobAccess<T>.DirectedEndpointChunkJobAdapter<TJob>`, `SomeEngine.ECS.Systems.RelationJobAccess<T>.DirectedEndpointReadChunkJobAdapter<TJob>`, `SomeEngine.ECS.Systems.RelationJobAccess<T>.UndirectedEndpointChunkJobAdapter<TJob>`, `SomeEngine.ECS.Systems.RelationJobAccess<T>.UndirectedEndpointReadChunkJobAdapter<TJob>`
- SCC 337: `SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>`, `SomeEngine.ECS.Systems.TopologyPacketFinalizer<TDomain>.CaptureAndScheduleJob<TJob>`
