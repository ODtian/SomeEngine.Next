# Performance

## Current design
- DefaultGame 统一场景平台提供标准 HUD 叠加层选项（约 1000 个元素、每帧变化的数字、一个长列表），在 1024 小球、Sponza、Bistro 三个场景中一起测量，记录重建次数、布局耗时、提取耗时与帧时间。每组对照记录相同的其它渲染选项、分辨率、场景行为、JIT 和采样协议。
- 诊断：重建原因（视图与变化的依赖）写入诊断缓冲，默认关闭；每种视图类型对应一个 Tracy 区段。
- 本机首次三场景实测校准的复测预算：静止 HUD 的界面更新线程每帧 0 字节分配，动态 HUD 平均每帧 ≤ 100 KiB；约 1000 个节点静止时，完整重建阶段的依赖与参数检查平均每帧 ≤ 17 ms；100 组数字变化时，重建加布局平均每帧 ≤ 140 ms。配置、范围和实测值见下方 #68 校准记录。

## 标准 HUD 与统一测量（#68）

`SceneTestPlatform.SetHudOverlay(enabled, changeNumbers: true)` 选择标准 HUD，保留当前场景选择并请求重载。
DefaultGame 的 F6 和 `HUD overlay` 复选框使用同一选项；`Update HUD numbers` 控制数字变化。
启动参数 `--hud` 选择每帧变化，`--hud-static` 选择静止数字。

`HudOverlay` 用公开视图、布局与文字部件声明 800 个静态标签、100 个读取帧号的数字视图及其文字子节点，
并加入 10000 项、24 行高、264×240 视口的 `VirtualList`。标准 1280×720 下总计 1017 个视图节点，
列表初始保留 11 行，滚动沿 `Visible` 与 `Key` 更新。HUD 的尺寸、帧号更新与释放通过公开实例方法完成。
场景中的计数面板和 HUD 共用一份字体与字形图集，沿既有 Game world → Rendering world 提取与持久画布 pass 绘制。

```powershell
./tools/Measure-Scenes.ps1
./tools/Measure-Scenes.ps1 -Hud
./tools/Measure-Scenes.ps1 -Hud -FreezeHud
./tools/Measure-Scenes.ps1 -Hud -Trace
```

每个命令完成一次 60 秒内的三场景反馈，内容构建另计。三种 HUD 负载和 trace 使用同一采样协议：
连续 16 帧流送就绪、至少 16 个渲染帧、首个渲染帧后至少 3 秒预热，然后采集至少 2 秒。
场景流送重新开始时重新预热；测量总时限为 45 秒，剩余时间按剩余场景分配。
时间型预热为不同 HUD 帧率提供共同的预热时长，帧数下限覆盖连续的提取与 GPU 完成边界。
原平台的 128 帧下限在本票中按 HUD 和一分钟反馈要求校准为 16；本票的全部对照使用此处的新协议。

结果记录分辨率、动画、渲染选项、固定步长、JIT、设备、采样条件和 HUD 负载。
`Performance/timing/` 保存常规结果，`Performance/trace/` 保存 trace 结果和逐场景采集。
脚本检查三场景的完整性、收敛状态、所选负载以及各测量值，并在反馈期限内结束应用。

`GameFrameSystems.MeasureViews` 运行相同的公开帧阶段，返回 `ViewTiming`。
`ViewRebuildSystem.BuildCount` 统计本阶段实际进入的视图 `Build`，包括同步子视图和失败尝试；
`ComputationCount` 单独统计响应式计算。异步续执行沿原构建继续，新的构建入口推进构建计数。
报告中的 `Rebuilds`、`Computations` 为采样合计，`*PerFrame` 为平均值。
`RebuildMs` 覆盖重建阶段的依赖检查、视图构建和响应式计算；`TextMs` 覆盖排版刷新阶段，
`LayoutMs` 覆盖完整布局及其文字测量；`ViewMs` 覆盖游戏世界的界面更新，`ExtractionMs` 覆盖同一世界的完整渲染提取。
`ViewAllocatedBytes` 与 `ExtractionAllocatedBytes` 为对应更新线程的平均每帧托管分配。
异步游戏更新把计时作为任务结果返回，呈现帧读取其对应的更新结果。
trace 通过 `Views.Rebuild`、`Views.TextMeasurement`、`Views.Layout` 和 `GameWorld.Extract` 定位同一阶段。

## 布局缓存（#27）

布局系统按节点保存上次测量与相对位置，约束和布局输入版本相同时复用。
每帧的版本与布局层级检查复用系统持有的集合；实际测量在输入或约束变化时执行。
固定尺寸子树内部的尺寸变化在重排边界处止步，父节点移动时从相对位置更新画布坐标。
`LayoutRect` 的写版本随实际矩形变化推进，供后续提取与动画使用。
Flex 的基础尺寸与最终尺寸测量共同参与失效判断，使局部更新保持全量布局的结果。

