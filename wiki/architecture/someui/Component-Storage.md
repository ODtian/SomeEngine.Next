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

## 视图参数的存储接入（#6）

视图的参数组件保持实际视图类型 `T`。Views 使用 ECS 内部的
`World.AddComponentData<T>` 与 `World.ReadComponentData<T>` 接入现有存储。
这两个 struct 泛型入口按 `default(T) is ISparseComponent` 选择 sparse 或表格路径，
沿相应的 World 访问范围操作原有组件存储。泛型静态视图注册中的开发检查要求视图
声明 `IComponent` 或 `ISparseComponent`。

参数新增和读取所需的内部泛型辅助方法采用 `struct` 约束，以支持这一编译期存储选择；
ECS 的公开 `Add`、`Read`、`AddSparse` 和 `ReadSparse` 延续各自的组件接口约束。
视图构建从 ECS 取当前参数值，组件继续是参数数据的主要持有者。

## 参数更新的存储接入（#7）

同一组内部 struct 泛型入口扩展到实体组件写版本读取、整值替换与静默覆盖。
它们沿相同的表格或 sparse 选择访问原有存储，参数的主要数据保持在 ECS 中。
整值替换沿已有写版本、索引与 hooks 路径执行；相等参数的静默覆盖更新实际值，
并保留实体组件写版本与组件类型写版本。表格的已有索引在下次读取时取最新值。
两种存储的写入都沿现有存储分离规则保持结构事务隔离。

## Relations
[[architecture/someui/Views|Views]] · [[architecture/someui/Build-Scope|Build Scope]]
