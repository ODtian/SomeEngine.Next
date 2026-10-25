# 设备碰撞网格与类型分桶

本页记录已实现的设备候选算法、公共使用入口与完成关系。源码入口为 [DeviceCollisionGrid](../../../src/SomeEngine.Physics/DeviceCollisionGrid.cs)、[查询接入](../../../src/SomeEngine.Physics/DeviceCollisionGrid.Candidates.cs)、[实体输入阶段](../../../src/SomeEngine.Physics/DeviceCollisionStages.cs)、[设备作业链](../../../src/SomeEngine.Physics/DeviceCollisionGrid.Jobs.cs) 和 [Slang 实现](../../../assets/Shaders/physics_grid_common.slang)。公开设备行为入口位于 [DeviceCollisionGridTests](../../../tests/SomeEngine.Graphics.Direct3D12.Tests/DeviceCollisionGridTests.cs)。

## 实体输入与项目选择

项目通过 `DeviceGridSettings(CellSize, LevelCount)` 显式选择网格。单层配置为均匀网格；多层配置按包围范围最长边选择层级，第 l 层边长为 `CellSize × 2^l`。`DeviceGridCapacity` 集中持有批次的实体、单元、哈希桶、输出配对和类型 bin 容量。

实体组件 `DeviceCollisionBody(Grid, Index)` 持有实际设备资源引用和该实体在批次中的索引。原始几何与运动数据继续由对应实体组件或设备表示持有。具体 `IDeviceCollisionReader<TComponent>` 从所选原组件形成 `DeviceCollisionInput`：派生范围、具体形状类型与几何索引、键、类别位图、允许类别位图及用户数值。

原几何与位姿的具体转换也可通过 `BeginInputs`、`WriteGeometryInputs`、`Build` 接入相同网格。
设备几何绑定、窄阶段、无序类型路由及共同接触消费由 [设备检测](Device-Collision-Detection.md) 维护。

`grid.Candidates<TComponent,TReader>(pairs, reader, rowsPerPacket, rules, transfer, readback)` 返回一个具体 `IPhysicsStage`，由现有 `PhysicsSimulation` 和 `Then` 组织。`pairs` 接受项目已经选择的 `world.Within(members)`、`world.Between(first,second)` 或 `world.Product(first,second)`。输入写入阶段读取所需原组件和网格绑定，并在原参与查询回调中调用 `Contains` 判断 First、Second 角色，保留原查询的行筛选。实体身份和角色由这一阶段写入设备输入，配对模式由所选关系来源转成内部设备参数。共同物理步的 `Bodies` 选择实际模拟节点，几何参与由这些原 pair 查询选择。

集合内与集合间输出互不相同实体的无序对，交叠集合按相同无序语义去重。位置积保留 First、Second 位置及自身配对。这组语义与 [CPU 网格](../ECS/Collision-Grids.md) 使用的原关系来源一致。

`DeviceInputTransfer.Upload` 在依赖就绪后转换当前组件数据、写入映射范围并提交批次复制。每轮转换标记本轮有效输入，设备构建只处理本轮参与实体。`Resident` 使用当前设备输入及其有效轮次。`grid.WriteInputs` 提供同一关系来源的单独转换阶段，供项目在选定时刻准备下一次上传；主机输入轮次和已提交设备轮次由网格分别持有。

## GPU 构建与三阶段分桶

GPU 构建为每个有效实体计算单元范围，原子分配单元记录并写入哈希链。单元记录保存实体批次索引、坐标和链索引；真实输入继续从 `CollisionBodies` 读取。设备范围和类型输出缓冲按批次容量持续复用。

候选枚举由每个实体查询同层和更粗层。相同层级按实体身份选择发起方，跨层由细层发起；共享单元中逐项取双方最小坐标的最大值作为唯一输出单元。最终使用真实范围相交判断及所选参与角色形成配对。

count 按发起实体 × 具体类型 bin 计数；prefix 对每 256 行计算块内偏移，再计算块与 bin 基址；write 复用同一候选、关系和类型入口，直接写入最终 `CollisionPairs` 的类型区间。`BinOffsets` 保存每个设备 bin 的起止位置。内置 `DeviceShapePairs` 按两种具体形状类型的无序组合选择 bin，并在配对中保留两端实际几何索引。

## 具体 Slang 扩展与资源

[physics_grid.slang](../../../assets/Shaders/physics_grid.slang) 给出内置 Count、Write 入口。用户源码包含 `physics_grid_common.slang`，用具体结构体实现 `IDevicePairRule.matches` 和 `IDevicePairClassifier.classify`，再从具体入口调用 `countDevicePairs`、`writeDevicePairs`。两次调用使用相同具体规则值和分类器值；`DevicePairAnd` 与 `DevicePairExcept` 持有并组合具体规则值。`DeviceCollisionRules` 读取相同设备输入，按显式 `DevicePairRules` 执行类别位图及键关系。

网格构造接收七个现有 `ComputePipelineDesc`。Shader 代码及反射从既有 Shader 资产、就绪 Shader 或后端编译入口提供；绑定按原 Slang 反射描述解释，并在构造时整理成后端使用的绑定数组。用户规则和分类器所需的额外设备资源通过 `extraBindings` 接入，并用 `extraAccesses` 声明对应实际资源范围。规则在相同具体设备入口中读取这些资源。

实体转换使用生成 `IJobEntity` 的 `JobResources<DeviceCollisionUploadResources>`，声明每个 `DeviceCollisionBody` 所指上传 Buffer 的实际字节范围。原组件和参与查询读取沿现有 ECS 访问声明接入。设备阶段声明真实输入、网格、计数、偏移、配对、参数、命令上下文及选定读回 Buffer 的范围。其他设备实体阶段可用 `DeviceCollisionBodyResources` 声明其输入范围。

## 完成、消费与归还

提交沿现有 RHI `CreateComputePipeline`、`Dispatch` 和 `Submit` 执行，阶段间通过资源 barrier 连接。实际 `QueueCompletion` 经 [DeviceQueueFence](../../../src/SomeEngine.Physics/DeviceQueueFence.cs) 适配到现有 `IJobExternalFence`；等待使用现有 Scheduler 作业调用 `WaitCpu`，设备错误由 fence 的终态结果传播。

`DevicePairReadback.Pairs` 在提交中复制偏移和配对，设备完成后由 CPU 作业读回。返回的 `PhysicsStepResult.Completion` 覆盖提交、外部 fence、选定读回与 CPU 接受、`ScheduleFinally` 调用的后端 `CollectCompleted`。完成后 `Number`、`Time` 表达这批设备结果所属的物理步编号和子步结束时间。

GPU 消费者通过公开 `Pairs`、`BinOffsets` 声明实际设备范围并接续返回完成链。CPU 消费者在所选 Pairs 读回完成后调用 `ReadBin`；异步消费使用 `GetPairReadbackAccesses(scheduler)` 读取实际配对和偏移数组。网格由创建方持有，在使用它的物理步完成后释放，释放持久映射及所属后端对象。
