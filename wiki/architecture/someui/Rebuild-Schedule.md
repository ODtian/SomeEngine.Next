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

## Relations
[[architecture/someui/Computations|Computations]] · [[architecture/someui/Async-Build|Async Build]] · [[architecture/someui/Render-Path|Render Path]]
