# 设备 XPBD 参数与投影

本页维护已实现的设备约束参数、原实体绑定与具体 XPBD 投影入口。
源码入口为 [DeviceConstraintData](../../../src/SomeEngine.Physics/DeviceConstraintData.cs)、
[输入阶段](../../../src/SomeEngine.Physics/DeviceConstraintInputs.cs)、
[数值历史](../../../src/SomeEngine.Physics/DeviceConstraintState.cs) 和
[DeviceXpbdProjection](../../../src/SomeEngine.Physics/DeviceXpbdProjection.cs)。
公开设备步行为见 `DeviceXpbdTests`。

## 原实体与设备数据

`DeviceConstraintBinding<TData>` 放在原约束实体上，保存共同参数资源及其中的行索引。
`DeviceConstraintData<TData>` 保存原物理参数的所选设备表示；每行 metadata 保留实际
Entity 索引、代次和上传时的查询参与标记。`BeginInputs()` 开始本次成员写入，
`WriteInputs<TComponent,TSource>(world,rows,source,rowsPerPacket)` 沿原查询筛选读取原组件，
由具体 `IDeviceConstraintSource<TComponent,TData>` 解析参数与跨实体引用。
`UploadInputs()` 将本次参数和成员 metadata 提交设备。

source 的 `AddReads`、`AddReadResources` 与静态组件资源描述器声明真实读取。
生成式 Job 的成员筛选由原定义的 `AsFilter()` 提供，原读取定义保持实际参数与引用访问。
`AddBindings` 使用项目提供的资源名与其 `Metadata` 后缀绑定 Slang 资源；
`GetReadAccesses` 为后续具体设备工作提供两个实际 buffer 的访问描述。

`DeviceXpbdState<TState>` 同样放在原约束实体，选择该约束的 XPBD 历史资源；
参数绑定中的行索引同时定位数值历史。
`DeviceConstraintState<TState>` 单处保存设备乘子，参数上传与投影分别声明自己的 buffer。
投影从原查询中的参数和历史绑定选择本次实际行，GPU 直接更新该行历史和共同
`DeviceBodies.States`。独立投影的索引列表同时保存本次选中实体的索引与代次，
内核将其与同一参数 buffer 的 metadata 匹配后执行；参数刷新沿原 writer 与上传阶段完成。

## 具体内核与阶段

`DeviceXpbdProjection<TData,TState>` 接收项目编译的初始化/投影入口、线程组大小及
额外资源绑定和访问。Slang 的 `IDeviceXpbdProjection` 提供 `active`、`initialize` 与
`project`，内置与用户具体内核使用同一调用入口。
项目具体 source 可以从已声明的原行读取自己的参数，Slang 具体投影可同时读取项目
提供的额外资源；两者的结果继续沿同一个物理步完成链返回原实体。
`Initialize()` 初始化所选行的子步历史，`Iterate(n)` 在相同子步内累计乘子。
同轮可同时写入的行由调用者提供的独立约束范围决定。
该入口同时接收 `DeviceBodyDynamics`，通过其原选择成员标记限定可写 Body。
每轮之间沿 RHI UAV barrier 保证前轮写入对后轮可见；提交和完成沿共同设备 Job/fence 链。

[DeviceLengthSource](../../../src/SomeEngine.Physics/DeviceChainConstraints.cs) 从原
`XpbdLength` 关系端点解析主体索引。16 字节参数保存两端索引、静止长度与柔度，
独立 float 历史保存原 XPBD 位移乘子。
[DeviceXpbdLength](../../../assets/Shaders/physics_xpbd_length.slang) 使用原
`Compliance / h²`、逆质量和累积乘子公式，只修正动态端的位置。
共同预测、速度与原组件消费由[设备主体](Device-Bodies.md)维护。

`DeviceXpbdTests.CompletedDeviceLengthsAndBendsProduceTheMappedSkeletonBones` 通过同一
公开设备步组装 Length 与 Bend 的独立约束范围，读回原 `BodyPose` 后接已有
`PhysicsPoseInstance.BindBodies` 和 `ReadPhysicsPose`。
`PhysicsPoseSystem` 继续将这些完成结果写入原 `SkeletonInstance` 的局部与模型骨骼姿势；
骨骼掩码、局部框架和后续提取沿原[物理姿势映射](../Animation.md)消费。

