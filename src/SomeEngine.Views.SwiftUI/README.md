# SomeEngine.Views.SwiftUI

`[State]` 为 `Rerun` struct 视图的 partial 属性提供实体上的持久状态，
`Binding<T>` 将同一份状态传给子视图。引用此项目或包后，Views 依赖提供生成器与调用位置拦截配置。

```csharp
using SomeEngine.ECS.Components;
using SomeEngine.Views;
using SomeEngine.Views.SwiftUI;

public partial record struct Counter(string Label, int Initial = 0) : IView, ISparseComponent
{
    [State] public partial int Count { get; set; } = Initial;

    public BuildTask Build()
    {
        Scope.Write<CounterText>(Scope.Entity) = new CounterText($"{Label}: {Count}");
        return default;
    }
}

public readonly record struct CounterText(string Value) : ISparseComponent;
```

视图与各级包含类型使用 `partial`。属性为实例 `get; set;` 声明，
访问位置在该视图的活动构建或计算中。C# 14 属性初始值由编译器初始化视图的值字段；
首次取得属性或 Binding 时，这个值用于创建实体上的 `State<T>`，省略初始值时采用 `default(T)`。
后续重跑与父视图参数更新继续使用同一份状态，节点释放与重新创建沿共同的子树生命周期处理。
泛型视图与 readonly 视图采用相同的属性接口。

每个属性同时生成同访问级别的 `<属性名>Binding`，例如在 `Counter.Build` 中使用
`Scope.View(new CounterEditor(CountBinding))`。取得 Binding 传递引用，
`binding.Value` 的读取登记当前计算的依赖，写入沿共同的 `State<T>` 更新路径通知读取者。
`new Binding<int>(new State<int>(3))` 也可将独立状态接入子视图。

`StateProperties.Get<TView, T>` 为生成代码访问实体状态的支持入口，
状态由一个 sparse 托管组件按属性编号持有；首次使用状态时自动标记 `LocalState`，
重复兄弟 key 的开发检查沿 Views 的统一规则处理。
`SEV009` 提示 `[State]` 的属性声明形状；活动构建、视图类型和 Binding 使用错误由 Core `DevelopmentCheck` 报告。

设计与行为见 [SwiftUI Frontend](../../wiki/architecture/someui/SwiftUI-Frontend.md)。
