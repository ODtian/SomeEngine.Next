# Testing

## Current design
- 只测试公开行为：创建 world，生成根视图，通过公开输入入口注入事件，推进若干帧，断言节点组件（例如 `Text`、`LayoutRect`）的值。不断言内部记录、缓存命中或执行次数。
- 每个内置控件在外部程序集中有一个只用公开 API 重写的等价版本，用同一组行为测试证明二者一致。
- 生成器测试编译并执行生成的 API，包括 `[Composable]` 拦截后的行为与分析器的错误诊断。
- NativeAOT 发布与启动沿用现有 `just ecs-aot` 方式加入界面冒烟场景。

## 首次构建验收（#6）

2026-10-03，集成基线 `ab8a17d7d3305edf8a638835938901970ab5dde7`，
Windows x64，.NET SDK `10.0.112`，执行环境 `.NET 10.0.12`。
`HeadlessFrameDriver` 通过公开 World、视图与系统接口驱动帧。

`FirstBuildTests` 验证有序子节点与类型编号、表格与 sparse 参数、同帧三层构建、
构建前的参数修改、带父实体的根、独立 World、`Once` 的首次内容与跨帧保留、
同步完成的 await，以及开发构建中的作用域错误和真实挂起报告。
`FirstBuildChecks` 的共享行为同时由测试运行器和正式 ECS NativeAOT 入口执行。

| 命令 | 结果 |
|---|---|
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Debug` | 10 个行为用例通过 |
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Release --no-restore` | 8 个行为用例通过 |
| `dotnet test tests/SomeEngine.ECS.Tests/SomeEngine.ECS.Tests.csproj -c Release` | 552 个回归用例通过 |
| `dotnet test tests/SomeEngine.ECS.Systems.Tests/SomeEngine.ECS.Systems.Tests.csproj -c Release` | 288 个回归用例通过 |
| `just ecs-aot win-x64` | 原生发布与执行成功，退出码 0，`viewsVerified: true` 与 `managedReferencesVerified: true` |

Release 沿统一开发检查规则编译；两个开发检查专用用例在 Debug 中执行。
NativeAOT 构建输出的 IL2026、IL3050 和 IL2070 来自现有 `TraceCapture` 与
`JobStorageTypeShape` 诊断路径；本次原生结果覆盖视图首次构建与原有 ECS 冒烟行为。

## 依赖登记作用域验收（#15）

2026-10-04，实现提交 `496cd58afdd2933cbefac7aae65d91ced535092f`，
已对齐集成分支提交 `ece6c1f1f544139959271cba3ccd0a0367254a35`。
Windows x64，.NET SDK `10.0.112`，执行环境 `.NET 10.0.12`。

`UntrackTests` 通过视图写出的组件值验证表格与 sparse 的局部取值、块外依赖更新、
嵌套块、异常退出、父子构建模式与开发构建中的作用域使用错误。
`ReadDependencyTests` 继续验证 `Once` 读取后保留首次内容，
`ParameterBuildTests` 验证参数更新后的内容行为。
`FirstBuildChecks` 将表格与 sparse 的局部取值及块外依赖更新共享给原生冒烟入口。

| 命令 | 结果 |
|---|---|
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj --no-restore --verbosity minimal` | 57 个公共行为用例通过 |
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Release --no-restore --verbosity minimal` | 50 个公共行为用例通过 |
| `just ecs-aot win-x64` | 原生发布与执行成功，退出码 0，`viewsVerified: true` 与 `managedReferencesVerified: true` |

原生构建输出的 IL2026、IL3050 与 IL2070 位于已有 `TraceCapture` 与
`JobStorageTypeShape` 诊断路径，本次执行覆盖新增的局部读取作用域与原有冒烟行为。

## 构建异常隔离验收（#18）

2026-10-04，基于集成提交 `07569891caad24fe22b403f7854be05440121128`
加本票实现，Windows x64，.NET SDK `10.0.112`。

`BuildErrorTests` 经公开 World、视图、帧驱动与 `Trace` listeners 验证兄弟继续构建、
失败声明的节点及后代恢复、相等参数中的回调恢复、原异常与帧号、首次失败、
后备内容、依赖修复、同步完成的异步构建与 `.Content()` 块内外的失败恢复。
`FirstBuildChecks` 将子声明恢复和祖先后备恢复一并用于测试运行器及 NativeAOT 冒烟。
Core 的公开检查验收通过参数求值的可观察效果验证开发报告的编译边界。