接口、缓存清除与验收依据由 [Layout](Layout.md#布局缓存与重排边界27) 维护。
DefaultGame 的 HUD、布局和提取预算按本页的统一场景平台测量。

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
    public VersionCell? SourceObject { get; init; }
}
```

`Enabled` 默认 `false`，在重建阶段之间切换，作用于所有 World。
开启后，每个 World 的 `ViewRebuildSystem` 在阶段开始清空并复用自己的诊断缓冲。
`LastFrame(world)` 在阶段结束后读取该 World 的本次记录；调用方在该 World 下次重建阶段
或开关变化之前消费返回的只读 span。首次运行阶段之前、静止帧以及关闭期间返回空 span。
关闭会释放各 World 已收集的记录，再次开启从下一次重建阶段开始收集。

记录在视图构建与响应式计算入口、读取依赖重新登记之前形成。每个变化来源对应一条记录；
同次重建的参数和多个读取来源全部保留。父视图当场构建的子视图沿相同入口记录来源。
候选在执行前完成存活与版本复核，因此本次记录对应实际发生的构建。
失败的构建尝试同样保留来源，异常内容与帧号通过视图的公开 `BuildError` 读取。

对象读取通过 `SourceObject` 返回原 `State<T>`、`VersionCell` 或 `Memo<T>` 引用，实体来源字段使用
`Entity.Null` 与组件编号 0。Memo 的来源变化与 Memo 值变化分别按其计算和消费者的实际读取记录。
诊断与调度共用组件、存在性、查询成员、环境及对象的变化规则；对象的读取帧、奇数版本和错误变化沿同一规则比较。
读取依赖的来源身份规则由依赖数据统一提供，视图与无节点计算使用同一规则去重。
检查点读回后的视图按恢复后的首次构建记录；参数版本比较按 `BuildMode` 选择。

| 原因 | `View` | `Source` | `Component` |
|---|---|---|---|
| 读取的表格或 sparse 组件写版本变化 | 构建的视图 | 被读取的实体 | `ComponentId<T>.Value` |
| `Has<T>` 的存在性变化 | 构建的视图 | 被检查的实体 | `ComponentId<T>.Value` |
| `ReadUp<T>` 的值或提供者变化 | 构建的视图 | 上次读取的提供者，缺省来源为 `Entity.Null` | `ComponentId<T>.Value` |
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

### 关闭采集的本地对照（2026-10-04）

基线为 `af3bd85e23b57804fd1c15ca85aa6e0fe7d16fb0` 的 Views 实现。
本票实现为 `58bbfe003b1bd1a186ee755d8ebab80370e9b937`。
两版在同一进程的各自加载上下文中使用相同的 ECS、Core 和 Job 程序集，
每版建立一个无头 World，1024 个根视图读取一个共享来源组件，并把数值写到自己的显示组件。
静止帧保留来源值，变化帧每帧写入新的来源值；末尾通过公开 World 读取验证显示结果。

Windows x64，Intel Xeon E5-2698B v3 @ 2.00GHz，.NET SDK `10.0.112`、
执行环境 `.NET 10.0.12`，Release，`DOTNET_TieredCompilation=0`，进程固定在逻辑处理器 31。
每版预热 512 个变化帧，然后每种负载采集 20 个配对批次，每批每版 16 帧，
逐帧交替两版并交替先后顺序。每个批次开始前完成 GC；计时覆盖 `SystemGroup.Update`，
变化帧同时覆盖来源写入。使用 `Stopwatch` 计时和当前线程分配计数，配对差异采用 95% t 区间。
本轮执行通过 `dotnet build -c Release` 构建临时公开 API 消费者，再由 `dotnet` 启动配对测量。

| 负载 | 基线平均每帧（ms） | 本票平均每帧（ms） | 配对差异与 95% 区间 | 基线 / 本票每帧分配（字节） |
|---|---:|---:|---|---:|
| 静止 | 1.815388 | 1.820134 | +0.25%，[-0.83%，+1.33%] | 0 / 0 |
| 全部视图随来源变化 | 9.912564 | 9.965794 | +0.56%，[-1.10%，+2.22%] | 450560 / 450560 |

两组配对耗时差异的统计区间均包含零，关闭采集的额外分配为零。
这些数据用于本票的诊断开关验收；DefaultGame 的 HUD、布局与提取预算沿统一场景平台测量。
Tracy 区段另用真实原生桥接、`tracy-capture.exe -a 127.0.0.1 -s 2 -f -o <capture>`
与 `tracy-csvexport.exe <capture>` 验证，导出结果包含根视图和同步子视图各自的类型区段。
临时测量消费者、原生桥接构建目录、采集文件和导出结果在使用后清理。

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
