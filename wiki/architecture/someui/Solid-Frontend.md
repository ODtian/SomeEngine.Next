# Solid Frontend

## Definition
Solid 写法的组件是普通函数，只运行一次；变化由计算直接写到目标节点的目标字段。

```csharp
static void Counter(string label, Entity player)
{
    var count = new State<int>(0);
    var hp = Memo(() => Read<Health>(player).Current);

    using (Row().Content())
    {
        Text(() => $"{label}: {count.Value}  HP {hp()}");
        Button("+", onClick: () => count.Value++);
        Show(() => count.Value > 10, () => Text("太多了"), fallback: () => Text("还不够"));
        For(() => Each(enemies), (enemy, index) => EnemyBar(enemy, index));
    }
}
```

## Invariants
- 原语：`Effect`、`Memo`、`Show`、`Switch`/`Match`、`For`、`Index`、`Dynamic`、`Insert`、`Untrack`。状态直接使用 `State<T>`。
- `Show`、`Switch` 只在登记依赖的状态下求条件；每个分支是 `Once` 模式的子视图，子内容不登记依赖。条件真假变化时释放并重建分支。
- `For` 按元素做 key，每行创建一次、重排时移动；`Index` 按位置做 key，元素以 getter 提供。行内容同样不登记依赖。
- `Insert` 是 `Rerun` 模式的子视图，用于动态插入与跨前端传值。
- 可响应的属性提供 `T` 与 `Func<T>` 两个重载；lambda 不能隐式转换为自定义包装类型（实测 CS1660）。
- 写入 `State<T>` 立即可读；Effect 在重建阶段执行，按帧合并；Memo 读取时当场重算。
- 导出给其它前端使用的组件标记 `[Component]`：来自 `[Composable]` 方法体或普通视图 `Build` 实现体的调用被拦截为 `Once` 模式的视图；Solid 组件之间的调用保持内联。
- 闭包只在组件运行时分配一次，计算重跑调用同一委托。

## Effect、Memo 与可响应属性（#55）

`SomeEngine.Views.Solid` 通过 Views 与 Elements 的公开 API 提供协调原语：

```csharp
namespace SomeEngine.Views.Solid;
public static class Solid
{
    public static void Effect(Action run);
    public static Func<T> Memo<T>(Func<T> compute);
    public static T Untrack<T>(Func<T> read);
    public static Entity Text(string value);
    public static Entity Text(Func<string> value);
    public static Entity Text(int value, string format = "", IFormatProvider? provider = null);
    public static Entity Text(Func<int> value, string format = "", IFormatProvider? provider = null);
    public static Entity Text<T>(T value, string format = "", IFormatProvider? provider = null)
        where T : struct, ISpanFormattable;
    public static Entity Text<T>(Func<T> value, string format = "", IFormatProvider? provider = null)
        where T : struct, ISpanFormattable;
}
```

普通组件函数在 `BuildMode.Once` 视图的 `Build` 中调用。静态导入 `Solid` 后，
组件调用共享元素与 `.Content()` 组合节点。Text 声明以创建次序确定节点身份，
每个 getter 重载创建一次文本节点并立即登记一次 `Scope.Compute`，之后使用同一个
委托读取来源，通过 `Scope.Write<Text>(node).Value` 写入已有节点。
数字重载调用 Elements 的作用域格式化写入，格式、文化与字符相等规则由
[[architecture/someui/Text|Text]] 定义。

`Effect` 转发 `Scope.Compute`。`Memo` 返回 getter，以保留规格中的 `hp()` 写法；
getter 闭包在创建时分配一次，后续读取委托转到同一个 `Scope.Memo` 对象的 `.Value`。
求值、结果相等比较、版本推进、异常和释放由共享计算管理。
`Untrack` 在 getter 求值期间进入 `Scope.Untrack()`，退出和异常退出时恢复调用方的登记状态。

`ReactiveModifiers` 提供以下 getter 重载，与 Elements 的值重载共同使用：

| 修饰 | getter 类型 | 目标字段 |
|---|---|---|
| `Background` | `Func<Color>` | `Surface.Fill` |
| `Corner` | `Func<float>` | `Surface.CornerRadius` |
| `Border` | `Func<Color>`、`Func<float>` | `Surface.BorderColor`、`Surface.BorderWidth` |
| `Font` | `Func<Font>` | `TextStyle.Font` |
| `FontSize` | `Func<float>` | `TextStyle.Size` |
| `TextColor` | `Func<Color>` | `TextStyle.Color` |
| `LineHeight` | `Func<float>` | `TextStyle.LineHeight` |

