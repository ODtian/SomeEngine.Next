# 设备几何、类型检测与接触消费

本页维护已实现的设备几何表示、公共检测与查询入口及资源职责。源码入口为
[几何绑定和 reader](../../../src/SomeEngine.Physics/DeviceCollisionGeometry.Data.cs)、
[转换阶段](../../../src/SomeEngine.Physics/DeviceCollisionGeometry.Stages.cs)、
[共享 Hull 数据](../../../src/SomeEngine.Physics/DeviceCollisionGeometry.Hulls.cs)、
[设备检测](../../../src/SomeEngine.Physics/DeviceCollisionDetection.cs) 与
[共同接触输出](../../../src/SomeEngine.Physics/DeviceCollisionDetection.Contacts.cs)。
公开行为入口为 [DeviceCollisionDetectionTests](../../../tests/SomeEngine.Graphics.Direct3D12.Tests/DeviceCollisionDetectionTests.cs)。

原 Body 组件的主体设备表示、端点引用范围和多端输出见[设备主体与引用几何](Device-Bodies.md)。

## 实体与设备表示

`DeviceGeometryBinding` 在实体组件中持有 `DeviceCollisionGeometry` 和该几何资源内的索引。
`DeviceCollisionBody` 继续持有候选网格及网格内索引。网格 pair 保存双方几何索引与网格索引，
两种索引分别由对应绑定提供。

`DeviceCollisionGeometry.Inputs` 保存 112 字节的具体设备几何输入：当前位置与旋转、形状参数、
Hull 范围、几何索引及本轮几何类型。`GeometryType` 与候选输入的类型由同一个转换阶段参数写入。
原始数值由实体组件或所选驻留设备表示持有，网格范围和类型索引属于派生数据。

`IDeviceGeometryReader<TGeometry>` 将原几何和 `BodyPose` 转成设备输入，并提供派生范围。
内置 reader 覆盖球、盒、胶囊、圆柱、平面和凸包。平面 reader 接受项目给出的有限候选区域，
实际窄阶段继续解释原平面单位法向与偏移。

`WriteGeometryInputs<TGeometry,TReader>` 沿生成式 Job 读取当前组件和两种绑定。
reader 通过同一 `IJobComponentResources<TGeometry>` 描述原引用资源；生成器使用
`JobResources(nameof(TReader))` 在编译期解析具体描述器。
Hull reader 的准备阶段在依赖就绪后读取原顶点、面及面索引，将同一个 `ConvexHullData`
共享到批次中的一份设备范围，再转换各实体的当前姿势及外扩半径。
新增共享数据只传输新加入的连续范围，容量和映射由 `DeviceCollisionGeometry` 持有至释放。

## 候选与具体类型

项目依次组装 `grid.BeginInputs()`、各具体 `grid.WriteGeometryInputs(...)`、
`grid.Build(...)` 和 `detection.Detect()`。写入和构建接收原有
`Within`、`Between` 或 `Product` 查询，沿原成员与行过滤取得参与位置。
候选算法、规则、分类器和 count/prefix/write 继续由
[设备网格](Device-Collision-Grids.md) 持有。

