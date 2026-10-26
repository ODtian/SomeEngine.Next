# 设备主体与引用几何

本页维护原 Body 组件的设备表示、引用几何范围及多端输出。源码入口为
[DeviceBodies](../../../src/SomeEngine.Physics/DeviceBodies.cs)、
[引用输入](../../../src/SomeEngine.Physics/DeviceGeometryReferences.cs)、
[范围阶段](../../../src/SomeEngine.Physics/UpdateDeviceGeometry.cs) 与
[引用检测](../../../assets/Shaders/physics_geometry_references.slang)。
公开使用入口见 `DeviceReferencedGeometryTests`。

## 主体与设备索引

`DeviceBodyBinding` 在实际模拟实体上保存 `DeviceBodies` 引用与该资源中的索引。
`DeviceBodyState` 是所选设备表示，包含原 `BodyPose`、`BodyMass`、`BodyVelocity`、
`BodyStepState` 的字段及完整 Entity 索引和代次。`DeviceBodies.States` 单处持有当前设备状态；
设备输入及后续设备求解沿该 buffer 读写。执行索引用于批次内寻址，接触身份使用 Entity 及代次。

`WriteInputs(rowsPerPacket)` 在生成式 Job 中读取当前组件，
将每个实体写入其绑定的独立上传区间。`UploadInputs()` 通过 RHI 提交输入，
返回的物理阶段完成链包含设备完成及资源归还。
项目在 CPU 输入需要交付设备时显式选用这些阶段，后续阶段读取 Resident 状态。
`GetReadAccess`、`GetWriteAccess` 为项目自己的设备工作提供原 buffer 的实际访问描述。

[DeviceBodyInputs](../../../src/SomeEngine.Physics/DeviceBodyInputs.cs) 在装配时绑定原 World、
参与查询和同一 DeviceBodies。`Upload()` 从有效 QueryRow 读取原 Pose、Mass、Velocity 与
StepState 的写版本；新实体按绑定初始化整行，既有实体只复制发生 CPU 修改的组件字段。
例如，只修改 Velocity 的输入保留设备当前 Pose，修改 Mass 的输入保留当前运动与步历史。
这是连续驻留步的一种公开输入用法；上述整批输入阶段继续服务显式初始化。

来源版本与已同步字段由 DeviceBodies 单处保存，查询和传输范围由输入持有者复用。
实际数据继续位于原 208B States，上传复用原 staging 和命令资源。

[CPU 输入变更](../../../src/SomeEngine.Physics/DeviceBodies.InputChanges.cs) 由同一 DeviceBodies 按需保存。
活动消费者绑定后，`DeviceBodyInputs` 在原组件写版本判定处生成实体身份、组件位及本轮步号、子步号，
沿原主体传输命令复制实际变更行。Pose、Mass、Velocity 对应位为 1、2、4，StepState 为 8。
`physics_body_input_changes.slang` 的 `deviceBodyCpuInputChanged` 按当前主体身份和调用方的本轮时刻读取前面三类修改；
旧步标记随步号、子步号失效，原结果回写继续使用上述同步确认。

[DeviceBodyInputBuffers](../../../src/SomeEngine.Physics/DeviceBodyInputBuffers.cs) 从同一参与查询读取
原 `BodyForce`、`BodyImpulse`、`BodyVelocityTarget` 与 `BodyKinematicTarget` 时间缓冲。
`WriteInputs()` 将当前子步的力积分、所属时段的冲量、最新速度目标和轨迹采样值形成派生设备输入，
保留原 Entity 编号与代次。`Apply()` 在预测前读取当前驻留质量、惯量、姿态和速度施加这些输入；
`ApplyKinematicTargets()` 在预测后写入运动学轨迹及共同预测值，速度仍由共同速度阶段重建。
力按时段交集积分，冲量和速度目标在包含其时刻的子步生效，轨迹在结束后保持最终位姿。

输入资源按主体批次容量复用。`AddBindings` 提供 `DeviceBodyInputs` 与 `BodyInputParameters`，
`GetReadAccesses` 提供原 Job 的读取描述，具体活动阶段可沿同一派生输入判断唤醒。
设备应用阶段按实际输入主体声明 States 写区间，提交与设备完成继续沿原外部 fence 链。

`DeviceBodyState` 与 `physics_bodies.slang` 对应，大小为 208 字节。
当前原点/旋转位于 0/16，线速度/逆质量位于 32/44，角速度位于 48；
局部质心位于 64，完整逆惯量的三行位于 80/96/112；
起点质心/旋转位于 128/144，预测质心/旋转位于 160/176，
步号/结果时刻位于 192/200。Entity 索引和代次位于 60/76。

## 预测、速度与组件消费

[DeviceBodyDynamics](../../../src/SomeEngine.Physics/DeviceBodyDynamics.cs) 接收原参与查询、
`DeviceBodies` 和项目显式提供的 `DeviceBodyKernels`。`Predict(acceleration)` 从当前组件绑定
生成本轮主体索引，再在设备原位写入起点、预测值及所属物理时刻；动态体接受加速度，
动态体与运动学体按速度积分，静态体保持位姿。旋转与平移围绕原局部质心计算。
`UpdateVelocities()` 从同一子步起点与完成位姿重建线、角速度。
`UpdateSelection()` 在 CPU 阶段读取原参与查询，更新同一主体索引、直接查找的成员标记与
本轮参数，供预测前的活动或图阶段使用。各数值提交继续沿同一捕获函数读取当前成员。
项目提供第三个具体 `GyroscopicTorque` 内核后，`ApplyGyroscopicTorque()` 在预测前
沿原完整体坐标惯量施加陀螺力矩；纯平移主体继续使用其三维自由度。
数学由 [physics_body_math](../../../assets/Shaders/physics_body_math.slang) 与
[physics_body_dynamics](../../../assets/Shaders/physics_body_dynamics.slang) 单处维护。

