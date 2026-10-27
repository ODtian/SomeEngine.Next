# 物理动画角色与骨骼布料

项目入口是 [PhysicsCrowdExample](../../../samples/DefaultGame/PhysicsScenes/PhysicsCrowdExample.cs)
的 `CreateDevice(characters, backend, device)`，随后以 `CreateSimulation(world)` 创建同一
[PhysicsCrowdScene](../../../samples/DefaultGame/PhysicsScenes/PhysicsCrowdScene.cs)。
角色定义复用原 `CrowdExample` 的 96 骨、walk 片段与四权重蒙皮数据。
每个角色的 20–23 骨对应一条实际实体链，4×4 布料骨继承角色骨架，
通过纵向、横向、对角 Length 和纵横 Bend 连接模拟节点。

每个角色包含 4 个角色 Body、16 个布料 Body、45 条 Length、18 条 Bend 和一个 Motor。
偶数角色选择 `JointMotorSpace.World`，奇数角色选择 `JointMotorSpace.Source`。
原 JointConstraint 关系保存两个实际端点，Motor 参数、目标及 AVBD 历史继续位于该关系实体。
场景集合保存目标、Body 和驱动关系的实体引用。

`DeviceCrowdMotorTargets` 在 `WriteTargets` 后读取同一个 `DevicePoseSources.Models`，
以已绑定角色引用及其模型范围取得当前手部和根骨位置。
世界空间驱动使用手部世界位置，父空间驱动沿当前源关节框架表达相对位置。
原 `DeviceMotorSource.Read` 继续读取组件中的系数、作用上限与作者目标；
GPU 将动画位移叠加到该目标，并把位置、速度和完成时间写入原 `DeviceMotorConstraint`。
上一目标保留在这条设备参数行，用于计算本步目标速度。初始姿势建立首个目标，
原 `DeviceAvbdStateInputs` 继续接收显式历史编辑，AVBD 沿同一参数及历史求解。

`DevicePosePhysics` 创建 Body、Length、Bend 及其所选设备表示。
`DevicePoseSources` 读取原动画来源，以 `WriteTargets` 在统一预测前写入骨骼目标。
同一约束图包含 Length、Bend 和 Motor；AVBD 读取 XPBD 同源投影的试算力与响应，
XPBD 继续更新原 Length/Bend 乘子并投影节点。完成一次速度重建后，
`MapBones` 和 `CopyOutput` 沿原 Game 姿势输出交付 Number、Time 与骨骼矩阵。
场景通过原 `IGameFramePhysics` 推进及消费完成步。

共同图在方程初始化前建立，AVBD 的 `Initialize(false, equationInputs)` 使用同次图和已捕获参与。
项目在设备请求 `IndirectCommands` 时，为 XPBD 选择原图行范围生成的间接派发参数；
原类型组、颜色次序、投影及乘子沿同一约束图推进。

约束参数的 `WriteDeviceConstraintInputs` 读取原选定实体引用，
以调用方的 `rowsPerPacket` 交给 `EntityQueryJobs`。每包在一个来源查询范围内
调用原 `IDeviceConstraintSource.Read(component, row, access)`；组件值和写版本从实际行取得，
各个 `DeviceConstraintBinding.Index` 继续登记自身参数及元数据的上传写入范围。
项目沿原查询成员版本和块版本复用已绑定的 Length、Bend 与 Joint 输入。
主体绑定、约束参数、双端引用、参与关系、弯曲引用缓冲、Joint 历史或其绑定发生变化时，
原输入阶段重新读取组件并更新同一个设备表示；动画阶段继续直接维护实际段长。
Motor 的当前动画目标继续每步写入原设备参数行。

`CreateDevice(characters, backend, device, timings)` 接受原 `PhysicsSceneTimings`。
来源采样、参数输入、碰撞维护、约束图、方程初始化、每轮 AVBD/XPBD、
速度、骨骼输出和主体完成读回使用原 `Measure` 阶段；`timings.Describe()`
交付这些阶段的提交与依赖到完成耗时。原三参数入口沿同一装配推进。
输入阶段另记录主体组件、约束组件和 Motor 组件历史的耗时。

完成主体结果沿原 `DeviceBodyResults` 写回全部所选实体。
读取包按原实体捕获设备索引，写入 Job 按实际主体索引登记 CPU 版本数组中的元素，
原写查询在当前 chunk 一次绑定三列，按捕获实体写入位姿、速度和完成时钟，
并把该查询的真实共享写版本确认回原主体资源。

时段输入沿 `DeviceBodyInputBuffers` 的原查询块读取。
包含 `BodyForce`、`BodyImpulse`、`BodyVelocityTarget` 或 `BodyKinematicTarget` 的块进入原逐行合并，
各行继续按当前成员和同一时间窗选择实际作用量与运动目标。

