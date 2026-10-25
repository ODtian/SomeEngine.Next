# 实体长程约束与接触支撑

本页维护当前 LRC 数据、数学和公共用法。源码入口为[关节界](../../../src/SomeEngine.Physics/JointLongRangeConstraints.cs)、[关节图](../../../src/SomeEngine.Physics/JointLongRangeGraph.cs)、[接触支撑](../../../src/SomeEngine.Physics/ContactLongRangeConstraints.cs)、[支撑图](../../../src/SomeEngine.Physics/ContactSupportData.cs)与[局部支撑求解](../../../src/SomeEngine.Physics/ContactSupportData.Solve.cs)。公开行为入口为 [LongRangeConstraintTests](../../../tests/SomeEngine.Physics.Tests/LongRangeConstraintTests.cs)。数学迁移来源及许可见 [LongRange NOTICE](../../../src/SomeEngine.Physics/ThirdParty/LongRange/NOTICE)。

## 显式选择与数据职责

项目通过具体 `JointLongRangeConstraints`、`ContactLongRangeConstraints` 与原 `IAvbdConstraints` 集合组合来启用 LRC。两种阶段读取共同 `PhysicsBodies` 中的原位姿、质量和步输入，沿同一个 AVBD 主轮装配、修正位姿并更新反力。

| 数据 | 持有位置 | 内容 |
| --- | --- | --- |
| LongRangeJointBound Buffer | 原关节实体 | 远端关节引用、双方框架角色、距离上下界、乘子、数值度量与起点误差 |
| LongRangeBodySupport | 所选模拟实体 | COM 高度界、起点误差、当前反力及本子步运动资格 |
| LongRangeContactSupport | 原接触实体 | 锚高度界、高度传播量、当前反力与载荷、世界辅助力 |
| LongRangeSupportLoad Buffer | 所选模拟或接触实体 | 下层接触实体引用与插值权重 |
| LongRangeSupportEquation Buffer | 所选模拟实体 | 本地高度方程、Jacobian、当前分解的响应与活跃反力工作区 |

辅助反力及数值历史由这些组件持有。图保存实体索引、关节版本和几何派生量，其字典、队列、列表、凸包和批次容量持续复用。Body 的真实状态继续由共同组件持有。

## 关节距离界

调用方为选定的原 `JointConstraint` 实体添加 `LongRangeJointBound` Buffer，创建读取共同关节定义、受保护 `DirectedRelationEndpoints<JointConstraint>` 并读写该 Buffer 的查询。实际节点资格要求三轴线性锁定及三轴理想刚性材料 `float.PositiveInfinity`；有限 K 继续沿原材料方程求解。

图以关节位置为节点，以同一动态刚体内两处局部关节锚之间的距离为初始段。固定端的最短路径按总段长形成上界，并以最长段和总长形成下界；项目给出的 `Levels` 控制后续 2、4、8 等段级组合。相同节点对的路径取上下界交集。

`LongRangeJointBound.FirstEnd` 和 `OtherEnd` 用 `JointEnd.Source/Target` 表示原关节的框架角色；`OtherJoint` 指向远端关节。两处 Body 和局部锚均在有效查询中从原关节及受保护端点取得。派生图按当前查询成员、关节定义、端点及相关质量组件的写入版本刷新。关节框架和拓扑变化进入下一次初始化，原 Buffer 容量继续复用。

关节图从对应 `QueryRow.WriteVersion<T>()` 读取原行版本。数值内核使用共同 `PhysicsQueryRows` 借用原行，其入口可包装普通查询或已准入的实体范围；接触作用端优先从共同 Body 读取视图中的原参与边取得。状态、反力和本地方程继续写入同一组件及 Buffer。

每个子步从共同起点计算上下界初始误差，按 `MultiplierRetention` 保留自己的力乘子。数值度量为 `PenaltyScale / (h² · pointResponse)`，其中 pointResponse 使用双方逆质量、质心杠杆和完整世界逆惯量。`InitialErrorFraction` 选择本子步保留的初始界误差。

上界和下界各用非负乘子，净力为上界减下界；等长界使用一个有符号力。活动界的力、Jacobian 与正几何曲率进入当前六维 Body 方程，全部 Body 更新后进行辅助界 dual 更新。原关节和长程界分别保存各自乘子，单位为 N；距离及初始误差为 m，数值度量为 N/m。

## 接触支撑范围与构造

`ContactLongRangeConstraints(supportBodies, contacts, settings)` 接收两个显式工作查询。`supportBodies` 只选择项目附加 LRC 工作组件和 Buffer 的模拟实体；共同 AVBD Body 查询继续包含该步实际参与的刚体或节点。支撑阶段在有效回调中同时借用工作查询与原接触查询，沿外层 `PhysicsBodies` 读取真实状态。固定地面和运动学参考端使用其共同状态提供支撑输入。