`DeviceBendSource` 从原 `ConstraintBodyLink` 顺序读取三个受保护参与端，
通过 `ConstraintBodies.GetBody(link,access)` 解析实际 Body，再转换为本轮执行索引。
32 字节参数保存三个索引、最小方向余弦和柔度；端点身份继续由原参与关系持有。
[DeviceXpbdBend](../../../assets/Shaders/physics_xpbd_bend.slang) 复用原三端方向余弦梯度，
将累积乘子投影到大于等于零的范围，按各动态端的逆质量修正原位姿。

## 关节与 motor

[DeviceJointSource](../../../src/SomeEngine.Physics/DeviceJointData.cs) 从原关节实体读取双方
局部框架、六轴运动/限位、物理刚度和受保护端点，转换为 208 字节 `DeviceJointConstraint`。
`DeviceMotorSource` 从同一实体读取驱动、目标和目标时刻，192 字节 `DeviceMotorConstraint`
通过 `JointRow` 引用这份框架与端点参数。两者沿共同 carrier 和 source writer 上传。

[DeviceXpbdJoint](../../../assets/Shaders/physics_xpbd_joint.slang) 逐轴使用共同关节坐标与
Jacobian，采用 `1 / (K h²)` 和累积位移乘子；限位乘子按共同单边方向投影。
修正通过共同 Body 数学作用于质心与完整逆惯量。
[DeviceXpbdMotor](../../../assets/Shaders/physics_xpbd_motor.slang) 使用同一六轴数学，
按子步结束时刻采样目标位置/旋转和速度，将原阻尼按 `D / h` 合入等效刚度与平衡点。
物理力/力矩上限转换为乘子上限 `Maximum * h²`。
Source/World 空间和 Target/Both 反馈由原 motor 参数选择。

`DeviceXpbdState<XpbdJointState>` 与 `DeviceXpbdState<XpbdMotorState>` 选择各自的六轴乘子，
参数上传保持同一份物理数据。初始化与继续迭代沿共同阶段，完成结果继续回到原 Body。
`DeviceXpbdJointTests` 对应的公开方法位于 `DeviceXpbdTests` 的同名分部文件中，
覆盖有限刚度、双向物理上限与世界空间定时阻尼驱动的实际设备运动。

## 图与 SIMT 行块

[DeviceXpbdSolver](../../../src/SomeEngine.Physics/DeviceXpbdSolver.cs) 接收同一
`DeviceConstraintGraph`、项目编译的具体入口、输入资源与原乘子 buffers。
`DeviceXpbdSettings` 显式选择迭代数和 `RowsPerBlock`，线程组大小与实际 shader 对齐。
初始化、继续迭代和完整求解分别由 `Initialize()`、`Iterate(n)` 与 `Solve()` 提供。

[physics_xpbd_graph](../../../assets/Shaders/physics_xpbd_graph.slang) 用
`DeviceXpbdConstraintRows<TBodies,TProjection>` 连接具体参与角色与原投影，
`DeviceXpbdConstraintGroups<TFirst,TSecond>` 保持类型组顺序。
每轮按图的类型组、颜色顺序派发，`RowOrder` 和 `RowRanges` 决定每个实际行块；
独立岛的同色行合在同一范围，块间和块内的独立行沿实际 SIMT 工作项执行。
颜色之间使用 UAV barrier，原主体状态与乘子在设备原位更新。

Body 选择由共同 `IDeviceBodySelection` 解释。独立投影采用 Dynamics 同一次捕获的
`DeviceSelectedBodies` 成员标记，查询按原 Body index 直接寻址；图投影采用
`DeviceGraphBodies` 的 selected-dynamic 索引。长度、弯曲、Joint 和 Motor 的同源投影
分别以该具体选择类型实例化，分母权重与实际校正都沿选定写范围；其他动态端提供读取。
`DeviceXpbdStateRows<TState>` 从原实体的实际历史绑定选择参与范围。

