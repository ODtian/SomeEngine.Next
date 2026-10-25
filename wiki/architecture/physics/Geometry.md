# 实体几何、接触与独立查询

本页维护当前几何组件、类型检测入口、共同接触数据和独立查询方法。源码入口为 [几何接口](../../../src/SomeEngine.Physics/CollisionGeometry.cs)、[类型检测](../../../src/SomeEngine.Physics/GeometryPairDetection.cs)、[共同接触](../../../src/SomeEngine.Physics/CollisionContact.cs)与[几何查询](../../../src/SomeEngine.Physics/CollisionGeometryQueries.cs)。公开行为检查见 [PrimitiveCollisionTests](../../../tests/SomeEngine.Physics.Tests/PrimitiveCollisionTests.cs) 和 [PrimitiveGeometryQueryTests](../../../tests/SomeEngine.Physics.Tests/PrimitiveGeometryQueryTests.cs)。

## 几何与当前状态

`SphereGeometry`、`BoxGeometry`、`CapsuleGeometry` 和 `CylinderGeometry` 表达有界形状，`PlaneGeometry` 表达单位法向及偏移定义的实心半空间。球的参数为半径；盒的参数为三轴半宽；胶囊和圆柱的轴向为局部 Y，分别提供半线段长度和半高度。`BodyPose` 提供几何原点的世界位置与旋转。

`ICollisionGeometry<TGeometry>` 提供范围、点的带符号距离、支持特征面和球扫描。`ICollisionConvex<TGeometry>` 提供具体类型的核心支持点与球形外扩半径。球的核心是点，胶囊的核心是线段；盒和圆柱的核心包含完整实体形状。具体类型通过静态调用解释自己的几何数据。

`UpdateGeometryBounds<TGeometry>` 在共同物理步中从原几何和 `BodyPose` 列计算派生 `CollisionBounds`。预测、范围更新、碰撞树维护及检测通过已有 Job 完成依赖连接。独立查询在同一个 World 读取作用域中消费当前组件，树及范围由调用方沿这一顺序维护。

## 距离、穿透与流形

四种有界形状的两两组合由 `ConvexCollision<TFirst,TSecond>` 提供核心 GJK 距离、EPA 穿透及球形外扩。`ConvexCollisionSettings` 显式给出距离容差、距离迭代数和穿透迭代数。`SphereCollision<TGeometry>` 复用目标形状的点距离，`CapsuleCollision` 直接计算线段距离；它们是项目可选择的具体特化。每种有界形状与平面的组合由 `ConvexPlaneCollision<TGeometry>` 计算。`ReverseCollision` 交换参与方、接触点、特征和法向。

`ICollisionNarrowPhase<TFirst,TSecond>` 提供 `Distance`、`GetManifold` 与所需接触容量。`CollisionDistance` 保存双方世界接触点、双方特征、第一方指向第二方的单位法向及带符号间距；正间距表示分离，负间距表示穿透。

有界形状之间的支持面经过侧平面裁剪；面域接触保留至多四个分布于接触区域的点，平行线特征可提供两个端点，点特征提供一个接触。平面路径将有限支持面投影到当前平面。几何距离和流形共用原组件输入及同一组支持函数。

算法阅读参考为 [Jolt 的 GJK 实现](https://github.com/jrouwe/JoltPhysics/blob/master/Jolt/Geometry/GJKClosestPoint.h) 和 [支持面流形说明](https://github.com/jrouwe/JoltPhysics/blob/master/Jolt/Physics/Collision/ManifoldBetweenTwoFaces.h)；本项目的当前调用与状态职责以本页链接的 C# 源码为准。

## 类型 bin 与实际作用端

[类型 bin](../ECS/Collision-Pair-Bins.md) 先按项目提供的分类器完成 count、prefix、write。`DetectPairsWithQuery` 从所选 bin 读取身份对，以独立的完整组件访问视图调用具体检测；原参与集合继续持有配对范围。检测可沿实体引用读取配对集合之外的模拟节点。

`CollisionGeometry<TFirst,TSecond>.Detect` 将具体窄阶段及输出接入这个终端。默认 `ComponentGeometry<TGeometry>` 读取几何组件和同实体 `BodyPose`。用户实现 `ICollisionGeometryReader<TGeometry>` 时，声明实际读取、取得当前几何、提供作用端数量，并将世界接触点映射到模拟实体的局部作用点与权重。几何的短期计算值在本次内核中使用，原模拟状态由对应实体组件持有。

以下是盒与圆柱检测的使用例子；`pairs`、`bins`、`bin` 和 `writer` 由项目组装：

```csharp
var kernel = new ConvexCollision<BoxGeometry, CylinderGeometry>(
    new ConvexCollisionSettings(1e-5f, 32, 64));
pairs.DetectPairsWithQuery(bins, bin,
    CollisionGeometry<BoxGeometry, CylinderGeometry>.Detect(kernel, writer, contactDistance: .01f));
```

`ICollisionContactOutput.Add` 接收 `CollisionContact` 及双方 `ContactBody` 跨度。接触头保存双方几何实体、特征、流形点序号、几何局部接触点、法向与间距。`ContactBody` 保存实际模拟实体、该实体局部接触点及插值权重。双方作用端连续写入同一个接触实体缓冲时，`FirstBodyCount` 保存双方分界。输出缓冲由调用方持有，后续接触维护将物理参数与数值历史保存到相应接触实体。

## 独立查询与完成结果消费

`Collision.Query<TGeometry>(world, members, tree, settings)` 创建具体几何查询，其中 `CollisionQuerySettings` 显式提供扫描容差及迭代数。`Raycast`、`SweepSphere` 和 `TryGetTransform` 接入现有 `ICollisionQueries` 消费者；命中结果携带实体、世界接触点、表面朝外法向、沿运动方向的距离、当前变换与特征。`GeometryVersion` 可由调用方提供表面修订值。

`Distance` 使用项目提供的具体窄阶段，返回最小带符号间距及双方见证点。`Overlap` 使用相同距离计算输出相交实体。`Sweep` 接收具体 `ICollisionSweep<TQuery,TGeometry>`；`ConvexShapeSweep` 复用所选距离内核沿分离平面推进平移查询。几何类型也可通过自己的静态 `CastSphere` 提供扫描计算。

批量射线和球扫描共享一次组件读取作用域，每个输入分别遍历持久树。三角面查询的 `CollisionHit.Feature` 表达命中三角形编号，具体几何查询的同一字段表达对应几何特征。物理完成点及查询调用顺序由已有 Job 依赖与调用方消费位置连接。
