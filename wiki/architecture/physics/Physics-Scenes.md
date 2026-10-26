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

`PhysicsSceneSettings.JointBounds` 给出所选自动长程界的层数、罚参数缩放、乘子保留和初始误差比例。正层数为场景原关节附加 `LongRangeJointBound` Buffer，并沿原 `JointLongRangeGraphConstraints` 进入同一图求解与活动误差；零层数保持空工作查询。原关节实体、受保护端点和框架继续提供唯一的拓扑事实。

`PhysicsSceneSettings.ContactSupports` 选择接触长程支撑及其向上方向、接触资格和数值参数。所选动态 Body 保存 `LongRangeBodySupport`、载荷与当前支撑方程，原持久接触实体保存 `LongRangeContactSupport` 和载荷引用。`AvbdSupportedContactMath` 与 `ContactLongRangeGraphConstraints` 复用同一次 Body 分解、原几何间距和当前反力；普通接触继续使用自己的具体约束集合。支撑工作同时参与原活动误差判断。

带具体 `IPhysicsEventConsumer` 的 `CreateCpu` 重载把接触和活动结果交给原帧消费窗口。`ScenePhysicsEventStream` 标记完成步事件实体；`ConsumePhysicsEvents` 将截至当前完成步的全部历史交给调用方，再通过原命令链确认这些实体。调用方传入 `eventAccesses` 声明消费者资料；原四参数和场景数据入口委托同一创建与事件链。

`PhysicsSceneSettings` 显式保存外层迭代、每体局部迭代、乘子保留比例、图组织、接触距离和整步预算声明。CPU 与 GPU 读取同一乘子保留字段，工作量描述同时列出该值。原步长及子步数来自创建数据。输入的 `Frame` 表示物理步编号；连接移除与位置变化在该步的第一子步、共同预测之前沿原命令链回放。速度增量按 `mass * deltaVelocity` 转成世界冲量，由 `ApplyBodyInputs` 在对应时间区间应用。

`ContactNormalPenalty` 和 `ContactTangentPenalty` 分别选择新接触的法向、切向 ALM 罚度量，`FrictionRegularizationSpeed` 选择滑移曲率的正则化速度。CPU 工厂将这些值交给原 `AvbdContactComponents`，在持久接触实体创建时写入 `AvbdContactState`；后续共同求解在该实体上继续更新乘子和反力。接触材料刚度由同一实体的 `ContactMaterial` 给出。

CPU 场景选用项目的 `SceneContactPenaltyMath<TMath>`，由现有 `IAvbdConstraint<ContactPoint>` 组合普通或长程支撑接触。每步根据步长平方、当前接触雅可比和参与体的逆质量/世界逆惯量计算初始度量，并保留原点的罚参数历史；每轮先完成原接触双变量更新，再按穿透和压力长度尺度更新同一 `AvbdContactState` 中的罚参数。项目显式选择最小值 1、最大值 1e9、每米增长量 100000 和 0.0005 m 压力长度尺度，工作量描述列出这些选择。

内层具体类型为 `AvbdContactPotentialMath` 和 `AvbdSupportedContactPotentialMath`：共同使用原接触的法向、切向力和乘子更新，普通接触保留正的法向曲率上界，角向方程加入接触合力大小乘力臂长度的正曲率。所选长程支撑的附加反力继续参与摩擦投影与角向曲率。原 2D 掉落金字塔沿公共完成帧连续检查 180 步机械能，行为入口见 [PhysicsScenePyramidTests](../../../tests/SomeEngine.Runtime.Tests/PhysicsScenePyramidTests.cs)；工作量描述同时记录两个具体方程类型。

`PhysicsSceneSimulation.Frame` 引用原 `PhysicsFrame`。帧消费者从实体 `BodyPose` 更新同一实体的 `LocalTransform`，保留其几何缩放；`CompleteForRead()` 直接完成该帧与消费者的原 Job 链，并交回完成步编号和时刻。下一步继续依赖同一消费完成。完成边界见[物理帧](Physics-Frames.md)。

`PhysicsSceneSimulation` 同时实现 `IGameFramePhysics`，将时间推进、完成消费、后续提交和完成句柄委托给上述同一帧。`GameFrameSystems.Physics` 持有整个场景对象，沿既有帧生命周期先完成物理及消费者，再释放场景查询和派生资源。

## 统一场景平台与测量

[PhysicsSceneExample](../../../samples/DefaultGame/PhysicsScenes/PhysicsSceneExample.cs) 将原目录接入 `TestScene.Initialize` 与 `DescribeWorkload`。`CreateCpu` 与 [CreateDevice](../../../samples/DefaultGame/PhysicsScenes/DevicePhysicsSceneExample.cs) 分别选定对应工厂，沿内部数据重载消费同一份创建输入。原 Body 实体附加 `WorldTransform`、盒体 `MeshInstance` 与项目材质 Buffer；项目明确选择 `StandardClusterVertexEncoder`。`LocalTransform` 经原 `TransformSystem`、`MeshRenderExtractor` 进入 Rendering world，场景相机根据原动态 Body 的范围定位。

DefaultGame 的 `--physics` 选择完整物理目录，`--physics=<id>` 选择一个原场景。CPU 示例明确选择 AVBD、BVH refit、二维 `PlanarBoxCollision` 或三维 `ConvexCollision`、8 轮外迭代、每体 1 轮、6 层关节长程界、接触长程支撑、0.02 m 接触距离和每步 1000/60 ms 预算。工作量描述包含实际实体与约束规模、原步长、子步、图组织、原事件以及完成事件统计；每种事件分别列出首末物理步和事件时刻。

