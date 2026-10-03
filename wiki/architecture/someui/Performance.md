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

public readonly record struct RebuildRecord(Entity View, Entity Source, ushort Component)
{
    public QueryHandle Query { get; init; }
}
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
| `Has<T>` 的存在性变化 | 构建的视图 | 被检查的实体 | `ComponentId<T>.Value` |
| 参数写版本变化 | 构建的视图 | 视图自身 | 参数类型的 `ComponentId<T>.Value` |
| `Each(query)` 的成员变化 | 构建的视图 | `Entity.Null` | `0` |
| 首次构建 | 构建的视图 | `Entity.Null` | `0` |

成员变化的记录通过 `Query` 保存触发重建的查询，其余记录的 `Query` 为默认句柄。
保留票文中的三参数构造方式，并增加这个可选属性，使已集成的查询依赖也能返回具体来源。

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

## #11 本地无变化检查测量（2026-10-04）

场景为无头 World 中的 1000 个根视图。每个视图读取自己来源实体上的 5 个组件：
3 个表格类型、2 个 sparse 类型，共 5000 条读取依赖。首次构建后保持数据与参数相同，
由 `SystemGroup<ImmediateSystemContext>` 驱动 `ViewRebuildSystem`。

| 实现 | 每帧平均耗时（ms） | 批次平均耗时中位数（ms） | 批次平均耗时 P95（ms） |
|---|---:|---:|---:|
| 逐条比较，`ece6c1f1f544139959271cba3ccd0a0367254a35` | 7.821855 | 7.073464 | 16.240041 |
| #11 按组件类型跳过，`codex/someui-ticket-11` 的本次实现 | 2.096470 | 1.838513 | 3.019228 |

两次使用相同场景、Release 配置与 `DOTNET_TieredCompilation=0`。每次先构建一帧，
随后预热 1000 帧，再以 20 个批次、每批 500 帧采样；使用 `Stopwatch.GetTimestamp`
与 `Stopwatch.GetElapsedTime` 计算每批平均每帧耗时，再汇总均值、中位数与 P95。
计时覆盖无变化时的完整 `SystemGroup.Update`，包括视图收集、参数检查、依赖缓冲访问
和依赖版本检查。测量末尾通过公开 World 读取确认每个视图保留 5 个来源值之和。

本机：Intel Xeon E5-2698B v3 @ 2.00GHz，Windows 10.0.26200，x64，
.NET SDK 10.0.112、.NET 10.0.12。测量工具执行普通的 Release 构建与进程启动，
构建完成后采样，临时测量项目与产物随后清理。这些数值作为本地检查阶段记录；
公开行为验收由 `SomeEngine.Views.Tests` 负责。

## Relations
[[architecture/someui/Testing|Testing]] · [[architecture/someui/Open-Questions|Open Questions]]
