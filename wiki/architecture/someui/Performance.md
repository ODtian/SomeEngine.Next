# Performance

## Current design
- 在 DefaultGame 统一场景平台加入标准 HUD 叠加层选项（约 1000 个元素、每帧变化的数字、一个长列表），在 1024 小球、Sponza、Bistro 三个场景中一起测量，记录重建次数、布局耗时与提取耗时。对比时该选项与其它渲染选项保持一致。
- 诊断：重建原因（视图与变化的依赖）写入诊断缓冲，默认关闭；每种视图类型对应一个 Tracy 区段。
- 初始预算，以首次实测结果校准：稳态（只有数值变化）每帧 0 分配；1000 个元素无变化时的依赖检查 ≤ 0.1 ms；100 个元素变化时重建加布局 ≤ 0.5 ms。

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