`--physics-device=gpu` 选择设备示例，指定 ID 同时查询 `PhysicsScaleScenes.All`。交互运行通过原 `IApplicationGeometryFeature.Install` 获取应用的 Backend 与 Device；创建、完成步、变换及网格提取继续采用上述共同路径。调用方也可向 `PhysicsSceneExample.CreateDevice(description, backend, device)` 直接提供具体设备。

| 配置 | CPU 示例 | GPU 示例 |
| --- | --- | --- |
| 空间候选 | BVH refit | 分层网格，基础单元 1 m、16 层 |
| 检测 | 二维 `PlanarBoxCollision`；三维 `ConvexCollision<BoxGeometry, BoxGeometry>` | `DeviceConvexCollision<DeviceBox, DeviceBox>` |
| AVBD 外层迭代 | 8 | 16 |
| AVBD 每体迭代 | 1 | 2 |
| 关节长程界层数 | 6 | 0 |
| 接触约束 | 普通接触与长程支撑 | 普通接触 |
| 接触距离 | 0.02 m | 0.02 m |
| 乘子保留比例 | 0.8 | 0.8 |
| 整步预算 | 1000/60 ms | 1000/60 ms |

GPU 示例按 Body 数量派生网格单元、桶、pair 和接触容量，工作量描述记录这些容量、所选设备名称、驱动与专用显存。接触、原关节和原显式距离边共同进入设备约束图；参数与数值历史沿原实体的设备表示保存，完成后读取所选 Body 并更新原 `LocalTransform`。

设备场景在同一帧消费阶段从当前接触几何、主体活动和图 Parents 归约全局结果，仅传回 16 字节的最大穿透、活动主体数、接触点数和最大连通主体数。`PhysicsSceneSimulation.DeviceAwakeBodies`、`DeviceContactPoints`、`DeviceLargestIslandBodies` 交付这些完成步结果；岛大小工作缓冲按原主体容量复用。

`ReadbackBodies` 列出本次实际读取位姿和速度的原主体引用。测量中的 `BodyMotionScope` 与 `ConstraintErrorScope` 声明位姿、动量、速度和两端约束误差的覆盖范围；全局接触、活动及连通统计继续来自上述设备归约。`LastBodyPosition` 与速度对应所选读取列表的最后一个主体，`LastBodyEntityIndex` 标明其实体索引。

`--physics-content=<目录>` 将 DefaultGame 现有已构建内容目录交给原 `TestScene.ContentDirectory`，共享其材质模板、灯光和渲染配置。示例显式接收项目的 `StandardMaterial` 模板，为静态与动态 Body 选择不同颜色及少量自发光，共用同一盒体几何。

`TestScene.WorldNameplates` 在首次创建和场景切换时选定该内容的名牌配置。物理示例以原 Body 取代加载内容的网格实体，选择关闭原小球名牌；返回原内容时恢复启动配置。

[MeasurePhysicsScenes](../../../samples/DefaultGame/PhysicsScenes/MeasurePhysicsScenes.cs) 通过 `--physics-check` 运行全部原目录，`--physics-check=<id>` 运行单个场景。物理窗口为至少 180 步，并覆盖最后一次原输入及其后 120 步运动。每次同步完成一个原物理步；采样时钟保留半步的提交余量，报告的结果编号与时刻继续采用原物理区间。

附加 `--physics-device=gpu` 通过 `RunDevice` 在所选 RHI 后端的计算设备上执行同一个测量循环。`--physics-check=bricks-110000 --physics-device=gpu` 是连续砖结构的入口例子；公共队列同时覆盖其构建、启动与测量。每个结果继续携带该次实际设置与预算比较。

渲染帧统计使用既有第 128 帧起的就绪窗口，窗口长度随上述完整场景时长声明；物理整步统计从原步骤 0 开始。统一渲染三场景仍由 `--regression` 选择其原目录与测量时限。

主计时从 `Frame.AdvanceTime` 开始，至 `CompleteForRead` 完成原变换与事件消费者结束，包含所有阶段的调用线程工作和异步完成等待。结果列出整步平均值、p95、p99、最大值、当前及峰值实体/接触/活动量、真实连接与几何结果、最终动态体质心及范围、总线动量、最终线/角速度及其全过程峰值、托管存活内存及进程峰值工作集。原输入线动量同时列于工作量描述，供自由碰撞与惯性场景核对。诊断读取另列耗时。进程峰值范围包含该进程的创建和此前场景；单场景独立进程可得到各自的进程峰值范围。

每个场景声明零热身步数，全部原步骤与首步成本进入采样，并单列首步耗时及当前进程 JIT 配置。`WithinBudget` 表达所测整步最大耗时与该场景预算的比较；行为验收继续核对相应场景的运动、约束间距和事件结果。

附加 `--physics-trace=<外部路径>` 使用现有 `EngineTrace` 捕获同一运行窗口。具体阶段包装记录 `Schedule` 提交耗时和依赖就绪至完成的区间；两个范围可以重叠，分别呈现。阶段诊断为每段增加 Start 与 Finish Job，开销随诊断结果保留。普通预算运行沿同一工厂选择 `timings=null`。原分区准备、pair count、bin prefix、pair write 和 narrow phase 的 Trace 站点进一步分解碰撞成本，并记录候选数和检测数。带 trace 的定位与普通整步预算分别比较，测量沿根级公共队列提交，捕获消费后清理。

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
