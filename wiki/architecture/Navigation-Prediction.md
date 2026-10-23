# Navigation prediction

本页记录当前预测 Buffer、强类型运动模型与调度入口。对应实现为 `src/SomeEngine.Navigation/NavigationPrediction.cs`，公开行为入口见 `tests/SomeEngine.Navigation.Tests/NavigationPredictionTests.cs`。

`NavigationTrajectoryPoint` 是 ECS Buffer 元素，保存调用方时间基准中的 `Time`、世界位置 `Position`、实际模型速度 `Velocity` 与 XZ 朝向 `Heading`。每次写入的第一个点来自本步实际运动读取器，随后按角色配置的采样间隔生成未来点，最后一个点位于配置的预测时长。

`NavigationComponents.AddPrediction<TModel>(world, entity, horizon, interval)` 安装角色的模型选择、采样配置与结果 Buffer。`NavigationPrediction<TModel>` 由游戏维护；对应 `NavigationPredictionSystem<TModel, TMotion, TReader>` 是该组合的结果写入方。每个角色的 Buffer 由生成式 Job 的角色范围独占写入。

调用链为：

1. 路径或流场生成 Guidance，选定避让阶段生成 Steering。
2. `prediction.Schedule(time, steeringReady, motionReady)` 合并已完成的建议与实际运动输入，返回预测完成句柄。
3. 动画 Job 依赖该句柄，通过 `BufferView<NavigationTrajectoryPoint>` 读取整段轨迹。
4. 移动消费者执行建议并写入实际位姿、速度和朝向；后续 Progress 与下一轮预测以这些实际数据继续。

`INavigationPredictionModel.Integrate` 接受实际起点、完成的 Steering、Profile 与经过时间。模型实现是静态强类型调用。业务可以用同一模型积分入口执行实际运动，从而使预测与消费保持相同的定义。

`NavigationVelocityPrediction` 使用本步 `Steering.Response` 的闭式速度及位移积分，并按 `Profile.MaximumAngularSpeed` 限制朝向变化。AVO 的可达目标速度及响应时间随 Steering 交付，预测继续使用这份已选模型参数。原始实际速度保留在第一个轨迹点中。

公共行为检查中的开阔方形地图与移动偏移是验收例子：动画取得未来转向序列，运动消费者产生位移，下一次 Buffer 的首点与该实际反馈对应。模型、采样时长、时间基准与角色数量由公开输入决定。
