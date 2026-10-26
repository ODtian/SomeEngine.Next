# Physics frames

本页记录物理完成步在游戏帧中的消费与提交。入口为
[`PhysicsFrame<TStage,TConsumer>`](../../../src/SomeEngine.Physics/PhysicsFrame.cs)、
[`IGameFramePhysics`](../../../src/SomeEngine.Core/ECS/IGameFramePhysics.cs)、
[`GameWorld.Update`](../../../src/SomeEngine.Core/ECS/GameWorld.cs) 和
[`GameFrameSystems`](../../../src/SomeEngine.Runtime/GameFrameSystems.cs)。

## 固定步与结果时刻

项目通过 `PhysicsSimulation.CreateFrame` 提供具体物理阶段、结果消费阶段、固定步长、
允许延迟、首步编号和起始时刻。两个阶段均实现已有的 `IPhysicsStage`，沿相同的
`PhysicsStepContext`、参与实体查询和 Job 依赖执行。消费阶段把实体物理结果写入项目使用的
变换、骨骼或游戏输出组件。

`AdvanceTime(deltaTime)` 累计目标时刻。完整固定步的数量由累计时间除以步长取得；
`SubmitDueSteps` 按步号顺序提交到该时刻，每步依赖前一步的完成。步号与
`PhysicsStepResult.StartTime/EndTime` 一起描述结果所属的时间区间。
`Result` 保存已经选择消费的步及其消费完成句柄，`SubmittedStep` 保存最近提交的步。
实体组件持续持有姿势、速度和其他数值状态。

[完成步事件](Events.md)由各步的普通 ECS 事件实体保存。一次消费跨过多个物理步时，事件实体继续保留各自的原步号和时刻，消费阶段确认后通过同一命令链释放。

例如，以下是固定步配置的行为示例：60 Hz 物理在两个 120 Hz 呈现帧后完成一个到期步，
一个 30 Hz 呈现帧产生两个到期步。呈现间隔只影响本次到期的步数，结果时刻沿固定步推进。

## 消费窗口

`ConsumeCompleted` 在游戏输入之后选择已经完成的提交步；消费阶段依赖该步，返回句柄覆盖
消费输出。到期物理时刻与上次消费结果相差达到 `maximumDelay` 时，入口提交全部到期步，
并返回这些步及最终消费的完成句柄。调用方在变换和后继结果读取之前完成它。

强交互消费者调用 `CompleteForRead`，在实际读取点提交并完成当前全部到期步与结果消费，
随后通过返回的 `PhysicsStepResult` 取得步号和时刻。弱交互消费者继续读取上次消费得到的
派生变换等组件，独立输入、界面动画及 Rendering world 沿自己的实际资源推进。

`GameWorld.Update(deltaTime, consume)` 的顺序是累计时间、游戏系统、系统完成、结构命令回放、
选择物理结果并完成消费、变换传播、`consume` 读取回调、提交后续到期步。
`GameFrameSystems.UpdateFrame` 在游戏输入组完成和命令回放后消费物理，随后运行
`GameTransforms`、视图、动画、布局与读取回调，最后提交后续步。

物理阶段原位写入 `BodyPose` 等组件，下一步提交发生在本次读取窗口结束后。
读取实际物理列的查询按其列资源等待；读取已完成派生输出的查询按派生输出声明访问。
真实结构编辑沿 World 的结构写依赖完成相关作业。

## 应用与生命周期

[`RuntimeApplication`](../../../src/SomeEngine.Runtime/RuntimeApplication.cs) 在应用输入前累计
本轮时刻，复用 `GameFrameSystems` 的消费和变换阶段。动画、渲染提取、应用回调以及
`FrameCompleted` 的读取全部结束后，应用提交后续到期步。
游戏输出继续通过现有提取写入 Rendering world。

帧接入对象持有配置、步号、时刻和完成依赖；调度、协作等待与作业资源归还沿既有 Job 完成链。
关闭时先完成最后提交的物理作业，再执行应用关闭回调并释放系统和两个 World。

公开行为位于
[`PhysicsFrameTests`](../../../tests/SomeEngine.Runtime.Tests/PhysicsFrameTests.cs) 与
[`ApplicationPhysicsTests`](../../../tests/SomeEngine.Runtime.Tests/ApplicationPhysicsTests.cs)。
实际声明的查询与引用数据读取见 [[architecture/ECS/Query-Reads|Query reads]]，
提取交接见 [[architecture/rendering/Extraction|Extraction]]。
