# SwiftUI Frontend

## Definition
SwiftUI 写法直接使用 `Rerun` 模式的视图 struct，并增加由生成器实现的 `[State]` partial 属性。

```csharp
using SomeEngine.Views.SwiftUI;

partial record struct Counter(string Label, int Initial = 0) : IView, ISparseComponent
{
    [State] partial int Count { get; set; } = Initial;

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

## 属性与 Binding 实现（#62）

`SomeEngine.Views.SwiftUI` 提供 `StateAttribute` 和 `Binding<T>`，引用 Views 依赖中的生成器与拦截配置。
视图及各级包含类型声明为 `partial`，属性采用实例 `partial T Name { get; set; }`。
默认构建模式为 `Rerun`，属性和生成的 Binding 在该视图的活动构建或计算中取得。
泛型视图、readonly 视图及由生成器默认选择的 sparse 视图采用同一套属性规则。

属性按名称排序后编号，一个实体上的 sparse 托管组件保存这些编号对应的 `State<T>`。
首次访问属性或对应的 Binding 时创建该项状态。显式初始值采用 C# 14 的 partial 属性初始值语义：
视图构造时，编译器将表达式值保存到属性的初始值字段，生成访问器以该值创建状态；
省略初始值时使用 `default(T)`。后续 getter 与 setter 访问实体上的状态，初始值字段继续作为构造数据。
重跑或父视图传入新参数时，同一节点的状态持续保留。

每个属性生成同访问级别的 `<属性名>Binding`，例如 `CountBinding`。
取得 Binding 传递状态引用，读取 `Value` 时登记当前计算对状态的依赖，写入使用 `State<T>` 的统一路径。
可以在父视图中声明 `Scope.View(new CounterEditor(CountBinding))`，
子视图对 `binding.Value` 的更新使读取该状态的父视图与子视图按依赖重跑。

```csharp
public readonly struct Binding<T>
{
    public Binding(State<T> state);
    public T Value { get; set; }
}

var state = new State<int>(3);
var binding = new Binding<int>(state);
binding.Value = 7;
```

状态首次取得时自动在视图实体上加入 `LocalState`，沿统一节点身份规则参与重复兄弟 key 的开发检查。
`if` 与 `else` 中的同类视图使用各自调用位置的身份，各自的当前节点在重跑与参数更新时保持状态。
分支切换后的旧子树释放与新节点创建沿共同的子树规则处理；重新创建的节点从初始值开始。

`SEV009` 在编译期报告 `[State]` 的属性声明形状。
活动构建、视图类型、`Rerun` 模式和 Binding 使用错误通过 Core `DevelopmentCheck` 报告，shipping 编译移除这些检查调用及参数求值。
生成访问器通过 `StateProperties.Get<TView, T>` 进入实体存储；初始值的 Binding 投影沿编译期确定的字段访问器读取构造数据。

实现：`src/SomeEngine.Views.SwiftUI/`、`src/SomeEngine.Views.Generators/ViewGenerator.State.cs`。
公开行为验收：`tests/SomeEngine.Views.Tests/SwiftUIStateTests.cs`、
`tests/SomeEngine.Views.Generators.Tests/SwiftUITests.cs`。
NativeAOT 通过 `just views-aot win-x64` 执行 `tools/SomeEngine.Views.AotSmoke/SwiftUIChecks.cs`，
覆盖泛型 readonly 视图的初始值、默认 sparse 存储、托管回收后的 Binding 写入、父参数更新与分支身份。

## Relations
[[architecture/someui/Frontends|Frontends]] · [[architecture/someui/State|State]]