| 命令 | 结果 |
|---|---|
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Debug` | 112 通过 |
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Release` | 95 通过 |
| `dotnet test tests/SomeEngine.Core.Tests/SomeEngine.Core.Tests.csproj -c Debug` | 59 通过 |
| `dotnet test tests/SomeEngine.Core.Tests/SomeEngine.Core.Tests.csproj -c Release` | 59 通过 |
| `dotnet test tests/SomeEngine.ECS.Tests/SomeEngine.ECS.Tests.csproj -c Debug` | 633 通过 |
| `dotnet test tests/SomeEngine.ECS.Systems.Tests/SomeEngine.ECS.Systems.Tests.csproj -c Debug` | 298 通过 |
| `just ecs-aot win-x64` | 原生发布与执行成功，退出码 0，`viewsVerified: true` 与 `managedReferencesVerified: true` |

## 重建诊断验收（#65）

2026-10-04，实现提交 `58bbfe003b1bd1a186ee755d8ebab80370e9b937`，
已合入集成分支 `af3bd85e23b57804fd1c15ca85aa6e0fe7d16fb0`。
Windows x64，.NET SDK `10.0.112`，执行环境 `.NET 10.0.12`。

`RebuildDiagnosticsTests` 通过公开 World、视图、系统帧与 `LastFrame` 验证表格和 sparse 来源、
参数、存在性、查询成员、多个同时变化的来源、同步子构建、首次构建、失败构建、
World 隔离、逐帧替换、关闭与再次开启。开发构建通过公开诊断入口验证 World 使用错误。
类型区段通过公开 EngineTrace 采集与 Trace 文件读取 API 验证，并以真实 Tracy 采集导出复核。
采集关闭时的配对计时和分配数据见 [[architecture/someui/Performance|Performance]]。

| 命令 | 结果 |
|---|---|
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Debug --no-restore --verbosity minimal` | 129 个公共行为用例通过 |
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Release --no-restore --verbosity minimal` | 111 个公共行为用例通过 |
| `dotnet test tests/SomeEngine.ECS.Tests/SomeEngine.ECS.Tests.csproj -c Release --verbosity minimal` | 620 个回归用例通过 |
| `dotnet test tests/SomeEngine.ECS.Systems.Tests/SomeEngine.ECS.Systems.Tests.csproj -c Release --no-restore --verbosity minimal` | 298 个回归用例通过 |
| `dotnet test tests/SomeEngine.Core.Tests/SomeEngine.Core.Tests.csproj -c Debug --no-restore --verbosity minimal` | 59 个回归用例通过 |
| `dotnet test tests/SomeEngine.Core.Tests/SomeEngine.Core.Tests.csproj -c Release --verbosity minimal` | 59 个回归用例通过 |
| `dotnet test tests/SomeEngine.Diagnostics.Tests/SomeEngine.Diagnostics.Tests.csproj -c Debug --verbosity minimal` | 20 个回归用例通过 |
| `just ecs-aot win-x64` | 原生发布与执行成功，退出码 0，`viewsVerified: true` 与 `managedReferencesVerified: true` |

NativeAOT 构建的 IL2026、IL3050 与 IL2070 位于已有 `TraceCapture` 与
`JobStorageTypeShape` 路径，本次原生执行覆盖合入后的视图、组件字节访问与动画冒烟行为。

## 环境数据验收（#13）

2026-10-04，源码验收提交 `99ada7094a4103680d023b5e1f10b014913586e7`，
已对齐集成分支 `b259174a8f9f117bc5dedc2c3b69f5d494a248a5`。
Windows x64，.NET SDK `10.0.112`，执行环境 `.NET 10.0.12`。

[EnvironmentTests](../../../tests/SomeEngine.Views.Tests/EnvironmentTests.cs) 经公开 World、
视图、`Provide`、`ReadUp` 与系统帧验证表格和 sparse 的最近来源、自身提供、完整默认值、
祖先提供后的当帧更新、中间提供者、提供者增删、自身及祖先移动、相同值下的来源切换、
其它后代的内容与写版本保留，以及 `Once` 的首次取值。
绑定计算与 Memo 的验收验证所属视图移动、来源移除、恢复提供后的显示，
诊断验收通过公开 `LastFrame` 读取相同主题值下的来源切换记录。
环境组在 Debug 中包含 31 个用例，Release 中包含 29 个用例。

