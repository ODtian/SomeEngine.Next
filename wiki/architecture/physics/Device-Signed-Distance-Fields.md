# 设备有符号距离场

本页维护设备 SDF 的输入、采样、检测与完成消费。源码入口为
[场资源](../../../src/SomeEngine.Physics/DeviceSdfFields.cs)、
[具体源](../../../src/SomeEngine.Physics/DeviceSdfSource.cs)、
[采样场](../../../assets/Shaders/physics_sdf_grid.slang)、
[范围更新](../../../assets/Shaders/physics_sdf_update.slang)和
[接触数学](../../../assets/Shaders/physics_sdf_collision.slang)。
原场含义由[有符号距离场](Signed-Distance-Fields.md)维护，
主体与多端职责由[设备主体](Device-Bodies.md)维护。

## 原场与设备资源

`SdfGeometry<TField>` 保存原具体场、统一 Scale 与 TraceStepScale。
实体上的 `DeviceSdfBinding` 保存字段批次及元数据索引；
`DeviceSdfFields` 持有实际设备 metadata、共享样本及上传资源。
`DeviceSdfCapacity(Fields,Samples)` 分别给出 80 字节元数据记录数与标量样本数。
同一个 `SdfGridData` 在该批次取得一个样本区间，每轮选定场读取当前原数组并写入该区间。

`DeviceSdfSource<TField,TReader>` 沿原生成式 Job 和输入依赖执行具体 reader，
声明原场及样本资源，待数据就绪后填写 metadata 与上传区间。
`DeviceTrilinearSdfReader` 读取原 `SdfGridData.Distances`，保留 X 连续的三轴计数、
原点、单元尺寸与样本起点。`IDeviceSdfReader.Read(geometry,destination,index)`
同时提供当前字段索引，用户 reader 可填写自己声明的设备参数。

具体源的 AddReads 声明所读绑定列，AddReadResources 登记托管绑定列描述器；
原组件所指的数值资源由具体源的 Collect/GetVersion 进入同一个生成式 Job。
几何引用的 FieldIndex 和元数据的 FieldIndex 从原 `DeviceSdfBinding.Index` 派生，
分别负责几何寻址与用户参数寻址。`GetBindings()` 提供 RHI 绑定，
`GetReadAccesses(scheduler)` 提供实际设备读取范围。
所有选定 field writer 结束后，项目调用一次 `fields.UploadInputs()`。

## 采样与范围

Slang `IDeviceSdfField.sample(input,localPoint)` 返回局部有符号距离、梯度与特征。
`DeviceTrilinearSdfGrid` 从原八个标量三线性插值，并解析求局部梯度。
外侧单元多项式、展平单元特征及统一缩放沿原 CPU 数学。
检测从当前主体状态派生框架，再逆旋转并除以 Scale 取得局部点；
距离乘 Scale，归一化梯度经当前旋转成为世界外法向。

`DeviceSdfUpdate` 将相同 metadata 的局部 Min/Max 缩放、旋转并平移，
写入原网格候选范围。主体设备状态由 `DeviceBodies` 持有，范围与框架是本轮派生数据。
凸体的 `IDevicePointGeometry` 在原 support/face 上提供实际点距离；
球、盒、胶囊、圆柱、凸包和三角面分别沿对应 CPU 点距离及特征数学。
动态胶囊与边使用当前端点生成的胶囊，动态面使用当前三角形。

## 类型检测

| 组合 | 具体 Slang 内核 | 数学输入 |
| --- | --- | --- |
| 凸体、动态胶囊/边/面与场 | `DeviceSdfCollision<TGeometry,TField>` | support/face 到场，以及场零表面到凸体的投影 |
| 场与场 | `DeviceSdfFieldCollision<TFirst,TSecond>` | 分别采样双方零表面并测量另一场 |
| 场与平面 | `DeviceSdfPlaneCollision<TField>` | 场零表面对实际半空间测量 |

采样显式给出三轴分段数、二分次数和局部容差，凸体路径另给支持方向迭代数。
三轴格边上的零点及异号边二分点形成连续探针；
接触沿共同见证去重和分布裁减保留至多四点，采样设置定义该轮表面分辨率。
`DeviceReverseCollision` 交换见证、特征和法向，
`DeviceTypePairCollision` 将无序类型 bin 的具体内核结果恢复到原 pair 参与位置。

以下是具体窄阶段的使用例子，bin 与资源绑定由项目组装：

```slang
DeviceSdfCollision<DeviceCapsule, DeviceTrilinearSdfGrid> kernel;
kernel.settings.cells = uint3(8, 8, 8);
kernel.settings.rootIterations = 24;
kernel.settings.supportIterations = 16;
kernel.settings.tolerance = 1e-5f;
detectDeviceReferencedCollision(id.x, kernel);
```

用户场沿同一具体 `IDeviceSdfField` 入口实现采样，
原参数由自己的 reader 和资源描述器交付。
额外绑定由 Slang 反射解释，并与实际 Job 范围一起传给原检测或范围阶段。

## 完成与共同消费

完整次序为：输入依赖 → 主体上传及 field/reference writer → 场上传 →
首项几何/候选输入 Upload → 其余范围 Resident → 原网格 count/prefix/write →
Resident 类型检测 → 选定读回与共同输出。
完成链覆盖提交、设备完成及终态收集。

独立查询调用原 `DeviceCollisionDetection.Schedule`，按所选物理步和依赖读取 Resident 状态。
选择 Contacts 时，`WriteContacts` 交付原 104 字节接触头及双方实际作用端；
`ContactPointOutput → ContactPoints.Refresh` 将结果写入原 ECS 关系和组件，
后续 AVBD、XPBD 或项目响应使用共同锚及权重语义。
结果时刻由原检测 Number/Time 和共同物理步保持。
