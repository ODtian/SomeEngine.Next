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
- 导出给其它前端使用的组件标记 `[Component]`：只有来自 Compose 或 SwiftUI 函数体的调用被拦截为 `Once` 模式的视图；Solid 组件之间的调用保持内联。
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
采用默认格式提供者的数字、整数与日期绑定把 `Locale` 登记为该计算的依赖，
语言切换时更新同一个文本节点的参数和显示内容。显式格式提供者按自身规则格式化，
对应结果保持相等时也保留文本与显示内容的写版本。

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

## Relations
[[architecture/someui/Computations|Computations]] · [[architecture/someui/Frontends|Frontends]]
