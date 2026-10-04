# SomeEngine.Views.Solid

Solid 组件是普通函数，在 `BuildMode.Once` 视图的 `Build` 中调用一次。
组件直接使用共享的 `State<T>`，后续变化由 `Effect`、`Memo` 和属性计算送到已有节点。

```csharp
using SomeEngine.Elements;
using SomeEngine.Views;
using SomeEngine.Views.Solid;
using static SomeEngine.Views.Solid.Solid;
using static SomeEngine.Layout.FlexFunctions;

static void Counter(State<int> count)
{
    var positive = Memo(() => count.Value > 0);
    using (Row().Content())
    {
        Text(() => count.Value, "D4");
        Text(() => positive() ? "positive" : "negative")
            .Background(() => positive() ? new(0, 1, 0) : new(1, 0, 0));
    }
}
```

`Effect(Action)` 使用 `Scope.Compute`，立即执行一次，随后在重建阶段按帧合并更新。
`Memo<T>(Func<T>)` 返回 `Func<T>`，保留 `hp()` 形式的读取；求值与生命周期由
`Scope.Memo` 管理。相等结果保持下游文本和组件版本。
`Untrack<T>(Func<T>)` 在取值期间暂停当前计算的依赖登记，退出时恢复。

`Text` 提供字符串、整数及 `ISpanFormattable` 数据的值与 getter 重载。
数字重载使用共享格式化写入，格式化字符相等时复用已有字符串与写版本。
响应式修饰写入各自的目标字段，并保留节点上的其它字段。

完整 API 与验收入口见仓库 Wiki `architecture/someui/Solid-Frontend`。
