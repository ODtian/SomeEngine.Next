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

## 焦点与键盘、手柄导航验收（#39）

2026-10-04，实现提交 `8f36fdd4509faa095cc6cc787195a4143a104ae1`，源码验收提交
`cb50ddb56e5d902932e1fecc20ea938419b116dd`，已合入集成提交
`f72a355041cdecced3cb0778e50ed7fa08c987b3`。
Windows x64，.NET SDK `10.0.112`，执行环境 `.NET 10.0.12`。

`FocusTests` 通过公开输入、World、视图、布局与系统调度验证 3×3 网格、确认消费、焦点读取依赖、
祖先转移与清空、整枝释放、父链移动、焦点资格变化、布局透明组合、最近中心与上一帧布局、
画布内导航、World 隔离、输入顺序和捕获字段。内置与外部重写共用网格检查；
自定义系统通过调度禁用默认导航，从公开事件缓冲领取输入并执行自己的规则。
Debug 的 25 项焦点检查包含 9 项统一开发检查；Release 与原生入口共用 16 项行为检查。

各测试项目的路径为 `tests/<项目>/<项目>.csproj`，执行两种配置：

```text
dotnet test <项目路径> -c Debug --nologo -v quiet -m:1 -p:UseSharedCompilation=false
dotnet test <项目路径> -c Release --nologo -v quiet -m:1 -p:UseSharedCompilation=false
```

ECS、系统、存档与提取的 Release 回归加入 `-nr:false -p:BuildInParallel=false`。
系统 Release 完整验收使用以下命令完成：

```text
dotnet test tests/SomeEngine.ECS.Systems.Tests/SomeEngine.ECS.Systems.Tests.csproj -c Release --no-build --nologo -m:1 --logger "console;verbosity=normal" --blame-hang-timeout 60s --blame-hang-dump-type none --results-directory .focus-validation
```

| 项目 | Debug 通过 | Release 通过 |
|---|---:|---:|
| SomeEngine.Elements.Consumer.Tests | 72 | 47 |
| SomeEngine.Views.Tests | 259 | 230 |
| SomeEngine.Layout.Tests | 94 | 74 |
| SomeEngine.ECS.Tests | 635 | 620 |
| SomeEngine.ECS.Systems.Tests | 298 | 298 |
| SomeEngine.ECS.Serialization.Tests | 169 | 169 |
| SomeEngine.Elements.Render.Tests | 10 | 10 |

`just views-aot win-x64` 成功发布并执行原生入口，退出码 0，覆盖全部共享焦点检查与
已有视图、文本、指针、布局和检查点行为。检查点在独立进程中完成类型编号重映射、读回首帧
与后续依赖更新。诊断目录、检查点文件及原生验收产物在使用后清理。
行为与接口见 [[architecture/someui/Input-and-Behaviors|Input and Behaviors]]。

随后对齐 #38：源码验收提交 `a2d5d364961f6fdcddc1c22bb40f12f82ab33793`，
包含集成提交 `3da0051c0116fb962997fa40d3f3a0c12803c257`。
消费者工程重新执行 Debug 与 Release，均使用
`--nologo -v quiet -m:1 -nr:false -p:BuildInParallel=false -p:UseSharedCompilation=false`，
结果分别为 80 与 55 通过。新增组合检查验证指针点击和焦点确认共用节点上的 `Clicks`，
调用方消费计数后继续接收下一次键盘确认；当前焦点检查为 Debug 26 项、Release 17 项。
`just views-aot win-x64` 再次成功发布、执行全部 17 项共享焦点检查与指针冒泡检查，
并完成检查点写入与读回，退出码 0。原生验收产物在执行后清理。

最终源码验收提交 `f36b17ca54aada9b827751c014e782f0efde5b92`，包含集成提交
`e26a6d4bbcb0b5b3a544b7c5e06c216314dc4b85`，覆盖已合入的异步构建与 Button 交付。
按上一段命令参数重新执行消费者、Views 与 Layout 的 Debug / Release 回归：
消费者 101 / 73 通过，Views 279 / 250 通过，Layout 94 / 74 通过。
`just views-aot win-x64` 在该源码提交成功发布并执行共享焦点、指针路由、Button 与
现有视图检查，随后完成两个独立进程的检查点写入、类型编号重映射和读回，退出码 0。
焦点公共行为保持 Debug 26 项与 Release / NativeAOT 17 项；原生验收产物在使用后清理。

## Button 与外部控件对等验收（#43）

[ButtonTests](../../../tests/SomeEngine.Elements.Consumer.Tests/ButtonTests.cs)
对内置 Button、[用户 Button](../../../tests/SomeEngine.Elements.Consumer.Tests/Rewrites/Button.cs)
和[用户 IconButton](../../../tests/SomeEngine.Elements.Consumer.Tests/Rewrites/IconButton.cs)
运行同一组公开行为用例。验收通过视图函数声明节点，通过 `Pointer` 注入输入并推进系统帧，
读取公开的 Surface、Box、Text、ShapedText、Hover、Clicks 和调用方写出的点击显示数据。

