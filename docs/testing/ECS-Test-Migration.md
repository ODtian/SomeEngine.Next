# ECS 行为测试迁移（#90）

来源：[规格 #82](https://github.com/ODtian/SomeEngine.Next/issues/82)、[任务 #90](https://github.com/ODtian/SomeEngine.Next/issues/90)、[行为检查规则](../../wiki/harness/Behavior-Checks.md)。基线：e2854029172b6fb4b344e30ebb2c33b84421bb05。

ECS 与 ECS.Systems 的测试从公开 API 取得结果。两项目的友元授权与规则白名单已移除，每个测试持有自己的调度器、世界、回调数据和同步对象。程序集使用四路并行。

## 实现与覆盖对照

| 原访问或断言 | 公开行为与理由 |
| --- | --- |
| 内部注册、Chunk、集合与 proof/range 构造 | 通过世界、实体、组件、查询、typed hierarchy/relation、调度返回的分区以及命令回放覆盖。仅实现形状的构造、容量、缓存和算法计数测试清理。 |
| 内部当前版本与列版本数组 | 公共 ExecuteQuery 自动取得当前版本；Changed/Added/Removed 选择、读取的行值与世界写版本验证调用方结果。 |
| JobSystem.Initialize 与静态回调状态 | 调度器和世界按测试创建；生命周期上下文显式持有调度器；unmanaged 回调使用本测试分配的 GCHandle 数据，作业结束后释放。 |
| 异常消息匹配 | 引擎失败断言异常类型；作业与销毁回调的注入错误用 TestFault.Code 区分。 |
| 毫秒窗口及线程调度状态判定 | 开始信号、调用方观察到的关闭状态和事件队列证明先后顺序；所有超时用作挂起上限，finally 释放并等待参与者。 |
| ECS 内部性能形状及耗时阈值 | BulkBundleTests 验证 100,000 行值与批量/标量等价结果；[ECS measurements](../../benchmarks/SomeEngine.ECS.Benchmarks/README.md) 继续提供独立测量入口。 |

独立调度器验证覆盖了 ReadOnlyQueryPacketJobs 的命令录制：私有、不可变的描述数组由同步查询持有至录制完成，与普通 packet 执行使用同一持有方式。外部输出资源仍由调用方声明，返回的 JobCommandBuffer 持有复制后的命令数据。重复执行、输出比较和命令回放用例验证此行为。

## 验收

| 命令 | 结果 |
| --- | --- |
| dotnet test tests/SomeEngine.ECS.Tests/SomeEngine.ECS.Tests.csproj --no-restore --verbosity quiet | 445 项通过 |
| dotnet test tests/SomeEngine.ECS.Systems.Tests/SomeEngine.ECS.Systems.Tests.csproj --no-restore --verbosity quiet | 278 项通过 |
| 上述两个项目分别使用 --no-build --no-restore --verbosity quiet，连续五轮 | 每轮 445 + 278 项通过；3,615 次检查通过 |
| dotnet msbuild tests/SomeEngine.ECS.Tests/SomeEngine.ECS.Tests.csproj -t:CheckTestRules -v:quiet | 存量白名单 0 项、新违规 0 项 |
| dotnet msbuild tests/SomeEngine.ECS.Systems.Tests/SomeEngine.ECS.Systems.Tests.csproj -t:CheckTestRules -v:quiet | 存量白名单 0 项、新违规 0 项 |
| dotnet test tests/SomeEngine.ECS.SourceGen.Tests/SomeEngine.ECS.SourceGen.Tests.csproj --verbosity quiet | 39 项通过 |
| dotnet test tests/SomeEngine.ECS.Fuzz.Tests/SomeEngine.ECS.Fuzz.Tests.csproj --verbosity quiet | 17 项通过 |
| dotnet test tests/SomeEngine.TestRules.Tests/SomeEngine.TestRules.Tests.csproj --verbosity quiet | 51 项通过 |

连续五轮为最后一版代码的四路并行运行。定位过程中发现的关闭夹具等待已通过显式同步和 finally 清理修复；定位序列文件已清理。

## 逐文件、逐用例对照

以下清单供 PR 描述引用。“原名”列包含删除或改名的用例；“保留原名并改写”列列出动作、公开访问或断言的修改。“现有覆盖”说明删除理由与覆盖入口。

### ArchetypeRegistryTests.cs

现有覆盖：注册表与边缓存仅由库持有；World、Bundle、查询选择与实体迁移提供公共覆盖。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `AllArchetypes_ReturnsAll`<br>`GetOrCreateAddEdge_FirstCall_Computes`<br>`GetOrCreateRemoveEdge_FirstCall_Computes`<br>`GetOrCreate_CreatesNewArchetype`<br>`InsertSorted_AtBeginning`<br>`InsertSorted_AtEnd`<br>`InsertSorted_InsertsInOrder`<br>`RemoveSorted_NotFound_Throws`<br>`RemoveSorted_RemovesCorrectly` |

### ArchetypeTests.cs

现有覆盖：类型构造、列排列、容量与存储分组属于实现；组件、标签、共享值和查询结果提供公共覆盖。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `ArchetypeId_IsAssigned`<br>`Archetype_ComponentIds_AreSorted`<br>`Archetype_TableComponentIds_ExcludesTags`<br>`Archetype_TagOnly_NoTableColumns`<br>`ColumnOperations_ParallelTableColumns`<br>`EntityCapacityPerChunk_MinimumIsOne`<br>`EntityCapacityPerChunk_TagOnly_UsesEntityRowSize`<br>`EntityCapacityPerChunk_TwoFloat2Components`<br>`GetColumnIndex_ReturnsCorrectIndex`<br>`GetColumnIndex_Tag_Throws`<br>`HasComponent_ExistingComponent_ReturnsTrue`<br>`HasComponent_NonExistent_ReturnsFalse`<br>`HasComponent_Tag_ReturnsTrue`<br>`StableHash_Deterministic`<br>`StableHash_DifferentInputs_DifferentHashes`<br>`TryGetColumnIndex_Existing_ReturnsTrue`<br>`TryGetColumnIndex_NonExistent_ReturnsFalse`<br>`TypeIdHash_DifferentInput_DifferentHash`<br>`TypeIdHash_SameInput_SameHash` |

### BundleAddTests.cs

现有覆盖：保留组件值与存在断言，清理内部类型计数。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `AddThreeComponents`<br>`AddTwoComponents`<br>`SingleAddWorks` |

### BundleExecutionTests.cs

现有覆盖：保留失败回滚、组件值与实体数量断言，清理内部类型计数。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `ExecuteBundleSpawn_CallbackFaultDiscardsCandidateAndReleasesRuntime` |

### BundleTests.cs

现有覆盖：内部 Chunk 与空余行数改为公开共享值查询和已有实体下的额外批量创建结果。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `ReserveBundle_WithExistingOccupancy_ReservesAdditionalFreeRows` |
| 保留原名并改写 | `Spawn_BundleWithSharedComponent_RoutesInitialChunkAndQueryFilter` |
| 当前新增或改名 | `ReservedBundleCanSpawnAdditionalRowsWithExistingEntities` |

### ChangeTrackingTests.cs

现有覆盖：内部 VersionClock 算术测试清理；公共写版本、Changed/Added/Removed 的选择与行值保留，移除事实通过公开查询计数。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `CoarseVersionPublicationUsesClockWrapOrdering`<br>`VersionIsNewer_WrapAroundHandling` |
| 保留原名并改写 | `AcquireSystemVersion_ReturnsTheNewlyAdvancedWriteVersion`<br>`Added_Filter_IncludesRecentlyAdded`<br>`ChangedSameRow`<br>`Changed_Filter_IncludesRecentlyModified`<br>`Changed_Filter_SkipsUnchangedChunks`<br>`ChunkSpanRejectsRowFilter`<br>`QueryReadWriteRef_BumpsChangeVersion`<br>`RemoveReaddRemoveBeforeClear_RefreshesOneRemovedFact` |

### ChunkTests.cs

现有覆盖：直接创建 Chunk 并断言容量、列数组与换行存储属于实现；World、DynamicBuffer、EntityCopy、启用状态和变化查询保留数据行为。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `AllocateRow_IncreasesCount`<br>`AllocateRow_SequentialRows`<br>`Capacity_Minimum_IsOne`<br>`Capacity_PositionVelocity_IncludesRowVersions`<br>`GetComponentRef_ModifiesInPlace`<br>`IsFull_WhenCountEqualsCapacity`<br>`RemoveRow_LastRow_ReturnsNull`<br>`RemoveRow_ManagedComponent_ClearsTrailingSlot`<br>`RemoveRow_MiddleRow_SwapsLastToRemoved`<br>`RemoveRow_MultipleColumns_AllConsistent`<br>`RemoveRow_OnlyRow_CountBecomesZero`<br>`WriteRead_Managed_Roundtrip`<br>`WriteRead_Unmanaged_Roundtrip` |

### CommandBufferAtomicityTests.cs

现有覆盖：直接写库内存储的候选钩子场景清理；CommandBufferTests、BundleExecutionTests、TypedRelationshipCommandBufferTests 保留公开事务成功、失败、钩子故障和关系回滚覆盖；保留用例检查句柄与实体结果。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `FailedPlayback_PreservesAllocatorFrontierFreeListOrderAndGenerations`<br>`LaterFailure_DiscardsEveryCandidateOwnedMutation`<br>`PublishedSnapshotSpans_RemainStableAcrossSuccessAndFailure`<br>`ReaderDuringBlockedCandidate_SeesOnlyOldPublishedRootUntilPublication`<br>`SuccessfulPlayback_PublishesEveryCandidateOwnedMutationTogether` |
| 保留原名并改写 | `DeferredHandles_AreUnresolvedInsideHookAndResolveOnlyAfterPublication`<br>`FailedDeferredHandles_RemainPermanentlyInvalidAfterLaterPublication`<br>`PublishedHookWaveSealsOldWorldBufferAndReacquireRecordsALaterWave` |
| 当前新增或改名 | `FailedPlaybackPreservesLiveEntitiesAndInvalidatesCreatedHandles` |

### CommandBufferTests.cs

现有覆盖：保留异常类型及命令生命周期结果，清理消息匹配。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `CustomBufferRejectsRecordingAfterPlaybackUntilClear`<br>`WorldOwnedBufferRejectsDirectLifecycleAndRemainsFlushable` |

### ComponentRegistryTests.cs

现有覆盖：内部 ComponentInfo/ComponentOperations 的复制、移除由 ComponentStorageTests 的实体复制和销毁覆盖；公共元数据仍由 ComponentMetadataTests 验证。

当前文件：[ComponentStorageTests.cs](../../tests/SomeEngine.ECS.Tests/ComponentStorageTests.cs)。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `CreateArray_ManagedType_ReturnsCorrectType`<br>`CreateArray_ReturnsCorrectTypeAndLength`<br>`Get_ReturnsCorrectInfo_ForPosition`<br>`Get_ReturnsCorrectInfo_ForTag`<br>`Get_ThrowsOnInvalidId_Negative`<br>`Get_ThrowsOnInvalidId_TooLarge`<br>`Get_ThrowsOnInvalidId_Zero`<br>`RefCopy_ManagedWithNullString_CopiesCorrectly`<br>`RefCopy_ManagedWithString_CopiesCorrectly`<br>`RefCopy_UnmanagedPosition_CopiesCorrectly`<br>`SwapRemove_ManagedComponent_ClearsLastSlot`<br>`SwapRemove_SameIndex_NoMove`<br>`SwapRemove_UnmanagedPosition_MovesLastToRemoved` |
| 当前新增或改名 | `CopyEntityPreservesManagedComponentValues`<br>`CopyEntityPreservesNullManagedValues`<br>`DestroyEntityPreservesOtherManagedComponents` |

### DynamicBufferTests.cs

现有覆盖：内部 ReplaceWith 改为公共 Clear/Add，保留内联、溢出、清空及迁移后的内容；查询版本由公共入口取得。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `World_BufferReplaceWith_ReplacesInlineAndOverflowContents` |
| 保留原名并改写 | `ChangedBuffer_ReturnsOnlyWrittenInlineRowFromSharedChunk`<br>`ChangedBuffer_ReturnsOnlyWrittenOverflowRowFromSharedChunk`<br>`ChangedBuffer_RowVersionSurvivesStructuralRowMove`<br>`World_BufferReadOnlyAccess_DoesNotBumpChangeVersion`<br>`World_BufferWriteAccess_BumpsChangeVersion` |
| 当前新增或改名 | `BufferClearAndAddReplaceInlineAndOverflowContents` |

### EnableableComponentTests.cs

现有覆盖：固定分块容量断言改为 513 个实体的启用查询选择结果。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `Archetype_WithEnableableColumn_IsCappedTo128EntitiesPerChunk` |
| 当前新增或改名 | `EnabledQuerySelectsEnabledEntitiesAcrossLargePopulation` |

### EntityStoreTests.cs

现有覆盖：句柄通过 World 创建、销毁与重新创建取得；EntityIdentityTests 保留相等、代数变化、哈希与字典键行为。

当前文件：[EntityIdentityTests.cs](../../tests/SomeEngine.ECS.Tests/EntityIdentityTests.cs)。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `Entity_CanBeUsedAsDictionaryKey`<br>`Entity_Equals_SameValues`<br>`Entity_GetHashCode_ConsistentForEqualValues`<br>`Entity_NotEquals_DifferentGeneration`<br>`Entity_NotEquals_DifferentIndex` |

### HierarchyModelTests.cs

现有覆盖：内部域注册与存储计数清理；层级公共 parent/children、未知域默认值、并发查询和子树销毁保留。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `ComponentIdFirstAccess_UsesTypedRegistrationForParentAndChildren`<br>`LargeSubtreeDestroy_UsesConstantCanonicalParentPasses`<br>`UnknownDomainScalarReads_DoNotMaterializePersistentDomainState` |
| 保留原名并改写 | `ConcurrentUnknownDomainReads_DoNotRegisterOrRaceWithKnownDomainRegistration`<br>`OrdinaryDestroy_UsesAppliedAndDirtyLocalIndicesWithoutLosingChildren`<br>`ParentAndChildren_UseNativeAddedChangedRemovedFacts` |
| 当前新增或改名 | `DestroySubtreeRemovesEveryDescendant`<br>`UnknownDomainScalarReadsReturnEmptyParentAndDefaultOrder` |

### PerformanceRegressionTests.cs

现有覆盖：批量实体值与标量等价性改为 BulkBundleTests；容量、内部提交计数与耗时阈值清理。ECS 测量继续使用 benchmarks/SomeEngine.ECS.Benchmarks 的独立入口。

当前文件：[BulkBundleTests.cs](../../tests/SomeEngine.ECS.Tests/BulkBundleTests.cs)。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `ExecuteBundleSpawnBatch_AmortizesStructuralPublicationAgainstScalarCreate`<br>`ExecuteBundleSpawnBatch_ReservedOneComponent_KeepsChunkShape`<br>`ExecuteBundleSpawnBatch_ReservedOneComponent_RespectsOptInLocalBudget`<br>`ExecuteBundleSpawnBatch_ReservedThreeComponents_KeepsChunkShape` |
| 当前新增或改名 | `BatchAndScalarCreationProduceEquivalentRows`<br>`ReservedMultiComponentBatchCreatesEveryRequestedValue`<br>`ReservedSingleComponentBatchCreatesEveryRequestedValue` |

### QueryCursorIterationTests.cs

现有覆盖：内部列版本数组改为 Changed 查询选择；公共行与分块读写、混合访问和版本上下文保留。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `QueryCursor_ChunkEarlyBreak_ReleasesGuard`<br>`ReadWriteRead_SelectiveBump`<br>`ReadWrite_Bumps_ChangeVersion`<br>`Read_DoesNotBump_ChangeVersion` |
| 保留原名并改写 | `QueryCursor_ChunkRows_PreserveRunVersions` |
| 当前新增或改名 | `MixedQueryChangesOnlyItsWritableComponent`<br>`QueryCursorEarlyBreakAndFollowingMutationDoesNotThrow`<br>`ReadOnlyQueryPreservesChangedFilterBaseline`<br>`WritableQueryMakesItsComponentVisibleToChangedFilter` |

### QueryModelTests.cs

现有覆盖：内部访问缓存清理；公开查询的句柄有效性、变化筛选、组件存在和写入结果保留，当前版本交由公共执行入口取得。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `JobAdmissionAccessSet_CachesRelationshipWriteClassification`<br>`JobAdmissionAccessSet_TreatsChangeAndEnableFiltersAsDataReads` |
| 保留原名并改写 | `DirectBufferElementQuery_IsRejectedWithClearError`<br>`PresenceOnly_All_DoesNotBumpChangeVersion`<br>`QueryLifetimeMutation_IsRejectedInsideStructuralTransaction`<br>`ReadAccess_DoesNotBump_AndWriteAccess_BumpsOnceReached`<br>`ReleaseQuery_InvalidatesEveryHandleCopyAndReusesSlotGeneration`<br>`ReleaseQuery_KeepsIndependentAcquisitionOfInternedDefinitionAlive` |

### RelationEdgeModelTests.cs

现有覆盖：保留端点、邻接、存活和不可变读取结果，清理库内注册及派生索引状态。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `DestroyingEmptyOrderedEndpoint_RetiresItsDerivedShardState` |
| 保留原名并改写 | `EndpointsAndAdjacency_UseNativeAddedChangedRemovedFacts`<br>`UnregisteredEdgesBetweenLookup_DoesNotReadLivenessOrRegisterRelationState` |
| 当前新增或改名 | `DestroyingEmptyOrderedEndpointDoesNotThrow` |

### RelationshipComponentProtectionTests.cs

现有覆盖：内部添加伪造关系组件改为公共 Hierarchy 的 Parent/Children 夹具；保留全部 mutation/copy/command/bundle 拒绝行为与公共元数据。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `CommandAndBundleEntryPoints_RejectRelationshipMutationBeforeRecordingOrSpawning`<br>`EntityCopy_RejectsRelationshipComponentsInSelectedTableSurface`<br>`WorldGenericMutation_RejectsSourceAndTarget_ButAllowsRead` |
| 保留原名并改写 | `PublicHooks_CannotReplaceRelationshipLifecycleMaintenance` |
| 当前新增或改名 | `CommandAndBundleRelationshipMutationsFailBeforeRecording`<br>`EntityCopyRequiresExcludingRelationshipTableComponents`<br>`GenericRelationshipMutationRequiresTypedHierarchyApi` |

### SmallListTests.cs

现有覆盖：库内集合的布局与扩容由 World、查询和批量组件行为间接覆盖。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `SmallList_Clear_AllowsReuse`<br>`SmallList_DefaultValue_UsesInlinePathAndEnumeratesInOrder`<br>`SmallList_FourthItem_UsesOverflowPathButPreservesOrder`<br>`SmallList_Indexer_AllowsInPlaceMutationForValueTypes`<br>`SmallList_RemoveAt_PreservesOrder_ForInlineAndOverflow`<br>`SmallList_RemoveStable_RemovesByValueAndPreservesOrder`<br>`SmallList_RemoveSwapBack_RemovesByValueWithoutPreservingOrder` |

### SortedValueKeyTests.cs

现有覆盖：库内排序键由等价查询定义、组件排序和共享值选择间接覆盖。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `SortedValueKey_CanBeUsedAsDictionaryKey`<br>`SortedValueKey_ConstructorOwnsStableCopy`<br>`SortedValueKey_Equal_SameIds`<br>`SortedValueKey_NotEqual_DifferentIds`<br>`StableHash_Deterministic`<br>`StableHash_DifferentInputs_DifferentHashes` |

### SparseSetAndQueryTests.cs

现有覆盖：内部 SparseSet 构造与密集存储断言由 WorldSparseTests 的增删、复制、读写和查询结果覆盖。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `Add_Duplicate_Throws`<br>`Add_GrowsFromZeroInitialCapacity`<br>`Add_Has_Get`<br>`BulkAddRemove_DataConsistency`<br>`Count_TracksAddRemove`<br>`DenseEntities_DenseData_Correct`<br>`Get_Ref_ModifiesInPlace`<br>`HighIndexEntity_AllocatesPage`<br>`NullEntity_Add_Throws`<br>`NullEntity_GetAndRemove_Throw`<br>`NullEntity_Has_ReturnsFalse`<br>`Read_ReturnsCopy`<br>`Remove_HasBecomesFalse`<br>`Remove_NonExistent_Throws`<br>`StaleGeneration_HasReturnsFalse`<br>`SwapRemove_MovedEntityStillAccessible` |

### TypedRelationshipCommandBufferTests.cs

现有覆盖：保留命令行为和句柄有效性；分配器构造与算法工作次数改为公共结果或显式不抛出命名。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `AlternatingDeferredAndImmediateCommands_ValidateDirtyImageOnceAtBatchEnd`<br>`DirtyProjectionSynchronization_ScansOnceAcrossUnrelatedImmediateCommands` |
| 保留原名并改写 | `OrderedEmptyEndpointDestroyedInSameBatch_DropsOverlayBeforeIndexReuse` |
| 当前新增或改名 | `AlternatingRelationshipCommandPlaybackDoesNotThrow`<br>`MixedRelationshipCommandPlaybackDoesNotThrow` |

### WorldJobFailClosedTests.cs

现有覆盖：世界通过作业参数传入，调度器按测试创建；保留 raw Job 的拒绝与世界状态。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `RawJobFailsClosedWhenAdmissionIntegrationWasNeverLoaded` |

### WorldReadSnapshotTests.cs

现有覆盖：读取与写入采用每测试同步对象、尝试开始点和记录的事件顺序；保留嵌套读取及公开 mutation 拒绝结果。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `ReadSnapshot_BlocksAWriterUntilItsCallbackReturns`<br>`ReadSnapshot_RejectsExactWorldMutationAndAllowsNestedReads`<br>`ReadSnapshot_WaitsForAnAdmittedWriterAndReadsItsPublishedValue` |

### WorldTests.cs

现有覆盖：内部类型分组与容量改为公开查询、组件值和大量实体销毁后创建行为。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `ChunkRecycle_ThenCreate_ReallocatesCorrectly`<br>`Spawn_SortBundle_UsesSameArchetype` |
| 当前新增或改名 | `CreateEntityAfterLargePopulationDeletionPreservesValues`<br>`SpawnBundleMatchesQueriesInComponentOrder` |

### BufferJobAccessTests.cs

现有覆盖：按测试持有 Scheduler/World，保留原 worker 数、安全模式、载荷策略和公共行为结果；失败检查异常类型，时序通过同步与事件顺序验证。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `BufferCallbackInsideJob_RejectsMissingLogicalResource`<br>`DifferentBufferTypesAndWorlds_DoNotShareLogicalResource`<br>`FaultedBufferBody_ReleasesIterationAndSynchronousResourceOwner`<br>`RepeatedBufferAccessInvokesEveryCallback`<br>`SameWorldSameBuffer_ReadOwnersCanRunConcurrently`<br>`SameWorldSameBuffer_WriteOwnerBlocksReadersAndWriters`<br>`TopologyWriteHook_CanBorrowBufferAfterLaterBufferOwnerWasRegistered` |

### ComponentJobAccessTests.cs

现有覆盖：按测试创建 Scheduler/World，保留 worker 数、安全模式和载荷策略；记录等待返回与后续回调顺序，直接线程增加尝试开始信号。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `BufferQuery_StillUsesOneBufferLogicalCapability`<br>`RepeatedDirectComponentAccessCompletes` |
| 保留原名并改写 | `Clock_AcquireIsUniqueAndMonotonicAcrossThreads`<br>`DifferentComponentTypesAndWorlds_DoNotShareTypedResource`<br>`DirectComponentEntryInsideJob_RejectsMissingTopologyCapability`<br>`DirectComponentEntryInsideJob_RejectsMissingTypedCapability`<br>`DirectReplace_HoldsTypedOwnerThroughHookAndBlocksScheduledReader`<br>`HasInsideJob_RequiresOnlyTopologyRead`<br>`OrdinaryTableQuery_UsesMatchingTypedReadAndWriteCapabilities`<br>`ReadCapability_DoesNotAuthorizeComponentWrite`<br>`RelationshipSchedulers_UseTheSameTableComponentKeys`<br>`SameComponentIrrelevantHookEvents_DoNotUpgradeValueReplaceToTopologyWrite`<br>`SameWorldSameComponent_ReadOwnersRunConcurrently`<br>`SameWorldSameComponent_WriteOwnerBlocksReadersAndWriters`<br>`ScheduledWriter_BlocksDirectReadUntilScheduledOwnerReturns`<br>`UnrelatedComponentHook_DoesNotUpgradeValueReplaceToTopologyWrite`<br>`ValueReplaceHook_RequiresExplicitTopologyWrite` |
| 当前新增或改名 | `BufferQueryWithDeclaredAccessDoesNotThrow`<br>`RepeatedDirectComponentAccessDoesNotThrow` |

### GeneratedJobEntityTests.cs

现有覆盖：静态探针改为按测试持有并释放的 CallbackData；保留公共生成 Job 的并发、命令录制限制、拓扑等待和行写入行为；版本一致性通过公共时钟与 Changed 查询覆盖。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `GeneratedSerialAndParallelJobsRejectSharedCommandBufferEscapeBeforeRecording`<br>`ScheduleParallel_FirstCowWriteDetachesSharedChunkOnceForAllPackets`<br>`ScheduleParallel_HoldsTopologyLeaseAcrossAllPacketBodies`<br>`ScheduleParallel_UsesOneExecutionVersionAcrossAllPacketsAndTableBufferStorage`<br>`ScheduleParallel_WritesDisjointPacketsConcurrently` |
| 保留原名并改写 | `ChunkChangedFilter_WithSameFamilyDirectWriteIsExplicitlySerialOnly`<br>`ColdBufferDescriptor_CompilesLogicalBufferAdmissionBeforeAnyBufferExists`<br>`Descriptor_RejectsReferenceBearingDirectStorageEvenForSerialScheduling`<br>`EmptySerialAndParallelJobs_DoNotAcquireAnExecutionVersion`<br>`EnabledFilter_OwnsPacketBitRangesAndSkipsDisabledRows`<br>`ExactChangedFilter_RunsAfterRangeHazardsAndBeforeCurrentRowWrite`<br>`ExplicitFilter_ComposesSelectionWithoutAddingValueAccess`<br>`FullyChunkFilteredParallelPackets_DoNotAcquireAnExecutionVersion`<br>`FullyRowFilteredSerialAndParallelJobs_DoNotAcquireAnExecutionVersion`<br>`MatchingReadOnlySerialAndParallelJobs_DoNotAcquireAnExecutionVersion`<br>`RuntimeBoundaryHoist_StillRejectsAnUndeclaredSparseBorrow`<br>`ScheduleParallel_BufferWrapperBorrowsOnlyCurrentPacketRows`<br>`ScheduleParallel_CapturesAfterDynamicTopologyWriterDescendant`<br>`ScheduleParallel_RegisteredTopologySuccessorCannotSilentlyInvalidateCapturedPackets`<br>`ScheduleParallel_ResolvesPacketsAfterSemanticDependencyWithoutBlockingIntermediateTopology`<br>`ScheduleParallel_SparseWrapperFiltersAndWritesRequiredRows`<br>`Schedule_RegistersQueryResourcesAfterDynamicTopologyAndDataWriterDescendant`<br>`SerialCanonicalParentWriter_UsesWorldFinalizerAndHasNoParallelSurface` |
| 当前新增或改名 | `GeneratedCallbacksFinishBeforeStructuralMutation`<br>`GeneratedCommandRecordingRequiresAJobCommandWriter`<br>`GeneratedParallelWritesEachMatchingEntityOnce`<br>`GeneratedParallelWritesPreserveValuesAfterCommandPlayback`<br>`GeneratedStorageWritesShareOneWorldWriteVersion` |

### HierarchyMaintenanceSystemTests.cs

现有覆盖：按测试持有 Scheduler/World，保留原 worker 数、安全模式、载荷策略和公共行为结果；失败检查异常类型，时序通过同步与事件顺序验证。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `ImmutableChildrenSnapshot_IsSafeWithoutALifetimeResourceOwner`<br>`OwnerBoundParentMutation_RejectsMissingOrReadOnlyCapability`<br>`OwnerBoundParentMutation_RejectsMultiBatchParallelOwner`<br>`RejectManagedPayloadPolicy_IsHonoredAndNotMutatedBySchedule`<br>`RelaxedReaderSeesLastApplied_AndFreshReaderDependsOnMaintenanceHandle`<br>`ReplacingTheDefaultSchedulerDoesNotRebindAnExplicitWorld`<br>`TopologyWrites_SerializeAcrossDomainsButNotWorlds` |

### HierarchyPropagationRestrictedApiTests.cs

现有覆盖：库内部伪造能力的空传播测试由公共 ComponentJobAccess/GeneratedQueryAccessDescriptor 的拒绝覆盖；每种受限回调逃逸仍独立验证，状态改为 CallbackData。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `EmptyPropagation_StillValidatesDeclaredComponentCapabilities` |
| 保留原名并改写 | `ExplicitSuccessor_DoesNotFormARingWithDeferredPropagationPackets`<br>`PropagationCallback_RejectsEveryOrdinaryWorldEscapeBeforeSideEffects` |

### JobBufferCommandTests.cs

现有覆盖：按测试持有 Scheduler/World，保留原 worker 数、安全模式、载荷策略和公共行为结果；失败检查异常类型，时序通过同步与事件顺序验证。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `CommandBufferOwnsAddReplaceAndRemovePayloads`<br>`FailedBufferCommandRollsBackTheCompleteStructuralImage`<br>`ParallelJobSegmentsPublishPipelineOwnedBuffersByStableProducerKey` |

### JobCommandBufferTests.cs

现有覆盖：调度器与世界按测试持有；保留 producer 合并、依赖故障、命令回放、未解析句柄和结构化异常断言，清理内部邻接诊断。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `DeferredUniqueTargetSwapAcrossSegmentsValidatesTheMergedFinalImage`<br>`EmptyBatchConsumesNoTopologyRevision`<br>`FaultedProducerDependencyStillRunsCleanupAndReleasesPlaybackOwnership`<br>`FaultedTopologyPredecessorStillRunsCleanup`<br>`FinalizerScheduleFailureRecoversAlreadyScheduledPublication`<br>`ManyRelationshipProducerSegmentsShareOneTypedGenerationAndLinearAdjacencyBatch`<br>`ParallelProducersMergeByStableLogicalIndex`<br>`PlaybackFaultRollsBackEarlierProducerSegments`<br>`ProducerCannotSchedulePlaybackForItsOwningBatch`<br>`ProducerCannotSynchronouslyPlaybackItsOwningBatch`<br>`ProducerFaultInvalidatesEverySegmentWithoutPublishing`<br>`ProducerScopeDoesNotAuthorizeWorldSharedCommandBuffer`<br>`RelationshipFaultInLaterSegmentRollsBackWholeMergedBatch`<br>`ScheduleAndDisposeRaceHasExactlyOneOwner`<br>`ScheduleAndPlaybackRaceIsLinearizable`<br>`ZeroProducerBatchIsAValidTopologyFreeNoOp`<br>`ZeroProducerParallelSchedulePreservesItsExplicitDependency` |

### JobStorageAliasSafetyTests.cs

现有覆盖：内部共享过滤器与伪造资源能力清理；公共动态共享选择、HasShared、类型能力工厂及生成描述的拒绝行为保留。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `EmptyPropagationRejectsNativeSizedHandleAccessBeforeSchedulingCallback`<br>`SharedPresenceAndPrecomputedIndexFilterRemainTopologyOnlyForManagedValue` |
| 保留原名并改写 | `AliasFreeDynamicSharedFilterRequiresExactSharedReadCapability`<br>`DynamicSharedValueFilterRejectsAliasesInsideTopologyReadJob`<br>`NativeSizedHandleTableIsRejectedByOrdinaryAndGeneratedAccesses`<br>`ReadAccessRejectsReferenceBearingTableBufferSparseAndSharedStorage`<br>`RecursivelyAliasFreeStorageKeepsOrdinaryTypedSchedulingAvailable`<br>`SharedReadCapabilityIsExactByComponentType`<br>`SharedValueAliasesAreRejectedOnlyWhenAJobBorrowsTheValue`<br>`TypedScheduleRejectsAliasBearingStorageBeforeJobCallback` |

### LifecycleClosureTests.cs

现有覆盖：上下文显式持有本测试调度器；失败使用 TestFault.Code 区分；关闭测试使用显式开始信号、公共关闭状态和事件顺序，finally 释放并等待参与者。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `WorldDispose_DrainsUnboundScopeAndRejectsLateMutationAdmission` |
| 保留原名并改写 | `ConcurrentDispose_WaitsForClosureAndPendingJobControlFailsFast`<br>`CreateAndUpdateCallbacks_RejectReentrantLifecycleControlWithoutMutation`<br>`Disable_WaitsOutstandingScopeAndEnableCreatesFreshLifetime`<br>`DisposeFromJobCallback_RejectsBeforeMutatingSystemGroupState`<br>`Dispose_AggregatesJobAndDestroyFaultsAndContinuesTeardown`<br>`Dispose_WaitsRootAndDescendantScopeBeforeOnDestroy`<br>`DriverLifecycleCallbacks_RejectControlBeforeMutatingGroupState`<br>`ExternalDispose_WaitsForActiveUpdateCallback`<br>`ObservedJobFailuresDoNotPreventLaterUpdatesAndDisposal`<br>`OnDestroyFault_StillDrainsCleanupJobAndAggregatesBothFaults`<br>`OnDestroy_CanScheduleParallelCleanupAndDisposeDrainsEveryWorkItem`<br>`OnDestroy_LaunchesCleanupRootAndDisposeDrainsIt`<br>`OnDestroy_RejectsEveryReentrantControlWithoutMutatingSlots`<br>`RemoveAndDispose_WaitForOnDestroyCleanupJobs`<br>`Remove_WaitsOutstandingScopeDestroysSystemAndShiftsSlots`<br>`RepeatedUpdatesFinishEverySubmittedJobBeforeGroupDisposal`<br>`SystemTeardownDoesNotBlockReadOnlyGroupQueries`<br>`SystemTeardownFailureDoesNotBlockReadOnlyGroupQueries`<br>`WorldClosing_RejectsUnrelatedJobCallback`<br>`WorldDisposalWaitsForAnActiveQueryAndRejectsNewOperations`<br>`WorldDisposalWaitsForDataUseWithoutConsumingTheTaskFailure`<br>`WorldDispose_FromBoundBundleCallbackRejectsBeforePublication`<br>`WorldDispose_FromBoundHookCallbackRejectsBeforeClosing`<br>`WorldDispose_FromBoundQueryCallbackRejectsBeforeClosing`<br>`WorldDispose_RejectsNewWorkAndWaitsTypedRootAndDescendant` |
| 当前新增或改名 | `WorldDisposalDrainsQueryAndRejectsLaterMutation` |

### QueryRuntimeOwnershipTests.cs

现有覆盖：按测试持有 Scheduler/World，保留原 worker 数、安全模式、载荷策略和公共行为结果；失败检查异常类型，时序通过同步与事件顺序验证。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `DistinctReadQueriesRemainExactUnderConcurrentRepeatedLookup` |
| 保留原名并改写 | `DifferentTableComponents_EnterQueryCallbacksConcurrently`<br>`FaultedOrdinaryQuery_ReleasesBorrowAndSynchronousResourceOwner`<br>`ReadonlyRelationshipQuery_CanOverlapUnrelatedTableQuery`<br>`SameTableComponent_InDifferentWorldsDoesNotShareQueryOwner`<br>`SameTableComponent_WriteQueryBlocksReadQueryAtResourceFrontier`<br>`WritableRelationshipOwner_DefensivelyRejectsNestedOrdinaryQuery`<br>`WritableRelationshipQuery_BlocksOrdinaryQueryAtTopologyFrontier`<br>`WritableRelationshipQuery_BodyFaultStillRollsBackCanonicalValue` |
| 当前新增或改名 | `ConcurrentRepeatedReadQueriesDoesNotThrow` |

### ReadOnlyQueryPacketJobTests.cs

现有覆盖：显式世界调度器与输出资源；保留 packet 选择、重复执行、生命周期与命令记录，失败检查异常类型。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `AllRowsChangedSinceRequiresEveryRowInTheSlice` |
| 保留原名并改写 | `PacketReadRejectsComponentsNotDeclaredByTheQuery`<br>`PreparedPlanReusesPacketsForSeveralPassesAndCommandRecording`<br>`RowFiltersBecomeDenseContiguousPacketsWithDisjointOutputRanges` |
| 当前新增或改名 | `ChangedRowSliceChecksDoesNotThrow` |

### RelationMaintenanceSystemTests.cs

现有覆盖：按测试持有 Scheduler/World，保留原 worker 数、安全模式、载荷策略和公共行为结果；失败检查异常类型，时序通过同步与事件顺序验证。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `ImmutableAdjacencySnapshot_IsSafeWithoutALifetimeResourceOwner`<br>`OwnerBoundEndpointMutation_RejectsMissingOrReadOnlyCapability`<br>`OwnerBoundEndpointMutation_RejectsMultiBatchParallelOwner`<br>`PinnedAdjacencyReader_DoesNotBlockNextMaintenancePublication`<br>`RejectManagedPayloadPolicy_IsHonoredAndNotMutatedBySchedule`<br>`RelaxedReaderSeesLastApplied_AndFreshReaderDependsOnMaintenanceHandle`<br>`ReplacingTheDefaultSchedulerDoesNotRebindAnExplicitWorld`<br>`TopologyWrites_SerializeAcrossPayloadsButNotWorlds` |

### RelationshipChunkJobTests.cs

现有覆盖：按测试持有 Scheduler/World，保留原 worker 数、安全模式、载荷策略和公共行为结果；失败检查异常类型，时序通过同步与事件顺序验证。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `DirectedEndpointChunks_UniqueTargetSwapValidatesOneFinalImage`<br>`ParentChunks_BulkWriteCanonicalAndLeaveChildrenLastAppliedUntilMaintenance`<br>`ParentChunks_InvalidFinalForestRollsBackEveryChunkWrite`<br>`ParentChunks_RejectMissingReadonlyAndRowFilteredQueryShapes`<br>`ParentReadChunks_RejectMissingAndWriteOnlyQueryAccess`<br>`ParentReadChunks_WaitForSameWorldTopologyWriter`<br>`ReadChunkVariants_ExposeCanonicalProtectedSources`<br>`UndirectedEndpointChunks_BulkWriteCanonicalAndLeaveIncidentLastApplied` |

### RelationshipJobAccessTests.cs

现有覆盖：按测试持有 Scheduler/World，保留原 worker 数、安全模式、载荷策略和公共行为结果；失败检查异常类型，时序通过同步与事件顺序验证。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `CanonicalParentRead_RequiresTopologyReadAndWaitsForAnyWorldTopologyWriter`<br>`TopologyWrite_IsSharedAcrossHierarchyAndRelationsPerWorld` |

### RootComponentContractTests.cs

现有覆盖：按测试持有 Scheduler/World，保留原 worker 数、安全模式、载荷策略和公共行为结果；失败检查异常类型，时序通过同步与事件顺序验证。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `CanonicalComponentContract_IsAcceptedBySystemsAccess`<br>`GeneratedJobEntity_AcceptsRootOnlyComponentAtCompileAndRuntime`<br>`RootOnlyComponent_WorksWithComponentJobAccessAndJobCommandBuffer` |

### SparseJobAccessTests.cs

现有覆盖：按测试持有 Scheduler/World，保留原 worker 数、安全模式、载荷策略和公共行为结果；失败检查异常类型，时序通过同步与事件顺序验证。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `DifferentSparseTypesAndWorlds_DoNotShareLogicalResource`<br>`DirectSparseOwner_BlocksLaterScheduledConflictUntilCallbackReturns`<br>`FaultedSparseBody_ReleasesIterationAndSynchronousResourceOwner`<br>`RepeatedSparseAccessPreservesCallbackResults`<br>`SameWorldSameSparseType_ReadOwnersRunConcurrently`<br>`SameWorldSameSparseType_WriteOwnerBlocksReadersAndWriters`<br>`ScheduledSparseWriter_BlocksDirectReadSparseUntilOwnerReturns`<br>`SparseCallbackInsideJob_RejectsMissingLogicalResource`<br>`SparseCallbackInsideJob_RejectsMissingTopologyReadResource`<br>`TopologyWriteHook_CanBorrowSparseAfterLaterSparseOwnerWasRegistered` |

### SystemGroupTests.cs

现有覆盖：保留回调异常类型与系统状态结果，清理消息匹配。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `SystemExceptions_BubbleWithoutBeingHidden` |

### TopologyFinalizerAndHierarchyPropagationTests.cs

现有覆盖：直接构造库内部 proof/range 的测试清理；公共调度返回的分区、规范化根、回滚和传播结果保留；托管值从公共能力工厂验证；状态实例化，钩子绑定采用同步点与事件顺序。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `HookBindingWaitsForActivePropagationTopologyReaders`<br>`Propagation_RejectsManagedComponentAliasesBeforeAnyPacketWrites`<br>`StableTopologyProof_DerivesEveryStagingOffsetAndRejectsSelfOverlapClaim`<br>`StableTopologyProof_RejectsARepeatedPersistentChunkRange`<br>`StableTopologyProof_RejectsIncompleteChunkTail`<br>`StableTopologyProof_RejectsInconsistentChunkRowCount`<br>`StableTopologyProof_RejectsPacketGapsAndOverlaps`<br>`StableTopologyProof_RejectsTotalRowOffsetOverflow` |
| 保留原名并改写 | `CompletedMaintenanceToken_BecomesStaleAfterDeferredReparent`<br>`EquivalentParentAba_IsSerializableBeforeTheFinalizer`<br>`FaultedDependency_PermanentlyRejectsPartitionWithoutRunningCapturePackets`<br>`FinalImageValidationFault_DiscardsEveryStagedParentAndInverseEdit`<br>`NoopParentPackets_DoNotAcquireAWorldWriterOrPublishARevision`<br>`PacketFault_PreventsFinalizerAndLeavesLiveRootUntouched`<br>`ParentPackets_RunConcurrently_ButPublishOnlyThroughOneFinalizer`<br>`ParentUpdatesRemainVisibleToChangedQueryAfterInterveningWrites`<br>`PropagationSchedule_DoesNotSynchronouslyWaitForMaintenance`<br>`PropagationUpdatesEveryRootDespiteInterveningClockChanges`<br>`Propagation_DerivesDisjointRoots_VisitsOrganizationNodes_AndSupportsMixedOrder`<br>`Propagation_EmptyNormalizedRootsDoNotAcquireAnExecutionVersion`<br>`Propagation_MatchingIrrelevantHookEventsDoNotDisableWritablePackets`<br>`Propagation_MatchingValueReplaceHookIsRejectedBeforeAnyPacketWrite`<br>`Propagation_MissingWritableComponentReportsTheComponentErrorBeforeRangeSafety`<br>`Propagation_ReadOnlyCallbacksDoNotAcquireAnExecutionVersion`<br>`Propagation_RejectsCrossRootReadFromAWritableComponentFamily`<br>`Propagation_RejectsMissingWrongWorldAndOverlappingProofEvidence`<br>`Propagation_UnrelatedComponentHookDoesNotDisableWritablePackets`<br>`Propagation_UnusedWriteCapabilityDoesNotAcquireAnExecutionVersion`<br>`Propagation_UsesExplicitMaintenanceDependencyForFreshChildren`<br>`Propagation_WritableFamilyAdmitsExternalAncestorsAsReadOnlyRanges`<br>`SameCardinalityQueryMembershipReplacement_AbortsTheFinalizer`<br>`TopologySchedule_DoesNotBlockAndCapturesTheDependencyFinalImage`<br>`UnrelatedTopologyWrite_MergesWithTheCapturedQueryTransaction`<br>`WorldWriterMayRunDuringStaging_AndOptimisticPreimageConflictAbortsFinalizer` |
| 当前新增或改名 | `HookBindingFollowsPropagationCallbackRelease`<br>`TypedPropagationCapabilitiesRejectManagedValuesBeforeWrites` |

### WorldControlPlaneAdmissionTests.cs

现有覆盖：按测试持有 Scheduler/World，保留原 worker 数、安全模式、载荷策略和公共行为结果；失败检查异常类型，时序通过同步与事件顺序验证。

| 类别 | 用例 |
| --- | --- |
| 保留原名并改写 | `AcquireSystemTickWaitsForCandidatePublicationAndCannotBeLost`<br>`ConvenienceQueryCapturesCurrentTickFromItsAdmittedPublishedRoot` |

### WorldJobAdmissionTests.cs

现有覆盖：按测试持有 Scheduler/World，保留原 worker 数、安全模式、载荷策略和公共行为结果；失败检查异常类型，时序通过同步与事件顺序验证。

| 类别 | 用例 |
| --- | --- |
| 原名：删除或改名 | `FreshWorldStaticEscapeCannotCreateWithoutDeclaredAccess` |
| 保留原名并改写 | `ConcurrentFlushReservesTheExactEarlierWaveWithoutBlockingHookRecording`<br>`DirectHierarchyMutationWaitsForAnExistingTopologyOwner`<br>`DirectStructuralCallHoldsTopologyAdmissionThroughImmediateHooks`<br>`EmptyFlushCannotDisposeAWriterMintedByAnExecutingHook`<br>`FreshWorldManagedPayloadEscapeCannotReplaceWithoutDeclaredAccess`<br>`FreshWorldPrebuiltCommandBufferCannotRecordFromJob`<br>`FreshWorldRemainsUsableBySynchronousCallersWithoutTypedAccessBinding`<br>`HookFaultPreservesOriginalExceptionAndDiscardsOwnedOverlayInsideJob`<br>`ImmediateHookCannotEscapeThroughCapturedWorldCommands`<br>`ImmediateHookCannotRecordIntoAnotherPrebuiltCommandBuffer`<br>`JobCommandBufferRejectionDoesNotConsumePendingCommandWave`<br>`JobCommandBufferRejectionDoesNotPoisonDirectPlayback`<br>`LeakedDeferredWorldCannotMintACommandWriterAfterItsHookReturns`<br>`MultiWorkItemOwnerCannotUseOrdinaryComponentWriteApis`<br>`MultiWorkItemOwnerCannotUseOrdinaryTopologyWriteApis`<br>`MultiWorkItemReadOnlyWorldAccessRemainsAvailable`<br>`OrdinaryJobCannotRecordIntoPrebuiltCommandBuffer`<br>`RawDeferredOverloadsRejectJobAccessBeforeReadingInvalidHandles`<br>`SerialTopologyWriteJobCanRecordNextWaveFromImmediateHook`<br>`SingleWorkItemParallelOwnerCanUseOrdinaryTopologyWriteApis`<br>`SynchronousQueryCannotChangeItsWorldExecutionMode`<br>`SynchronousQueryCannotMakeRawJobsUseItsWorld`<br>`SynchronousWorldRejectsSchedulingDuringAndAfterAQuery` |
| 当前新增或改名 | `CapturedWorldCreateRequiresDeclaredAccess` |
