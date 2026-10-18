# 空间筛选交付检查：2026-10-19

范围为 #239，分支 `codex/ecs-relations-239`。记录随该票实现提交，使用 worker3 的单套 Release 输出。运行环境为 Windows、.NET 10、C# 泛型关系使用方。

## 公开行为

```powershell
dotnet test tests/SomeEngine.Physics.Tests/SomeEngine.Physics.Tests.csproj -c Release --nologo -v minimal --no-restore -p:EmitCompilerGeneratedFiles=true
```

结果为 10/10，通过自动测试规则检查，违规数为 0。其中 #238 四项继续检查稀疏、均匀、密集空间输入和默认业务组合；#239 六项检查：

- 同键全覆盖组的排除结果。
- 不同角色组的四个交叉实体对。
- 角色键缺失和混合组保留普通 Body。
- 单向类别许可按最终实体身份位置执行。
- 默认动力学、双方类别、空间和角色键组合，及独立手写组遍历，均输出四个字面接触对；Count 为 4，Any 为 true。
- 类别排除与普通语义继续组合，Span 输出收到一个实体对并保留提前结束状态。

完整业务数据、调用和期望位于 `tests/SomeEngine.Physics.Tests/SpatialFilterTests.cs`。手写组遍历位于 `benchmarks/SomeEngine.ECS.Benchmarks/Relations/HandwrittenSpatialGroups.cs`。两侧各持有一棵同布局树和具体摘要，分别进入真实 World 读取范围并消费实体对。

## 可选 Physics 引用

```powershell
dotnet build tests/SomeEngine.ECS.Relations.Tests/SomeEngine.ECS.Relations.Tests.csproj -c Release --nologo -v minimal
```

既有 Relations 使用方仅引用其原有包项目，构建为 0 个警告、0 个错误。空间生成器通过使用方 Compilation 中的 Physics 类型选择对应入口。

## 实际生成入口

```powershell
ilspycmd -t SomeEngine.ECS.Benchmarks.Relations.Spatial.RelationQueryExtensions tests/SomeEngine.Physics.Tests/bin/Release/net10.0/SomeEngine.Physics.Tests.dll
```

从实际使用方 DLL 读取到 28 个返回 `RelationQuery<SpatialCandidates<…>,…>` 的空间入口。它们来自四个业务语义和三个具体实现，每个分别具有初始来源、已组合来源的 Where / Except。

已组合普通语义入口的返回类型包含 `SpatialAnd<TSpatialFilter,SpatialComponentFilter<DefaultContacts,Body,Body>,TSpatialResults,GroupMatch>`；结果类型为 `(TSpatialResults,GroupMatch)`。键、bitmap 入口保留各自的实现类型、键或许可类型，以及具体组证明类型。

## 实际 IL 接点

```powershell
ilspycmd -il src/SomeEngine.Physics/bin/Release/net10.0/SomeEngine.Physics.dll
```

`SpatialTraversal.Enumerate<TFilter,TGroupResults,TOutput>` 中观察到：

- `IL_00fc`、`IL_0122` 的 `constrained. !!TFilter` 分别进入两个方向的 `ISpatialFilter<TGroupResults>.MatchGroups`。
- `IL_02b3` 的 `constrained. !!TFilter` 进入 `MatchesLeaves`，组结果通过只读引用传入。
- `IL_02c7` 的 `constrained. !!TOutput` 进入 `IRelationOutput<(Entity,Entity)>.Add`，输出行通过只读引用传入。

`SpatialBitmapFilter` 还保留对 TGroupPermission 和 TPermission 的 constrained 静态调用。NativeAOT 完整使用方与性能场景由 #242、#243 接续验证。

## 持续维护 #240

在同一 worker3 的 Release 输出集合执行：

```powershell
dotnet test tests/SomeEngine.Physics.Tests/SomeEngine.Physics.Tests.csproj -c Release --nologo -v minimal --no-restore
```

结果为 13/13 通过，TestRules 0 项违规，其中新增的三个 `CollisionMaintenanceTests` 通过真实 checked World 观察：

- 连续系统窗口的位置、类别和角色键写入，扩展及独立手写维护各自给出字面接触对，最终结果与完整重建一致；只读枚举保留当前版本。
- Body 移除、成员插入、标签迁移及实体销毁，双方维护各自树和摘要，结果与字面身份对及完整重建一致。
- 两个 worker 的组件写作业通过显式同步点开始，维护依赖写入，查询及消费依赖各自维护。两侧得到三对接触，每个参与实体有两次接触，穿透深度总量分别为 1.2、1.0、1.4。

首个切片从缺少 CreateMaintenance 的公共调用 red 开始；随后分别从缺少手写维护和帧消费入口的 red 实施对应切片。最终命令覆盖已有空间候选、组证明与本次持续维护行为。维护数据和资源生命周期见 [碰撞树维护](../wiki/architecture/ECS/Spatial-Maintenance.md)。

## 分区输出与并行 #241

同一 Release 输出集合执行上述 Physics 命令，结果为 16/16 通过，TestRules 0 项违规。新增三个 `SpatialParallelTests`：

- 完整条件链的单线程及并行 count/prefix/fill 都返回四个指定身份对，读取保持当前版本。
- 扩展单线程、扩展并行、独立手写单线程及手写并行在角色键、类别和成员更新后，从五对变为三对；实际接触次数与穿透深度累计一致。
- 显式同步点在首个计数语义调用时安排组件写作业。当前填充交付原三对，写入完成后的维护交付更新的一对；带显式系统基线的 Changed 成员查询交付对应的成员窗口。

首个切片由缺少 CollisionPairBuffer/FillPairs 的公开调用 red 开始，随后由缺少手写分区和真实帧消费入口实施第二个切片。显式版本重载从缺少 FillPairsParallel(lastSystemVersion,buffer) 的编译 red 实施。

实际 Release DLL 的 `ilspycmd -il` 输出保留以下接点：

- `CollisionPairBuffer.CountJob<TEnumerable>.Execute` 的 `IL_0034 constrained. !TEnumerable` 调用 `Enumerate<PairCount>`。
- `CollisionPairBuffer.FillJob<TEnumerable>.Execute` 的 `IL_0075 constrained. !TEnumerable` 调用 `Enumerate<PairArrayOutput>`，输出通过引用传入。
- `CollisionPairBuffer.Fill<TEnumerable>` 的 `IL_0003 constrained. !!TEnumerable` 调用具体来源 Prepare。
- `ReadOnlyQueryPairJobs.PairJob<TJob>.Execute` 的 `IL_0025 constrained. !TJob` 调用具体分区 Execute；两个 QueryCursor 在实际子任务中取得。

有效读取范围、每分区实际 token 区间、阶段依赖与成对业务调用见 [空间关系分区执行](../wiki/architecture/ECS/Spatial-Parallel-Queries.md)。
