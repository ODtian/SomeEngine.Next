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
同一约束图包含 Length、Bend 和 Motor；AVBD 读取 XPBD 的当前乘子力，
XPBD 继续更新原 Length/Bend 乘子并投影节点。完成一次速度重建后，
`MapBones` 和 `CopyOutput` 沿原 Game 姿势输出交付 Number、Time 与骨骼矩阵。
场景通过原 `IGameFramePhysics` 推进及消费完成步。

公开行为 `PhysicsCrowdSceneTests.TwoThousandCharactersAndTheirBoneClothCompleteTheSharedPhysicalFrame`
运行 2,000 个角色及各自布料，共 40,001 个 Body（含地面），90,000 条 Length、
36,000 条 Bend 和 2,000 个 Motor。末角色的实际实体接收 3 N·s Z 向冲量，
同一完成帧交付其位移、速度、Body 步号/时刻以及对应骨骼设备输出。
