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

[主体创建](../../../samples/DefaultGame/PhysicsScenes/CreatePhysicsSceneBodies.cs)按输入中连续相同的形状组件分批，沿 `World.ExecuteBundleSpawnBatch` 一次声明主体、位姿、质量、速度、步缓存、活动、范围、材料、变换和形状组件。行回调写入该条原输入，并按同一索引交回 Entity；盒体、球体和共享凸包由项目的具体几何类型写入。原 `Bodies` 顺序继续提供关节、距离边和定时事件的实体引用。设备路径先按原创建容量构造 `DeviceBodies`、方程和几何对象，再通过项目内的具体 `SceneDeviceBodyBindings` 将三个绑定加入同一次主体创建批次；绑定沿原索引指向这些对象。

二维场景保留面积质量和仅 Z 轴转动惯量，几何 Z 厚度用于原范围与显示。调用方显式选择 `PlanarBoxCollision` 或三维具体窄相；活动角轴的共同方程见[求解阶段](Solver-Steps.md)。默认场景摩擦合成使用两侧系数的几何平均。

## 共同物理步和输入时刻

工厂组合原定时输入、陀螺预测、共同预测、预测范围、空间维护、分桶、具体检测、持久点刷新、响应初始化、图求解和速度重建。接触、关节和显式距离区间通过具体约束集合进入同一 AVBD 图求解。

CPU 场景通过 `solve.Activity` 共享原求解图，`BodyActivity` 单处保存休眠和安静时间。输入后的唤醒、检测后的连通唤醒及 `PredictActiveBodies.Resume()` 继续同一子步，完成后按显式 `PhysicsSceneSettings.Activity` 阈值判断休眠。相关工作范围与误差见[Body 活动](Body-Activity.md)。

`PhysicsSceneSettings.JointBounds` 给出所选自动长程界的层数、罚参数缩放、乘子保留和初始误差比例。正层数为场景原关节附加 `LongRangeJointBound` Buffer，并沿原 `JointLongRangeGraphConstraints` 进入同一图求解与活动误差；零层数保持空工作查询。原关节实体、受保护端点和框架继续提供唯一的拓扑事实。

`PhysicsSceneSettings.ContactSupports` 选择接触长程支撑及其向上方向、接触资格和数值参数。所选动态 Body 保存 `LongRangeBodySupport`、载荷与当前支撑方程，原持久接触实体保存 `LongRangeContactSupport` 和载荷引用。`AvbdSupportedContactMath` 与 `ContactLongRangeGraphConstraints` 复用同一次 Body 分解、原几何间距和当前反力；普通接触继续使用自己的具体约束集合。支撑工作同时参与原活动误差判断。

带具体 `IPhysicsEventConsumer` 的 `CreateCpu` 重载把接触和活动结果交给原帧消费窗口。`ScenePhysicsEventStream` 标记完成步事件实体；`ConsumePhysicsEvents` 将截至当前完成步的全部历史交给调用方，再通过原命令链确认这些实体。调用方传入 `eventAccesses` 声明消费者资料；原四参数和场景数据入口委托同一创建与事件链。

`PhysicsSceneSettings` 显式保存外层迭代、每体局部迭代、乘子保留比例、图组织、接触距离和整步预算声明。CPU 与 GPU 读取同一乘子保留字段，工作量描述同时列出该值。原步长及子步数来自创建数据。输入的 `Frame` 表示物理步编号；连接移除与位置变化在该步的第一子步、共同预测之前沿原命令链回放。速度增量按 `mass * deltaVelocity` 转成世界冲量，由 `ApplyBodyInputs` 在对应时间区间应用。

`ContactNormalPenalty` 和 `ContactTangentPenalty` 分别选择新接触的法向、切向 ALM 罚度量，`FrictionRegularizationSpeed` 选择滑移曲率的正则化速度。CPU 与 GPU 工厂都将这些值交给原 `AvbdContactComponents`，在持久接触实体创建时写入 `AvbdContactState`；后续共同求解在该实体的所选表示中继续更新乘子和反力。接触材料刚度由同一实体的 `ContactMaterial` 给出。

