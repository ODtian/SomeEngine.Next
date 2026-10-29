# 有符号距离场

相同采样、特征与共同接触语义的设备实现见[设备有符号距离场](Device-Signed-Distance-Fields.md)。

本页记录当前场数据、采样数学、接触与公共查询用法。对应源码为
[SdfGeometry](../../../src/SomeEngine.Physics/SdfGeometry.cs)、
[TrilinearSdfGrid](../../../src/SomeEngine.Physics/TrilinearSdfGrid.cs)、
[表面采样](../../../src/SomeEngine.Physics/SdfSurfaceSampling.cs)与
[SdfCollision](../../../src/SomeEngine.Physics/SdfCollision.cs)。
公开行为入口为 [SdfGeometryTests](../../../tests/SomeEngine.Physics.Tests/SdfGeometryTests.cs)。

## 数据与空间

`ISdfField<TField>` 由具体场类型静态提供局部 `Bounds`、`Sample`、引用资源收集及绑定版本。
`SdfSample` 保存局部带符号距离、局部梯度和特征编号；负值表示实心内部，梯度指向表面外侧。
用户解析场与内置采样场使用同一接口。

`SdfGridData` 单处持有三轴样本数和共享 `ReadOnlyMemory<float>`，X 为连续轴，
索引为 `x + XCount * (y + YCount * z)`。`SdfGridSampling` 给出局部原点和各轴单元尺寸。
`TrilinearSdfGrid` 从原八个角点三线性插值标量距离，解析求插值梯度，按单元尺寸换算到局部坐标。
特征编号是三轴下侧单元索引的展平值。边界外沿最外单元的插值多项式外推。
各轴样本数至少为二，单元尺寸为正，项目按需要在共享数组上填充或更新样本。

`SdfGeometry<TField>` 保存具体场引用、正的统一 `Scale` 和推进比例 `TraceStepScale`。
世界采样点先经 `BodyPose` 逆刚体变换及 Scale 换成局部坐标；标量距离乘 Scale，
梯度归一化后经旋转得到世界外法向。点的表面见证沿该法向回退采样距离。
零梯度采用局部 X 方向。`Bounds` 把场的局部范围统一缩放并刚体变换，
`UpdateSdfBounds<TField>` 从原组件与当前位姿更新 `CollisionBounds`。

## 接触组合与采样精度

| 输入组合 | 具体窄阶段 | 距离与流形来源 |
| --- | --- | --- |
| 球、盒、胶囊、圆柱、凸包与场 | `SdfCollision<TConvex,TField>` | 凸体支持点、支持面与场零表面的双向采样 |
| 动态胶囊、边、面与场 | 同一 `SdfCollision` 与动态 reader | 当前节点生成的凸体及其连续内部投影，作用端沿 reader 还原 |
| 场与平面 | `SdfPlaneCollision<TField>` | 场零表面对平面计算带符号距离 |
| 场与场 | `SdfFieldCollision<TFirst,TSecond>` | 分别采样双方零表面并求另一场距离 |

各组合经 `CollisionGeometry<TFirst,TSecond>.Detect`、原类型 bins 和独立查询进入同一窄阶段。
`ReverseCollision` 交换双方见证、特征、法向与作用端顺序。
球对场也可选择 `SphereCollision<SdfGeometry<TField>>`，直接消费球心的场采样。

`SdfSurfaceSampling` 显式给出局部范围内 X/Y/Z 分段数、二分迭代数与局部标量容差。
采样遍历三轴格边，输出接近零的顶点和异号边上的二分零点。
`SdfCollisionSettings` 另给支持点迭代数和世界接触容差；凸体支持方向沿场梯度迭代，
零表面点向凸体投影后再采样场，使连续边和面内部能够贡献接触。

`Distance` 返回本次采样探针中的最小带符号间距。`GetManifold` 按接触距离收集候选，
沿双方见证位置去重，并用共同流形裁减保留至多四个分布点。
分段数决定能被格边观察到的零表面细节；二分容差与迭代数决定每条异号边的定位精度。
因此调用者通过采样设置选择非凸场的接触分辨率，查询结果表达该分辨率下的接触几何。

共同输出保存双方实体、特征、几何局部接触、世界见证中点、单位法向及间距。
动态 reader 将各见证映射到原模拟节点、节点局部锚和插值权重；持久点与算法历史沿
[共同接触](Contacts.md)继续持有。

## 查询、资源与阶段

射线及球扫描沿 `SdfGeometry.CastSphere` 从运动点的场距离减去球半径，
每步前进 `gap * TraceStepScale`，在场范围扩张球半径的区间内查找首次达到查询容差的位置。
`CollisionQuerySettings` 给出世界间距容差与最大推进次数。
`SdfShapeSweep<TGeometry,TField,TKernel>` 将同一标量推进方式用于所选具体距离内核的形状扫描。
`TraceStepScale` 由项目依据场采样距离的步进性质提供。

以下为共享网格查询的使用例子，样本与原点、尺寸由项目提供：

```csharp
using var query = Collision.QueryWithResources<
    SdfGeometry<TrilinearSdfGrid>, SdfGeometryResources<TrilinearSdfGrid>>(
    world, fields, tree, new CollisionQuerySettings(1e-5f, 128));
query.Raycast(ray, out CollisionHit hit);
```

`SdfGeometryResources<TField>` 复用场类型的资源声明与版本。
`TrilinearSdfGrid` 声明原样本 Memory 的读取，资源有效期和输入完成链由现有
[QueryReadResources](../ECS/Query-Reads.md)及生成式 Job 组件资源机制承担。
同一共享数据直接供独立查询、范围更新和类型检测使用。

物理检测的使用例子：

```csharp
var resources = new QueryReadResources();
resources.Add<SdfGeometry<TrilinearSdfGrid>, SdfGeometryResources<TrilinearSdfGrid>>();
var narrow = ContactNarrowPhase.Create(bin,
    CollisionGeometry<SphereGeometry, SdfGeometry<TrilinearSdfGrid>>.Detect(
        new SdfCollision<SphereGeometry, TrilinearSdfGrid>(settings),
        new ContactPointOutput(detected), contactDistance: 0), resources);
using var detection = DetectContactPoints.Create(
    world, pairs, bins, classifier, narrow, detected, detectionInputs);
```

检测先填充本轮全部 bins，再从实际 pair 实体收集当前场资源，输入完成后取得资源并复核绑定，
随后每个所选 bin 执行一轮检测。样本数据由共享场持有，原位组件、查询和资源访问作用域
保持到各自消费者结束，工作批次复用派生范围与容量。
