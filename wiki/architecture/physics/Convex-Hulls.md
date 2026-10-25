# 凸包数据、检测与查询

本页维护已实现的凸包数据职责与公共调用方法。源码入口为 [ConvexHullData](../../../src/SomeEngine.Physics/ConvexHullData.cs)、[ConvexHullGeometry](../../../src/SomeEngine.Physics/ConvexHullGeometry.cs) 和 [UpdateConvexHullBounds](../../../src/SomeEngine.Physics/UpdateConvexHullBounds.cs)，公开行为入口为 [ConvexHullTests](../../../tests/SomeEngine.Physics.Tests/ConvexHullTests.cs)。共同几何、接触输出与查询接口见 [Geometry](Geometry.md)。

## 数据与使用期

项目创建已经就绪的 `ConvexHullData`，提供三组数组支持的 `ReadOnlyMemory`：局部顶点 `Vertices`、面描述 `Faces`、面顶点索引 `FaceVertices`。每个 `HullFace` 包含朝外单位法向与索引范围 `FirstIndex/IndexCount`；索引按该朝外法向绕面排列。数据对象保留项目提供的内存，多个实体通过 `ConvexHullGeometry.Data` 共享同一份顶点与面数据。

项目保持已提供数组只读。几何变化通过建立完整新数据并替换实体的 `ConvexHullGeometry` 生效。数据对象及组件的普通强引用保持数组有效，沿用 [已就绪使用对象](../Assets/Asset-Objects.md) 的数据持有方式。资源引用与顶点内容由几何对象持有，pair、树和 bin 继续保存实体引用及派生索引。

`ConvexHullGeometry` 还保存显式 `ConvexRadius`，表示核心外的球形外扩；`BodyPose` 提供世界位置与旋转。面特征为 `Faces` 中的索引，支持顶点特征为 `Faces.Length + vertexIndex`。面序及顶点序由提供该数据的项目确定。

以下是创建共享几何的使用例子；`vertices`、`faces`、`faceVertices` 是项目已经取得的数组内存：

```csharp
var data = new ConvexHullData(vertices, faces, faceVertices);
var geometry = new ConvexHullGeometry(data, ConvexRadius: .01f);
Entity entity = world.CreateEntity(geometry);
world.Add(entity, pose);
world.Add(entity, ConvexHullGeometry.Bounds(in geometry, in pose));
```

## 类型检测与几何数学

`ConvexHullGeometry` 实现与基础形状相同的 `ICollisionConvex<T>`。核心 support 在当前局部方向选取点积最大的原顶点，再经当前 `BodyPose` 变换到世界空间；外扩由共同 `CollisionConvex` 与窄阶段使用。面读取从原索引范围输出带面特征的世界点，所需容量由数据对象在创建时记录的最大面顶点数提供。

`ConvexCollision<ConvexHullGeometry,TGeometry>` 及反向类型组合复用共同 GJK、EPA 与支持面裁剪，覆盖另一凸包、球、盒、胶囊和圆柱。`ConvexPlaneCollision<ConvexHullGeometry>` 处理平面组合。项目显式传入各内核参数，再通过 `CollisionGeometry<TFirst,TSecond>.Detect` 和 `DetectPairsWithQuery` 进入类型 bin；内置及用户 `ICollisionConvex`、`ICollisionNarrowPhase` 实现经过相同的具体泛型调用。

默认几何 reader 在当前 Query 作用域读取组件及位姿；接触头保存双方几何实体、特征和局部接触点，`ContactBody` 表达实际模拟实体与作用点，具体类型的输出接收方通过 `ICollisionContactOutput` 取得这些结果。

点距离先检查凸包面半空间：内部点由最近退出面给出负距离；面域中的外部投影直接使用该面距离和法向；其余外部点复用三角面最近点数学。球形外扩随后调整表面见证点及距离。几何查询的射线和球扫描复用该距离，形状扫描、距离与重叠使用项目选择的共同窄阶段。

## 物理步与独立查询

`UpdateConvexHullBounds(rowsPerPacket)` 是共同物理步阶段，从原几何与位姿计算实体的派生 `CollisionBounds`。其生成式 Job 通过 `ConvexHullResources` 声明三组实际数组内存的读范围；现有组件资源收集在前置 Job 完成后取得当前组件引用，并保持所选资源至工作完成。因此几何替换、范围更新、树维护和实际查询按显式完成依赖接续。

以下是已建立实体及查询后的接续例子；`world`、`bodies`、`members`、`maintenance` 与 `inputReady` 由项目持有：

```csharp
var physics = PhysicsSimulation.Create(world, bodies,
    new UpdateConvexHullBounds(rowsPerPacket: 64), substeps: 1);
PhysicsStepResult step = physics.Schedule(0, startTime: 0, duration: 1.0 / 60, dependency: inputReady);
maintenance.Schedule(step.Completion).Complete();
using var queries = Collision.Query<ConvexHullGeometry>(world, members, maintenance.Tree,
    new CollisionQuerySettings(Tolerance: 1e-5f, Iterations: 64));
```

`Collision.Query<ConvexHullGeometry>` 直接提供 `Raycast`、`SweepSphere`、`Sweep`、`Overlap`、`Distance` 与 `TryGetTransform`。查询在有效组件访问范围内读取已就绪的共享只读几何，使用当前位姿及项目维护的碰撞树，返回共同实体命中或距离结果。