具体活动内核调用同一 `predictDeviceBody(index,advance,resume)` 与
`updateDeviceBodyVelocity(index)`。`advance=false` 刷新共同起点、预测初值及步时刻；
`resume=true` 保留该子步起点，让活动阶段选定的新唤醒体完成一次运动预测。
项目以 `DeviceBodyKernels.Resume` 提供具体入口，`Dynamics.Resume(acceleration)` 接同一提交与完成链。
构造器的额外资源绑定与访问描述为该具体内核声明自己的活动数据。

[DeviceBodyResults](../../../src/SomeEngine.Physics/DeviceBodyResults.cs) 在项目选择 CPU 消费时创建。
`Readback(rowsPerPacket)` 提交原状态 buffer 的复制，沿外部 fence 等待真实设备完成，
随后按原 `DeviceBodyBinding` 捕获消费实体，经 EntityQueryJobs 的实际行范围写入位姿、速度及 `BodyStepState`。
回写按设备结果中的 Entity 编号与代次匹配实际实体，旧步结果保持其来源身份。
写入与自身来源版本确认处于同一行写访问期。较早完成结果在设备后续步运行后消费时，
下次输入将这次 CPU 镜像写入识别为已同步来源，继续保留设备当前状态；外部 CPU 修改仍按新版本上传。
阶段返回的 JobHandle 覆盖提交、读回、组件写入与设备完成收集。
后续变换和动画继续读取同一实体组件。公开行为见 `DeviceXpbdTests`。

消费者可用 `DeviceBodyResults(backend,device,bodies,world,members,capacity)` 绑定自己的原成员查询与
最大结果条数。读取阶段在输入依赖完成后取得当前 Entity 与设备索引，按索引合并相邻范围，
仅为这些区间声明 States 读取并复制到紧凑读回区。CPU 数组与读回资源按消费者容量分配。
原查询筛选及 Entity 代次继续限定组件写入，其余主体保留在设备中。

## 引用与派生范围

`DeviceCollisionGeometry(..., bodies)` 为几何批次绑定上述主体资源。
`WriteGeometryReferences<TComponent,TSource>` 接受既有 Within、Between 或 Product 来源，
从实际参与查询取得本轮成员角色。具体 `IDeviceGeometrySource<TComponent>`
在有效组件访问范围中读取原参数与 Body 绑定，生成形状参数、端点索引及局部采样锚。
跨实体端点查询覆盖所需 Body 绑定列，模拟节点可处于几何参与集合之外。

物理步的 Bodies 选择实际模拟节点。几何输入按自身组件和设备绑定执行，
原 pair 查询生成参与角色；几何 Prepare 显式接收自己的筛选条件。
因此独立几何实体与模拟节点分别保持各自的数据范围。

`DeviceComponentSource<TGeometry,TReader>` 复用原具体形状 reader；
`DeviceDynamicCapsuleSource`、`DeviceDynamicEdgeSource` 保留两个原端点，`DeviceDynamicFaceSource` 保留三个原端点。
几何输入的 pose 和 `DeviceGeometryPoints` 是当前设备状态的派生结果。
`UpdateDeviceGeometry` 使用项目给定的 Slang `IDeviceGeometryUpdate`，
从当前主体状态求值端点，更新派生形状和原网格候选输入中的范围。
胶囊范围由两端包围并外扩半径，面域范围由三端包围并外扩半径。

刚体球、盒、胶囊、圆柱及凸包分别使用具体范围更新类型读取当前设备姿态。
平面源保留项目给出的世界候选区域，`DevicePlaneUpdate` 同时更新实际平面框架。
SDF 参数、采样与组合由[设备有符号距离场](Device-Signed-Distance-Fields.md)维护。

所有 reference writer 完成后，第一项范围更新选择 Upload，
将整批参数、引用与候选身份上传一次；其余具体范围更新选择 Resident。
后续网格 Build、类型检测也选择 Resident，连续消费已更新的范围与形状。
各更新阶段声明主体读取、几何/点/候选范围写入及必要传输资源。

## 接触作用端与完成

`detectDeviceReferencedCollision` 复用原 `IDeviceCollisionNarrowPhase`，
再按当前引用几何把见证点映射成共同 `ContactBody`。
刚体使用当前设备姿态取得局部锚；胶囊沿线段投影取得两端权重；
面域沿三角形最近点取得重心权重。端点保留原局部采样锚，
同一 Body 的多个局部锚逐项保留，后续响应按共同合并规则形成该 Body 的贡献。

`DeviceContactBody` 包含共同 `ContactBody` 与派生 `StateIndex`，大小 32 字节。
`DeviceCollisionDetection.ContactBodies`、`ContactBodyCounts` 保存每个流形点的作用端，
每点容量对应两侧各至多三个端点。原接触头保持 104 字节和 `FirstBodyCount` 分界。
普通几何检测采用当前姿态采样，`Prediction` 使用共同头的 false/0 语义。

选择 Contacts 读回时，同一检测完成链读取头与作用端；`ReadContactBodies(pair,point)`
公开相应连续结果。`WriteContacts<TOutput>` 将双方实际跨度交给原
`ICollisionContactOutput`，继续连接 `ContactPointOutput`、持久点刷新及具体响应。
选择 None 时，结果留在设备 buffer 中供后续设备消费者读取。
提交、外部 fence、终态收集使用现有 Scheduler/RHI 完成机制，
调用方在所属完成链结束后复用或释放批次资源。
