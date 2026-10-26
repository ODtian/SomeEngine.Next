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
`DeviceConstraintActivityGroups<TFirst,TSecond>` 沿原参与分组组合实体身份与残差，
行偏移、读写角色和具体组编号继续复用 `DeviceConstraintBodyGroups`。

`WakeConnected()` 在当前完整图上读取运动、当前输入及具体行误差，唤醒同岛主体。
具体行通过 `IDeviceConstraintActivity.entity` 提供原约束实体身份。
活动阶段保存上次实体代次及参与端身份、角色的派生索引，连接新增、移出和改接时
标记旧端、当前端所在的连接岛。初始化沿已有 ClearSignals 阶段完成，容量随原图批次复用。
`Finish()` 按速度、质心位移、由半径换算的角运动及当前误差共同判断安静，
同岛成员满足安静时长后进入休眠并清零速度。

`physics_body_activity_dynamics.slang` 的具体 Predict/Resume 入口委托共同
`predictDeviceBody`。休眠主体仍取得当前子步的起点与时刻；新唤醒主体的 Resume
保留共同起点并完成一次实际预测。Dynamics 的附加访问使用活动资源的
`GetWriteAccesses`，图的输入使用 `GetReadAccesses`。

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
