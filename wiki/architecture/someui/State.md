# State

## Definition
`VersionCell` 是持有一个版本字段的类，任何数据来源继承它或持有它即可被依赖。`State<T>` 是三种前端共用的通用状态。

```csharp
public class VersionCell
{
    public uint Version;
    public void Advance();
    public void AdvanceShared();      // 跨线程写入时使用
}

public delegate void Change<T>(ref T value);
public delegate void Change<T, TArg>(ref T value, TArg arg);

public sealed class State<T> : VersionCell
{
    public State(T initial = default!);
    public T Value { get; set; }      // 读取时登记依赖；值相等的写入不推进版本
    public void Update(Change<T> change);
    public void Update<TArg>(TArg arg, Change<T, TArg> change);
}
```

## Invariants
- 不限制读写线程。版本号兼作 seqlock 序号：写入以 CAS 取得奇数版本、写值、发布下一个偶数版本；读取遇奇数版本等待，前后版本不一致时重读；依赖检查遇奇数版本视为已变化。
- 读改写必须使用 `Update`，回调在持有写权期间执行，必须很短且不能访问同一个 `State`；读取方采用有上限的自旋并让出时间片。
- 需要一起保持一致的多个字段放进同一个 `State<T>` 的 struct。
- 跨线程写入保证最终一致：一次构建可能读到中间组合，版本检查在后续重建轮次中纠正；本帧版本已发布时，同帧收敛显示完整组合。
- 各前端的状态写法都映射到 `State<T>`：Solid 的 signal、Compose 的 `MutableStateOf`、SwiftUI 的 `[State]` 与 `Binding`。

## 通用状态实现（#14）

`VersionCell` 可继承，公开 `uint Version` 字段。`Advance()` 供单写线程在修改数据后推进版本，
`AdvanceShared()` 用 `Interlocked.Add` 原子推进。两者每次加 `2`，以 `uint` 回绕，完成写入的版本保持偶数。
自定义来源负责自身数据的并发访问；需要完整值读写的来源使用 `State<T>`。

`State<T>` 的初始值由构造函数提供，默认值为 `default(T)`。赋值和两种 `Update` 共用写入路径：
CAS 把当前偶数版本加 `1` 取得写权，回调修改值副本，随后用 `EqualityComparer<T>.Default` 比较。
相等时保留原值与原版本；变化时存入新值并以原版本加 `2` 发布。
回调或相等比较抛出异常时恢复原版本，值副本随本次调用结束，之后的合法更新继续可用。
副本遵循 C# 的值复制规则，引用值的相等与所引用对象的数据维护由该类型定义。

读取先观察偶数版本、复制值，再完成内存屏障并复核版本。复核成功后，把这次完整值对应的版本交给
`Scope` 登记。跨线程写入发生在读值与登记之间时，后续版本比较仍能发现变化。
`SpinWait` 为读取重试和写权竞争提供有上限的忙等，随后让出时间片。
对象依赖记录本次读取的重建帧号。写入期间的奇数版本使视图在新帧失效，
本帧已观察该版本的视图等待来源发布新版本或进入下一帧。
完成写入的偶数版本变化继续按普通版本差异参与同帧收敛。

`Update` 回调使用 `ref value` 读改写当前状态，也可读取和更新其它状态。开发构建记录当前写入线程，
在同一状态的读取或写入入口通过 Core 的 `DevelopmentCheck` 报告回调重入。
shipping 编译移除这份线程记录、开发检查调用及其参数求值。

```csharp
var count = new State<int>();
count.Update(static (ref int value) => value++);
count.Update(3, static (ref int value, int amount) => value += amount);
```

实现：[VersionCell](../../../src/SomeEngine.Views/VersionCell.cs)、
[State](../../../src/SomeEngine.Views/State.cs)。
公开验收：[StateTests](../../../tests/SomeEngine.Views.Tests/StateTests.cs)，
覆盖跨线程逐帧显示、相等赋值与更新、并发累加、带校验字段的大 struct、回调异常恢复和开发检查。
对象来源与登记版本规则见 [[architecture/someui/Dependencies|Dependencies]]。

NativeAOT 沿现有 `just ecs-aot win-x64` 发布与执行入口，
在 [FirstBuildChecks](../../../tests/SomeEngine.Views.Tests/FirstBuildChecks.cs) 中经公开帧驱动验证
通用状态、自定义版本单元、两种回调、跨线程更新与托管回收后的显示结果。

## Relations
[[architecture/someui/Dependencies|Dependencies]] · [[architecture/someui/Frontends|Frontends]]
