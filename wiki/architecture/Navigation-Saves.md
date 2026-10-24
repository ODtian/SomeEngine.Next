# 导航持久存档与恢复

`SomeEngine.Assets.NavigationSave` 把资源来源、共享关系与导航意图接入现有 ECS DurableSave。游戏提供自己的 `SerializationRegistry` 保存实际运动、业务状态及动作执行进度；导航集成保存角色目标和资源绑定，并在新 World 中重新建立导航组合。公开行为入口为 `tests/SomeEngine.Assets.Tests/NavigationSaveTests*.cs`。

## 资源来源与共享关系

游戏使用 `NavigationSaveResources.Register` 登记由资产创建的普通对象及其强类型来源。组件持续直接引用 Profile、Policy、空间、Flow 和运动基元。保存时按对象引用分配局部资源编号，恢复时每个编号取得一次普通对象，因此多个 follower 继续引用同一 Flow，Flow、独立路径和局部控制继续共享对应的资源。

| 普通资源 | 保存来源 | 恢复创建入口 |
| --- | --- | --- |
| Profile | `AssetId<NavigationProfileAsset>` | `NavigationProfileBlock.Create` |
| Policy | `AssetId<NavigationPolicyAsset>` | `NavigationPolicyBlock.Create` |
| Mesh | `AssetId<NavigationAsset>` + `NavigationMeshSelection` | `NavigationMeshBlock.CreateAsync` |
| HeadingMesh | `AssetId<NavigationAsset>` + `NavigationMeshSelection` | `HeadingNavigationMeshBlock.CreateAsync` |
| Grid | `AssetId<NavigationAsset>` + `NavigationGridSelection` | `NavigationGridBlock.CreateAsync` |
| MotionSet | `AssetId<NavigationProfileAsset>` | `NavigationMotionBlock.CreateAsync` |
| 来源几何 | `AssetId<MeshAsset>` | `NavigationGeometryBlock.CreateAsync` |
| 动作连接 | `AssetId<NavigationAsset>` 与当前连接定义 | `NavigationLinkBlock.Create`，随后恢复当前定义 |

普通 Flow 通过 `Register(flow, flowAssetId)` 记录具体 `NavigationFlowBlock` 来源，Heading Flow 通过 `RegisterHeadingFlow` 记录 `HeadingNavigationFlowBlock` 来源。程序创建的 Flow 由其已登记的输入资源和当前共享目标重建。Heading Flow 保存区域、朝向网格、端口和连接，基础成本在重建 Graph 后接回同一 `graph.Costs`。

## 保存与恢复边界

调用方先完成本步实际运动、输入修改和导航 Apply，再调用 `NavigationSave.Write`。导航表使用具有显式稳定 schema 的 `WriteDurableComponent` 编码，随后由 `WriteDurableWorld` 写入游戏 registry 选择的组件和拓扑。配对的 `ReadAsync` 读取两个 durable 段，创建带指定 Scheduler 的新 World，完成资源取得、引用修复和场构建后返回 `NavigationSaveResult`。

下面是调用方式的例子；资产 ID、角色数量和业务组件由游戏提供：

```csharp
var resources = new NavigationSaveResources();
resources.Register(profile, profileId);
resources.Register(policy, policyId);
resources.Register(mesh, navigationId, meshSelection);
resources.Register(grid, navigationId, gridSelection);
resources.Register(flow, flowAssetId);

completedStep.Complete();
NavigationSave.Write(stream, world, gameRegistry, resources);

stream.Position = 0;
NavigationSaveResult restored = await NavigationSave.ReadAsync(
    stream, gameRegistry, loader, scheduler);
world = restored.World;
resources = restored.Resources;
```

`gameRegistry` 登记游戏拥有的实际运动、Transform 和动作执行组件。导航表拥有导航配置、目标、启用状态与引用绑定；路径、搜索、邻居、MPPI 工作区及轨迹结果由恢复后的公开组合重新建立。恢复使用保存的 Entity 身份，因此实体目标继续指向同一保存对象。取得的普通资源在资产及 loader 关闭后继续持有自己的数据。

内置路径组合保留 Polygon、Polyanya、Scene、D* Lite、ASA、Heading Scene、State Lattice 和 Hybrid A* 的选择。车辆空间还保留 Hybrid A* 参数与共享运动基元。角色配置保存 Avoidance、速度模型预测、车辆 MPPI 参数及轨迹来源选择；`NavigationCrowdAgent` 保存权重与启用位，包含单独参与密度采集的业务实体。

## 世界修改与动作交接

分块 Grid 的资产登记、来源区域登记和恢复登记共同使用其 `NavigationGridRegionGraph` 表达来源关系；流送、目标更新和部分驻留 Apply 生成的 Grid 继续引用这一图。资产身份与选择项由 `NavigationSaveResources` 持有，完整数据由对应 Block 或来源区域重建。

成本捕获沿 `NavigationCosts.Source` 取得完整输入，并在分配保存记录前合并同一输入的引用。记录包含已退出驻留范围的通行修改，多个 Flow 在恢复后共享对应的同一 Costs。普通 Grid 的单元位置、支撑层和有向连接标识用于应用保存值；恢复先通过 `NavigationCostSystem` 建立当前基线，再应用保存的完整成本。Flow 的 `Solver` 保存求解阶段选定的方法，首次预算求解期间也能保存这一配置。恢复根据 Costs、目标及该方法建立完成场，后续驻留投影继续持有该完整输入。

Heading Flow 修改后的成本保存所属 Graph 和按该图 `Nodes`、`Transitions` 顺序排列的单元与边成本。节点的 Region、Polygon、Channel 和 Pose 由保存的区域、网格、端口与连接顺序重建，同一位置的各朝向及各动作边分别取得对应成本。恢复后的多个 Flow 继续共享这一 Costs 对象与 HeadingGraph，并按保存的费用选择实际旋转和动作交接。

来源驱动的区域使用 `resources.RegisterRegion(region, gridBuildSystem)` 登记重建配置。保存表包含来源/障碍的几何引用、版本、启用状态、参与方式和区域构建参数；实际 Transform 由游戏 registry 保存。恢复时用 `new NavigationSaveOptions().UseSources<TTransform,TReader>()` 提供强类型读取器，经过 Source → Build → Apply，再通过 `NavigationGridBuildSystem` 形成 Grid/Costs。`restored.Resources.Regions` 提供完成的区域对象，供游戏继续创建更新系统。

场景通过 `Register(scene, placements, profile, policy)` 保存区域来源与 Transform，连接成本、能力、启用状态和容量随当前连接定义恢复。Heading 场景通过 `Register(scene, headingMesh)` 保存对应的表示来源。

`NavigationTraversalIntent` 保存当前交接请求和单调请求序号。`CapturePathTraversal` / `RestorePathTraversal` 与 Flow 对应入口将该意图接回公开组合；游戏动作消费者从自己的 durable 执行状态继续更新实际运动及 `NavigationTraversalResult`。正在执行的连接重新建立 Passage 占用，动作完成且实际位置达到出口后，导航从该实际位置重新求解。后续请求延续已保存的序号。

验收例子覆盖独立目标与共享 Flow 的成本修改、流送后完整成本恢复、来源障碍重建后的实际绕行、Heading Flow 的修改成本与动作途中恢复、MotionBlock 与车辆控制恢复。车辆检查同时验证恢复后实际下一步与 MPPI 选中 Buffer 的对应点一致。