每个重载登记一个计算，使用 Elements 的字段修饰写入当前组件副本。
首次计算求得的字段也写入调用方正在形成的副本，使静态和可响应修饰在任意顺序中
保留各自的字段。后续计算从当帧组件值取得副本，保留其它来源写入的字段。
getter、作用域及字号、字体、行高的使用错误沿 Core 的 `DevelopmentCheck` 报告。

计算复用的作用域、组件副本与提交数据随该计算持有，规则见
[[architecture/someui/Computations|Computations]]。Text 的样式求值独立观察样式与主题，
文字变化直接交给排版与布局，规则见 [[architecture/someui/Text|Text]]。

公开验收：[SolidTests](../../../tests/SomeEngine.Views.Solid.Tests/SolidTests.cs)、
[SolidChecks](../../../tests/SomeEngine.Views.Solid.Tests/SolidChecks.cs)。
用例经 State、Solid、Scope、World 与有序层级读取验证文本与组件的独立更新、节点身份、
Memo 相等结果、同帧读取、Effect 的按帧更新、Untrack、格式化和字段组合。
Release 用例预热 1000 帧后采样 4096 帧：数字结果相等时分配为 0 字节；
每帧文字变化时，与直接数字格式化写入的分配量相等。
背景字段持续变化时也保持 0 字节分配。`SolidChecks` 在托管测试与
`just views-aot win-x64` 中复用同一组公开行为，覆盖 Memo、Effect、Untrack、
数字文本、背景字段与连续状态变化的分配。

## Insert 与组件导出（#63）

```csharp
namespace SomeEngine.Views.Solid;

[AttributeUsage(AttributeTargets.Method)]
public sealed class ComponentAttribute : Attribute;

public static class Solid
{
    public static void Insert(Action content);
}

public static partial class Components
{
    [Component]
    public static void Counter(State<int> count)
    {
        Text(() => count.Value);
        Insert(() => ComposeCounter(count.Value));
    }

    [Composable]
    private static void ComposeCounter(int value) { /* 普通参数随 Insert 更新 */ }
}
```

`Insert` 按创建次序声明一个 `Rerun` 子视图，由该节点持有内容委托和读取依赖。
首次声明立即执行内容；读取的 State、ECS 数据或其它来源变化后，在重建阶段再次执行。
内容中的 Compose、普通视图声明沿各自的调用位置 key、出现次数与显式 Key 复用节点，
相等参数保留已有内容，变化参数更新原节点，省略的节点沿共同的子树释放路径处理。
构建失败时，`BuildError` 记录在插入节点上，原有子内容沿共同恢复边界保留；
依赖再次变化后执行内容并恢复。
空内容及作用域使用错误沿 Core `DevelopmentCheck` 报告。

导出组件使用 `[Component] static void` 或 `[Component] static BuildTask` 方法，
参数按值传递，方法的包含类型使用 `partial`。
生成器在包含类型内生成 readonly record struct `<Name>_View`，使用 sparse 存储与
`BuildMode.Once`，字段对应原方法参数，首次 `Build` 执行原函数。
泛型约束、命名参数和可选参数沿 C# 绑定处理；重载视图通过参数签名后缀区分。
公开方法的生成视图可供引用程序集直接使用。

来自 `[Composable]` 方法体或 `IView.Build` 实现体的直接调用，
由拦截器使用调用位置 key 声明生成视图；Compose 的内容体和内联分组也沿这一边界处理。
带自身 `[Composable]` 标记的 lambda 沿其方法标记声明 Solid 子视图。
Solid 组件函数之间的调用保持普通函数内联，共享调用方的节点和计算作用域。
生成视图的 `Build` 执行原函数后保留其局部 State、Memo、Effect 和内容，
宿主刷新继续更新同一视图的参数组件。
参数比较与回调转发共用 [[architecture/someui/Build-Modes|Build Modes]] 的生成路径，
内部计算通过 State、getter 或 `Scope.Read` 观察变化来源。

公开行为验收由 `tests/SomeEngine.Views.Solid.Tests/FrontendChecks.cs` 与
`tests/SomeEngine.Views.Generators.Tests/ComponentTests.cs` 提供，
覆盖跨前端参数送达、动态分支释放与错误恢复、宿主刷新后的局部状态和计算、
函数调用边界、编译诊断与引用程序集消费。
同一组前端行为接入 `just views-aot win-x64`。

## Relations
[[architecture/someui/Computations|Computations]] · [[architecture/someui/Frontends|Frontends]]
