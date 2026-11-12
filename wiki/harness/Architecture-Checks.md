# Architecture Checks

Architecture checks turn durable Wiki constraints into executable dependency/API-shape/build rules where automation has clear value.

They must not become a second architecture source. If a rule changes, update the owning Wiki concept first, then update the check.

Checks that only diagnose programmer mistakes during development must not force equivalent shipping-path validation.

## SomeUI public assembly access

[[architecture/someui/Assemblies|Assemblies]] owns the public API boundary for Elements,
its rendering layer, the frontends, and their consumers.
`AssemblyBoundaryTests.ElementLayerUsesPublicAssemblyAccess` reads the compiled
assembly attributes across the external consumer's engine dependencies. An
`InternalsVisibleTo` grant to one of those public layers fails the check and
reports the granting assembly and recipient.

```powershell
dotnet test tests/SomeEngine.Elements.Consumer.Tests/SomeEngine.Elements.Consumer.Tests.csproj --filter FullyQualifiedName~AssemblyBoundaryTests
```

## 新增类型命名（ARCH-30）

根级 `AGENTS.md` 持有类型命名中的禁用词规则。`tools/ArchitectureRules` 使用 Roslyn 读取类、结构、记录、接口、枚举和委托的声明，以命名空间、包含类型和泛型参数数量辨认类型。检查将当前声明与指定合入起点提交的声明对应起来，向新增类型报告禁用词及源代码位置。已有类型可以沿用其名字移动文件；注释和字符串按文本内容处理。

当前源码范围来自 Git 登记文件及当前新增的 C# 文件；`external` 按第三方来源处理，`bin` 和 `obj` 按构建输出处理。合入起点通过 `--base` 显式传入。

```powershell
& tools/Invoke-Benchmark.ps1 --cpu --session ecs-physics-integration dotnet run --project tools/ArchitectureRules/SomeEngine.ArchitectureRules.csproj -c Release -- . --base 05cf28fb8 --rules ARCH-30
dotnet test tests/SomeEngine.ArchitectureRules.Tests/SomeEngine.ArchitectureRules.Tests.csproj
```

行为检查覆盖新增名字的诊断、已有类型移动文件，以及源码声明与注释、字符串的对应范围。
