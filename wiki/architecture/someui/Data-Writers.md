# Data Writers

## Definition
节点上的每个组件只有一个写入方。

| 数据 | 唯一写入方 |
|---|---|
| 视图参数（包括作为元素数据的 `Text`、`Flex`） | 调用方的 `View`，或 Solid 计算 |
| 修饰数据（`Box`、`Surface`）与子节点的布局参数（`FlexItem`、`GridCell`、`AbsolutePosition`） | 调用方作用域 |
| 环境数据（用户定义的主题等组件） | 调用方通过 `Entity.Provide` 写入 |
| `Clicks`、`Hover`、`Focused` | 输入与焦点系统；视图用 `Ensure<T>` 声明需要 |
| 视图局部状态与 `Remember`/`[State]` 存储 | 该视图自己的构建 |
| `LayoutRect`、`LayoutCache` | 布局系统 |
| `Canvas`（目标、尺寸、缩放） | 应用 |
| `ShapedText` | 排版系统 |
| `BuildError` | 视图重建系统 |
| 动画通道的基准值（`BaseValue<T>`） | 调用方（经 `.Animate` 或 `PropertyAnimation.To`） |
| 动画通道的呈现值、`PresentationOffset` | 属性动画的求值或合成系统 |
| `Timeline`、`Track<T>`、`Contribution<T>` | 属性动画 API 与系统 |
| `AnimationLayers` 的权重 | 调用方，或动画它的属性动画系统 |

## Invariants
- 不存在多层数据按优先级合并。
- 新增组件时必须写明它的唯一写入方。

## 写入入口
写入方在构建中通过 `Scope.Write<T>` 修改组件副本，成功计算结束时比较提交；
修饰与属性绑定沿同一个入口写入。行为数据通过 `Scope.Ensure<T>` 声明，
由对应系统继续持有写入职责。具体提交与版本规则见
[[architecture/someui/Build-Scope|Build Scope]]。

## Relations
[[architecture/someui/Modifiers|Modifiers]] · [[architecture/someui/Composition|Composition]]