[physics_collision.slang](../../../assets/Shaders/physics_collision.slang) 提供具体
`IDeviceConvex`、`IDeviceCollisionNarrowPhase` 和公共 `detectDeviceCollision` 入口。
球核心为点、胶囊核心为线段，盒与圆柱使用真实核心 support。
[Hull 扩展](../../../assets/Shaders/physics_collision_hull.slang) 从共享设备顶点和面索引读取 support 及支持面。
有界形状通过 `DeviceConvexCollision<TFirst,TSecond>` 执行 GJK、EPA 和支持面裁剪，
平面组合通过 `DeviceConvexPlaneCollision<TGeometry>`，两端交换通过 `DeviceReverseCollision<TKernel>`。
CPU 与设备 simplex 共用的数学职责见[距离、穿透与流形](Geometry.md#距离穿透与流形)。

设备 simplex 的线段投影、三角形重心权重、四面体行列式及 GJK 收敛点积使用
`double` 局部运算，几何输入、支持点、权重输出和接触结果沿原 `float` 表示交付。
这使宽地板与微小转角盒体的浅穿透保持竖直法向，行为分别由
[CPU 接触检查](../../../tests/SomeEngine.Physics.Tests/BoxFloorContactTests.cs) 和
[设备接触检查](../../../tests/SomeEngine.Graphics.Direct3D12.Tests/DeviceCollisionDetectionTests.BoxFloor.cs) 覆盖。

无序类型 bin 同时包含两种参与顺序时，项目用 `DeviceTypePairCollision<TKernel>` 指定
内核的 `firstType`。包装按本轮类型交换数值输入，并还原见证点、特征与法向；
最终实体和位置角色继续由原 pair 提供。
下面是盒/球 bin 的 Slang 使用例子，`1` 是该项目传给盒转换阶段的类型值：

```hlsl
DeviceTypePairCollision<DeviceConvexCollision<DeviceBox, DeviceSphere>> kernel;
kernel.firstType = 1;
detectDeviceCollision(id.x, kernel);
```

用户窄阶段实现相同接口并从自己的具体入口调用公共函数。额外 Buffer 通过原
`ComputePipelineDesc` 的 Slang 反射及 `extraBindings` 绑定，`extraAccesses` 声明实际范围。
`DeviceGeometryInput.GeometryIndex` 可供具体内核索引自己的附加设备数据。

## 容量与共同结果

`DeviceDetectionCapacity(Pairs, FaceVertices, WorkItems)` 分别给出结果 pair 容量、支持面顶点容量
和并行设备工作项数量。每个工作项按 `work + k × WorkItems` 处理当前 bin 中的 pair，
复用自己的 EPA 与裁剪工作区；每个 pair 只在自身结果位置写出。
数值 scratch 按 `WorkItems` 分配，持久结果按 `Pairs` 分配。
构造可接收从 `firstBin` 开始的连续 `ComputePipelineDesc` 范围；每个 bin 由项目提供具体 kernel。
各派发复用同一 scratch，派发间沿原 Buffer 屏障同步，独立参数保存在各自的 256 字节范围。
结果索引为原 pair 位置减去首 bin 的起点，全部 bin 共用 `Distances`、`Contacts` 和 `ContactCounts`。
同一个 `DeviceContactMatches` 继续将该连续结果匹配到原接触实体、参数索引和数值历史。
单 kernel 构造以单元素范围接入相同执行链。
`BodiesPerContact` 由项目按具体组合设置，默认 6；例如刚体 Box/Box 选择 2，
两节点 Capsule/Sphere 选择 3，三节点 Face/Sphere 选择 4。
同一值决定检测端设备缓冲、所选 CPU 读回数组及写入跨度；
`DeviceContactGeometry` 同时读取检测端跨度和持久锚的跨度，向原接触行更新几何。

`Distances` 保存双方实体和共同 `CollisionDistance`；见证点为世界坐标，法向从第一方指向第二方，
间距为正表示分离、为负表示穿透。
`Contacts` 为每个 pair 保留至多四个共同 `CollisionContact`，`ContactCounts` 保存实际点数。
接触头包括原实体、特征、点序号、双方几何局部锚、世界见证中点、法向、间距及共同预测字段。
104 字节设备头与当前 CPU 结构对齐，普通检测写入 `Prediction.Enabled=false` 和零样本时刻；
样本时刻的设备布局使用两个 uint 保存原 double 位模式。

`DeviceCollisionReadback` 在构造时选择 CPU 输出。`None` 保留驻留结果，CPU 结果数组为空；
`Distances` 和 `Contacts` 分别取得所选数据的读回 Buffer 区间及 CPU 容量。
CPU 消费通过 `ReadDistances`、`ReadContacts` 和 `GetReadbackAccesses`，设备消费者读取公开的
`Distances`、`Contacts`、`ContactCounts` 及原网格 `BinOffsets`。

## 完成链与实际消费

检测既可作为具体 `IPhysicsStage`，也可独立调用 `Schedule(in context, dependency)` 查询已维护的
设备 bin。`Upload` 传输本轮转换结果，`Resident` 读取当前设备输入。
项目按具体阶段选择传输及结果消费时机，并使用原物理区间描述查询所得数据。

调度声明所用 bin、pair、几何、工作区、参数、命令上下文、附加资源和所选读回范围。
RHI 提交之后接原 `DeviceQueueFence`，再接受所选 CPU 数据并执行 `ScheduleFinally` 的后端归还。
返回 Job 完成覆盖全部工作；`Number`、`Time` 在接受完成时记录原步号与子步结束时刻。
创建方在使用这些资源的完成链结束后释放检测和几何批次。

`WriteContacts<TOutput>` 是具体的后继物理阶段，读取已选接触结果并调用原
`ICollisionContactOutput.Add`。primitive 与 Hull 为两端各提供同实体局部锚及权重 1，
`ContactPointOutput` 可直接接入现有 [ContactPoints.Refresh 和响应](Contacts.md)。
多个设备 bin 向同一输出追加，项目在每步写入前持有该输出的写范围并清理一次，
再把各 bin 的写入及刷新放入同一完成链。
