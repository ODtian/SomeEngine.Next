# Open Questions

## ECS 前置能力
- 公开读取某实体某组件的当前写版本。
- 按组件类型的全局最新写版本。
- 计算结束时比较再提交的写入语义（临时副本后 `Replace`，或取引用不自动推进版本、之后显式提交）。
- 查询成员版本。

## 待验证
- NativeAOT 是否把 `default(T) is ISparseComponent` 折叠为常量。
- 表格与 sparse 组件是否支持 `string`、委托等托管字段。
- `State<T>` seqlock 在写入回调期间读取方的等待成本。
- 视图类型较多时的 archetype 与块占用。
- `ElementTree` 中按插入位置调整顺序的维护成本。
- 依赖检查在 1000 个视图、每个约 5 条依赖下的每帧成本。
- 结构块与列表切换时的分配量。
- 拦截器对 `[Composable]` 函数体内所有产出节点调用的覆盖与生成代码体积。

## Relations
[[architecture/someui/Performance|Performance]]
