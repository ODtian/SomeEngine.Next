# Rebuild Schedule

## Definition
`ViewRebuildSystem` 是一个普通 ECS 系统，每个 `World` 各自运行。游戏 UI 在 Game world，编辑器与工具 UI 在各自的 world。

## Current design
每帧顺序：

1. 平台输入。
2. 命中测试：使用上一帧的布局结果，写指针数据、焦点与 `InputCapture`。
3. 游戏输入与模拟。
4. `ViewRebuildSystem`：先恢复已就绪的异步构建，再执行失效的计算。
5. 用户的界面系统（可选）。
6. 动画：`TimelineSystem` → 各 `TrackSystem<T, TOps>` → `ComposeSystem<T, TOps>` → 时间线收尾。
7. 文本排版与测量，随后布局。
8. `FlipSystem`：比较布局前后的快照，在内联轨道上重定 `PresentationOffset`。
9. 增量提取到 Rendering world。

## Invariants
- 失效计算按所属节点的层级深度从父到子执行，同一节点内按创建顺序；父重跑时已释放的子计算跳过。
- 子视图在新建或参数不相等时由父当场构建；本次已构建的视图不在同一次重建中重复构建。
- 计算写入了自己本次已读取的数据时，在本帧再次执行；超过迭代上限由开发检查报告。
- `Build` 抛出异常时，在视图实体上写入 `BuildError`，保留上一次完整的子节点，由开发检查报告，整帧继续。祖先读取 `BuildError` 即可显示后备内容。
- 布局、排版与提取使用 Job 系统按块并行；构建先单线程执行，作用域与对象池按线程隔离，为互不相交子树的并行构建留出条件。

## 首次构建实现（#6）

`ViewRebuildSystem` 实现 `ISystem<ImmediateSystemContext>`，经
`SystemGroup<ImmediateSystemContext>` 与 `ImmediateSystemDriver` 驱动。
每个 World 的系统实例持有自己的视图查询与可复用实体列表。

`Views.Spawn` 在根实体上加入内部的构建请求标记。系统先在公开查询回调中收集视图实体，
结束借用后筛出请求、取出标记并执行类型表中的构建函数；构建中的实体创建、参数写入和层级维护
沿 ECS World 的既有结构操作执行。同步子构建在父构建中完成，三层树在同一帧可见。
后续帧继续使用已经生成的树。系统销毁时释放自己取得的查询。

无头帧工具位于 `tests/SomeEngine.Views.Tests/HeadlessFrameDriver.cs`，
使用普通 World 和系统组注册当前已有的 `ViewRebuildSystem`；`Frame()` 推进该阶段。
输入、动画、排版、布局和提取任务增加系统时，按上面的帧顺序扩展这一工具。

## 参数重建实现（#7）

系统通过 `ViewType` 查询收集当前视图实体，结束查询借用后筛出首次请求与
参数写版本已经变化的 `Rerun` 节点。每帧的候选列表按 `ElementTree` 深度排序，
先执行父节点，再执行子节点。World 保留唯一的参数组件与节点版本记录，
系统列表在每帧复用，仅保存此次检查与执行所需的实体和深度。

每个候选执行前再次检查当前参数版本。父构建中已经当场构建的子视图已消费新版本，
后续检查保持这份结果；父参数相等时，子视图自己的外部参数写入仍由同一检查处理。
版本记录对应构建开始时使用的参数，成功完成构建后写回节点。

`ParameterBuildTests.cs` 经公开 World、视图与无头帧入口验证更新时机，
并用视图写出的显示历史验证父子参数同帧变化时的结果。

## 读取依赖重建实现（#9）

每帧收集阶段把首次请求、参数版本变化或读取依赖版本变化的视图放入同一个候选列表，
按 `ElementTree` 深度从父到子执行。读取依赖的存储与版本语义见
[[architecture/someui/Dependencies|Dependencies]]。

每个候选执行前先确认视图实体存活，再复核当前参数与读取依赖版本。
父构建释放的子视图在存活检查处跳过；父构建中当场重建的子视图已经重新登记读取版本，
复核消费这份最新结果。父传入相等参数时，子视图保留自己的读取依赖，
来源变化仍由候选复核触发子视图构建。查询借用在构建前结束，
依赖缓冲借用也在各次登记或比较完成后结束，构建继续通过 World 执行结构修改。

`ReadDependencyTests.cs` 通过无头帧驱动、公开视图和组件数据显示历史验证父子顺序，
并通过父视图改为声明空内容，验证成功构建后的子树释放与后续帧结果。

## 结构依赖重建实现（#12）

组件存在性与查询成员依赖进入同一个视图候选与复核路径。
每帧检查 `Has` 的当前布尔结果以及 `Each` 的查询成员版本；
源实体销毁或组件移除使组件读取依赖进入失效状态。
父列表按既有深度顺序先声明当前成员，其后继续检查存活行的各自数据依赖。
源实体销毁后，对应行由父列表在本帧释放；查询重排通过既有 key 匹配更新层级位置。
公开验收由 `EachDependencyTests.cs` 覆盖列表增删、单行血量更新、查询顺序与启用过滤变化。

## Relations
[[architecture/someui/Computations|Computations]] · [[architecture/someui/Async-Build|Async Build]] · [[architecture/someui/Render-Path|Render Path]]
