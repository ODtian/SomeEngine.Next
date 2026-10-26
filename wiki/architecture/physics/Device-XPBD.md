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
Entity 索引、代次和本次查询参与标记。`BeginInputs()` 开始本次成员写入，
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
`DeviceBodies.States`。索引列表保存派生执行位置。

## 具体内核与阶段

`DeviceXpbdProjection<TData,TState>` 接收项目编译的初始化/投影入口、线程组大小及
额外资源绑定和访问。Slang 的 `IDeviceXpbdProjection` 提供 `active`、`initialize` 与
`project`，内置与用户具体内核使用同一调用入口。
项目具体 source 可以从已声明的原行读取自己的参数，Slang 具体投影可同时读取项目
提供的额外资源；两者的结果继续沿同一个物理步完成链返回原实体。
`Initialize()` 初始化所选行的子步历史，`Iterate(n)` 在相同子步内累计乘子。
同轮可同时写入的行由调用者提供的独立约束范围决定。
每轮之间沿 RHI UAV barrier 保证前轮写入对后轮可见；提交和完成沿共同设备 Job/fence 链。

[DeviceLengthSource](../../../src/SomeEngine.Physics/DeviceChainConstraints.cs) 从原
`XpbdLength` 关系端点解析主体索引。16 字节参数保存两端索引、静止长度与柔度，
独立 float 历史保存原 XPBD 位移乘子。
[DeviceXpbdLength](../../../assets/Shaders/physics_xpbd_length.slang) 使用原
`Compliance / h²`、逆质量和累积乘子公式，只修正动态端的位置。
共同预测、速度与原组件消费由[设备主体](Device-Bodies.md)维护。

`DeviceBendSource` 从原 `ConstraintBodyLink` 顺序读取三个受保护参与端，
通过 `ConstraintBodies.GetBody(link,access)` 解析实际 Body，再转换为本轮执行索引。
32 字节参数保存三个索引、最小方向余弦和柔度；端点身份继续由原参与关系持有。
[DeviceXpbdBend](../../../assets/Shaders/physics_xpbd_bend.slang) 复用原三端方向余弦梯度，
将累积乘子投影到大于等于零的范围，按各动态端的逆质量修正原位姿。
