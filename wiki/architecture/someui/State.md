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

public sealed class State<T> : VersionCell
{
    public T Value { get; set; }      // 读取时登记依赖；值相等的写入不推进版本
    public void Update(Change<T> change);
    public void Update<TArg>(TArg arg, Change<T, TArg> change);
}
```

## Invariants
- 不限制读写线程。版本号兼作 seqlock 序号：写入以 CAS 取得奇数版本、写值、发布下一个偶数版本；读取遇奇数版本等待，前后版本不一致时重读；依赖检查遇奇数版本视为已变化。
- 读改写必须使用 `Update`，回调在持有写权期间执行，必须很短且不能访问同一个 `State`；读取方采用有上限的自旋并让出时间片。
- 需要一起保持一致的多个字段放进同一个 `State<T>` 的 struct。
- 跨线程写入只保证最终一致：一次构建可能读到中间组合，版本检查会在下一次重建中纠正。
- 各前端的状态写法都映射到 `State<T>`：Solid 的 signal、Compose 的 `MutableStateOf`、SwiftUI 的 `[State]` 与 `Binding`。

## Relations
[[architecture/someui/Dependencies|Dependencies]] · [[architecture/someui/Frontends|Frontends]]
