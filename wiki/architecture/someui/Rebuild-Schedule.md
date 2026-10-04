# Rebuild Schedule

## Definition
`ViewRebuildSystem` 是一个普通 ECS 系统，每个 `World` 各自运行。游戏 UI 在 Game world，编辑器与工具 UI 在各自的 world。

## Current design
每帧顺序：

1. 平台输入。
2. 输入系统：`HitTestSystem` 与 `FocusNavigationSystem` 使用上一帧布局，写指针数据、焦点与 `InputCapture`；默认焦点系统按事件顺序调用 `TextEditingSystem` 写入 `EditableText`。
3. 游戏输入与模拟。
4. `ViewRebuildSystem`：先恢复已就绪的异步构建，再执行失效的计算。
5. 用户的界面系统（可选）。
6. 动画：`TimelineSystem` → 各 `TrackSystem<T, TOps>` → `ComposeSystem<T, TOps>` → 时间线收尾。
7. 文本排版与测量，随后布局。
8. `FlipSystem`：比较布局前后的快照，在内联轨道上重定 `PresentationOffset`。
9. 增量提取到 Rendering world。

## Invariants
- 失效计算按所属节点的层级深度从父到子执行，同一节点内按创建顺序；父重跑时已释放的子计算跳过。
- 子视图在新建或参数不相等时由父当场构建；已消费的参数与依赖版本保留结果，后续版本变化按同帧收敛轮次继续执行。
- 计算写入了自己本次已读取的数据时，在本帧再次执行；超过迭代上限由开发检查报告。
- `Build` 抛出异常时，在视图实体上写入 `BuildError`，保留上一次完整的子节点，由开发检查报告，整帧继续。祖先读取 `BuildError` 即可显示后备内容。
- 布局、排版与提取使用 Job 系统按块并行；构建先单线程执行，作用域与对象池按线程隔离，为互不相交子树的并行构建留出条件。

## 首次构建实现（#6）

`ViewRebuildSystem` 实现 `ISystem<ImmediateSystemContext>`，经
`SystemGroup<ImmediateSystemContext>` 与 `ImmediateSystemDriver` 驱动。
每个 World 的系统实例持有自己的视图查询与可复用实体列表。

`Views.Spawn` 在根实体上加入内部的构建请求标记。系统先在公开查询回调中收集视图实体，
结束借用后筛出请求并执行类型表中的构建函数；统一构建入口消费首次请求，构建中的实体创建、参数写入和层级维护
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
统一构建入口同时消费子视图的首次请求，后续候选检查保持这份结果；父参数相等时，子视图自己的外部参数写入仍由同一检查处理。
版本记录对应构建开始时使用的参数，每次构建结束后写回节点。

`ParameterBuildTests.cs` 经公开 World、视图与无头帧入口验证更新时机，
并用视图写出的显示历史验证父子参数同帧变化时的结果。

## 读取依赖重建实现（#9）

每帧收集阶段把首次请求、参数版本变化或读取依赖版本变化的视图放入同一个候选列表，
按 `ElementTree` 深度从父到子执行。收集时按组件类型写计数跳过计数相等类型的实体组件版本比较，
读取依赖的存储与版本语义见
[[architecture/someui/Dependencies|Dependencies]]。

每个候选执行前先确认视图实体存活，再复核当前参数与读取依赖版本；
类型写计数复核同时观察候选收集后的构建写入。
父构建释放的子视图在存活检查处跳过；父构建中当场重建的子视图已经重新登记读取版本，
复核消费这份最新结果。父传入相等参数时，子视图保留自己的读取依赖，
来源变化仍由候选复核触发子视图构建。查询借用在构建前结束，
依赖缓冲借用也在各次登记或比较完成后结束，构建继续通过 World 执行结构修改。

`ReadDependencyTests.cs` 通过无头帧驱动、公开视图和组件数据显示历史验证父子顺序，
并通过父视图改为声明空内容，验证成功构建后的子树释放与后续帧结果。

## 异步恢复实现（#20）

`ViewRebuildSystem` 在恢复前先比较挂起构建的参数及读取依赖，取消失效构建。
随后执行该 World 的逐帧等待通知，并取出本次
已就绪的异步状态机。恢复项按 `ElementTree` 深度、实体 `Index`、实体 `Generation`
排序；每项执行前设置保存的构建作用域。活动构建检查节点存活，取消构建继续执行清理，
结束后恢复线程作用域栈。
恢复期间登记的就绪项进入下一次重建阶段，等待对象的完成顺序只影响就绪时机。

