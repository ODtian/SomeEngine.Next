# SwiftUI Frontend

## Definition
SwiftUI 写法直接使用 `Rerun` 模式的视图 struct，并增加由生成器实现的 `[State]` partial 属性。

```csharp
partial record struct Counter(string Label) : IView, ISparseComponent
{
    [State] partial int Count { get; set; }

    public BuildTask Build()
    {
        Text($"{Label}: {Count}");
        if (Button("+").Clicked()) Count++;
        return default;
    }
}
```

## Invariants
- 视图 struct 每次由父视图重新传入，状态不存于 struct：生成的属性按属性编号从视图实体的状态存储中取对应的 `State<T>`。
- `Binding<T>` 就是该 `State<T>` 的引用。
- 节点身份与其它写法相同，统一由拦截器提供调用位置常量，不需要额外标记。
- 构建方法沿用 `Build`，修饰沿用共用的 `Entity` 扩展方法，不另设 `body` 与修饰链。

## Relations
[[architecture/someui/Frontends|Frontends]] · [[architecture/someui/State|State]]
