# 设备 AVBD 与共同接触响应

本页维护当前设备 ALM 数学、约束实体绑定和共同接触阶段。
主体表示和积分见[设备主体](Device-Bodies.md)，共同参数和 source writer 见
[设备约束输入](Device-XPBD.md#原实体与设备数据)。
源码入口为 [DeviceAvbdSolver](../../../src/SomeEngine.Physics/DeviceAvbdSolver.cs)、
[局部方程](../../../src/SomeEngine.Physics/DeviceAvbdBodyEquations.cs)、
[数值输入](../../../src/SomeEngine.Physics/DeviceAvbdStateInputs.cs) 和
[公开设备步行为](../../../tests/SomeEngine.Graphics.Direct3D12.Tests/DeviceAvbdTests.cs)。

## 实体数值状态与局部方程

`DeviceAvbdState<TState>` 放在原约束实体，选择其 ALM 历史 buffer；
原 `DeviceConstraintBinding<TData>.Index` 同时定位共同参数和数值历史。
`DeviceAvbdStateInputs<TComponent,TData,TState,TSource>` 读取原组件实际行版本，
将新实体、绑定移动和显式组件修改的行上传到所选历史。
其容量缓存保存实体、行位置和版本；逐轮乘子与罚参数保存在设备历史中。

`DeviceAvbdEquationBinding` 放在具有原 `AvbdBodyEquation` 的 Body 上，
选择 `DeviceAvbdBodyEquations` 的数值行。
每轮从共同 `DeviceBodies.States` 的原预测结果组装惯性梯度与 Hessian，
叠加约束当前世界 Jacobian 和力，再以 LDL 求响应。
全零逆惯量使用三维平移方程；具有完整逆惯量的刚体使用六维质心平移和旋转方程。
旋转惯性采用完整张量，锚点 Jacobian 以质心为力臂原点。

`AvbdMaterialTerms` 对应的设备函数 `deviceAvbdMaterial(K,rho)` 分开处理材料刚度
与数值罚参数。`physics_avbd_elastic.slang` 从原 `ElasticConnection` 的同一物理参数
组装线性/角弹性，当前误差与累积对偶力共同进入 Body 方程。

`DeviceAvbdSolver.Initialize()` 初始化子步历史和 Body 方程索引，
`Iterate(n)` 在当前子步继续 primal/dual 迭代；`Solve()` 组合这两项职责。
项目提供具体 Slang `IDeviceAvbdConstraints` 内核与实际绑定，
`DeviceAvbdConstraintGroups<TFirst,TSecond>` 将多种具体约束组装进同一 Body 局部方程。
`adjust` 位于局部响应求出之后、位姿提交之前。

`DeviceAvbdJointState` 保存六轴力乘子、数值罚参数与子步初始误差，
对应原 `AvbdJointState`；`DeviceAvbdMotorState` 对应原 `AvbdMotorState`。
两者由同一显式组件输入阶段选择和更新历史资源。
[关节 ALM](../../../assets/Shaders/physics_avbd_joint.slang) 沿共同关节行处理
锁定、自由和单边限位，再将 `K/rho` 对应力与曲率叠加到 Body 方程。
[motor ALM](../../../assets/Shaders/physics_avbd_motor.slang) 沿同一关节参数行读取框架与端点，
在子步结束时刻计算目标位置、旋转和速度，将阻尼 `D/h` 合入平衡点与等效刚度。
物理力/力矩按原上限投影，对偶力继续保存在对应历史行。

共同 `physics_joint_bodies.slang` 和 `physics_motor_bodies.slang` 持有原参与角色。
Target 反馈的源 Body 参与当前框架读取，目标 Body 参与力装配；Both 反馈的两端共同受力。
局部 Body 装配沿图的实际写端筛选，图连接和颜色仍覆盖动态读取依赖。
世界目标的反作用力使用原导数，源端曲率保持其世界目标语义。

项目自己的 `IDeviceConstraintSource` 可在实际原行读取附加组件；
自己的 `IDeviceAvbdConstraints` 可绑定额外设备资源，使用共同 `DeviceAvbdStep`、
`deviceAvbdMaterial` 与 `deviceAvbdAddRow` 装配能量。
[用户能量行为](../../../tests/SomeEngine.Graphics.Direct3D12.Tests/DeviceAvbdUserTests.cs)
将项目目标能量与内置连接放在同一个具体组合中，
两个对偶历史分别驻留，并在同一个 Body 方程中同时贡献梯度与曲率。

## 实际设备参与图

[DeviceConstraintGraph](../../../src/SomeEngine.Physics/DeviceConstraintGraph.cs) 使用
`IDeviceConstraintBodies` 的实际参与端和读写角色，保存派生邻接、岛、颜色与执行索引。
Body 候选沿共同预测阶段的 `DeviceBodyIndices` 读取，动态状态沿同一个 208 字节主体读取。
`DeviceConstraintSelection` 从原查询保存当前实体索引和代次；每个具体组同时核对
参数 metadata 与当前选择，算法组进一步核对原数值状态资源的选择。

图的行参与写入采用具体线程组；当前图构造入口按设备顺序生成并查集、邻接和贪心颜色。
Body 颜色按当前约束连接，约束颜色按具体组与实际动态写端生成。
不同颜色之间使用 UAV barrier，同色 Body 在 SIMT dispatch 中执行局部数值块。
构图的控制计数通过 16 字节完成读回供颜色派发使用。
该等待、原查询与设备输入访问、后续数值提交处在同一个获准的 Job 范围，
最终 GPU 完成继续沿共同 `DevicePhysicsJobs` 和 RHI queue fence。

## 多端接触参数与公共作用量

[DeviceContactSource](../../../src/SomeEngine.Physics/DeviceContactData.cs) 在同一次原行读取中
解析 `ContactPoint`、`ContactMaterial`、`ContactBodyLink` 及受保护的 `ContactParticipant` 端点。
80 字节 header 保存双方端数、法向、间距、几何采样锚差、材料和预测采样时刻。
`DeviceContactBodies` 保存原 32 字节 `DeviceContactBody`：实际 Entity、局部锚、插值权重
及共同 Body 状态索引。项目显式给出接触数和每点端数容量。
source 的 side upload 写入通过 `WriteInputs(...,extraAccesses)` 声明，header 与 side
共享同次读取与完成依赖，再分别上传各自的设备表示。

[共同锚点数学](../../../assets/Shaders/physics_contact_math.slang) 以加权第二侧减第一侧计算位移。
同一 Body 的多个锚、跨双方出现的贡献先合并线性与角 Jacobian，再进行一次权重、
方程装配或位姿修正。设备端点身份同时核对当前 Body 的 Entity 与代次。
权重、位姿修正和反弹冲量使用共同 `IDeviceBodySelection`；
`DeviceGraphBodies` 从当前图参与标记选择实际数值主体，其他动态端继续作为读取输入。

[设备接触 ALM](../../../assets/Shaders/physics_avbd_contact.slang) 使用单边法向力、
接近方向阻尼及静/动摩擦圆盘。法向数值罚参数与材料刚度分别持有，
切向试探力按总法向压力投影，完整惯量沿共同 Body 方程参与。

`DeviceContactResponseBinding` 放在原具有 `ContactResponse` 的接触实体，
选择公共的 `DeviceContactResponseState` buffer。
[DeviceContactActions](../../../src/SomeEngine.Physics/DeviceContactActions.cs) 的
`Initialize()` 在位置求解前采样接近速度并开始作用区间，
`ApplyRestitution(n)` 在共同速度重建后沿约束颜色施加法向冲量。
这两个阶段与算法历史初始化分别调用，可与具体 XPBD/AVBD 阶段组合。
Slang `DeviceContactResponseGroups` 保持同一图组顺序，
其他约束组由 `DeviceNoContactResponses<T>` 接续其行范围。

[DeviceContactResults](../../../src/SomeEngine.Physics/DeviceContactResults.cs) 将完成的世界力、
冲量、初始速度及时间读回原 `ContactResponse`，并核对原实体索引和代次。
Body 位置与速度沿共同 `DeviceBodyResults` 返回原实体。