挂起构建由每个 World 的构建队列持有。失效后的重建候选及当场子构建在同一阶段重新开始，
旧构建保留自己的清理作用域，取消与恢复的具体语义见 Async Build。
完成整个 `BuildTask` 时提交子节点清理及构建开始时的参数版本，随后照常收集参数与
读取依赖变化。状态机、作用域、逐帧等待和异常传播的具体语义由
[[architecture/someui/Async-Build|Async Build]] 维护。

系统销毁时关闭该 World 的就绪队列并释放视图查询。其它 World 的系统各自消费自己的队列。
`AsyncBuildTests.cs` 通过无头帧驱动与组件输出验证深度、实体排序和恢复线程。
`FirstBuildChecks.cs` 将逐帧流程和异步加载纳入已有的 NativeAOT 冒烟入口。
恢复结束后，视图与 Compute/Memo 共用下文的 `DependencyChanges` 轮次收敛检查；
挂起视图的独立计算沿相同路径更新，继续等待的构建在后续重建阶段恢复。
组件类型在本帧已经观察到的写入标记保留到帧结束，各轮继续比较具体读取版本，
使恢复前的取消检查与恢复后的同步视图检查共同观察本帧变化。

## 同帧收敛实现（#10）

`ViewRebuildSystem` 按轮次执行重建。每轮重新收集当前 World 的视图，
筛选首次请求、参数或依赖版本变化的节点，按 `ElementTree` 深度从父到子执行。
每轮收集前刷新已经观察的组件类型写计数，与上一轮或上一帧的观察值比较，
候选执行前继续复核收集后构建产生的写入。存在性与查询成员依赖沿各自的结构检查复核。
执行前继续复核存活状态与版本，所以父构建释放的节点自然退出，
父构建中已消费最新参数和依赖版本的子构建保留结果。

一轮中的提交推进 ECS 既有实体写版本。下一轮继续按读取时保存的版本拉取，
因此自己读过的数据发生提交，以及两个计算互相改变来源，都能在当前帧继续重跑。
候选为空时收敛；上一轮添加的节点与依赖也使用同一轮次规则。
首次子构建读过的来源在父构建后续写入时，子计算也在当前帧更新显示。
对象依赖的版本差异沿同一轮次收敛。写入期间的奇数版本按读取帧观察，
同一视图本帧已登记该版本后，来源发布新版本或进入下一帧时继续失效；
具体观察规则见 [[architecture/someui/Dependencies|Dependencies]]。

共享对象的版本变化与持续写入分别按
[[architecture/someui/Dependencies|Dependencies]] 的读取版本、帧号规则进入重建轮次。

公开属性 `ViewRebuildSystem.IterationLimit` 配置每帧重建轮次上限，默认 `8`，
开发检查要求正值。在最后一轮后再次比较版本，刚好收敛时正常完成；
仍有失效节点时停止该帧重建，并通过 `Core.Diagnostics.DevelopmentCheck` 报告上限与节点。
shipping 构建保留轮次上限，通过统一条件编译移除报告。
下一帧使用已提交的数据和该次实际读取的依赖版本继续处理。

公开验收：[BuildConvergenceTests](../../../tests/SomeEngine.Views.Tests/BuildConvergenceTests.cs)
覆盖互相触发、配置上限、停止后的恢复与最后一轮收敛；
[ComponentWriteTests](../../../tests/SomeEngine.Views.Tests/ComponentWriteTests.cs)
通过计数按钮的显示数据验证 `Read` + `Write` 和 `ReadWrite` 的同帧结果。

## 结构依赖重建实现（#12）

组件存在性与查询成员依赖进入同一个视图候选与复核路径。
每轮检查 `Has` 的当前布尔结果以及 `Each` 的查询成员版本；
源实体销毁或组件移除使组件读取依赖进入失效状态。
父列表按既有深度顺序先声明当前成员，其后继续检查存活行的各自数据依赖。
源实体销毁后，对应行由父列表在本帧释放；查询重排通过既有 key 匹配更新层级位置。
公开验收由 `EachDependencyTests.cs` 覆盖列表增删、单行血量更新、查询顺序与启用过滤变化。

## 无节点计算重建实现（#16）

视图构建与无节点计算分别检查自己的依赖，共用每个 World 的组件类型写计数筛选和深度排序。
候选节点执行时先复核 `Build`，随后处理该节点的计算。计算的执行列表保存当次收集的引用，
按创建顺序执行前复核计算生命周期；创建者重跑释放的旧计算在此处跳过。
当次新建的计算已经立即执行，后续复核消费它们新登记的依赖。
Memo 值的求值由读取和对象依赖检查触发，规则见
[[architecture/someui/Computations|Computations]]。