公开接口与父链移动的验收采用先红后绿流程；联合回归同时覆盖已集成的组件副本提交、
计算、状态、异常恢复、生成器与重建诊断。

| 命令 | 结果 |
|---|---|
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Debug --no-restore --verbosity minimal` | 246 个公共行为用例通过 |
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Release --no-restore --verbosity minimal` | 219 个公共行为用例通过 |
| `dotnet test tests/SomeEngine.Views.Generators.Tests/SomeEngine.Views.Generators.Tests.csproj -c Debug --no-build --no-restore --verbosity minimal` | 41 个生成器用例通过 |
| `dotnet test tests/SomeEngine.ECS.Tests/SomeEngine.ECS.Tests.csproj -c Release --no-restore --verbosity minimal` | 620 个回归用例通过 |
| `dotnet test tests/SomeEngine.ECS.Systems.Tests/SomeEngine.ECS.Systems.Tests.csproj -c Release --no-restore --verbosity minimal` | 298 个回归用例通过 |

API 与提交规则见 [[architecture/someui/Composition|Composition]]，
环境来源与版本检查见 [[architecture/someui/Dependencies|Dependencies]]。

## 布局协议验收（#24）

2026-10-04，代码验收提交 `f0894b6a41b718d667e5215209984426ff0e5a28`，
已合入集成提交 `bf0a73c511b166ffdc1049f52d5c97ee70cd9442`。
Windows x64，.NET SDK `10.0.112`。

`LayoutTests` 通过公开视图构建、修饰、World、系统组与 `LayoutRect` 验证
800×600 画布中的绝对定位、内边距的同帧更新、透明组合、用户稀疏环形布局与
Absolute 嵌套、最小/最大尺寸、单轴自动尺寸、零尺寸、非均匀内边距和多个画布。
`LayoutChecks` 由单元测试和 Views NativeAOT 入口共用，验证显式 `IView.Measure`、
稀疏参数、缩放画布、嵌套坐标及画布尺寸更新。
开发检查专用用例在 Debug 验证冲突的 Box 尺寸区间。

| 命令 | 结果 |
|---|---|
| `dotnet test tests/SomeEngine.Layout.Tests/SomeEngine.Layout.Tests.csproj -c Debug --no-restore --nologo -v quiet` | 14 通过 |
| `dotnet test tests/SomeEngine.Layout.Tests/SomeEngine.Layout.Tests.csproj -c Release --no-restore --nologo -v quiet` | 13 通过 |
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Debug --no-restore --nologo -v quiet` | 246 通过 |
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Release --no-restore --nologo -v quiet` | 219 通过 |
| `dotnet test tests/SomeEngine.Views.Generators.Tests/SomeEngine.Views.Generators.Tests.csproj -c Debug --no-restore --nologo -v quiet` | 41 通过 |
| `dotnet test tests/SomeEngine.Views.Generators.Tests/SomeEngine.Views.Generators.Tests.csproj -c Release --no-restore --nologo -v quiet` | 41 通过 |
| `dotnet test tests/SomeEngine.ECS.Tests/SomeEngine.ECS.Tests.csproj --nologo -v quiet` | 635 通过 |
| `dotnet test tests/SomeEngine.ECS.Systems.Tests/SomeEngine.ECS.Systems.Tests.csproj --nologo -v quiet` | 298 通过 |
| `just views-aot win-x64` | 原生发布、布局/Compose/视图检查、存档类型编号重映射和下一帧重建全部通过，退出码 0 |

## Surface 与外部对等验收（#28）

`SomeEngine.Elements.Consumer.Tests` 直接引用公开的 Views、Layout 与 Elements 项目。
架构用例同时引用公开的 Animation、Typography、Compose 与 SwiftUI 项目。
`Rewrites/SurfaceModifiers.cs` 提供用户的三个等价修饰，`SurfaceTests` 将同一组
节点数据、返回实体、布局结果与写版本用例应用于内置修饰和用户修饰。
尺寸变化后的重建保留相同 Surface 的节点写版本与类型写版本，填充色变化则推进两者。
开发构建中的三种修饰共用 Core 的作用域检查。

`SurfaceChecks` 与用户修饰示例源文件还由现有 Views NativeAOT 工程编译，
在 `Column` 节点上使用相同公开 API 验证布局尺寸变化、相等值的写版本保留和填充色更新。