CPU 场景选用项目的 `SceneContactPenaltyMath<TMath>`，由现有 `IAvbdConstraint<ContactPoint>` 组合普通或长程支撑接触。每步根据步长平方、当前接触雅可比和参与体的逆质量/世界逆惯量计算初始度量，并保留原点的罚参数历史；每轮先完成原接触双变量更新，再按穿透和压力长度尺度更新同一 `AvbdContactState` 中的罚参数。项目显式选择最小值 1、最大值 1e9、每米增长量 100000 和 0.0005 m 压力长度尺度，工作量描述列出这些选择。

内层具体类型为 `AvbdContactPotentialMath` 和 `AvbdSupportedContactPotentialMath`：共同使用原接触的法向、切向力和乘子更新，普通接触保留正的法向曲率上界，角向方程加入接触合力大小乘力臂长度的正曲率。所选长程支撑的附加反力继续参与摩擦投影与角向曲率。原 2D 与 3D 掉落金字塔沿公共完成帧连续检查 180 步机械能，空间金字塔同时验证原层高对应的落稳能量与线速度，行为入口见 [PhysicsScenePyramidTests](../../../tests/SomeEngine.Runtime.Tests/PhysicsScenePyramidTests.cs)；工作量描述同时记录两个具体方程类型。

`PhysicsSceneSimulation.Frame` 引用原 `PhysicsFrame`。帧消费者从实体 `BodyPose` 更新同一实体的 `LocalTransform`，保留其几何缩放；`CompleteForRead()` 直接完成该帧与消费者的原 Job 链，并交回完成步编号和时刻。下一步继续依赖同一消费完成。完成边界见[物理帧](Physics-Frames.md)。

`PhysicsSceneSimulation` 同时实现 `IGameFramePhysics`，将时间推进、完成消费、后续提交和完成句柄委托给上述同一帧。`GameFrameSystems.Physics` 持有整个场景对象，沿既有帧生命周期先完成物理及消费者，再释放场景查询和派生资源。

## 统一场景平台与测量

[PhysicsSceneExample](../../../samples/DefaultGame/PhysicsScenes/PhysicsSceneExample.cs) 将原目录接入 `TestScene.Initialize` 与 `DescribeWorkload`。`CreateCpu` 与 [CreateDevice](../../../samples/DefaultGame/PhysicsScenes/DevicePhysicsSceneExample.cs) 分别选定对应工厂，沿内部数据重载消费同一份创建输入。原 Body 实体附加 `WorldTransform`、盒体 `MeshInstance` 与项目材质 Buffer；项目明确选择 `StandardClusterVertexEncoder`。`LocalTransform` 经原 `TransformSystem`、`MeshRenderExtractor` 进入 Rendering world，场景相机根据原动态 Body 的范围定位。

DefaultGame 的 `--physics` 选择完整物理目录，`--physics=<id>` 选择一个原场景。CPU 示例明确选择 AVBD、BVH refit、二维 `PlanarBoxCollision` 或三维 `ConvexCollision`、8 轮外迭代、每体 1 轮、6 层关节长程界、接触长程支撑、0.02 m 接触距离和每步 1000/60 ms 预算。工作量描述包含实际实体与约束规模、原步长、子步、图组织、原事件以及完成事件统计；每种事件分别列出首末物理步和事件时刻。

`--physics-device=gpu` 选择设备示例，指定 ID 同时查询 `PhysicsScaleScenes.All`。交互运行通过原 `IApplicationGeometryFeature.Install` 获取应用的 Backend 与 Device；创建、完成步、变换及网格提取继续采用上述共同路径。调用方也可向 `PhysicsSceneExample.CreateDevice(description, backend, device)` 直接提供具体设备。

