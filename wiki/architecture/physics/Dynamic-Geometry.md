# 实体采样驱动的连续几何

当前设备 Body 表示、端点更新及多端输出见[设备主体](Device-Bodies.md)，场组合见[设备有符号距离场](Device-Signed-Distance-Fields.md)。

本页维护动态几何的数据、读取、范围和接触输出职责。源码入口为 [动态胶囊与采样端](../../../src/SomeEngine.Physics/DynamicCapsule.cs)、[动态边](../../../src/SomeEngine.Physics/DynamicEdge.cs)、[动态面](../../../src/SomeEngine.Physics/DynamicFace.cs)、[三角面数学](../../../src/SomeEngine.Physics/TriangleGeometry.cs) 和 [范围更新](../../../src/SomeEngine.Physics/UpdateGeometryReaderBounds.cs)。公开行为入口为 [DynamicGeometryTests](../../../tests/SomeEngine.Physics.Tests/DynamicGeometryTests.cs)。共同类型检测与接触数据见 [Geometry](Geometry.md)。

## 实体与采样点

`GeometryEndpoint` 保存实际模拟 `Body` 实体与其局部采样点 `LocalPoint`。reader 在有效 Query 作用域中读取该实体当前 `BodyPose`，将采样点转换到世界空间。Body 的位置、旋转和其他物理状态由原实体组件持有；几何组件保存端点引用及形状参数。

| 几何组件 | 参数 | 当前数学输入 | 接触作用端 |
| --- | --- | --- | --- |
| `DynamicCapsule` | 两个采样端、半径 | 两采样点确定轴线与长度的 `CapsuleGeometry` | 两个原采样端与线段插值权重 |
| `DynamicEdge` | 两个采样端 | 半径为零的 `CapsuleGeometry` | 两个原采样端与线段插值权重 |
| `DynamicFace` | 三个采样端、表面半径 | 双面 `TriangleGeometry` 及球形外扩 | 三个原采样端与重心权重 |

胶囊与边使用当前中点和轴线建立几何 frame。动态面的 frame 使用第一端的 `BodyPose`，其余采样点在本次读取中转换到该 frame。三角面提供连续面域、两侧法向、顶点/边/面特征及球形外扩。射线和球扫描复用已有三角面解析计算，其余 support 距离、穿透和流形沿共同内核执行。

三角面特征 `0/1` 表达前后两面，`2/3/4` 对应三个输入顶点，`5/6/7` 对应第一、第二、第三重心坐标为零的三条对边。

业务可用局部零点表示模拟节点，也可用具体局部点采样同一刚体的多个位置。骨架、衣物和其他聚合由项目自己的实体引用组织；reader 消费当前采样位置。几何的暂态值在计算作用域内形成，派生 `CollisionBounds` 保存到几何实体。

## 独立查询与完整访问范围

`DynamicCapsuleReader`、`DynamicEdgeReader` 和 `DynamicFaceReader` 实现共同 `ICollisionGeometryReader<TGeometry>`。`AddReads` 声明几何组件与 `BodyPose`，`ReadGeometry` 取得当前数学输入，`GetBodies` 给出实际作用端和权重。

`Collision.Query<TGeometry,TReader>(world,members,tree,reader,settings)` 使用调用者的几何成员集合和具体 reader。查询持有成员查询及完整访问查询的注册引用，在嵌套作用域中同时使用成员过滤与跨实体读取；采样实体可在几何成员集合之外。原 `Collision.Query<TGeometry>` 通过 `ComponentGeometry<TGeometry>` 接入同一路径。

射线、球扫描、形状扫描、距离、重叠与 `TryGetTransform` 共用这一读取链。几何 reader 的具体 `HasGeometry` 判别保留混合碰撞树中的形状选择。`GeometryVersion` 继续由项目按所需的表面修订语义提供。

## 当前状态到范围与候选

`UpdateGeometryBounds<TGeometry,TReader>` 接收 `World`、几何成员查询、完整访问查询、reader 与 `rowsPerPacket`。完整访问查询在初始化时从 `reader.AddReads(QueryDefinition.Empty)` 建立，由调用方保持至相应工作完成。阶段在创建时记录该查询的 `QueryJobAccess`，执行时由已有生成式 Job 并行写各几何实体的 `CollisionBounds`，在其查询回调中直接读取当前采样组件。

以下是动态面范围阶段的使用例子；`world` 中的几何实体已经具有 `CollisionBounds`：

```csharp
var reader = new DynamicFaceReader();
QueryHandle members = world.Query(new QueryDefinitionBuilder().All<DynamicFace>());
QueryHandle access = world.Query(reader.AddReads(QueryDefinition.Empty));
var update = new UpdateGeometryBounds<TriangleGeometry, DynamicFaceReader>(
    world, members, access, reader, rowsPerPacket: 64);
var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, update, substeps: 1);
PhysicsStepResult step = physics.Schedule(0, 0, 1.0 / 60, dependency: inputReady);
```

完整调用链为：项目输入或预测更新采样 Body → 范围阶段沿其 Job 依赖读取当前组件 → 写几何实体 Bounds → 既有碰撞树维护 → 候选、类型 bin、检测或独立查询。树、pair 与 bin 继续持有派生实体索引和复用容量；真实采样状态保持在原组件。

## 多端接触的锚与权重

检测使用同一个 `CollisionGeometry<TFirst,TSecond>.Detect` 和 `DetectPairsWithQuery` 入口，向 `ICollisionContactOutput` 交付共同接触头及两侧 `ContactBody` 跨度。

胶囊及边从世界见证点求轴线的最近参数，输出两端权重；面域从最近三角点求三端重心权重。每项 `ContactBody.LocalPoint` 保存原端点采样锚。表面半径产生的偏移由共同几何见证点和 `Separation` 表达，因此几何表面信息与采样自由度具有各自明确的数据含义。

同一 Body 的多个局部采样锚在跨度中逐项保留。下游可以按实体合成这些加权位置及 Jacobian，再形成该 Body 的数值贡献。两侧几何实体身份、局部见证点、几何特征和法向沿共同接触头交付，实际模拟实体及其采样权重沿作用端跨度交付。