用例覆盖默认样式、标签与图标间距、非均匀内边距、子树主题更新、最近主题的完整默认值、
显式透明颜色、零圆角与零内边距、悬停与按下颜色、按住后移出、区域外释放、连续读取点击、同帧多次
点击的消费，以及修改标签与主题后的按下和点击保留。开发构建通过 Core 作用域检查报告
构建使用错误。

[ButtonChecks](../../../tests/SomeEngine.Elements.Consumer.Tests/ButtonChecks.cs)
和两个用户控件源文件由 Views NativeAOT 工程编译，点击消费、标签更新、主题与输入状态
在测试运行器和原生入口执行同一份检查。API 与默认主题由
[[architecture/someui/Composition|Composition]] 维护。

2026-10-04，本票实现提交 `7d0e472f0e053b38ae2779eb1f3c8785d9bc11e9`，
完整回归源码提交 `09f57726f1987f569285577d1faa180ad160460e`，已合入集成分支提交
`f72a355041cdecced3cb0778e50ed7fa08c987b3`。Windows x64，.NET SDK `10.0.112`，
执行环境 `.NET 10.0.12`。

公开 API 接入、主题读取、悬停配色与外部控件声明采用先红后绿流程。
本票新增的行为组在 Debug 中包含 21 个用例，Release 中包含 18 个用例；
三个开发作用域用例经 Core 的统一开发检查执行。
下表各 `dotnet test` 命令同时使用
`--nologo -v quiet -m:1 -nr:false -p:BuildInParallel=false -p:UseSharedCompilation=false`。

| 命令 | 结果 |
|---|---|
| `dotnet test tests/SomeEngine.Elements.Consumer.Tests/SomeEngine.Elements.Consumer.Tests.csproj -c Debug` | 68 通过 |
| `dotnet test tests/SomeEngine.Elements.Consumer.Tests/SomeEngine.Elements.Consumer.Tests.csproj -c Release` | 49 通过 |
| `dotnet test tests/SomeEngine.Layout.Tests/SomeEngine.Layout.Tests.csproj -c Debug` | 94 通过 |
| `dotnet test tests/SomeEngine.Layout.Tests/SomeEngine.Layout.Tests.csproj -c Release` | 74 通过 |
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Debug` | 259 通过 |
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Release` | 230 通过 |
| `dotnet test tests/SomeEngine.Views.Generators.Tests/SomeEngine.Views.Generators.Tests.csproj -c Debug` | 95 通过 |
| `dotnet test tests/SomeEngine.Views.Generators.Tests/SomeEngine.Views.Generators.Tests.csproj -c Release` | 95 通过 |
| `just views-aot win-x64` | 原生发布、三种按钮共享检查、现有视图检查、存档写入与读回通过，退出码 0 |

NativeAOT 的 IL2026、IL3050 与 IL2070 诊断位于已有 `TraceCapture` 和
`JobStorageTypeShape` 路径。本次验收的构建、原生产物及测试临时数据已清理。

最终输入联合回归源码提交 `538bd1d5488137786b6fb133c63fd4e30f0e493f`，
包含集成提交 `3da0051c0116fb962997fa40d3f3a0c12803c257` 的点击冒泡与子树悬停。
三种按钮沿当前命中系统运行，原生入口同时执行指针路由和按钮检查。
公共 Views、Layout 与生成器的回归采用上表的完整回归结果；这些源码在输入合入时保持相同。

| 命令 | 最终输入联合回归结果 |
|---|---|
| `dotnet test tests/SomeEngine.Elements.Consumer.Tests/SomeEngine.Elements.Consumer.Tests.csproj -c Debug` | 75 通过 |
| `dotnet test tests/SomeEngine.Elements.Consumer.Tests/SomeEngine.Elements.Consumer.Tests.csproj -c Release` | 56 通过 |
| `just views-aot win-x64` | 三种按钮、指针路由、现有视图行为与存档读写通过，退出码 0 |

本轮字体原生依赖复用 #27 工作树的同版本产物；复用前以 SHA-256 核对
`CMakeLists.txt`、`GlyphBitmap.c` 与 Typography 项目文件三个构建输入一致。
本轮构建、原生产物与测试临时数据在验收后清理。

## 指针冒泡与子树悬停验收（#38）

2026-10-04，Windows x64，.NET SDK `10.0.112`。
实现提交 `ff3b3fa5044ac34e0058e3028d3c824c8945c34f`，
验收源码提交 `a60aeb638f55f114bf5d63a05c4d19ec5b69713d`，
包含集成提交 `f72a355041cdecced3cb0778e50ed7fa08c987b3`。

点击冒泡、停止边界与子树悬停三个行为切片均先运行红测，再完成实现并转绿。
`PointerTests` 从公开指针入口和节点组件验证父子点击计数、目标及祖先的停止边界、
仅带停止标记的中间节点、嵌套画布边界，以及上一帧布局对静止指针的影响。
`PointerRoutingChecks` 通过公开视图、布局与 World 帧推进验证子树进出、兄弟切换、
区域外的持续按下、停止标记增删、调用方的 `Clicked()` 输出与 `Hover` 依赖输出。
同一组检查应用于内置 `Pressable` 和外部公开 API 重写，并编入 Views NativeAOT 工程。