Length 与 Bend 的投影共用 `physics_xpbd_projection_math.slang` 中的乘子增量，
其来源为 [XPBD 论文式 18](https://mmacklin.com/xpbd.pdf)。AVBD 沿当前位置、原乘子、
柔度、实际作用端 mobility 和步长计算试算力 `(λ + Δλ) / h²`，同时加入
`J Jᵀ / (h² mobility + compliance)` 的正响应矩阵；Bend 延续原非负乘子范围。
试算值在当前方程中使用，原乘子仍由 XPBD 初始化和更新。

`DeviceCrowdContacts` 为角色臂的三个相邻节点段创建 `DynamicCapsule`，
为每块布料的九个四边区域创建十八个 `DynamicFace`。几何实体通过原端点引用读取同一
`DeviceBodies`，更新设备几何和范围后进入分层网格及胶囊/面域类型 bin。
两个具体的 Capsule/Triangle 泛型检测方向保留实际几何实体的先后顺序。
每个接触的 `DeviceContactBodies` 总容量为 5，分别保存胶囊的两个端点和三角面的三个端点。
本项目在创建时固定这些形状参数、几何实体及其主体引用；首个物理步读取并上传这些输入，
随后沿原 `DeviceInputTransfer.Resident` 复用同一表示。
Box、Capsule 与 Face 每步继续读取当前主体状态，更新实际几何及候选范围。
原检测结果、匹配、当前几何更新和 `ContactPoints` 创建共同接触实体；
其 AVBD 接触参数、状态和作用端加入上述同一图。接触响应的组范围与图中的行范围对应。

公开行为 `PhysicsCrowdSceneTests.CharactersAndTheirBoneClothCompleteTheSharedPhysicalFrame`
运行 2,000 个角色及各自布料，共 40,001 个 Body（含地面），90,000 条 Length、
36,000 条 Bend 和 2,000 个 Motor。末角色的实际实体接收 3 N·s Z 向冲量，
同一完成帧交付其位移、速度、Body 步号/时刻以及对应骨骼设备输出。
该公开行为在同一场景对象连续推进 180 个完成步，观察全体主体的位置、速度范围和接触实体负载，
并记录整步均值、P95、P99、最大值及进程峰值工作集；首步沿同一计时边界保留。

`ACharacterCapsuleMovesAnotherCharactersBoneClothInTheirCompletedFrame` 沿原组件移动第二个角色及布料，
其臂端冲量通过胶囊/面域接触推动第一个角色的布料节点，并交付该布料的同帧骨骼输出。

`CreateMixedDevice(backend, device, timings)` 的设计配置复用上述角色和布料，
将原关节网生成器的 250 列、130 行和 6,755 个对角连接加入同一主体表示。
625 个角色提供 2,500 个臂体和 10,000 个布料点；网格的第一个静态锚使用同一地面实体，
另外 32,499 个节点保留原 Box、质量、惯量和关节局部锚。
刚体总数为 `2,500 + 32,499 + 1 = 35,000`，连同布料共 45,000 个主体。
关节总数为横向 32,370、纵向 32,250、对角 6,755 和角色的 625 个驱动关系，共 72,000。
网格沿 Y 向上坐标表达原位置、锚点与第 120 步面外冲量；原 100,000 节点入口继续使用完整对角连接。

普通 Joint 在 Length、Bend、Motor 之后进入同一约束图和 AVBD，接触位于这些约束之后。
XPBD 保留同样的行范围，更新原 Length/Bend 乘子；Joint 的数值历史由 AVBD 持有。
混合网格继续从原 `DeviceGeometryReference` 取得 Box 的实际主体索引，
沿原相邻碰撞排除关系筛选 Box 对；Box/Box、Box/Capsule、Box/Face 与 Capsule/Face
通过连续类型 bin 进入同一个检测、匹配、接触历史和响应数据。
全体主体继续在同一 Frame 完成后交付原 CPU 组件，角色和布料继续交付原 GPU 骨骼输出。

参数入口 `CreateMixedDevice(characters, columns, rows, diagonalJoints, backend, device, timings)`
复用同一装配。行为检查中的 `2, 4, 6, 8` 是小型验证例子：32 个刚体、32 个布料点和
48 个关节；实际移动其中一个网格 Box，使其接触骨骼布料，并观察共同完成号、位移和骨骼输出。
独立的静态锚行为观察普通 Joint 对下方动态节点的约束。
完整规模检查沿声明的 35,000/72,000/10,000 配置连续推进 180 步并报告整步与各阶段成本。
