# 场景表面查询

`SomeEngine.Physics` 在现有 `CollisionTree` 上查询真实三角形。输入沿用 World 查询成员、具体组件读取器与调用方持有的树；源码入口为 [CollisionSurface](../../../src/SomeEngine.Physics/CollisionSurface.cs)、[CollisionQueries](../../../src/SomeEngine.Physics/CollisionQueries.cs) 和 [CollisionTriangleSweeps](../../../src/SomeEngine.Physics/CollisionTriangleSweeps.cs)。

## 几何与空间

`CollisionMesh` 持有局部坐标的顶点和三角形索引视图，以及由顶点形成的局部范围。调用方在使用期间保持这些数组只读。`CollisionSurface` 组合网格、当前局部到世界矩阵和调用方提供的表面版本，其 `Bounds` 包含变换后的八个局部范围角点。

已有业务组件通过 `ICollisionSurfaceReader<TComponent>` 提供同样的几何形状。它同时实现 `ICollisionBoundsReader<TComponent>`，供宽相构建和持续维护读取范围。共享几何可由具体读取器持有，无引用的业务组件保存变换和版本，以接入现有 Job 访问与维护。地形的局部三角形数组和放置矩阵也可形成同一输入。

`Collision.Build` 完成初建后，`Collision.Query(world, members, tree)` 绑定同步查询。泛型重载接收具体组件和读取器。查询对象持有自己取得的只读查询描述，`Dispose` 释放该描述。树与原成员查询继续由调用方持有。

## 最近命中与扫掠

`CollisionRay` 使用世界坐标起点、单位方向与世界距离。`Raycast` 返回沿该线段最近的三角形命中；批量重载在同一个 World 读取作用域内写出每条射线的结果和命中标记，输出范围长度由调用方与输入对齐。

`CollisionSphereSweep` 描述世界坐标球心、半径、单位方向与行程。`SweepSphere` 返回首次接触，包含三角形面、边和顶点接触；批量重载在同一个 World 读取作用域内处理输入的全部球体。起始球体已接触三角形时行程为零。面接触通过偏移平面求交，边和顶点接触分别求射线与边圆柱、顶点球面的首次交点。

`CollisionHit` 保存完整支撑实体、世界接触点、朝向查询形状的单位法线、行程、当前支撑矩阵和表面版本。点与法线来自实际三角形；宽相范围用于选择候选。

## 连续更新

`TryGetSurface` 从当前成员读取指定实体的几何、矩阵和版本。运动方继续写入自己的组件，查询方在更新树后执行新的投射。可调用 `tree.Rebuild`，也可使用 [碰撞树维护](Spatial-Maintenance.md) 的具体无引用组件与读取器，将写入、维护完成和查询依次连接。

`ICollisionQueries` 公开批量射线、批量球形扫掠与 `TryGetTransform`；后者只返回支撑实体当前的矩阵与表面版本。足部与其它支撑相对消费者可通过该入口接入直接几何或其它已就绪的表面提供方。

公开调用位于 `tests/SomeEngine.Physics.Tests/SceneSurfaceQueryTests.cs`，包含平地、斜坡、台阶，以及沿现有维护路径更新的移动支撑。宽相数据职责见 [空间候选](Spatial-Candidates.md)。
