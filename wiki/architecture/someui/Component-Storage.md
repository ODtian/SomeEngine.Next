# Component Storage

## Definition
`IComponent`（表格存储）与 `ISparseComponent` 并列，按访问与变化频率选择。

| 选择 | 条件 |
|---|---|
| `IComponent` | 系统每帧批量遍历，持有实体集合稳定 |
| `ISparseComponent` | 只按实体查找、不批量遍历；或只有少数实体持有且频繁增删 |
| 组件内的字段 | 频繁切换的状态，不用组件增删表达 |

## Current design
| 数据 | 存储 |
|---|---|
| `Flex`、`Text`、`Image` 等元素参数，`Box`、`Surface`、`LayoutRect`、`LayoutCache`、`ElementKey`、`ViewType` | 表格 |
| 指针数据（`Clicks`、`Hover`） | 表格 |
| 依赖缓冲、对象依赖数组 | 表格，所有视图共用 |
| 复合与用户视图的参数（生成器默认）、计算列表、Compose/SwiftUI 状态存储 | sparse |
| `Focused`、`BuildError` | sparse |
| `Timeline`（可启用）、`Track<T>` 内联缓冲、`PresentationOffset` | 表格；首次声明时加入，之后只改缓冲元素与启用位 |
| `Contribution<T>`、`BaseValue<T>` | 表格缓冲，仅在多来源合成的节点上存在 |

## Relations
[[architecture/someui/Views|Views]] · [[architecture/someui/Build-Scope|Build Scope]]
