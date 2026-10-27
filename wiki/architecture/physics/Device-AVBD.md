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

`DeviceAvbdEquationBinding` 放在原模拟 Body 上，
选择 `DeviceAvbdBodyEquations` 的数值行。
每轮从共同 `DeviceBodies.States` 的原预测结果组装惯性梯度与 Hessian，
叠加约束当前世界 Jacobian 和力，再以 LDL 求响应。
全零逆惯量使用三维平移方程；有效维数为 `3 + popcount(bodyRotationMask)`。
完整三角轴采用世界 XYZ 角向基底，部分活动体轴随当前 Body 旋转；
Jacobian、惯性块与响应共同投影到该子空间。
局部数值缓存保存分解所用的角向基底，LRC 调整沿同一分解求响应。
同一份 256 字节局部方程同时保存当前邻接行数和分解步长。
当主体当前与上次均无约束邻接，且步长、旋转和惯性线性权重保持相同时，
局部迭代复用已有惯性矩阵及 LDL 分解，并从当前位姿与原预测结果重新计算梯度。
约束邻接参与的主体继续逐轮装配和分解，响应、`adjust` 与位姿提交保持相同次序。
`IDeviceAvbdConstraints.adjustsBody()` 表达具体类型的 Body 修正能力，默认返回 `true`。
内置空修正叶返回 `false`，具体组取双方能力的或，活动包装沿原类型转发。
当前主体无约束邻接、位姿与原预测结果完全相等且具体组返回 `false` 时，
局部迭代直接保留预测结果；此时惯性梯度和约束贡献均为零。
支撑叶和用户默认能力继续进入原局部方程及修正入口。
用户用法见[独立 Body 修正行为](../../../tests/SomeEngine.Graphics.Direct3D12.Tests/DeviceAvbdBodyAdjustmentTests.cs)。
对角方程直接以原对角构成 LDL 的 D，L 的严格下三角为零。
零力和零力矩的响应直接返回零向量；当前分解继续供后续 `adjust` 中的支撑响应使用。
旋转惯性采用原张量，锚点 Jacobian 以质心为力臂原点。
源码见 [设备局部数学](../../../assets/Shaders/physics_avbd_body.slang)。

`AvbdMaterialTerms` 对应的设备函数 `deviceAvbdMaterial(K,rho)` 分开处理材料刚度
与数值罚参数。`physics_avbd_elastic.slang` 从原 `ElasticConnection` 的同一物理参数
组装线性/角弹性，当前误差与累积对偶力共同进入 Body 方程。

`DeviceAvbdSolver.Initialize()` 先从当前参与和选择构建约束图，再初始化子步历史、
Body 方程索引和组数据；接触调节与支撑分组读取同次图的活动主体及连通根。
`Iterate(n)` 在当前子步继续 primal/dual 迭代；`Solve()` 组合这两项职责，迭代复用初始化时构建的图。
项目已显式建立共同图时，`Initialize(false, equationInputs)` 使用该图已捕获的参与，
再捕获求解器自身状态选择并初始化数值；`equationInputs` 继续选择上传或复用原方程绑定。
`Graph.ConstraintStageCount` 为零时，迭代跳过行对偶派发；组更新保持逐轮执行。
`DeviceAvbdKernels.ClearBodyIndices` 选择独立的逐 Body 索引清理内核，
其具体入口调用 `clearDeviceAvbdBodyIndices`；随后的初始化内核调用
`initializeDeviceAvbdRows`，逐方程行沿原 Entity metadata 建立 Body 到方程的映射。
清理与映射之间的 UAV barrier 保持同一索引 buffer 的读写顺序。
原 `initializeDeviceAvbd` 入口继续提供合并初始化。
`DeviceAvbdKernels.InitializeGroups` 和 `UpdateGroups` 是可选的具体组阶段，
分别位于逐行初始化之后和每轮逐行对偶更新之后；阶段之间使用同一提交内的 UAV barrier。
`GroupInitializationDispatches` 和 `GroupUpdateDispatches` 在对应阶段接续具体的
`DeviceAvbdGroupDispatch` 序列。每项给出 `ComputePipelineDesc` 与 RHI `DispatchArguments`，
求解器沿同一 `DeviceAvbdStep`、原资源绑定和提交记录派发，并在相邻派发之间维护 UAV barrier。
项目可用 `DevicePhysicsDispatch.CreateGroups` 组织线性主体工作，再接续读取该结果的组内计算；
当前场景的支撑初始化先并行清理所选活动主体，再沿原支撑状态建立承重路径。
项目提供具体 Slang `IDeviceAvbdConstraints` 内核与实际绑定，
`DeviceAvbdConstraintGroups<TFirst,TSecond>` 将多种具体约束组装进同一 Body 局部方程。
`adjust` 位于局部响应求出之后、位姿提交之前。

