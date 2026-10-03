# Modifiers

## Definition
修饰是写在节点上的数据，以 `Entity` 的扩展方法提供，返回同一个实体以便连续调用。

```csharp
public static Entity Padding(this Entity element, float value)
{
    Write<Box>(element).Padding = value;
    return element;
}

using (Column().Padding(16).Background(fill).Content()) { ... }
```

## Invariants
- 所有构建函数返回 `Entity`；只有 `.Content()` 返回用于 `using` 的 `ParentScope`。
- 修饰由调用方的作用域写入，按计算结束时比较提交；视图不给自己写修饰数据，默认值放在自身参数中。
- 修饰不是视图，不创建实体，也不参与子视图的参数比较。
- 影响布局的特殊包装（宽高比、偏移等）写成只有一个子节点的布局视图，不做修饰链。
- 用户的修饰与内置修饰写法相同。
- 修饰在计算之外调用由开发检查报告；游戏代码直接使用 `World.Write`。

## Relations
[[architecture/someui/Data-Writers|Data Writers]] · [[architecture/someui/Layout|Layout]]
