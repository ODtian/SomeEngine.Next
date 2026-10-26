# 公共物理场景创建与帧消费

本页维护已实现的场景目录、创建输入和公共使用方法。入口为 DefaultGame 的 [PhysicsScenes.CreateCpu](../../../samples/DefaultGame/PhysicsScenes/CreateCpuPhysicsScene.cs)、[目录](../../../samples/DefaultGame/PhysicsScenes/PhysicsSceneCatalog.cs)、[创建数据](../../../samples/DefaultGame/PhysicsScenes/PhysicsSceneData.cs)及[帧与实体引用](../../../samples/DefaultGame/PhysicsScenes/PhysicsSceneSimulation.cs)。公开创建、运动和定时输入行为见 [PhysicsSceneTests](../../../tests/SomeEngine.Runtime.Tests/PhysicsSceneTests.cs)。

## 目录与创建数据

`PhysicsScenes.All` 保存原 112 个场景的 ID、名称、维度、来源编号、类型、触发方式和观测说明。前 17 个原生及派生场景的创建值见 [OriginalPhysicsScenes](../../../samples/DefaultGame/PhysicsScenes/OriginalPhysicsScenes.cs)，95 个 typed 场景的布局和事件见 [PhysicsScenes](../../../samples/DefaultGame/PhysicsScenes/PhysicsScenes.cs)及 [PhysicsStressScenes](../../../samples/DefaultGame/PhysicsScenes/PhysicsStressScenes.cs)。

创建记录包含原位姿、尺寸、质量与主惯量、摩擦、初速度、局部关节锚、碰撞排除、手动距离区间和物理步事件。初始化将索引解析为真实实体引用：

| 创建数据 | 实体中的数据 |
| --- | --- |
| Body | `BodyPose`、`BodyMass`、`BodyVelocity`、`BodyStepState`、所选数值工作组件、`BoxGeometry` 与范围 |
| Joint | 受保护有向端点、`JointConstraint` 与独立 `AvbdJointState` |
| 碰撞排除 | `SceneCollisionExclusion` 无向关系 |
| 手动距离边 | 受保护无向端点、`DistanceRange` 与 `AvbdDistanceRangeState` |
| 速度增量事件 | 以原质量换算的 `BodyImpulse`，时间为物理步编号乘原步长 |
| 位移或连接移除事件 | `SceneInputChange` Buffer 与当前输入索引 |

`Bodies`、`Joints` 和 `DistanceRanges` 公开只读 Entity 引用，顺序对应原创建数据。实际物理参数、当前状态与可复用数值历史继续由所属实体组件持有。

共同 `CreateEntities` 执行一次源数据到实体的创建，交回同一组实体引用和输入记录。CPU 路径为所选 Body 附加自己的 `AvbdBodyEquation` 分解工作组件。角锁连接的目标局部框架保存创建时双方相对旋转，保持原横杆、配重等场景的初始连接姿势。

二维场景保留面积质量和仅 Z 轴转动惯量，几何 Z 厚度用于原范围与显示。调用方显式选择 `PlanarBoxCollision` 或三维具体窄相；活动角轴的共同方程见[求解阶段](Solver-Steps.md)。默认场景摩擦合成使用两侧系数的几何平均。

## 共同物理步和输入时刻

工厂组合原定时输入、陀螺预测、共同预测、预测范围、空间维护、分桶、具体检测、持久点刷新、响应初始化、图求解和速度重建。接触、关节和显式距离区间通过具体约束集合进入同一 AVBD 图求解。

CPU 场景通过 `solve.Activity` 共享原求解图，`BodyActivity` 单处保存休眠和安静时间。输入后的唤醒、检测后的连通唤醒及 `PredictActiveBodies.Resume()` 继续同一子步，完成后按显式 `PhysicsSceneSettings.Activity` 阈值判断休眠。相关工作范围与误差见[Body 活动](Body-Activity.md)。

带具体 `IPhysicsEventConsumer` 的 `CreateCpu` 重载把接触和活动结果交给原帧消费窗口。`ScenePhysicsEventStream` 标记完成步事件实体；`ConsumePhysicsEvents` 将截至当前完成步的全部历史交给调用方，再通过原命令链确认这些实体。调用方传入 `eventAccesses` 声明消费者资料；原四参数和场景数据入口委托同一创建与事件链。

`PhysicsSceneSettings` 显式保存外层迭代、每体局部迭代和整步预算声明。原步长及子步数来自创建数据。输入的 `Frame` 表示物理步编号；连接移除与位置变化在该步的第一子步、共同预测之前沿原命令链回放。速度增量按 `mass * deltaVelocity` 转成世界冲量，由 `ApplyBodyInputs` 在对应时间区间应用。

`PhysicsSceneSimulation.Frame` 引用原 `PhysicsFrame`。帧消费者从实体 `BodyPose` 更新同一实体的 `LocalTransform`，保留其几何缩放；`CompleteForRead()` 直接完成该帧与消费者的原 Job 链，并交回完成步编号和时刻。下一步继续依赖同一消费完成。完成边界见[物理帧](Physics-Frames.md)。

## 使用例子

下面是使用二维原生堆叠场景的例子，`world` 由调用方创建并绑定所选 Scheduler：

```csharp
PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "2d-stack");
using var scene = PhysicsScenes.CreateCpu(world, description, new PlanarBoxCollision(1e-5f),
    new PhysicsSceneSettings(Iterations: 8, BodyIterations: 1, StepBudgetMilliseconds: 1000d / 60));
scene.Frame.AdvanceTime(scene.StepSeconds);
PhysicsStepResult result = scene.CompleteForRead();
BodyPose pose = world.Read<BodyPose>(scene.Bodies.Span[1]);
```

场景对象持有查询登记、派生容量和相关阶段的资源，释放时先完成原帧，再释放这些引用。World 持有实体及组件生命周期。
