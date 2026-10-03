# Performance

## Current design
- 在 DefaultGame 统一场景平台加入标准 HUD 叠加层选项（约 1000 个元素、每帧变化的数字、一个长列表），在 1024 小球、Sponza、Bistro 三个场景中一起测量，记录重建次数、布局耗时与提取耗时。对比时该选项与其它渲染选项保持一致。
- 诊断：重建原因（视图与变化的依赖）写入诊断缓冲，默认关闭；每种视图类型对应一个 Tracy 区段。
- 初始预算，以首次实测结果校准：稳态（只有数值变化）每帧 0 分配；1000 个元素无变化时的依赖检查 ≤ 0.1 ms；100 个元素变化时重建加布局 ≤ 0.5 ms。

## 重建诊断（#65）

```csharp
public static class RebuildDiagnostics
{
    public static bool Enabled { get; set; }
    public static ReadOnlySpan<RebuildRecord> LastFrame(World world);
}

public readonly record struct RebuildRecord(Entity View, Entity Source, ushort Component);
```

`Enabled` 默认 `false`，在重建阶段之间切换，作用于所有 World。
开启后，每个 World 的 `ViewRebuildSystem` 在阶段开始清空并复用自己的诊断缓冲。
`LastFrame(world)` 在阶段结束后读取该 World 的本次记录；调用方在该 World 下次重建阶段
或开关变化之前消费返回的只读 span。首次运行阶段之前、静止帧以及关闭期间返回空 span。
关闭会释放各 World 已收集的记录，再次开启从下一次重建阶段开始收集。

记录在视图构建入口、读取依赖重新登记之前形成。每个变化来源对应一条记录；
同次重建的参数和多个读取来源全部保留。父视图当场构建的子视图沿相同入口记录来源。
候选在执行前完成存活与版本复核，因此本次记录对应实际发生的构建。

| 原因 | `View` | `Source` | `Component` |
|---|---|---|---|
| 读取的表格或 sparse 组件写版本变化 | 构建的视图 | 被读取的实体 | `ComponentId<T>.Value` |
| 参数写版本变化 | 构建的视图 | 视图自身 | 参数类型的 `ComponentId<T>.Value` |
| 首次构建 | 构建的视图 | `Entity.Null` | `0` |

缓冲按 World 的生存期保存，唯一的诊断数据由 `RebuildDiagnostics` 持有。
默认关闭路径只检查采集开关；缓冲与来源版本遍历在开启时执行。
API 的 World 使用错误通过 Core 的 `DevelopmentCheck` 报告，shipping 调用移除开发检查。

每种视图类型的构建使用 `<Type.FullName>.Build` 作为区段名，根视图与同步子视图沿同一入口插桩。
Tracy 经现有 `Profiler.BeginScope` 采集；`EngineTrace` 使用同名、类别为 `view` 的静态 `TraceSite`。
两种采集沿各自的开关启用，`RebuildDiagnostics.Enabled` 控制来源记录。
类型名称与 TraceSite 在首次采集该类型时建立，后续构建复用。

实现：[诊断 API](../../../src/SomeEngine.Views/RebuildDiagnostics.cs)、
[构建入口](../../../src/SomeEngine.Views/ViewTypes.cs)。
公开验收：[RebuildDiagnosticsTests](../../../tests/SomeEngine.Views.Tests/RebuildDiagnosticsTests.cs)，
经 World、视图、帧驱动、诊断 span 与公开 Trace 文件读取 API 验证来源与类型区段。

## Relations
[[architecture/someui/Testing|Testing]] · [[architecture/someui/Open-Questions|Open Questions]]
