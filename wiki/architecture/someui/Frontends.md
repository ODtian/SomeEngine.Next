# Frontends

## Definition
前端是在 C# 中借鉴 Compose、SwiftUI、SolidJS 的写法。各前端只在协调方式上不同：

| 前端 | 构建模式 | 节点身份 | 变化如何送达 |
|---|---|---|---|
| Compose | `Rerun` | 调用位置 | 重跑后比较参数 |
| SwiftUI | `Rerun` | 调用位置 | 重跑后比较参数 |
| Solid | `Once` | 创建时确定 | 计算直接写目标字段 |

节点身份规则（调用位置）、元素、修饰、布局、输入、环境数据、动画、异步（`BuildTask`、`Launch`、`Resource<T>`）、状态 `State<T>`、`Key` 与错误处理只有一套，所有前端共用，不为还原原版语法另做别名。

## Invariants
- 状态完全通用：各前端读写同一种 `State<T>`，跨前端的依赖正确失效。
- 组件在边界处混用：每个前端的组件在其它前端看来都是视图。
- 一个函数体内只使用一种前端的原语，违反时由分析器报告错误。
- 从 Solid 组件向其它前端组件传普通值只求值一次；需要随数据更新时使用 `Insert(() => ...)` 或传 `State<T>`。
- 结构嵌套统一使用 `using (entity.Content())`。

## Relations
[[architecture/someui/Compose-Frontend|Compose]] · [[architecture/someui/SwiftUI-Frontend|SwiftUI]] · [[architecture/someui/Solid-Frontend|Solid]] · ADR 0009