`DeviceLengthBodies`、[DeviceBendBodies](../../../assets/Shaders/physics_bend_graph.slang)、
共同 `DeviceJointBodies`、`DeviceMotorBodies` 和 `DeviceContactBodies` 提供各自的实际端点与写角色。
项目将这些具体类型以 `DeviceConstraintBodyGroups` 组成构图入口，再用相同顺序的
`DeviceXpbdConstraintGroups` 组成数值入口。Motor 从原 Joint 行读取框架，Target/Both 反馈
决定 source 的读写角色；参与身份继续由原实体绑定和本步选择匹配。

例如，下列具体组合是公共设备适配的一种用法：

```slang
typedef DeviceXpbdConstraintRows<DeviceLengthBodies,
    DeviceXpbdLengthProjection<DeviceGraphBodies>> LengthRows;
typedef DeviceXpbdConstraintRows<DeviceBendBodies,
    DeviceXpbdBendProjection<DeviceGraphBodies>> BendRows;
typedef DeviceXpbdConstraintGroups<LengthRows, BendRows> ProjectionRows;
```

用户的 `IDeviceXpbdProjection` 可以替换上述任一具体投影，并保持同一角色、颜色和完成链。
构图入口使用自身的常量 buffer 参数，数值入口使用 `XpbdGraphStep` 的普通数据布局；
项目分别编译两组入口，使各自反射布局与提交的数据对应。
完整组装见 [DeviceXpbdMixedGraphTests](../../../tests/SomeEngine.Graphics.Direct3D12.Tests/DeviceXpbdMixedGraphTests.cs)。

## 共同点接触

[DeviceXpbdContactProjection](../../../assets/Shaders/physics_xpbd_contact.slang) 读取共同
`DeviceContactConstraint` 和 `DeviceContactBody` 加权端，调用共同 delta、合并 Jacobian、
所选 Body 权重与校正。法向使用原有限刚度、接近阻尼和单边累积乘子；切向乘子
投影到原静、动摩擦范围。同一 Body 的多个局部锚先形成合并 Jacobian。

`DeviceXpbdState<XpbdContactState>` 选择该接触的设备位移乘子。
共同 `DeviceContactActions.Initialize()` 在位置求解前记录接近速度与时间区间，
投影将当前乘子作用量写入同一 `DeviceContactResponses`；速度重建后可调用
`ApplyRestitution(n)`，再由 `DeviceContactResults` 回到原 `ContactResponse`。
上述 stage 的句柄保持设备、必要读回、组件消费及资源归还的完整依赖。

## 与 AVBD 接触共同求解

`DeviceXpbdLengthForces` 沿同一长度参数、当前梯度和原设备乘子取得 `lambda / h²` 世界力，
通过 `IDeviceAvbdConstraints` 加入 AVBD 主体方程；XPBD 继续负责投影与乘子更新。
长度梯度和距离由 `physics_xpbd_length_math.slang` 单处计算。
项目可在一次 `Predict` 后初始化两类求解器，交替调用各自的 `Iterate`，最后统一 `UpdateVelocities`。
[公开互补与骨骼检查](../../../tests/SomeEngine.Graphics.Direct3D12.Tests/DeviceComplementaryPoseTests.cs)
将同一 Body 的完成位置、速度、接触力、编号与时刻交给原 `PhysicsPose` 消费。

`DevicePoseSources.CreateBodyStages(DevicePoseShaders.GetBodies(target))` 提供同源动画链的两个阶段。
`WriteTargets()` 捕获来源 FK、世界段长和运动学目标，并按当前子步施加指数阻尼。
项目随后用同一 `DevicePosePhysics.Bodies`、`Lengths`、`Bends` 和乘子组成共同预测及具体求解器，
在统一速度重建后调用 `MapBones()`，将完成位置映射回骨骼并继续原 rig 中随后的操作。
原动画求值入口复用相同的目标捕获和骨骼映射数学，保留自身链定义中的等分步长和迭代数。

来源输入时钟与物理完成时钟分别随 `DevicePoseOutput.Input` 和 `Number/Time` 交付。
[共同步与后续 Aim 检查](../../../tests/SomeEngine.Graphics.Direct3D12.Tests/DevicePoseBodyStageTests.cs)
使用保持的来源输入完成两个物理步，在同一 Body 上交替接触 AVBD 与长度 XPBD，
并通过 Body、完成骨骼、Aim 后代及两组时钟观察该完整组合。
