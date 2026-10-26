# 完成步事件与断裂

本页维护已实现的事件状态、公共消费入口及命令完成顺序。源码入口为
[PhysicsEvents](../../../src/SomeEngine.Physics/PhysicsEvents.cs)、
[ContactEvents](../../../src/SomeEngine.Physics/ContactEvents.cs)、
[BreakConstraints](../../../src/SomeEngine.Physics/BreakConstraints.cs)和
[活动阶段](../../../src/SomeEngine.Physics/PhysicsActivity.cs)。
公共行为见 [CompletedPhysicsEventsTests](../../../tests/SomeEngine.Physics.Tests/CompletedPhysicsEventsTests.cs)。

## 所属步与持有者

`PhysicsEventBuffer` 复用各子步的派生输出容量，提供显式 `ReadAccess` 与 `WriteAccess`。
`Emit()` 在最后子步通过原 `JobCommandBuffer` 创建事件实体；
实体上的 `PhysicsStepEvents` 保存完整步号和起止时刻，`PhysicsEvent` Buffer 保存该步发生的全部结果。
`Emit(components)` 允许具体 `IPhysicsEventComponents` 添加项目的事件筛选组件。
Buffer 先于 Header 记录，结构完成依赖覆盖创建、回放与命令释放。

每个 `PhysicsEvent` 保存 `Kind`、`Phase`、原 `Subject` 实体、子步与结果时刻。
接触及触发记录还保存双方几何身份、世界点、法向、当前间距和世界反力；
断裂记录保存实际载荷 Body、力 N 和关于该 Body 质心的力矩 N·m。
`First`、`Second` 在接触结果中为几何原始两侧，在断裂结果中为适配器的前两个参与角色；
任意端约束的 `Subject` 与 `Body` 给出断裂对象和触发阈值的实际参与体。
这些值是所属步的历史结果，当前物理状态继续由原 Body、约束、接触和算法组件持有。

`ConsumePhysicsEvents<TConsumer>` 保留调用者查询的成员筛选，读取步号不超过所消费结果的事件实体。
具体 `IPhysicsEventConsumer.Consume` 在有效借用内接收 Header、事件跨度和原 `JobCommandWriter`，
项目可记录后续 ECS 命令，并为回调使用的外部数据提供显式资源声明。
消费后记录事件实体销毁；返回完成句柄覆盖回调、确认回放和命令释放。

该阶段可作为 [PhysicsFrame](Physics-Frames.md) 的原消费者或与其他具体消费者组合。
中间步的实体保留到消费确认，帧层继续只保存完成步、时刻与 Job 句柄。
确认完成成为下一次实际物理写入的依赖。

## 接触、触发与生命周期

`ContactEvents<TScope>(world, bodies, points, events, touchDistance)` 显式选择 Body 输入与接触查询，
按当前加权锚计算 [共同接触间距](Contacts.md)。间距达到项目的触碰距离时发出 Begin，
持续触碰发出 Stay，离开时发出 End。Force 读取同点 `ContactResponse.ForceOnSecond`。
`TriggerContact` 选择 Trigger 种类；项目的响应查询通过自身筛选选择需要施加数值响应的点。
触发点与标准接触点共用几何、参与边和事件观察入口。

项目为本事件流选择具体 `TScope`，在点上添加 `ContactEventState<TScope>`；
`ContactEventComponents<TScope,TComponents>` 可包装原点组件写入器，在持久点创建时完成这一步。
观察状态保存 Touching 与上一条历史事件，采用原 `ICleanupComponent` 生命周期。
几何或点销毁后，原受保护关系及数值组件按 ECS 职责释放，cleanup 保留最后结果直到 End 入队；
观察阶段再记录本流 cleanup 组件移除；实体收尾继续沿原 ECS cleanup 职责进行。
筛选离开通过同流的派生成员索引和历史结果发出 End，容量持续复用。

`ContactEvents<TScope,TSource>` 用具体 `IContactEventSource` 提供当前
`ContactEventSample`，沿同一观察状态和 cleanup 流程交付结果。
来源通过 `AddBodyReads`、`AddReads` 与 `AddReadResources` 声明原查询中的实际读取，
`AddAccesses` 声明来源持有的其他资源；`TryRead` 在有效行和 Body 借用内返回双方几何、
世界点、法向、间距和力。常规入口选择 `BodyContactEventSource`。
源码见 [ContactEventSources](../../../src/SomeEngine.Physics/ContactEventSources.cs)。

[DeviceContactSamples](../../../src/SomeEngine.Physics/DeviceContactSamples.cs) 从项目选择的
接触参数、当前设备主体与加权锚取得完成步观测。
`Readback()` 先执行具体采样内核，再沿真实 RHI fence 读取观测数组；
`DeviceContactEventSource` 按原参数绑定和点的实体代次读取该观测。
主体结果可以在选定的结果消费者中读取，事件流继续经过原 `PhysicsEventBuffer` 和 Frame。
[共同采样数学](../../../assets/Shaders/physics_contact_samples.slang) 使用当前加权见证点的中点、
共同接触间距和原参数中的几何身份。`DeviceTriggerEventForces` 返回零力，
`DeviceResponseEventForces` 在 [响应采样头](../../../assets/Shaders/physics_contact_response_samples.slang)
读取所选 `DeviceContactResponseState`；用户具体 `IDeviceContactEventForces` 使用同一采样入口。

## 力阈值与具体适配

`ConstraintBreakSettings` 保存在约束实体，指定每个实际参与 Body 的最大力与最大力矩。
`BreakConstraints<TConstraint,TForces>` 从选定的原参数行读取当前载荷，任一 Body 超过对应上限时
记录一条 Break 结果，并通过普通结构命令销毁原约束。下一步图维护沿既有成员和关系变化接续。

`TForces` 实现共同 `IPhysicsConstraintForces<T>`，用原参与信息和当前 Jacobian 返回世界力与力矩。
内置 AVBD Joint、ElasticConnection 与 JointMotor 读取各自原力乘子；
原 `IXpbdConstraintForces<T>` 继承该接口，继续使用 `J λ / h²`。
`QueryReadResources` 可为用户适配持有当前组件中的引用资源，读取范围仍由原具体描述器提供。
用户约束沿同一参数、参与、力读取和断裂阶段入口接入。

以下是阶段组合的使用例子，具体查询、参数和消费者由项目提供：

```csharp
using var events = new PhysicsEventBuffer(WorldJobs.GetScheduler(world), capacity: 64);
using var activity = solve.Activity(activitySettings, events);
using var observe = new ContactEvents<GameContacts>(world, bodies, points, events, touchDistance);
using var breaking = new BreakConstraints<JointConstraint, AvbdJointMath>(world, bodies, joints, events);
var stages = activity.WakeConnected().Then(prediction)
    .Then(detection).Then(refresh).Then(activity.WakeConnected()).Then(prediction.Resume())
    .Then(initializeResponses).Then(solve).Then(new UpdateBodyVelocities(64))
    .Then(restitution).Then(activity.Finish()).Then(observe).Then(breaking).Then(events.Emit());
```
