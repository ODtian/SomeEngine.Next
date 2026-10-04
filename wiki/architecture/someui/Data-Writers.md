# Data Writers

## Definition
节点上的每个组件只有一个写入方。

| 数据 | 唯一写入方 |
|---|---|
| 节点实际视图类型的参数（包括 `Text`、`Flex`） | 调用方的 `View`，或 Solid 计算 |
| `Box`、普通布局节点的 `Surface` 与子节点布局参数（`FlexItem`、`GridCell`、`AbsolutePosition`） | 调用方作用域 |
| Dialog 节点的 `Surface`、Dialog/DialogActions 节点解析后的 `Flex` | 该复合容器的 Build |
| `TextStyle` 的显式样式字段 | 调用方作用域 |
| `TextAppearance`（文本此次构建解析的完整样式） | `Text.Build` |
| 环境数据（用户定义的主题等组件） | 调用方通过 `Entity.Provide` 写入 |
| `ButtonTheme` | 调用方通过 `Entity.Provide` 写入，Button 读取最近提供者 |
| `DialogTheme` | 调用方通过 `Entity.Provide` 写入，各复合部件读取最近提供者 |
| `Clicks` | 输入阶段：`HitTestSystem` 的指针点击与 `FocusNavigationSystem` 的焦点确认；视图用 `Ensure<T>` 声明需要 |
| `Hover` | `HitTestSystem`；视图用 `Ensure<T>` 声明需要 |
| `Pressable`、`StopsPointer` 标记 | 调用方作用域通过 `Ensure<T>` 声明 |
| `ClickSeen` | 调用方作用域通过 `Clicked()` 的 `Write<T>` 提交 |
| `ValueRange`、`ValueDrag` | 控件构建通过公开 `ValueDraggable` 写入调用方参数与拖动映射 |
| `ValueEdit` | 输入阶段：HitTestSystem 的数值拖动与 FocusNavigationSystem 的键盘步进 |
| `ValueEditSeen` | 调用方作用域通过 `Edited()` 的 `Write<T>` 提交 |
| 数值手势起始值 | HitTestSystem 的数值拖动阶段，跟随该节点当前 Drag.Count |
| `Drag`、`Selected` | `HitTestSystem` 的行为阶段；调用方通过 `Ensure<T>` 声明 |
| `ScrollInput` | `HitTestSystem`，记录当帧滚轮与拖动产生的实际位移及输入计数 |
| `ScrollOffset` | 布局系统，按当前内容与视口写入范围和受限偏移 |
| `ScrollSeen` | 布局系统，记录已应用的输入计数，保持公开偏移数据的写版本只随值和范围变化 |
| 画布指针事件缓冲 | `Pointer` 的追加与领取入口 |
| 画布的当前位置、各按钮按下状态与目标 | `HitTestSystem`；`Pointer` 首次输入时声明默认数据 |
| `InputCapture` | 输入阶段；`HitTestSystem` 写 `Pointer`，焦点系统写 `Keyboard` |
| `Focusable` 标记 | 调用方作用域通过 `Ensure<T>` 声明 |
| `FocusInput` 缓冲 | `Keyboard` / `Gamepad` 追加键盘、字符、组合与手柄输入，调度中的焦点系统按顺序领取 |
| `Focused` | 调度中的焦点系统，默认由 `FocusNavigationSystem` 写入和转移 |
| `EditableText` | 输入阶段的 `TextEditingSystem`；调用方通过 `Ensure<T>` 声明并读取 |
| `EditSeen` | 调用方作用域通过 `Edited(out text)` 的 `Write<T>` 提交 |
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

文本样式默认字段通过最近祖先的 `TextTheme` 环境读取取得，
排版系统沿布局测量约束维护 `ShapedText`；具体读取、提交和版本规则见
[[architecture/someui/Text|Text]]。

Button 的构建作为子 Row 和 Text 的调用方，写入 Row 的 `Surface`、`Box.Padding`
以及 Text 的显式颜色。主题、布局、悬停和点击继续由表格中各自的写入方维护，
组合规则与默认主题见 [[architecture/someui/Composition|Composition]]。

Dialog 与 DialogActions 是调用方提供内容的布局节点，Build 从 DialogTheme 写入自身的
排列输出；Dialog 同时写入自身的 Surface。调用方通过主题提供外观和排列参数，
通过 Box 修饰提供尺寸限制与内边距。DialogTitle 是子 Text 的调用方，写入其显式
字号和颜色。用户复合部件采用相同的写入职责，组合规则见 Composition。

## Relations
[[architecture/someui/Modifiers|Modifiers]] · [[architecture/someui/Composition|Composition]]
