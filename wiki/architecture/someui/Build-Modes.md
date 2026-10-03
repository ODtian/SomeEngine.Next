# Build Modes

## Definition
构建模式是视图类型声明的重跑规则。

| 模式 | `Build` 中的读取 | 参数变化后 |
|---|---|---|
| `Rerun`（默认） | 登记为依赖，依赖变化后重跑 | 重跑 `Build` |
| `Once` | 不登记依赖 | 不重跑 `Build`；读取参数的内部计算（`Read<Self>(Entity)`）重跑 |

## Invariants
- 模式由视图类型的静态虚属性确定，没有运行时开销。
- `Rerun` 视图隐式依赖自己的参数组件；`Once` 视图不依赖。
- 参数比较规则：值类型、record 与 `string` 用 `Equals`；其它类按引用；回调委托不参与比较但存储总是更新为最新；内容函数按引用比较。比较相等时静默覆盖存储，不推进版本。
- 一个视图参数被改写后如何向下传播由该视图自己的模式决定，不同前端在这一点衔接。

## 参数比较与重跑实现（#7）

`Scope.View<T>(in T)` 的约束为 `struct, IView, IEquatable<T>`，
由视图的 `Equals(T)` 决定参数是否相等。record struct 提供这一值比较入口。
回调、引用与内容函数的具体比较规则由生成器生成的 `Equals` 提供（#59）；
通用构建路径直接采用视图的相等判断结果。

父视图每次构建按 key 与出现次数复用子实体。
同一身份的类型变化会释放旧节点与后代，再创建新视图。
身份、`Key` 分组、声明顺序与释放规则由
[[architecture/someui/Node-Identity|Node Identity]] 维护。

| 子视图参数 | 存储与版本 | 内容行为 |
|---|---|---|
| 相等 | 覆盖为最新参数，保持实体组件与组件类型写版本 | 保持已有内容 |
| 变化，`Rerun` | 替换参数并推进写版本 | 父构建中立即以新参数构建 |
| 变化，`Once` | 替换参数并推进写版本 | 保持首次构建的内容 |
| 新节点，两种模式 | 新增参数组件 | 立即完成首次构建 |

`Rerun` 节点在 ECS 中保存本次成功构建所使用的参数写版本。
每帧重建阶段用 `!=` 对照当前实体组件版本，沿表格或 sparse 存储读取。
游戏系统或测试通过 ECS 写入参数后，下一次重建阶段消费新参数；
后续帧使用已更新的版本基线。`Once` 按自己的首次构建规则保留内容。

重建的父子顺序和当场构建后的版本复查见
[[architecture/someui/Rebuild-Schedule|Rebuild Schedule]]。
公开行为测试位于 `tests/SomeEngine.Views.Tests/ParameterBuildTests.cs`。

## 构建中的依赖登记（#9、#15）

`ViewRegistration<T>` 按 `T.Mode` 设置每次构建的读取登记状态。
`Once` 使用仅取值状态，表格与 sparse 组件读取均保留首次构建内容；
`Rerun` 使用登记状态，并在依赖变化后的重建中读取最新值。

`Scope.Untrack()` 在当前构建内暂时使用仅取值状态，结束后恢复进入前的状态。
嵌套块与子构建的状态边界见 [[architecture/someui/Build-Scope|Build Scope]]。
公开行为验收位于 `tests/SomeEngine.Views.Tests/ReadDependencyTests.cs` 与
`tests/SomeEngine.Views.Tests/UntrackTests.cs`。

## Relations
[[architecture/someui/Views|Views]] · [[architecture/someui/Frontends|Frontends]] · [[architecture/someui/Computations|Computations]]