项目完成当前点、参数和成员输入后，可先调度同一 `DeviceConstraintGraph.Build()`，
再选择 `Iterate(iterations, buildGraph: false)` 复用该次图结果。
该继续迭代入口同时复用本次初始化与建图已经捕获的参数成员和数值行索引；
约束输入及选择在相应初始化、建图阶段交付。默认继续迭代入口沿当前输入捕获并建图。
`DeviceXpbdSolver.Initialize(buildGraph: false)` 与同名迭代重载使用同一完成依赖；
原默认入口继续在自身求解链构建图。AVBD/XPBD 交替时各阶段共享当前主体、数值历史与图。
接触反弹的显式图复用重载同样沿其初始化阶段捕获的响应行执行。
主体、约束及并行图派发复用 `DevicePhysicsDispatch.CreateGroups` 的 X/Y 范围；
项目 Shader 以 `devicePhysicsThreadIndex` 或 `devicePhysicsGroupIndex` 读取对应线性索引。
固定主体与方程绑定的项目可在首次 `Initialize(DeviceInputTransfer.Upload)` 或 `Solve(Upload)`
交付原映射后，选择对应的 `Resident` 重载复用同一设备映射。
数值方程仍逐步初始化，现行状态及当前步长继续进入原 kernel；原默认入口继续上传当前绑定。
`DeviceContactActions.ApplyRestitution(iterations, buildGraph: false)` 可沿同次图完成结果接续速度响应。

`DeviceContactPotentialAvbdConstraints` 选择接触势能的正曲率上界。
它沿原接触评估、Jacobian 和 `DeviceAvbdContactStates` 装配方程，普通法向采用材料对应的
有效罚曲率，角向三轴叠加各加权锚的 `|weight| * |arm| * |force|`。
`deviceAccumulateAvbdContactPotential` 同时接受所选长程支撑的辅助力与支撑资格，
沿同一方程与原对偶更新接续。项目的具体约束类型继续选择每步和每轮的罚参数更新。

`DeviceAvbdJointState` 保存六轴力乘子、数值罚参数与子步初始误差，
对应原 `AvbdJointState`；`DeviceAvbdMotorState` 对应原 `AvbdMotorState`。
两者由同一显式组件输入阶段选择和更新历史资源。
[关节 ALM](../../../assets/Shaders/physics_avbd_joint.slang) 沿共同关节行处理
锁定、自由和单边限位，再将 `K/rho` 对应力与曲率叠加到 Body 方程。
[motor ALM](../../../assets/Shaders/physics_avbd_motor.slang) 沿同一关节参数行读取框架与端点，
在子步结束时刻计算目标位置、旋转和速度，将阻尼 `D/h` 合入平衡点与等效刚度。
物理力/力矩按原上限投影，对偶力继续保存在对应历史行。