`AssemblyBoundaryTests` 经编译程序集的公开属性读取访问边界。
向 Views 临时加入 `[assembly: InternalsVisibleTo("SomeEngine.Elements")]` 后，
架构用例报告该声明并以退出码 1 失败；移除该声明后的验收通过。
SDK 项目项形式分别向 `SomeEngine.Elements.Render` 与外部消费者授予内部访问时，
同一架构用例列出两个目标并以退出码 1 失败；项目内容随后恢复。
在 Animation 临时加入指向 Elements 的友元声明时，扩展后的架构用例报告来源与目标，
并以退出码 1 失败；临时声明随后清理。
访问边界规则由 [[architecture/someui/Assemblies|Assemblies]] 维护。

2026-10-04，验收基于集成提交
`220bca53e5941ae928d253d369c61f20e4c6837f`，其中包含 #25 的 Column 交付。
Windows x64，.NET SDK `10.0.112`。
以下测试命令均使用 `--nologo -v minimal -m:1 -nr:false -p:BuildInParallel=false -p:UseSharedCompilation=false`。

| 命令 | 结果 |
|---|---|
| `dotnet test tests/SomeEngine.Elements.Consumer.Tests/SomeEngine.Elements.Consumer.Tests.csproj -c Debug` | 13 通过 |
| `dotnet test tests/SomeEngine.Elements.Consumer.Tests/SomeEngine.Elements.Consumer.Tests.csproj -c Release` | 10 通过 |
| `dotnet test tests/SomeEngine.Layout.Tests/SomeEngine.Layout.Tests.csproj -c Debug` | 50 通过 |
| `dotnet test tests/SomeEngine.Layout.Tests/SomeEngine.Layout.Tests.csproj -c Release` | 48 通过 |
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Debug` | 253 通过 |
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Release` | 224 通过 |
| `dotnet test tests/SomeEngine.Views.Generators.Tests/SomeEngine.Views.Generators.Tests.csproj -c Debug` | 85 通过 |
| `dotnet test tests/SomeEngine.Views.Generators.Tests/SomeEngine.Views.Generators.Tests.csproj -c Release` | 85 通过 |
| `just views-aot win-x64` | 原生发布、Column Surface 与外部修饰、现有视图行为、存档写入与读回全部通过，退出码 0 |

原生发布输出的 IL2026、IL3050 与 IL2070 位于现有 `TraceCapture` 与
`JobStorageTypeShape` 路径。验收后的原生产物和临时友元声明已清理。

## 内容函数验收（#61）

2026-10-04，实现提交 `c8255fdd`，源码验收提交
`ac8086644c165fc53b34bf5695e8e6f47fcaedb1`，已对齐集成提交
`25f49a66061e7f19882f1d0f52303c7abf2bfb95`。
Windows x64，.NET SDK `10.0.112`。

`ContentFunctionTests` 的 16 个公开编译与行为用例通过生成视图、World、
`ElementTree`、`State<T>`、`BuildError` 和分析器诊断验收条件与循环调用、
内容引用变化、static lambda 的子内容保留、泛型跨程序集转交、各行独立依赖、
同步完成的 BuildTask 内容与错误恢复、自定义委托、命名与可选实参、局部及全局别名。
info 验收同时覆盖 Compose 与 SwiftUI，内容委托形状由编译诊断验收。
接口接入、引用刷新、捕获提示、声明错误和全局别名均按先红后绿流程完成。

| 命令 | 结果 |
|---|---|
| `dotnet test tests/SomeEngine.Views.Generators.Tests/SomeEngine.Views.Generators.Tests.csproj --verbosity minimal` | 57 个生成器用例通过 |
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj --no-restore --verbosity minimal` | 246 个公共行为用例通过 |
| `dotnet publish tools/SomeEngine.Views.AotSmoke/SomeEngine.Views.AotSmoke.csproj -c Release -r win-x64 --self-contained --verbosity minimal` | NativeAOT 原生发布成功 |
| `tools/SomeEngine.Views.AotSmoke/bin/Release/net10.0/win-x64/publish/SomeEngine.Views.AotSmoke.exe` | 原生执行成功，退出码 0；覆盖内容函数、Compose、节点身份、sparse 存储和用户布局 |

原生内容验收从公共树读取多行结果，并验证父参数变化后相等行的保留与变化行的刷新。
原生验收产物在执行后清理。接口与节点规则见
[[architecture/someui/Compose-Frontend|Compose Frontend]]。

## Relations
[[architecture/someui/Composition|Composition]] · [[architecture/someui/Performance|Performance]]
