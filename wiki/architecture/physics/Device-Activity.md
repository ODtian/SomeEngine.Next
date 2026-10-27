# 设备主体活动与完成事件

本页维护当前设备活动表示、实际图工作范围和完成结果接口。
源码入口为 [DeviceBodyActivities](../../../src/SomeEngine.Physics/DeviceBodyActivities.cs)、
[DevicePhysicsActivity](../../../src/SomeEngine.Physics/DevicePhysicsActivity.cs)、
[活动数学](../../../assets/Shaders/physics_body_activity.slang) 和
[Frame 活动行为](../../../tests/SomeEngine.Graphics.Direct3D12.Tests/DeviceActivityTests.cs)。
原组件与 CPU 活动阶段见 [Body 活动](Body-Activity.md)。

`DeviceBodyActivityBinding` 放在原具有 `BodyActivity` 的主体，选择其对应的
`DeviceBodyActivities`。设备活动状态沿同一个 `DeviceBodyBinding.Index` 保存实体身份、
休眠、安静时间、半径和本子步的预测标记；位姿、速度、质量与积分起点继续沿
共同的 [208 字节主体](Device-Bodies.md) 读取。

`DevicePhysicsActivity.UploadInputs()` 从项目给出的成员查询读取原活动组件版本，
将新实体和显式活动修改传入对应状态。其原版本缓存只保存实体及活动组件版本，
实际结果应用时在同一个资源确认自身写入。`BodyWake.Time` 按原
`[StartTime, EndTime)` 选取请求。
输入刷新在 chunk 边界读取可用组件、绑定变化与原 `BodyActivity` 行版本跨度，
沿 `RowIndices` 保留原成员筛选；已确认的行先比较实体与版本，再读取活动值。
[完成应用](../../../src/SomeEngine.Physics/DeviceBodyActivities.Results.cs) 在原查询的只读作用域内，
通过 `ReadOnlyQueryPacketJobs` 按 packet 借用 Body 绑定、活动绑定和 `BodyActivity` 原列。
每个 packet 读取对应设备结果，核对原实体代次、资源绑定、已确认版本和实际值变化，
将变化实体、设备索引及事件留在同一活动资源的可复用名单。
比较完成并 Join 后，`ComponentWriteJobs` 只为实际变化的实体取得 `BodyActivity` 写引用，
同批实际写版本随结果确认回原版本数组。事件按 packet 顺序追加到原 `PhysicsEventBuffer`，
本轮消费完成后清理名单计数并复用其容量。

常规输入使用原查询的 `MembershipVersion` 与组件列版本：成员变化或活动组件增删时
刷新当前成员，其余步骤只读取 Body 绑定、活动绑定或活动值有写入的 chunk。
相同资源已确认的完成写入沿原版本跳过再次输入。32 字节输入保留其成员身份和参数，
上一批实际输入索引负责清理修改与唤醒的瞬时标记。
`BodyWake` 通过冷登记的必需缓冲查询逐子步读取，并沿当前输入中的成员身份选择实际主体。
活动数值历史继续由同一设备状态持有。

原主体的显式位姿、质量和速度编辑沿 `DeviceBodyInputs` 的同一次变化判定形成
24 字节派生标记，记录原实体代次、组件位、步号和子步号。
活动内核读取当前子步的标记，主体结果消费沿原 source 同步确认自身写入。

`DeviceActivityKernels.ReadBodyInputs` 选择可选的当前时段输入读取入口。
[ReadDeviceBodyInputActivity](../../../assets/Shaders/physics_body_input_activity.slang)
逐条读取原 `DeviceBodyInputBuffers` 的 112 字节记录，核对当前主体身份，
用共同输入数学判断冲量、速度目标或运动学目标的实际变化，再标记原活动状态的唤醒请求。
它位于 `UploadInputs()` 的常规活动输入之后，沿同一阶段屏障完成；
项目将输入批次的 `AddBindings` 和 `GetReadAccesses` 传给活动阶段。
运动学目标在 Predict 后应用，预测前的当前输入标记使所引用的休眠岛在当步唤醒。

## 图、预测与数值范围