接触查询声明 [共同接触](Contacts.md)读取、`AvbdContactState`、`ContactResponse`、`LongRangeContactSupport` 和载荷 Buffer 读写。项目的新点组件写入器沿既有 `IContactPointComponents` 添加相应工作组件。选择 `AvbdSupportedContactMath` 后，原 AVBD 接触行以自身乘子加 LRC 世界辅助力计算总压力和摩擦投影。

支撑候选采用理想刚性法向材料及两侧各对应一个 Body 的接触。同侧多个局部锚沿实际权重合成。当前间距由几何 `Separation`、`BodyAnchorDelta` 与当前加权锚位移共同给出；投影位置使用检测交付的世界几何见证点。`UpDirection` 为项目给出的单位向上方向，平面投影和高度均沿该方向计算。

法向与静摩擦锥须能够承载向上的反力。接近或已有压力的接触，再按静止滑移距离、当前压力和点响应判断摩擦支撑资格。侧向 COM 位移和角位移超过显式门限时，该 Body 在本子步进入运动状态。动态下层须属于所选支撑工作范围，并由其下方凸包承载；固定或运动学下层使用实际步输入。

候选按高度处理，先将下层接触投影到向上方向的正交平面，再以凸包内的线段或三角形权重构造载荷路径。接触锚和支撑多边形内的 COM 各形成高度下界；几何表面偏移与模拟采样锚的差通过共同 gap 关系进入高度传播量。载荷权重保存对应接触实体。

## 当前分解、活跃点与反力交接

`SolveAvbd` 在每次 Body 局部更新的 `Factorize`、`SolveResponse` 之后，提交原位位姿之前调用 `IAvbdConstraints.AdjustBodyChange`。普通约束提供具体空实现，集合组合依次转发，支撑阶段在这里修正本次平移和旋转改变量。

当前六维 LDL 先提供向上力响应；COM 单点能满足全部高度界时直接使用这一反力。其余情况继续取得两个正交倾斜力矩的响应，并从 1、2、3 个活跃高度点中求非负反力。候选须满足全部高度下界，相关性门限与高度容差来自项目设置。局部方程和响应写入该 Body 的 `LongRangeSupportEquation` Buffer，支撑修正复用原分解。

主轮原接触 dual 更新后，LRC 将 COM 和锚反力按载荷权重从上向下传递，更新每个接触自己的 `ExtraForceOnSecond` 和共同 `ContactResponse.ForceOnSecond`。原接触乘子与辅助力保持各自持有，摩擦投影读取两者的总作用。世界辅助力、载荷和反力的单位均为 N，共同作用区间继续由 `InitializeContactResponses` 给出。

进入滑动或转动的 Body 触发当前支撑图更新。释放的辅助力投影到原接触的法向及切向乘子，沿相同库仑界限接续标准响应。持久接触的热启动开关和 `MultiplierRetention` 控制跨步辅助力保留。

## 组合用法与工作量

以下是组合用法的例子；实体、查询、材料和接触创建由项目提供：

```csharp
var originalJoints = new AvbdConstraints<JointConstraint, AvbdJointMath>(joints);
var jointBounds = new JointLongRangeConstraints(rangeJoints,
    new JointLongRangeSettings(Levels: 4, PenaltyScale: 1,
        MultiplierRetention: .99f, InitialErrorFraction: .99f));
var originalContacts = new AvbdConstraints<ContactPoint, AvbdSupportedContactMath>(contacts);
var supports = new ContactLongRangeConstraints(supportBodies, contacts,
    new ContactSupportSettings(up, DistanceTolerance: 1e-5f,
        MotionTolerance: 1e-4f, AngularMotionTolerance: 1e-4f,
        StickDistance: 1e-4f, MultiplierRetention: .99f,
        InitialErrorFraction: .99f, RelativePivotTolerance: 1e-6f));
var constraints = AvbdConstraintGroups.Combine(
    AvbdConstraintGroups.Combine(originalJoints, jointBounds),
    AvbdConstraintGroups.Combine(originalContacts, supports));
var stages = new PredictBodies(acceleration, 64)
    .Then(new InitializeContactResponses(world, bodies, contacts))
    .Then(SolveAvbd.Create(world, bodies, constraints,
        iterations: 8, bodyIterations: 1, multiplierRetention: 1, initialErrorFraction: 0))
    .Then(new UpdateBodyVelocities(64));
```

`supportBodies` 声明 `LongRangeBodySupport`、`LongRangeSupportLoad` Buffer、`LongRangeSupportEquation` Buffer 的实际读写；核心 Body 查询声明原位姿、质量、步状态、速度和 `AvbdBodyEquation`。每个集合沿原 `QueryJobAccess` 声明资源，其可复用索引作为所属集合资源进入现有 Scheduler。

`Levels`、主轮和每体局部轮数构成显式预算。完整步工作包含关节图刷新、支撑图构造与运动释放、辅助界装配及 dual、已有 Body 分解、支撑的至多三个六维右端求解、活跃点小方程和载荷交接。它们沿原求解 Job 完成依赖交付，整步计时入口将这些实际工作一并包含。
