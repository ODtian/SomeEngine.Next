# 预测式碰撞

本页维护已实现的运动范围、预测接触与共同物理步用法。源码入口为 [运动读取器](../../../src/SomeEngine.Physics/CollisionGeometryMotion.cs)、[范围更新](../../../src/SomeEngine.Physics/UpdatePredictedBounds.cs)、[用户读取器范围更新](../../../src/SomeEngine.Physics/UpdatePredictedReaderBounds.cs)、[预测检测](../../../src/SomeEngine.Physics/GeometryPairPrediction.cs)与[时间窄阶段](../../../src/SomeEngine.Physics/StepContactNarrowPhase.cs)。公开使用入口见 [PredictiveCollisionTests](../../../tests/SomeEngine.Physics.Tests/PredictiveCollisionTests.cs)。

## 运动与几何输入

`PredictBodies` 保存共同子步的起点及预测质心、旋转，并原位更新 `BodyPose`。预测检测继续读取这些 `BodyStepState`，其时间与其他约束和响应沿同一个 `PhysicsStepContext` 传递。

`ICollisionGeometryMotion<TGeometry>` 在普通 `ICollisionGeometryReader<TGeometry>` 上增加三个具体操作：

| 操作 | 输入与结果 |
| --- | --- |
| `ReadStartGeometry` | 从有效查询视图取得子步起点的具体几何与几何框架 |
| `GetStartBodies` | 将起点几何世界见证点映射为实际 Body、局部采样锚和权重 |
| `ReadMotionBounds` | 使用共同时间、原状态与显式 padding 生成该几何的运动范围 |

`ComponentGeometryMotion<T>` 读取同一实体上的形状与 Body 状态，起点框架由起点质心、旋转及 `BodyMass.CenterOfMass` 还原。用户读取器沿相同接口读取被引用的模拟实体，可让几何身份与实际作用体分别持有各自数据。

`UpdatePredictedBounds<T>(padding, rowsPerPacket)` 通过现有生成式组件 Job 更新原 `CollisionBounds`。范围取起点、共同预测及当前姿态的包围盒并集，并增加 `|angularVelocity| × radius × h` 的角向运动量及项目 padding。radius 由起点形状范围相对质心的最远角点确定；共同步的预测已包含所选输入与加速度。

`UpdatePredictedBounds<TGeometry, TMotion>(world, members, access, motion, padding, resources)` 支持引用式及用户几何。`members` 选择需要更新边界的几何实体，`access` 提供读取器实际使用的几何与 Body 访问范围。阶段复用所选实体引用容量；可选 `QueryReadResources` 按这些实体的当前组件引用登记实际资源，完成输入依赖后复核绑定，再由同一资源 Job 写入所选实体的原边界。构造取得的查询引用及工作资源由 `Dispose` 释放。

这些运动范围直接交给现有 BVH、网格或排序候选维护。pair 及类型 bins 继续使用原实体配对与过滤入口。

## 起点分离平面与预测接近

`CollisionGeometry<TFirst, TSecond>.Predict(firstMotion, secondMotion, kernel, output, contactDistance)` 接收双方具体运动读取器、原 `ICollisionNarrowPhase` 内核、共同接触输出和显式距离。它在起点几何上取得分离平面；起点距离进入接触范围时沿原内核生成流形，起点仍有间距时使用最近见证点。

两侧采样锚分别在共同起点与共同预测状态求值。其预测间距为：

```text
predictedGap = startSeparation + dot(startNormal, predictedAnchorDelta - startAnchorDelta)
```

起点或预测间距进入项目的 `contactDistance` 时，检测交付该起点接触。高速物体即使在预测终点已穿过薄障碍，起点平面仍为所选响应提供步内接近约束。旋转、偏心质心和多端采样的位移沿原局部锚与共同状态参与此投影。该模型使用一组起点分离平面描述本子步的预测接近。

输出保持双方几何实体、特征、点序号、几何局部见证点、世界见证中点、法向、起点间距及实际 Body 采样跨度。`ContactPrediction.Enabled` 标明起点采样，`SampleTime` 等于 `context.StartTime`。持久点刷新据此从相同起点状态计算 `BodyAnchorDelta`；响应继续使用[共同接触间距公式](Contacts.md#接触身份与作用端)读取当前锚，得到本轮残差。普通几何检测沿当前姿态采样路径交付其原接触。

`ContactResponse.StartTime`、`EndTime` 保存实际反力和恢复冲量的子步区间。预测采样时刻与响应作用区间分别由对应数据持有。

## 显式阶段组合

以下是阶段组合的使用例子，候选维护、查询与实际求解配置由项目提供：

```csharp
var prediction = CollisionGeometry<SphereGeometry, BoxGeometry>.Predict(
    new ComponentGeometryMotion<SphereGeometry>(),
    new ComponentGeometryMotion<BoxGeometry>(),
    new SphereCollision<BoxGeometry>(),
    new ContactPointOutput(detected), contactDistance: 0);
using var detection = DetectContactPoints.Create(world, pairs, bins, classifier,
    StepContactNarrowPhase.Create(bin, prediction), detected, detectionInputs);

var stages = new PredictBodies(acceleration, 64)
    .Then(new UpdatePredictedBounds<SphereGeometry>(padding, 64))
    .Then(new UpdatePredictedBounds<BoxGeometry>(padding, 64))
    .Then(updateCandidates)
    .Then(detection).Then(points.Refresh())
    .Then(initializeResponses).Then(solveContacts).Then(updateVelocities);
PhysicsSimulation.Create(world, bodyFilter, stages, substeps)
    .Schedule(number, startTime, duration).Complete();
```

`updateCandidates` 是项目对既有候选维护 `Schedule(dependency)` 的阶段适配。直接形状范围更新沿 `context.Bodies` 选择共同 Body 范围；引用式几何更新沿显式 `members`。预测接触刷新所用 Body 读取查询声明 `BodyPose`、`BodyMass` 与 `BodyStepState`；响应及求解沿其既有查询声明读写。

`StepContactNarrowPhase` 将完整 context 交给具体 `IPhysicsCollisionPairDetection`，并借用已登记的 bin 检测视图。`ContactNarrowPhases.Combine` 可将普通与预测窄阶段按项目选择组合。完成依赖依次覆盖范围写入、候选维护、bin 检测、持久点与参与关系刷新、所选数值响应及速度更新。

运动包围与预测局部锚的数学沿研究分支 `codex/job331-380` 的 `Islands.cpp::readCollisionExtent/findCollisionPairs`、`Bridge.cpp::readBodyTravel/readPredictedContacts` 接入共同步；来源说明见 [Newton NOTICE](../../../src/SomeEngine.Physics/ThirdParty/Newton/NOTICE)。