共同 `physics_joint_participants.slang` 和 `physics_motor_bodies.slang` 持有原参与角色。
`physics_constraint_bodies.slang` 提供具体参与接口和分组，
`physics_avbd_joint_state.slang` 持有关节对偶表示；这些纯数据入口可供独立完成结果读取使用。
原 `physics_joint_bodies.slang` 继续聚合关节参与与图入口。
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
图的完整连接与数值活动范围见[设备主体活动](Device-Activity.md)。

图的行参与写入采用具体线程组；两内核 `DeviceGraphKernels(writeRows, build)`
入口按设备顺序生成并查集、邻接和贪心颜色。
Body 颜色按当前约束连接，约束颜色按具体组与实际动态写端生成。
不同颜色之间使用 UAV barrier，同色 Body 在 SIMT dispatch 中执行局部数值块。
构图的控制计数通过 16 字节完成读回供颜色派发使用。
该等待、原查询与设备输入访问、后续数值提交处在同一个获准的 Job 范围，
最终 GPU 完成继续沿共同 `DevicePhysicsJobs` 和 RHI queue fence。

### 并行图构建

`DeviceGraphKernels(writeRows, parallelKernels)` 选择具体 `DeviceGraphParallelKernels`。
[并行阶段](../../../assets/Shaders/physics_constraint_graph_parallel.slang) 在同一个图资源中
初始化当前动态参与，按行进行原子连接与度计数，再压缩主体根。
邻接前缀采用 256 线程分块扫描，块偏移由小量顺序工作生成，逐主体应用偏移后按行写入原邻接。

主体与具体约束行各自先提案最小可用颜色，再以固定整数优先次序消解同色冲突。
提案缓冲在本轮消解期间保持只读；已接受颜色供下一轮提案使用。
行冲突继续考虑同一具体组和实际读写角色，同一主体上的共同只读依赖保持其相容性。
完成的颜色按组计数、前缀和打包到原 `BodyRanges/BodyOrder`、`RowRanges/RowOrder`。
数值阶段继续使用原缓冲与具体约束数学。

[DeviceConstraintGraph.Parallel](../../../src/SomeEngine.Physics/DeviceConstraintGraph.Parallel.cs)
按阶段屏障组织构建；每批颜色轮次在同一输入读取 Job 中取得控制计数和完成进度。
新增容量保存块总量、提案、组偏移与完成计数，主体和约束状态继续由其原表示持有。

## 关节长程界

[DeviceJointBounds](../../../src/SomeEngine.Physics/DeviceJointBounds.cs) 对应原关节上的
`LongRangeJointBound` 元素。`BuildInputs()` 复用原 `JointLongRangeGraph.Refresh`，
按原关节、受保护端点和 Body 质量版本维护刚体段、根路径和逐级距离界。
原拓扑推导继续持有原 Buffer，设备 source 在同一次有效原行读取中解析
`OtherJoint`、端角色和共同 `DeviceJointConstraint` 参数行。

16 字节 `DeviceJointBoundSet` 保存原关节参数行、界跨度和数量；
32 字节界数据保存另一关节行、双方端角色、另一关节实际 Entity、最小/最大半径。
局部锚和 Body 索引继续从共同关节参数读取。
`DeviceJointBoundsBinding` 在原关节选择这份表示，项目显式给出每关节的界容量。
header 与 side 输入共享原 source 读取和输出访问，随后沿同一完成链上传。

32 字节 `DeviceJointBoundState` 对应原界元素的乘子、罚参数与初始误差。
原拓扑实际重建时由原 Buffer 初始化这份数值历史，常规参数刷新保持设备历史。
[设备长程数学](../../../assets/Shaders/physics_avbd_joint_bounds.slang) 在子步初始化时
沿当前锚点和实际所选主体的移动率计算 `rho = PenaltyScale / (h² mobility)`。
每轮将区间上下界的单边力、径向与角曲率组装到共同局部方程，再更新当前对偶力。
长程参与端同时进入同一个 Body 图、邻接和颜色；该叶与普通关节及其他具体约束组合。
刚性界的推导范围沿原无限线性刚度和锁定坐标判定。

