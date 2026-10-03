# Input and Behaviors

## Definition
输入以数据表达：命中测试与路由系统写入节点上的指针数据，行为部件声明自己需要的数据，读取这些数据的计算因版本变化而执行。

## Invariants
- 点击是单调递增的计数 `Clicks`，与悬停、按下状态 `Hover` 分开存放，避免悬停变化使读取点击的计算重跑。`entity.Clicked()` 比较计数与该节点记录的已读值。
- 冒泡：路由系统把事件计数写到目标及其祖先，遇到 `StopsPointer` 标记停止，不使用回调链。
- 输入归属：命中测试在游戏输入之前运行，结果写入单例 `InputCapture`，游戏系统据此决定是否处理本次输入。
- 焦点：全局一个实体带 `Focused`；`Focusable` 为标记；键盘与手柄导航由可通过系统调度替换的 `FocusNavigationSystem` 根据布局结果计算。
- 文本输入：平台把字符与输入法组合文本写入 `TextInput`，编辑逻辑由 `TextEditing` 行为对应的系统处理。
- 行为部件（`Pressable`、`Draggable`、`Focusable`、`Scrollable`、`TextEditing`、`Selectable`）用 `Ensure<T>` 声明数据，可挂到任意节点。

## Relations
[[architecture/someui/Composition|Composition]] · [[architecture/someui/Data-Writers|Data Writers]] · [[architecture/someui/Rebuild-Schedule|Rebuild Schedule]]
