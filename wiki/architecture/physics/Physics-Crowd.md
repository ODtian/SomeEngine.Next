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

`DevicePosePhysics` 创建 Body、Length、Bend 及其所选设备表示。
`DevicePoseSources` 读取原动画来源，以 `WriteTargets` 在统一预测前写入骨骼目标。
同一约束图包含 Length、Bend 和 Motor；AVBD 读取 XPBD 同源投影的试算力与响应，
XPBD 继续更新原 Length/Bend 乘子并投影节点。完成一次速度重建后，
`MapBones` 和 `CopyOutput` 沿原 Game 姿势输出交付 Number、Time 与骨骼矩阵。
场景通过原 `IGameFramePhysics` 推进及消费完成步。

`CreateDevice(characters, backend, device, timings)` 接受原 `PhysicsSceneTimings`。
来源采样、参数输入、碰撞维护、约束图、方程初始化、每轮 AVBD/XPBD、
速度、骨骼输出和主体完成读回使用原 `Measure` 阶段；`timings.Describe()`
交付这些阶段的提交与依赖到完成耗时。原三参数入口沿同一装配推进。

Length 与 Bend 的投影共用 `physics_xpbd_projection_math.slang` 中的乘子增量，
其来源为 [XPBD 论文式 18](https://mmacklin.com/xpbd.pdf)。AVBD 沿当前位置、原乘子、
柔度、实际作用端 mobility 和步长计算试算力 `(λ + Δλ) / h²`，同时加入
`J Jᵀ / (h² mobility + compliance)` 的正响应矩阵；Bend 延续原非负乘子范围。
试算值在当前方程中使用，原乘子仍由 XPBD 初始化和更新。

`DeviceCrowdContacts` 为角色臂的三个相邻节点段创建 `DynamicCapsule`，
为每块布料的九个四边区域创建十八个 `DynamicFace`。几何实体通过原端点引用读取同一
`DeviceBodies`，更新设备几何和范围后进入分层网格及胶囊/面域类型 bin。
两个具体的 Capsule/Triangle 泛型检测方向保留实际几何实体的先后顺序。
原检测结果、匹配、当前几何更新和 `ContactPoints` 创建共同接触实体；
其 AVBD 接触参数、状态和作用端加入上述同一图。接触响应的组范围与图中的行范围对应。

公开行为 `PhysicsCrowdSceneTests.TwoThousandCharactersAndTheirBoneClothCompleteTheSharedPhysicalFrame`
运行 2,000 个角色及各自布料，共 40,001 个 Body（含地面），90,000 条 Length、
36,000 条 Bend 和 2,000 个 Motor。末角色的实际实体接收 3 N·s Z 向冲量，
同一完成帧交付其位移、速度、Body 步号/时刻以及对应骨骼设备输出。
该公开行为在同一场景对象连续推进三个完成步，并观察全体主体的位置与速度范围。

`ACharacterCapsuleMovesAnotherCharactersBoneClothInTheirCompletedFrame` 沿原组件移动第二个角色及布料，
其臂端冲量通过胶囊/面域接触推动第一个角色的布料节点，并交付该布料的同帧骨骼输出。