## 多端接触参数与公共作用量

[DeviceContactSource](../../../src/SomeEngine.Physics/DeviceContactData.cs) 在同一次原行读取中
解析 `ContactPoint`、`ContactMaterial`、`ContactBodyLink` 及受保护的 `ContactParticipant` 端点。
128 字节 header 保存双方端数、法向、间距、几何采样锚差、世界见证中点、材料、预测采样时刻及双方几何身份和特征。
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

## 接触长程支撑

[DeviceContactSupports](../../../src/SomeEngine.Physics/DeviceContactSupports.cs) 在原具有
`LongRangeBodySupport` 的实体通过 `DeviceSupportBodyBinding` 选择主体支撑表示；
`DeviceAvbdState<DeviceContactSupportState>` 在原具有 `LongRangeContactSupport` 的接触选择支撑历史。
主体和接触数值缓存、足迹参与索引、加载权重与局部响应方程由该资源持有。
主体仍使用同一份 `DeviceBodies`，接触几何和材料仍使用共同 contact header/side。
支撑主体同时核对原查询中的绑定、实际实体代次与共同预测阶段的 O(1) 主体成员标记。
`InitializeBuffers()` 将派生工作 buffer 转入共同 UAV 访问状态，并在首次调用时清零主体支撑缓存。
它接在支撑历史上传之后、首次支撑活动读取或求解之前。

[支撑分类与加载路径](../../../assets/Shaders/physics_contact_support_build.slang) 沿原静止判定、
无限法向刚度、摩擦锥、间距和滑移条件选择支撑。
世界见证点投影到垂直于 UpDirection 的平面，最多三个有效足迹点的非负仿射权重表达支撑位置。
这些三点组合覆盖足迹凸包；逐高度顺序将支撑面位移传递到上层接触和质心高度界。
派生足迹容量由项目显式给出。

[局部支撑响应](../../../assets/Shaders/physics_contact_support_solve.slang) 位于 Body 局部分解响应之后，
先尝试质心高度，再在高度和两个水平力矩方向内求至多三个活动支撑力。
修正继续使用当前三维/六维局部方程的响应，同时更新原支撑表示中的反力。
对偶更新后的组阶段按上层至下层顺序交接加载力，投影回原静摩擦圆盘并更新共同 `ContactResponse`。
运动变化触发足迹重建；退出支撑的辅助力交回普通接触乘子。

`DeviceSupportedContactAvbdConstraints` 将当前辅助世界力交给原接触法向与摩擦数学，
`DeviceContactSupportAvbdConstraints` 提供支撑 Body 的同一图参与和响应修正。
原 AVBD 接触乘子保存在自身 buffer，支撑贡献保存在自身对应表示。
该具体组合的组初始化与组更新入口分别调用 `initializeDeviceContactSupports` 和 `updateDeviceContactSupports`。
这两个入口接受具体 `IDeviceBodySelection`：传入 `DeviceAwakeBodies` 时，足迹重建、载荷交接和辅助力保留
仅更新活动参与分量，休眠接触沿原数值表示保留其支撑力。原单参数入口选择全部已绑定主体。

`IDeviceContactSupportWork` 提供当前支撑工作中的主体、接触及派生顺序范围。
原入口使用全部绑定范围，场景的 `DeviceSceneSupportGroups` 则沿当帧
`ConstraintGraphParents` 统计和压缩各动态连通分量的引用，分别初始化及交接载荷。
分组 buffer 保存原主体和接触索引、计数及前缀，数值继续保存在原支撑表示中。
分组接触引用按连通根、见证点高度及实体标识并行稳定归并，已排序范围直接生成支撑候选。
归并只移动引用并复用两份索引工作区，主体和接触的数值仍由各自设备表示保存。
每个足迹沿凸包边界和三角扇求仿射权重，盒体共享固定地面时各动态岛独立推进。