| 配置 | CPU 示例 | GPU 示例 |
| --- | --- | --- |
| 空间候选 | BVH refit | 分层网格，基础单元 1 m、16 层 |
| 候选过滤 | 主体运动类别与原 `SceneCollisionExclusion` 关系 | 主体运动类别与原 `SceneCollisionExclusion` 关系 |
| 检测 | 二维 `PlanarBoxCollision`；三维 `ConvexCollision<BoxGeometry, BoxGeometry>` | `DeviceConvexCollision<DeviceBox, DeviceBox>` |
| AVBD 外层迭代 | 8 | 16 |
| AVBD 每体迭代 | 1 | 2 |
| 关节长程界层数 | 6 | 既有目录 6；大规模目录 0 |
| 接触约束 | 普通接触与长程支撑 | 普通接触与长程支撑 |
| 接触距离 | 0.02 m | 0.02 m |
| 乘子保留比例 | 0.8 | 0.8 |
| 整步预算 | 1000/60 ms | 1000/60 ms |

GPU 示例按 Body 数量派生网格单元、桶、pair 和接触容量，工作量描述记录这些容量、所选设备名称、驱动与专用显存。接触、原关节和原显式距离边共同进入设备约束图；参数与数值历史沿原实体的设备表示保存，完成后读取所选 Body 并更新原 `LocalTransform`。

`million-activity` 选择原百万动态盒体和共享地板输入、16×2 AVBD、1 m/16 层网格、100 万 pair 与 400 万接触容量、法向与切向罚参数各 1e6。该项目配置使用普通接触约束，读取全部 1,000,001 个主体结果并更新原变换，完成步消费者记录 Sleep 与 Wake；工作量中的读取范围与计数明确对应全体主体。原第 180 步局部冲量和第 360 步共享地板位移使统一测量窗口覆盖 481 步。

`PhysicsSceneSettings.GeometryInputsFixed` 声明形状参数和主体引用在仿真期间固定。场景首次步通过原 `WriteGeometryReferences` 与 `UpdateDeviceGeometry(..., Upload)` 上传这些输入，后续步使用同一几何和网格的 `UpdateDeviceGeometry(..., Resident)`，沿当前设备位姿重算几何与候选范围。首次上传计入第一步耗时；主体的位姿输入、冲量和运动目标继续沿原主体输入入口生效。GPU 盒体目录选择该配置，并在工作量描述中记录该值。

`PhysicsSceneSettings.EquationBindingsFixed` 声明主体与方程行的绑定固定。首次步选择原 `DeviceAvbdSolver.Solve(Upload)`，后续步选择 `Solve(Resident)`；每个子步仍初始化当前数值方程并执行实际约束。GPU 示例同时记录这项配置。反弹阶段沿本步已经完成的约束图计算，后续活动阶段继续交付当前完成步结果。

设备候选规则沿共享 `DeviceBodies` 读取运动类别。`DeviceScenePairExclusions` 从原关系端点派生排序后的主体索引对，并沿原查询的成员版本刷新；设备规则组合既有 `DeviceCollisionRules` 和该索引查询。场景先建立共享主体及约束图，再通过原 `graph.AddBindings` 将同一主体表示交给网格。

GPU 示例选择 `DeviceContactPotentialAvbdConstraints` 的正法向和角向势能曲率，并沿项目的 `SceneContactAvbdConstraints` 按时间步、Jacobian mobility、当前法向压力和残差更新 ALM 罚度量。初始法向与切向值均为 100，正则化速度为 0.01 m/s；实际参数、上下界和压力长度尺度一并写入工作量描述。

`PhysicsSceneSettings.ContactSupports` 选择原 `DeviceContactSupports`：动态主体通过 `DeviceSupportBodyBinding` 参与支撑，同一接触实体通过 `DeviceAvbdState<DeviceContactSupportState>` 选择支撑历史。输入上传后初始化支撑工作缓冲，原约束图加入支撑主体行，AVBD 在各轮更新承重路径与反力，活动阶段读取同一支撑高度误差。示例按场景维度选择 Y 或 Z 向上，并沿 CPU 示例的容差、乘子保持率和初始误差比例装配。

`PhysicsSceneSettings.JointBounds` 同时选择原 `DeviceJointBounds`。原关节上的 `LongRangeJointBound` 保存推导出的另一关节、端角色和距离区间；共同 `DeviceJointConstraint` 保存原帧和主体索引。拓扑维护先更新这些原元素，再上传对应参数和历史，界参与同一约束图、AVBD 与活动误差。`JointBoundsPerJoint` 显式提供每关节的界容量；值为零时，场景按初始关节对的每节点上界派生容量，并在实际设置与工作量描述中交付结果。