设备图的 Parents、Islands 和邻接继续保存完整的所选动态连接。
`ConstraintGraphActiveBodies` 是同一图的派生活动标记；Body 颜色、约束颜色、
计数与打包沿它选择实际数值工作。
`DeviceGraphBodies` 同时供当前求解数学的权重和修正读取这个范围。

`DeviceConstraintGraph.BuildConnectivity()` 捕获当前约束参与，生成 Rows、Parents、
Islands 和 CSR 邻接，并沿原设备 fence 交付连通结果。
`UpdateActiveWork()` 从同一 Parents、邻接和当前活动状态生成 ActiveBodies、
Body/约束颜色、计数及打包范围。两项阶段共享图持有的原缓冲。
并行实现复用原 `DeviceGraphParallelKernels`：Initialize 按同一维度参数选择完整初始化
或活动工作初始化，后者保留当前连通与具体组数量，再执行原压缩、着色和工作打包。
原 `Build()` 仍一次生成连通和数值工作范围；串行 bundle 的两个细分入口执行原完整构建。

`WakeConnected(buildGraph: false)` 使用调用方先行交付的当前连通结果。
检测会产生新接触的子步先为输入唤醒构建连通，再预测、检测和刷新当前接触；
接触唤醒消费刷新后的第二次连通结果，Resume 随后补齐新唤醒主体的预测。
`UpdateActiveWork()` 位于这次唤醒、Resume 之后，再接续接触响应和数值初始化。
接触响应初始化的有效质量及求解器迭代由此读取同一新活动范围。
项目装配见 [设备场景阶段](../../../samples/DefaultGame/PhysicsScenes/CreateDevicePhysicsScene.cs)，
新约束的公开运动结果见 [设备拓扑唤醒](../../../tests/SomeEngine.Graphics.Direct3D12.Tests/DeviceActivityChangesTests.cs)。

项目的具体图初始化内核通过以下源码组合选择活动主体：

```slang
#include "physics_body_activity_types.slang"
#define DEVICE_GRAPH_BODY_ACTIVE(body) deviceBodyActivityAwake(body)
#include "physics_constraint_graph_parallel.slang"
```

常规图内核选择全体所选动态主体。
`DeviceNoConstraintBodies` 提供自由主体场景的零约束具体组。
活动行内核通过 `IDeviceConstraintActivity.error` 返回米、弧度两种残差，
`readDeviceActivityRows` 将当前误差及参考输入变化归约到原参与岛。
`DeviceLengthActivity` 沿共同 Length 参数和当前主体位置返回距离残差，
参与角色与参数选择委托原 `DeviceLengthBodies`。
`DeviceBendActivity` 沿原三端边方向取得余弦，将最低余弦的界外分量转换为弧度误差；
参与角色继续复用 `DeviceBendBodies`。
`DeviceContactActivity` 沿同一当前加权锚返回法向穿透残差，参与角色继续由
`DeviceContactBodies` 提供。
`DeviceJointActivity` 复用共同坐标和限位误差；自由坐标的残差为零。
`DeviceMotorActivity` 复用当前子步时刻的目标与阻尼平衡点，按实际启用轴返回米、弧度误差。
`DeviceElasticActivity` 复用原局部锚、静止旋转和当前主体坐标，按实际启用的线性、角向刚度返回残差。
`DeviceSupportBodyActivity` 和 `DeviceSupportedContactActivity` 沿同一支撑高度界、
当前质心和加权锚返回高度误差，普通接触残差与原参与角色继续复用。
`DeviceJointBoundActivity` 复用原 LongRangeJointBound 两端锚与距离上下界，
`DeviceDistanceRangeActivity` 复用原 DistanceRange 的两局部锚与径向范围；两者都返回以米计的界外距离。
`DeviceConstraintActivityGroups<TFirst,TSecond>` 沿原参与分组组合实体身份与残差，
行偏移、读写角色和具体组编号继续复用 `DeviceConstraintBodyGroups`。

