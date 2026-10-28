# 设备几何、类型检测与接触消费

本页维护已实现的设备几何表示、公共检测与查询入口及资源职责。源码入口为
[几何绑定和 reader](../../../src/SomeEngine.Physics/DeviceCollisionGeometry.Data.cs)、
[转换阶段](../../../src/SomeEngine.Physics/DeviceCollisionGeometry.Stages.cs)、
[共享 Hull 数据](../../../src/SomeEngine.Physics/DeviceCollisionGeometry.Hulls.cs)、
[设备检测](../../../src/SomeEngine.Physics/DeviceCollisionDetection.cs)、
[检测读回](../../../src/SomeEngine.Physics/DeviceCollisionDetection.Readback.cs) 与
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

EPA 的初始三角平面、四面体内部点、面平面和可见性判断也使用局部 `double` 运算。
可见性由当前面三个顶点的绕序计算，面法向和距离继续存入原 28 字节工作区。
这使阵列坐标下微小旋转的相邻单位盒保持侧向浅接触；对应输入由
[CPU 侧接触检查](../../../tests/SomeEngine.Physics.Tests/BoxSideContactTests.cs) 和
[设备侧接触检查](../../../tests/SomeEngine.Graphics.Direct3D12.Tests/DeviceCollisionDetectionTests.BoxSides.cs) 覆盖。

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

## 刚体预测接触

`physics_collision_prediction.slang` 提供显式 `predictDeviceRigidCollision(work, kernel)` 入口。
它读取原 `DeviceGeometryReference.firstBody` 对应的刚体状态，在共同步起点重建形状框架，
沿 `IDeviceCollisionPrediction` 取得分离平面和候选流形，再用双方采样锚在预测状态下的
相对位移筛选接近。接触保存起点法向、间距、几何局部锚和 Body 局部锚，
`Prediction.SampleTime` 使用本次 `PhysicsStepContext.StartTime`。
检测参数沿每个 kernel 原有的 256 字节范围传递该时刻。

`IDeviceCollisionPrediction` 复用普通窄阶段并增加两个实际操作：
`getMaximumProjection(geometry, direction)` 给出具体形状沿单位方向的最远投影，
`getPredictedManifold(..., manifoldDistance, points)` 按本 pair 的候选距离产生流形。
具体凸形状的 support 和外扩半径提供投影；内置凸形状碰撞的带距离重载复用原裁剪算法。
预测距离由起点间距和两侧运动闭合量形成，逐点筛选仍使用项目的原接触距离。
闭合量读取同一 Body 的步初及预测 COM、旋转。对单位方向 `u`，
`d = inverse(Rpredicted) * u - inverse(Rstart) * u`；几何副本取原局部形状、
单位旋转与 `position = -CenterOfMass`，沿 `d / |d|` 的最远投影乘 `|d|`，
再加上 `dot(u, predictedCOM - startCOM)`。合并后取与零的最大值；
`d` 为零时旋转项为零。此值界住同一材料点在两端姿势间的投影位移，
first 使用法向、second 使用反向，覆盖旋转中靠近的原流形面点。

刚体候选范围由 `physics_rigid_geometry_motion.slang` 中的
`DeviceRigidMotionUpdate<TBounds>` 生成。`TBounds` 实现
`IDeviceGeometryBounds.getBounds(DeviceGeometryInput)`，仅计算给定形状框架的范围。
内置球、盒和凸包更新器复用此计算；运动更新器读取同一设备 Body 的步初、共同预测和
当前姿势，合并三个完整形状范围。
`IDeviceGeometryBounds.getRotationRadius(localGeometry)` 由具体形状给出角行程扩张所用的半径 `r`。
该输入保留原形状参数，位置取 `-CenterOfMass`、旋转取单位旋转；默认实现沿局部 bounds 的
最远角点求半径，原有 bounds 实现继续复用这一计算。
球体显式返回 `length(localGeometry.position)`，表示球心绕 COM 的轨迹半径。
球面半径保留在三个完整形状范围中；COM 居中时角行程扩张为零，偏心时按球心轨迹扩张。
完整静态 AABB 与形状旋转特性分别由这两个操作提供，具体形状持有该知识。
运动更新器以 `r * min(|angularVelocity| * h, 2)` 扩张范围，覆盖本步角行程。
`h` 由 `UpdateDeviceGeometry.Schedule` 按值捕获共同子步的 `context.Duration`，
沿同一提交及参数缓冲传入；直接 `IDevicePhysicsSubmission` 提交使用完整旋转包络，
继续按完整局部 bounds 的最远角点求半径，并沿步初至预测质心的线段扩张。
项目通过现有 `UpdateDeviceGeometry` 和具体 shader 选择该更新器，再执行原网格构建。

DefaultGame 料仓显式选择运动范围和刚体预测检测。完整数据链为
Body 预测 → 几何运动范围 → 网格与类型 bins → 起点预测接触 → 原接触匹配及几何刷新
→ AVBD → 速度响应 → 完成帧读回。匹配和几何刷新沿 `Prediction.Enabled` 选择相同的
步初锚，AVBD 用当前锚相对起点锚的位移形成接触间距；响应时间继续由共同子步持有。

`DeviceCollisionReadback` 在构造时选择 CPU 输出。`None` 保留驻留结果，CPU 结果数组为空；
`Distances` 和 `Contacts` 分别取得所选数据的读回 Buffer 区间及 CPU 容量。
检测先读回所选连续 bin 的起止位置，共 8 字节。`Count` 为这两个位置之差。
有候选时，后继提交按 `Count` 复制距离、点数、每 pair 四个接触头及对应参与端范围；
CPU 随后分别映射这些有效范围。Buffer 和 CPU 数组的容量由同一个检测对象持有，
每轮有效范围由当前 bin 界限确定。空候选沿界限读回完成本轮步号和时刻更新。
CPU 消费通过 `ReadDistances`、`ReadContacts` 和 `GetReadbackAccesses`，设备消费者读取公开的
`Distances`、`Contacts`、`ContactCounts` 及原网格 `BinOffsets`。

## 完成链与实际消费

检测既可作为具体 `IPhysicsStage`，也可独立调用 `Schedule(in context, dependency)` 查询已维护的
设备 bin。`Upload` 传输本轮转换结果，`Resident` 读取当前设备输入。
项目按具体阶段选择传输及结果消费时机，并使用原物理区间描述查询所得数据。

调度声明所用 bin、pair、几何、工作区、参数、命令上下文、附加资源和所选读回范围。
RHI 提交之后接原 `DeviceQueueFence`；界限读回就绪后，有效结果复制沿同一命令上下文提交并接续设备完成信号。
所选 CPU 数据接受完成后，`ScheduleFinally` 执行后端归还。
返回 Job 完成覆盖全部工作；`Number`、`Time` 在接受完成时记录原步号与子步结束时刻。
`Completion` 表示本轮最后一次设备提交，包含所选结果的实际复制。
创建方在使用这些资源的完成链结束后释放检测和几何批次。

`WriteContacts<TOutput>` 是具体的后继物理阶段，读取已选接触结果并调用原
`ICollisionContactOutput.Add`。primitive 与 Hull 为两端各提供同实体局部锚及权重 1，
`ContactPointOutput` 可直接接入现有 [ContactPoints.Refresh 和响应](Contacts.md)。
多个设备 bin 向同一输出追加，项目在每步写入前持有该输出的写范围并清理一次，
再把各 bin 的写入及刷新放入同一完成链。