关节受力沿 `DeviceConstraintForceSamples<DeviceJointConstraint>` 读取当前原状态，通过原 `BreakConstraints` 检查同一实体的 `ConstraintBreakSettings`，使用原命令链移除过载关节。公开 `CreateDevice<TEvents>` 重载把这些完成步事件交给具体消费者；下一个子步沿原成员及参数维护释放该约束。

`CreateDevice<TEvents>` 同时安装 `DeviceContactSamples` 和原 `ContactEvents<ScenePhysicsEventStream, DeviceContactEventSource>`。原接触实体保存该流的 `ContactEventState`；设备采样提供完成后的几何和当前反力，接触生命周期沿原历史交付 Begin、Stay 与 End。采样接在速度、反弹和活动结果之后，接触结束与关节断裂、休眠唤醒共同进入原完成步事件缓冲，再交给同帧消费者。

设备场景在同一帧消费阶段从当前接触几何、主体活动和图 Parents 归约全局结果，仅传回 16 字节的最大穿透、活动主体数、接触点数和最大连通主体数。`PhysicsSceneSimulation.DeviceAwakeBodies`、`DeviceContactPoints`、`DeviceLargestIslandBodies` 交付这些完成步结果；岛大小工作缓冲按原主体容量复用。

`ReadbackBodies` 列出本次实际读取位姿和速度的原主体引用。测量中的 `BodyMotionScope` 与 `ConstraintErrorScope` 声明位姿、动量、速度和两端约束误差的覆盖范围；全局接触、活动及连通统计继续来自上述设备归约。`LastBodyPosition` 与速度对应所选读取列表的最后一个主体，`LastBodyEntityIndex` 标明其实体索引。

全体读取沿原 `ScenePhysicsBody` 成员过滤、批量 `DeviceBodyResults` 和同一变换消费查询执行。部分读取通过 `SceneReadbackBody` 标记实际选择的实体，保持显式读取列表及对应容量；结果观测使用同一完整或部分成员范围。

`--physics-content=<目录>` 将 DefaultGame 现有已构建内容目录交给原 `TestScene.ContentDirectory`，共享其材质模板、灯光和渲染配置。示例显式接收项目的 `StandardMaterial` 模板，为静态与动态 Body 选择不同颜色及少量自发光，共用同一盒体几何。

`TestScene.WorldNameplates` 在首次创建和场景切换时选定该内容的名牌配置。物理示例以原 Body 取代加载内容的网格实体，选择关闭原小球名牌；返回原内容时恢复启动配置。

[MeasurePhysicsScenes](../../../samples/DefaultGame/PhysicsScenes/MeasurePhysicsScenes.cs) 通过 `--physics-check` 运行全部原目录，`--physics-check=<id>` 运行单个场景。物理窗口为至少 180 步，并覆盖最后一次原输入及其后 120 步运动。每次同步完成一个原物理步；采样时钟保留半步的提交余量，报告的结果编号与时刻继续采用原物理区间。

附加 `--physics-device=gpu` 通过 `RunDevice` 在所选 RHI 后端的计算设备上执行同一个测量循环。`--physics-check=bricks-110000 --physics-device=gpu` 是连续砖结构的入口例子；公共队列同时覆盖其构建、启动与测量。每个结果继续携带该次实际设置与预算比较。

渲染帧统计使用既有第 128 帧起的就绪窗口，窗口长度随上述完整场景时长声明；物理整步统计从原步骤 0 开始。统一渲染三场景仍由 `--regression` 选择其原目录与测量时限。

主计时从 `Frame.AdvanceTime` 开始，至 `CompleteForRead` 完成原变换与事件消费者结束，包含所有阶段的调用线程工作和异步完成等待。结果列出整步平均值、p95、p99、最大值、当前及峰值实体/接触/活动量、真实连接与几何结果、最终动态体质心及范围、总线动量、最终线/角速度及其全过程峰值、托管存活内存及进程峰值工作集。原输入线动量同时列于工作量描述，供自由碰撞与惯性场景核对。诊断读取另列耗时。进程峰值范围包含该进程的创建和此前场景；单场景独立进程可得到各自的进程峰值范围。

