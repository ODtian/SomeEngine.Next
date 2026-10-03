# Data Writers

## Definition
节点上的每个组件只有一个写入方。

| 数据 | 唯一写入方 |
|---|---|
| 视图参数（包括作为元素数据的 `Text`、`Flex`） | 调用方的 `View`，或 Solid 计算 |
| 修饰数据（`Box`）与子节点的布局参数（`FlexItem`、`GridCell`、`AbsolutePosition`） | 调用方作用域 |
| `Clicks`、`Hover`、`Focused` | 输入与焦点系统；视图用 `Ensure<T>` 声明需要 |
| 视图局部状态与 `Remember`/`[State]` 存储 | 该视图自己的构建 |
| `LayoutRect`、`LayoutCache` | 布局系统 |
| `ShapedText` | 排版系统 |
| `BuildError` | 视图重建系统 |
| 动画通道的基准值（`BaseValue<T>`） | 调用方（经 `.Animate` 或 `PropertyAnimation.To`） |
| 动画通道的呈现值、`PresentationOffset` | 属性动画的求值或合成系统 |
| `Timeline`、`Track<T>`、`Contribution<T>` | 属性动画 API 与系统 |
| `AnimationLayers` 的权重 | 调用方，或动画它的属性动画系统 |

## Invariants
- 不存在多层数据按优先级合并。
- 新增组件时必须写明它的唯一写入方。

## Relations
[[architecture/someui/Modifiers|Modifiers]] · [[architecture/someui/Composition|Composition]]
