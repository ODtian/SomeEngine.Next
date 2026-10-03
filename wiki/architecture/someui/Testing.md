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

## Relations
[[architecture/someui/Composition|Composition]] · [[architecture/someui/Performance|Performance]]
