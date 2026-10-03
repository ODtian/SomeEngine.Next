# Child Source

## Definition
子节点来源是视图类型声明的子节点声明者，表达数据归属，不表达内部结构。

```csharp
public enum ChildSource { Build, Caller }
```

- `Build`：子节点由视图自己的 `Build` 声明，调用方不能打开它。
- `Caller`：视图是容器（如 `Flex`），`Build` 只写自身数据，子节点由调用方通过 `entity.Content()` 打开后声明。

## Invariants
- 节点实体本身归创建它的构建；子节点归打开它的 `.Content()` 块。
- `.Content()` 块结束时，块内声明的就是该节点的全部子节点，未声明的旧子节点被释放。
- 一个视图不会同时属于两种来源；不存在具名内容区或槽。
- 对 `Build` 来源的视图调用 `.Content()`，或同一次重建中两个作用域声明同一节点的子节点，由开发检查报告。

## Non-goals
- 具名内容区（`Content<TArea>`）与区域标记类型。复合控件改用复合部件，见 [[architecture/someui/Composition|Composition]]。

## Relations
[[architecture/someui/Views|Views]] · [[architecture/someui/Composition|Composition]]