`WakeConnected()` 在当前完整图上读取运动、当前输入及具体行误差，唤醒同岛主体。
具体行通过 `IDeviceConstraintActivity.entity` 提供原约束实体身份。
`sourceVersions` 可返回两项实际输入版本，默认返回零。
通用参数 writer 在原 16 字节 metadata 的第四字记录主组件行版本；
`DeviceContactInputs` 沿已有点、材料、端点与 Buffer Header/Inline 的精确版本记录输入修改。
保留设备几何的刷新继续通过 `RetainsGeometry` 跳过 Native 几何与锚维护写，
相同材料沿原比较保持其版本，驻留更新继续由同一设备数据持有。
完整的检测、匹配、刷新、活动与完成步消费组合见
[连续刷新接触行为](../../../tests/SomeEngine.Graphics.Direct3D12.Tests/DeviceRefreshedContactActivityTests.cs)。
活动阶段用每行 20 字节保存实体代次、参与数和输入版本，
参与端身份、角色继续沿原派生索引保存。输入修改、连接新增、移出和改接时
标记旧端、当前端所在的连接岛。初始化沿已有 ClearSignals 阶段完成，容量随原图批次复用。
首次初始化清理原行元数据中的参与数；参与缓存由本轮实际参与端写入，
后续读取限定在原行保存的参与数之内。活动分发与具体 `ReadRows` 入口使用
[共同线性索引](Device-Dispatch.md)，按同一线程组宽度覆盖实际工作范围。
`Finish()` 按速度、质心位移、由半径换算的角运动及当前误差共同判断安静，
同岛成员满足安静时长后进入休眠并清零速度。

`physics_body_activity_dynamics.slang` 的具体 Predict/Resume 入口委托共同
`predictDeviceBody`。休眠主体仍取得当前子步的起点与时刻；新唤醒主体的 Resume
保留共同起点并完成一次实际预测。Dynamics 的附加访问使用活动资源的
`GetWriteAccesses`，图的输入使用 `GetReadAccesses`。

同一头文件的 `UpdateDeviceActiveBodyVelocities` 入口只为活动主体重建速度。
休眠主体保持 `Finish()` 已写入的零线速度和零角速度，持续静止时沿原活动表示保持休眠。
项目将该入口作为同一 `DeviceBodyKernels` 的速度内核，继续调用原 `dynamics.UpdateVelocities()` 阶段。

[DeviceAwakeAvbdConstraints](../../../assets/Shaders/physics_awake_avbd.slang) 保留原组的成员、
参与端和读写角色，在有活动受力主体时初始化该行历史；Body 与 dual 迭代继续沿原图打包范围。
`initializeDeviceAvbd` 和 `initializeDeviceAvbdRows` 的第三个具体 `IDeviceBodySelection`
参数选择本次初始化的 Body 方程。传入 `DeviceAwakeBodies` 时，睡眠方程保留其原分解缓存；
原两参入口选择全部已绑定方程。
[DeviceAwakeContactResponseActions](../../../assets/Shaders/physics_awake_contact_actions.slang)
同样按活动受力主体初始化响应，休眠期间保持原支撑力。

## 完成结果

`Readback(events)` 沿实际 RHI fence 读取活动转换，将当前实体的结果应用回原
`BodyActivity`，并向原 `PhysicsEventBuffer` 添加 Sleep/Wake。
`events.Emit()` 将该步输出转入普通 `PhysicsStepEvents` 实体；
原 `ConsumePhysicsEvents` 在 Frame 消费阶段读取并通过普通命令确认。
完成链与消费者见 [物理事件](Events.md)。

下面是活动阶段的使用例子；图、具体内核、Dynamics 和结果资源由项目预先构造：

```csharp
var stages = bodyInputs.Upload()
    .Then(activity.UploadInputs())
    .Then(dynamics.UpdateSelection()).Then(activity.WakeConnected())
    .Then(dynamics.Predict(acceleration)).Then(solve)
    .Then(dynamics.UpdateVelocities()).Then(activity.Finish())
    .Then(activity.Readback(events)).Then(bodyResults.Readback(rowsPerPacket))
    .Then(events.Emit());
```

首次 Wake 位于 Predict 之前时，`UpdateSelection()` 从原查询形成当前主体集合；
它复用 Dynamics 的原索引、成员标记和参数缓冲。
