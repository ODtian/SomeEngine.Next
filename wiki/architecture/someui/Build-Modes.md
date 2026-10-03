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

## Relations
[[architecture/someui/Views|Views]] · [[architecture/someui/Frontends|Frontends]] · [[architecture/someui/Computations|Computations]]
