# Body 活动与共同子步

本页记录当前活动组件、共同预测与实际图工作范围。源码入口为 [BodyActivity](../../../src/SomeEngine.Physics/BodyActivity.cs)、[PredictActiveBodies](../../../src/SomeEngine.Physics/PredictActiveBodies.cs)、[PhysicsActivity](../../../src/SomeEngine.Physics/PhysicsActivity.cs) 和[图变化维护](../../../src/SomeEngine.Physics/PhysicsGraph.Changes.cs)。公开入口见 [PhysicsActivityTests](../../../tests/SomeEngine.Physics.Tests/PhysicsActivityTests.cs)。

`BodyActivity` 在 Body 实体上保存 `Sleeping`、`QuietSeconds` 和角运动的长度尺度 `Radius`。同一组件的内部 `MotionPredicted` 标记本子步实际运动预测是否完成。真实位姿、速度、质量和共同子步输入继续由原 Body 组件持有。

`PredictActiveBodies(world, bodies, acceleration, rowsPerPacket)` 在初始化时注册输入、运动和只读查询。每次调度在依赖完成后取得当前成员和 awake 集合；第一次写入前再次复核这两个集合，变化沿原实体范围桥重新捕获。

共同输入范围为全体所选 Body 更新 `BodyStepState` 起点、预测初值、步号、子步号与时刻；位姿和速度在该范围只读。实际运动范围只声明 awake 动态 Body 与运动学 Body 的位姿、速度和预测结果写入。两类范围通过原 `EntityQueryJobs.ScheduleGroup` 一次持有并同步分包执行；实体集合、查询数据与包容量保持到返回完成句柄的全部工作结束，再归还复用池。

`Resume()` 沿同一预测实例为新唤醒且本子步尚未推进的 Body 执行运动预测，保留原共同起点。项目可将它组合在检测、持久接触刷新和 `WakeConnected()` 之后，再进入响应与速度重建。已经推进的 Body 继续使用本子步的原预测。

`SolveXpbdGraph.Activity(settings)` 与 `SolveAvbdGraph.Activity(settings)` 返回活动阶段，直接共享对应求解器的原图、查询和具体 `IPhysicsGraphInputs`。同一 Body 的活动状态由 `BodyActivity` 提供，两类算法共同读取它。全量动态连通关系继续由原 `PhysicsGraphData` 持有；活动变化进入受影响岛的增量维护，休眠岛保留连接数据，实际 Body 和约束批次选择 awake 岛。

`WakeConnected()` 沿当前岛读取速度、已观察的位姿及质量版本和静态、运动学输入，再传播到同岛动态 Body。`Finish()` 使用速度、质心位移和由 `Radius` 换算的角运动，与项目的 `SpeedThreshold`、`QuietDuration` 共同更新安静时间；同岛成员满足当前条件后进入休眠并清零速度。版本索引沿当前实体成员回收条目，容量继续复用。

活动数学参考项目研究分支 `codex/job331-380` 的 `Native/Islands.cpp` 中 `wakeBody`、`researchBuildIslands` 和 `finishIslandStep`：动态连通传播、速度与尺度角速度、位移及安静时间使用同样的物理量。原分支和本 C# 实现使用仓库 Apache 2.0 LICENSE。

项目显式安排输入、共同预测、接触刷新、唤醒、求解和速度重建的次序。以下是使用例子，`solve` 为项目已创建的完整图求解器：

```csharp
using var activity = solve.Activity(activitySettings);
using var prediction = new PredictActiveBodies(world, bodies, acceleration, rowsPerPacket: 64);
var stages = new ApplyBodyInputs(64)
    .Then(activity.WakeConnected())
    .Then(prediction)
    .Then(detection).Then(points.Refresh())
    .Then(activity.WakeConnected()).Then(prediction.Resume())
    .Then(initializeResponses).Then(solve)
    .Then(new UpdateBodyVelocities(64)).Then(activity.Finish());
```
