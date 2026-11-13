# 接触

CPU 的 `ContactManifolds<TState>` 持有当前接触的公共列和一个具体求解器的 `TState` 列。`ContactColumns` 持有公共数值；`PhysicsContacts` 引用这同一份数据，把完成点和读取声明交给游戏侧。设备执行使用 `DeviceContactManifolds<TState>`；显式回读结果由设备结果对象持有其 CPU 公共列。

## 形状对、点与作用端

`ContactPair` 保存两个几何实体、两个形状编号，以及连续点区间。每个点的公共列保存形状对下标、双方特征、局部点、世界点、法向、间距、预测数据和法向参考的初始差。点的作用端由 `BodyStarts`、`BodyCounts` 和 `FirstBodyCounts` 划分：前一段属于第一方，后一段属于第二方。

`ContactBodyLink` 保存实际主体、主体局部锚点、插值权重和 `NormalReference`。一方可以连接多个主体，同一主体也可以多次出现。求解时先合并该主体的雅可比，再施加一次响应。`MaterialPoint` 使用材料锚点计算法向位移；`BodyOrigin` 使用主体原点计算法向位移，摩擦仍使用材料锚点。

公共记录的明确布局是：`ContactPair` 32 字节、`ContactBodyLink` 28 字节、`ContactMaterial` 24 字节、`ContactResponse` 48 字节。设备或 CPU 执行器的主体稠密编号属于执行索引。`ContactResponse` 保存初始法向速度、恢复冲量、第二方受力和作用时间区间。AVBD 状态、XPBD 乘子和支撑状态各进入具体 `TState`。

## 检测与刷新

检测把几何和加权作用端写入 `ContactPointBuffer`。`RefreshContactManifolds<TState,TCombine>` 在依赖完成后取得原始主体列和材料读取声明，按几何实体及形状编号构造形状对，再按双方特征、局部锚点距离和法向匹配旧点。匹配成功的点保留数值状态及前次响应；新点使用构造时提供的初始状态。局部坐标匹配使共同平移保持接触历史。

`ContactPoint` 是数值公式临时组装的几何值。持久关系实体继续用于关节、距离和变长主体约束。接触的生命周期由执行器的列更新表达。

## 数值求解

CPU 父任务在语义依赖完成后收集当前组件范围和公共列资源。数值子任务持有原生查询范围，通过具体 `IChunkAction` 把主体及持久约束绑定到原始 ECS 列地址。岛和颜色阶段的 `ExecuteRanges` 直接消费这些地址与稠密索引，每个阶段完成后进入下一阶段。

`IAvbdContactMath<TState>` 为一个具体状态类型提供初始化、只读状态的主体方程累积和可写状态的对偶更新。内置 AVBD、势能和支撑公式复用这一入口。XPBD 直接投影当前公共列与乘子列。LRC 的点引用、支撑多边形顶点、载荷路径和方程使用当前步点下标；接触支撑状态是 `AvbdSupportedContactState.Support`，主体支撑和方程由支撑数据持有。

`InitializeContactResponses` 在全部动态作用端休眠时保留驻留力。速度重建后的 `ApplyContactRestitution` 使用材料恢复系数及初始接近速度施加法向冲量。

## 项目修改与读取

项目把自己的普通 Job 或设备 kernel 插入检测和求解之间，声明公共列的写访问，直接修改法向、材料、作用端或触发标记。

完整步骤返回 `PhysicsStepResult` 后，CPU 调用方用 `contacts.Publish(in result)` 交付最终完成点。消费者依赖 `contacts.Contacts.Access.Completed`，声明其 `Reads`，再调用 `OpenView()`。`ContactView.Pairs` 读取形状对，`PointsOf(pair)` 返回点区间，`PairsOf(body)` 返回主体到形状对的 CSR 下标，`GetPoint`、`BodyLinksOf`、`Materials` 和 `Responses` 读取公共值。该 CSR 从公共作用端派生，也服务只做检测和事件的组合。

接触事件见 [Events](Events.md)。