观测同时读取初始、当前和峰值机械能，定义为平移动能、体空间转动动能与重力势能之和。重力向量来自同一工作量的公开 `Gravity`；势能零面位于从世界原点沿重力方向两米处，与原金字塔行为检查使用相同基准。`MechanicalEnergyScope` 声明全体动态主体或所选读回动态主体的覆盖范围。每个原输入步记录其前一完成步和当前完成步的能量及差值；该区间覆盖声明的速度、位移或连接输入以及当步模拟与耗散，行为判据结合对应场景的原输入解释这些数值。初始读取在场景创建后进行，每步读取沿原诊断观测，整步计时保持上述完成边界。

每个场景声明零热身步数，全部原步骤与首步成本进入采样，并单列首步耗时及当前进程 JIT 配置。`WithinBudget` 表达所测整步最大耗时与该场景预算的比较；行为验收继续核对相应场景的运动、约束间距和事件结果。

附加 `--physics-trace=<外部路径>` 使用现有 `EngineTrace` 捕获同一运行窗口。具体阶段包装记录 `Schedule` 提交耗时和依赖就绪至完成的区间；两个范围可以重叠，分别呈现。阶段诊断为每段增加 Start 与 Finish Job，开销随诊断结果保留。普通预算运行沿同一工厂选择 `timings=null`。原分区准备、pair count、bin prefix、pair write 和 narrow phase 的 Trace 站点进一步分解碰撞成本，并记录候选数和检测数。带 trace 的定位与普通整步预算分别比较，测量沿根级公共队列提交，捕获消费后清理。

## 混合形状料仓

`PhysicsSceneExample.CreateSiloDevice` 通过同一 GPU 工厂选择 Sphere、Box 与共享八面体 Hull，
按实体序列轮流分配三种形状。颗粒质量为 1 kg、半径或半边长为 0.2 m，
材料采用系数 0.5 的 Coulomb 摩擦和零接触阻尼；Body 位姿、速度与设备表示沿原实体保存。
六种具体双向类型 bin 共同消费原候选，进入同一接触历史、约束图、AVBD、活动与完成帧。
当前项目选择 16 轮外迭代、每体 2 轮，步长为 1/60 s。

规模目录中的 `silo-100000` 与 `silo-500000` 分别选择十万和五十万颗粒，
交互平台通过 `--physics=silo-100000 --physics-device=gpu` 进入同一创建和显示过程。
`--physics-check=silo-100000 --physics-device=gpu` 沿同一工厂测量完整 301 步，
五十万规模使用对应的 `silo-500000` 标识。两者从原步骤 0 开始，闸板在步骤 180 移开。
默认料仓消费全部主体的位姿、速度与步号时刻，沿既有整段设备结果读回后写入原实体，
`BodyPose → LocalTransform → MeshInstance` 使用同一场景变换提取链。
工作量中的 `ReadbackBodyCount` 与完成结果中的实际主体范围一同描述该成本，
整步预算包含全部可见主体的完成消费。

24 粒行为检查沿全部颗粒读取完成后的位姿和速度：闭闸的 180 步内，
颗粒下降、保持在内侧边界 `|x|, |y| ≤ 3 m`，并由当前接触关系承托。
闭闸结束时的全体机械能与创建能量使用同一 2 m 势能基准；
该区间的材料耗散结合重力势能、平动及转动动能解释。
球的滚动采用接触点速度 `v + ω×r` 区分质心运动与接触滑移，Coulomb 材料允许近似无滑动滚动。

第 180 步移开中央闸板，实际出口覆盖 `|x|, |y| ≤ 2 m`，外圈固定底板继续承托颗粒。
检查跟踪全部颗粒实际穿过出口平面的集合，并在完整 301 步中验证三种形状各有颗粒泄流到 `z < -2 m`。
该集合中的当前接触关系同时体现与固定容器的接触退出。
世界中的匹配接触保留原几何，当前设备锚点由 `DeviceContactGeometry.Update` 写入同一设备参数与 Body 引用；
设备穿透统计沿原 `DeviceSceneStatistics` 读取当前几何。

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
