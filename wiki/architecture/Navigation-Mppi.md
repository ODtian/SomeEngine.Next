# MPPI 局部运动轨迹

`NavigationAvoidanceSystem<TSpace,TModel,TMotion,TReader>` 使用 MPPI 为角色选择控制序列，并把同一完成序列的第一段交给 Steering、整段交给 `NavigationTrajectoryPoint` Buffer。Profile、Guidance、路径、通行 Costs、共同邻居及实际运动组成每次求解输入。公开行为入口见 `tests/SomeEngine.Navigation.Tests/NavigationMppiTests.cs`。

## 运动模型与配置

`NavigationComponents.AddMppi<TSpace,TModel>` 安装角色配置、独立控制工作区及 `NavigationMppiResult`，并把 `NavigationTrajectorySource` 设为 `Mppi`。角色通过普通路径组件取得目标、完成路径和 Guidance。`NavigationTrajectorySource.Kind` 选择本角色的轨迹写入方；`ModelPrediction` 选择预测系统，`Mppi` 选择局部控制器。

`INavigationMppiModel<TSpace>` 定义两维控制向量的参考控制、约束、积分、完整步进通行查询与 Steering 转换。每项静态调用使用相同 Profile 和控制定义。`NavigationVehicleMppiModel` 的两维控制分别是有符号速度和曲率：速度变化受最大加速度约束，曲率受最小转弯半径约束，速度与曲率的乘积受最大角速度约束。车辆积分调用 `NavigationVehicleModel`，通行评价调用 `NavigationVehicleSpace.TryTraverse` 的完整轮廓扫掠及 Costs 查询。

`NavigationMppiSettings` 控制候选数量、时域步数、每步时长、迭代次数、两维扰动标准差、时间相关系数、温度和各项代价权重。`CollisionResolution` 控制动态邻居检测的采样距离，`ReferenceLookahead` 控制参考点前视距离，`Seed` 控制可复现采样。调用方把 `TimeStep` 与实际控制消费时长对应，并在下一次求解传入同一时间基准中的当前时间。

## 持续控制序列与评价

每个角色保留控制序列及参考路径位置。初次求解以路径参考控制初始化；后续按经过时间前移已选序列，并用当前路径参考补齐末段。路径或请求版本变化时重新建立参考。每轮所有候选都从读取器提供的实际位姿、速度和朝向展开。

候选扰动使用 Gaussian 采样，协方差为每个控制轴的 `sigma² [(1-rho) I + rho 11ᵀ]`。控制代价使用相应逆协方差计算控制与扰动的 likelihood 项。归并按 `exp(-(cost-minimum)/temperature)` 计算权重，并对整段扰动加权更新控制序列。

每段轨迹累计路径位置误差、朝向误差、通行成本及邻居间距代价，末端累计到目标范围的距离和目标朝向代价。模型通行查询覆盖车体轮廓扫掠、局部可走空间、区域策略和 Costs。动态邻居查询使用同一邻居索引中采集的完成预测；预测按当前实际反馈对齐位置与朝向，轨迹末端按末速度延伸，只有实际样本的角色按实际速度展开。动态检测按相对平移及车体转动增加采样，计算本角色轮廓与邻居圆盘的间距和高度重叠。

加权后的控制序列再次执行完整评价。求解器依次选取通过评价的更新序列、最低代价候选或制动序列。`NavigationMppiResult` 交付 `Ready`、`Idle` 或 `NoFeasibleTrajectory` 状态；完整结果包含代价与请求版本。选中序列的第一段生成 `Steering.VehicleControl`，Buffer 保存实际起点及后续模型积分结果。

## Job 与公开调用

生成式准备 Job 按角色独占其工作区，并读取 Profile、路径和空间的实际资源。每个候选 Job 写入自己在扰动、控制、展开轨迹和分数数组中的范围。候选与归并分别通过空间和角色的资源描述收集实际输入，在各自工作体执行期间持有读取访问。每轮归并依赖全部候选，按角色独占更新控制、权重及选中轨迹；下一轮继续使用这份完成序列。父句柄在所有子 Job 完成后结束，资源访问随各自工作体释放。输出 Job 随后写入 Steering、结果组件和选中的轨迹 Buffer。

每轮捕获 `TSpace.GetVersion` 与该绑定的实际资源集合，候选和归并在取得完整访问后分别核对版本。空间更新替换绑定时，归并重新收集输入并作为子工作重做当前整轮，使分数、候选后备轨迹和选中轨迹对应同一绑定。后续轮和输出等待原归并句柄的全部子工作完成；候选继续写入各自独立范围。可变空间通过 `CollectResources` 声明保存绑定的对象及其当前数组，更新任务写入同一绑定对象并推进版本。

下面是公开调度顺序的例子；角色数量、空间数据、目标和运动组件来自游戏：

```csharp
var neighbors = new NavigationNeighborSystem<Motion, MotionReader>(world, scheduler);
var mppi = new NavigationAvoidanceSystem<
    NavigationVehicleSpace, NavigationVehicleMppiModel, Motion, MotionReader>(world, neighbors);

NavigationComponents.AddMppi<NavigationVehicleSpace, NavigationVehicleMppiModel>(
    world, actor, vehicleSpace, new NavigationMppiSettings { TimeStep = deltaTime }, avoidance);

JobHandle guidance = paths.ScheduleGuidance(deltaTime, expansionBudget, actualReady).Guidance;
JobHandle neighborReady = neighbors.Schedule(actualReady, neighborPredictionReady);
JobHandle selected = mppi.Schedule(time, guidance, neighborReady, actualReady);
JobHandle moved = new MoveVehicle(deltaTime).ScheduleParallel(world, selected);
JobHandle progress = paths.ScheduleProgress(moved);
```

动画依赖 `selected` 读取整段 Buffer；`MoveVehicle` 使用同一 `NavigationVehicleModel` 执行 `Steering.VehicleControl` 并写回实际位姿、速度、朝向。Progress 依赖移动完成句柄，下一轮求解读取该实际反馈。

验收中的 L 形区域、长形车辆及横向移动角色是一组例子：完成轨迹的车体位于可走范围，实际运动的下一步与 Buffer 的对应点一致，车辆绕过弯道并推进目标距离。同一验收同时配置模型预测，轨迹来源选择保持 MPPI 完成序列。

算法依据为 [Nav2 MPPI 官方文档](https://docs.nav2.org/rolling/configuration_and_development/configuration_guide/controller_plugins/mppi_controller/configuring_mppic/) 介绍的采样控制、温度加权和轨迹评价流程；本实现把模型调用、数据所有权和候选分区接入引擎 Job 与 ECS。
