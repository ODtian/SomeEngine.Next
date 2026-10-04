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
| 指针数据（`Clicks`、`Hover`、`ClickSeen`、`Pressable`）、`InputCapture` 与画布指针状态 | 表格；画布输入事件使用表格缓冲 |
| 依赖缓冲、对象依赖数组 | 表格，所有视图共用 |
| 复合与用户视图的参数（生成器默认）、计算列表、Compose/SwiftUI 状态存储 | sparse |
| `Focused`、`BuildError` | sparse |
| `Timeline`（可启用）、`Track<T>` 内联缓冲、`PresentationOffset`、`ElementTransform` | 表格；首次声明时加入，之后改组件值、缓冲元素与启用位 |
| `.Animate` 字段声明、`BaseValue<T>` | 节点上的表格缓冲；字段声明保存通道位置与 Spec，基准值由属性动画缓冲持有 |
| FLIP 运动设置与前帧数据（`FlipSettings`、`FlipFrame`） | 表格；调用方更新设置，FLIP 系统持有前帧数据 |
| `Contribution<T>` | 表格缓冲，用于多来源合成 |

## 视图参数的存储接入（#6）

视图的参数组件保持实际视图类型 `T`。Views 使用 ECS 内部的
`World.AddComponentData<T>` 与 `World.ReadComponentData<T>` 接入现有存储。
这两个 struct 泛型入口按 `default(T) is ISparseComponent` 选择 sparse 或表格路径，
沿相应的 World 访问范围操作原有组件存储。泛型静态视图注册中的开发检查要求视图
声明 `IComponent` 或 `ISparseComponent`。

参数新增和读取所需的内部泛型辅助方法采用 `struct` 约束，以支持这一编译期存储选择；
ECS 的公开 `Add`、`Replace`、`Read` 保留 `IComponent` 约束；`AddSparse`、`ReplaceSparse`、
`ReadSparse`、`HasSparse` 保留 `ISparseComponent` 约束。
视图构建从 ECS 取当前参数值，组件继续是参数数据的主要持有者。

## 参数更新的存储接入（#7）

同一组内部 struct 泛型入口扩展到实体组件写版本读取、整值替换与静默覆盖。
它们沿相同的表格或 sparse 选择访问原有存储，参数的主要数据保持在 ECS 中。
整值替换沿已有写版本、索引与 hooks 路径执行；相等参数的静默覆盖更新实际值，
并保留实体组件写版本与组件类型写版本。表格的已有索引在下次读取时取最新值。
两种存储的写入都沿现有存储分离规则保持结构事务隔离。

`Scope.Read`、`Write`、`ReadWrite`、`Ensure` 的公开 `struct` API 经 Views 使用这组可信内部入口。
World 的公开入口与内部入口共用同一份 table 或 sparse 操作实现，包含既有写版本、访问范围、索引与 hooks。
成功构建提交比较后的值；失败构建通过内部恢复入口还原旧值并撤回本次新增的数据。
SwiftUI 的生成访问器通过公开 Scope API 核对视图，开发检查中的读取使用 `Untrack`。
Elements 及之后的程序集继续从公开 Scope 或有组件接口约束的 World API 接入。

## 生成器的默认选择（#19）

带主构造函数的 `partial IView` struct 省略存储接口时，
生成器在该视图的 partial 声明中补充 `ISparseComponent`，并给出 `SEV002` info 提示。
这份参数仍使用实际视图类型 `T`，通过同一 ECS sparse 路径存取。
显式的 `IComponent` 或 `ISparseComponent` 声明供用户决定访问方式。
声明与编译诊断的详细规则见 [[architecture/someui/Views|Views]] 与 [[architecture/someui/Checks|Checks]]。

## Relations
[[architecture/someui/Views|Views]] · [[architecture/someui/Build-Scope|Build Scope]]