以下 `dotnet test` 与 `dotnet publish` 命令均附加
`--nologo -v minimal -m:1 -nr:false -p:BuildInParallel=false -p:UseSharedCompilation=false`。

| 命令 | 结果 |
|---|---|
| `dotnet test tests/SomeEngine.Elements.Consumer.Tests/SomeEngine.Elements.Consumer.Tests.csproj -c Debug` | 54 通过 |
| `dotnet test tests/SomeEngine.Elements.Consumer.Tests/SomeEngine.Elements.Consumer.Tests.csproj -c Release` | 38 通过 |
| `dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj -c Debug` | 259 通过 |
| `dotnet test tests/SomeEngine.Layout.Tests/SomeEngine.Layout.Tests.csproj -c Debug` | 94 通过 |
| `dotnet publish tools/SomeEngine.Views.AotSmoke/SomeEngine.Views.AotSmoke.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:TrimmerSingleWarn=false` | NativeAOT 原生发布成功 |
| `tools/SomeEngine.Views.AotSmoke/bin/Release/net10.0/win-x64/publish/SomeEngine.Views.AotSmoke.exe` | 原生冒烟通过，退出码 0；包含内置与用户重写的冒泡、停止标记与子树悬停 |
| `pwsh -NoProfile -File tools/SomeEngine.Views.AotSmoke/Test-Checkpoint.ps1 -Executable ./tools/SomeEngine.Views.AotSmoke/bin/Release/net10.0/win-x64/publish/SomeEngine.Views.AotSmoke.exe` | 存档写入、类型编号重映射、读回和下一帧重建通过，退出码 0 |

原生发布输出的 IL2026、IL3050 与 IL2070 位于既有 `TraceCapture` 与
`JobStorageTypeShape` 路径。验收运行产物在执行后清理。
最终回归的字体原生依赖复用 #27 工作树的同版产物；复用前以 SHA-256 核对
`CMakeLists.txt`、`GlyphBitmap.c` 与 Typography 项目文件的构建输入一致。

路由行为与数据写入范围由
[[architecture/someui/Input-and-Behaviors|Input and Behaviors]] 维护。

## 拖动、滚动与选择验收（#40）

2026-10-04，Windows x64，.NET SDK `10.0.112`。
本票实现从 `98238b412da5532204744472a3da86f48888305f` 开始，
基础回归在合入布局缓存后的 `230c75a6409df68b4a0883044c2a854223099e29` 执行。
随后合入指针冒泡集成提交 `3da0051c0116fb962997fa40d3f3a0c12803c257`，
消费者完整回归与原生验收在行为提交 `78d1b341b873b1381bc95b89689d9734f37dc001` 执行。
交付前继续合入异步构建与 Button 集成提交
`e26a6d4bbcb0b5b3a544b7c5e06c216314dc4b85`，基础层完整回归在
`f0398e176a9d6b0f3a20c964565605f36ea0815a` 执行，最终消费者回归与原生入口
基于合并提交 `0d1dd5405f774c3f72944c3df1675b5b4d26c392`。

本票新增的 20 项公开行为用例通过 Pointer、行为声明、World、ElementTree、
系统组和组件读取验证 Drag 字段、静止帧、区域外释放、再次拖动、按钮配对、
目标退出交互树、选择依赖输出、同帧点击、冒泡停止边界、滚轮事件顺序、
透明组合视口、可点击内容、嵌套滚动、内边距、内容与视口收缩、重复布局、
范围边界的公开写版本以及真实文本的自然测量。
其中三项使用错误验收在 Debug 通过共用 Core 开发检查执行。
每个行为的外部用户重写使用相同公开组件与 Scope.Ensure，并与内置声明共用行为检查。

以下工程分别执行
`dotnet test tests/<项目>/<项目>.csproj -c Debug --nologo -v quiet -m:1 -nr:false -p:BuildInParallel=false -p:UseSharedCompilation=false`
及对应的 `-c Release` 命令。依赖还原完成后的重复验证加 `--no-restore`。

| 项目 | Debug 通过 | Release 通过 |
|---|---:|---:|
| SomeEngine.Elements.Consumer.Tests | 95 | 73 |
| SomeEngine.Layout.Tests | 94 | 74 |
| SomeEngine.Views.Tests | 279 | 250 |
| SomeEngine.Views.Generators.Tests | 95 | 95 |
| SomeEngine.Elements.Render.Tests | 10 | 10 |

`just views-aot win-x64` 完成原生发布、执行和两进程检查点验收，退出码 0。
原生入口同时执行内置与用户重写的拖动、滚动、选择，及已交付的指针冒泡、
StopsPointer、子树悬停、文本、布局、视图与各前端检查。
检查点第二进程将面板 ViewType.Id 从 1 映射为 5，读回后的重建与依赖更新通过。
发布中的既有 IL2026、IL3050、IL2070 诊断分别来自 TraceCapture 与 JobStorageTypeShape。
临时构建输出、原生产物与检查点验收文件随验证完成清理。

## Relations
[[architecture/someui/Composition|Composition]] · [[architecture/someui/Performance|Performance]]