无节点计算写回本次已经读取的来源时，沿全帧收敛循环继续按深度与创建顺序复核变化。
构建与无节点计算共用 `IterationLimit` 和上述开发检查上限，轮次由重建系统统一持有。
计算异常使用既有 `BuildError` 和统一开发报告边界，兄弟计算与其它视图继续执行，恢复行为见 Computations。

## 构建异常隔离实现（#18）

```csharp
public struct BuildError : ISparseComponent
{
    public Exception Exception;
    public ulong Frame;
}
```

每个 World 的 `ViewRebuildSystem` 从 1 开始推进自己的重建帧号。
系统执行与父构建中当场执行的子构建共用这一帧号。
构建入口捕获 `Build` 及已完成 `BuildTask` 中的异常，作用域先恢复子声明，
随后在出错视图实体上添加或替换 sparse `BuildError`，保留原异常对象与当次帧号。
开发报告通过 [[architecture/someui/Checks|Checks]] 的统一入口送出，兄弟和本帧后续视图继续构建。
子声明的恢复与释放边界见 [[architecture/someui/Node-Identity|Node Identity]]。

父构建失败时，当场完成的子构建也恢复为父构建开始时的结果：参数、已提交的 table 与 sparse 输出、
后代、依赖和响应式计算一起恢复。随后帧继续观察旧结果对应的来源；旧来源再次变化时，恢复的构建或计算
更新原节点。构建中的 `Ensure` 新增数据随同撤回，组件版本检查继续使用 ECS 的统一入口。
子视图的参数消费状态和构建请求随参数恢复；构建前已收到的独立参数更新继续触发该子视图构建。

每次构建尝试消费开始时的参数版本，并保留本次实际读取的依赖。
这些数据再次变化后，普通失效检查触发重建；成功构建移除该视图的 `BuildError`。
首次构建失败时，视图实体仍在树上，子内容恢复到空集合。

祖先通过 `Scope.Has<BuildError>(child)` 登记错误组件的存在依赖，
随后通过 `Scope.Read<BuildError>(child)` 读取异常或帧号，并声明自己的后备节点。
当场子构建的错误在 `Scope.View` 返回后即可读取；在本帧祖先执行之后出现或清除的错误，
由当前帧后续轮次的失效收集触发祖先重建。

公开验收见 [BuildErrorTests](../../../tests/SomeEngine.Views.Tests/BuildErrorTests.cs)：
覆盖系统调度与当场子构建、key 重排、类型替换、新增声明回滚、嵌套后代参数恢复、
输出与依赖恢复、计算生命周期、`Ensure` 新增数据、首次失败、后备内容恢复、同步完成的异步异常及开发报告。

## 应用帧接入（#42）

`SomeEngine.Runtime.GameFrameSystems` 将每个 Game world 的输入与视图系统按上述阶段注册。
`Describe()` 从实际系统组生成阶段和系统类型描述，供应用及公共调度验收读取。
平台输入、游戏输入与模拟、提取在描述中标明应用调用边界。

应用先转接窗口事件，再调用 `UpdateInput()` 执行 `HitTestSystem` 与
`FocusNavigationSystem`；游戏输入随后读取同一 World 的 `InputCapture`。
模拟之后调用 `UpdateViews(deltaSeconds)`，依次执行重建、用户界面系统、
`AnimationSystems.AddDefaults` 注册的动画、`TextShapingSystem`、`LayoutSystem`
和布局后的 FLIP 系统。文本系统同时作为布局测量的 `ILayoutDataUpdate`，
在布局给出内容宽度时完成对应排版。最后沿既有 `RenderExtractionSystems.Extract`
将数据提取到 Rendering world。

`AddInterfaceSystem` 在重建之后、动画之前加入用户系统；`AddFlipSystem` 在布局之后、
提取之前加入系统。FLIP 的实现由 #52 提供，该扩展阶段随实现交付安装。

同步帧在当前呈现前完成视图更新和提取。重叠帧在取得前次 Game world 工作结果后
处理当前平台输入，并在当前帧的 CPU 渲染读取结束后执行模拟、视图更新和提取；
下一次呈现消费这份数据。初始化先构建并布局，首轮命中测试取得已完成的布局。

公开验收位于 `tests/SomeEngine.Runtime.Tests/GameFrameTests.cs`，使用调度描述、
World 数据和应用输入入口检查阶段顺序及交互结果。

## Relations
[[architecture/someui/Computations|Computations]] · [[architecture/someui/Async-Build|Async Build]] · [[architecture/someui/Render-Path|Render Path]]
