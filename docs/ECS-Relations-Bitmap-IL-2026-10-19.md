# ECS bitmap 编译证据

验证日期：2026-10-19。构建分支：`codex/ecs-relations-237`。使用 worker2 的同一套 Release 输出，工程为 `SomeEngine.ECS.Relations` 和真实使用方 `SomeEngine.ECS.Relations.Tests`。行为检查命令：

```powershell
dotnet test tests/SomeEngine.ECS.Relations.Tests/SomeEngine.ECS.Relations.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~BitmapRelationTests --nologo --verbosity minimal
```

结果为六条行为检查通过，自动测试规则检查 0 项违规。测试直接链接 benchmark 目录中的业务例子、固定候选、手写循环及结果消费源码。

## 实际产物与读取命令

编译产物为 `src/SomeEngine.ECS.Relations/bin/Release/net10.0/SomeEngine.ECS.Relations.dll` 和 `tests/SomeEngine.ECS.Relations.Tests/bin/Release/net10.0/SomeEngine.ECS.Relations.Tests.dll`。使用本机 `ilspycmd 9.1.0.7988` 读取 IL 与具体扩展类：

```powershell
ilspycmd -il src/SomeEngine.ECS.Relations/bin/Release/net10.0/SomeEngine.ECS.Relations.dll
ilspycmd -il tests/SomeEngine.ECS.Relations.Tests/bin/Release/net10.0/SomeEngine.ECS.Relations.Tests.dll
ilspycmd -t SomeEngine.ECS.Benchmarks.Relations.Bitmap.RelationQueryExtensions tests/SomeEngine.ECS.Relations.Tests/bin/Release/net10.0/SomeEngine.ECS.Relations.Tests.dll
```

## 直接许可指令

`OneWayPermission.Matches` 的两个输入均为 `[in] CategoryBits&`。其实际方法体大小为 18 字节，核心 IL 如下：

```il
IL_0000: ldarg.0
IL_0001: ldfld uint64 SomeEngine.ECS.Relations.CategoryBits::AllowedCategories
IL_0006: ldarg.1
IL_0007: ldfld uint64 SomeEngine.ECS.Relations.CategoryBits::Categories
IL_000c: and
IL_000d: ldc.i4.0
IL_000e: conv.i8
IL_000f: cgt.un
IL_0011: ret
```

`MutualPermission.Matches` 同样使用两个 `[in] CategoryBits&`。方法体大小为 35 字节，两次字段读取与位交集之间采用短路分支：

```il
IL_0000: ldarg.0
IL_0001: ldfld uint64 SomeEngine.ECS.Relations.CategoryBits::AllowedCategories
IL_0006: ldarg.1
IL_0007: ldfld uint64 SomeEngine.ECS.Relations.CategoryBits::Categories
IL_000c: and
IL_000d: brfalse.s IL_0021
IL_000f: ldarg.1
IL_0010: ldfld uint64 SomeEngine.ECS.Relations.CategoryBits::AllowedCategories
IL_0015: ldarg.0
IL_0016: ldfld uint64 SomeEngine.ECS.Relations.CategoryBits::Categories
IL_001b: and
IL_001c: ldc.i4.0
IL_001d: conv.i8
IL_001e: cgt.un
IL_0020: ret
IL_0021: ldc.i4.0
IL_0022: ret
```

## 具体读取与生成入口

`BitmapRelation<TRelation,TImplementation,TComponent,TPermission>.Matches` 的实际 IL 先调用两次 `QueryRow.ReadRef<!TComponent>()`，分别在 `IL_0051`、`IL_0070` 使用 `constrained. !TImplementation` 调用 `IBitmapRelation.Read`；`IL_0081` 使用 `constrained. !TPermission`，随后 `IL_0087` 调用静态 `ICategoryPermission.Matches`。传入的是语义实例和两份组件只读引用，位数据以两个局部 `CategoryBits` 值传递。

真实使用方的扩展类反编译显示具体返回类型：

```csharp
RelationQuery<BitmapFilter<TEnumerable, OneWayContacts, OneWayContactBitmap, CollisionLayer, OneWayPermission>, (Entity, Entity)>
RelationQuery<BitmapFilter<TEnumerable, MutualContacts, MutualContactBitmap, CollisionLayer, MutualPermission>, (Entity, Entity)>
```

每个类型分别对应 Where 与 Except，方法体只取得 `source.Enumerable`、语义实例和实现实例，再构造对应共享筛选器。标准二元实体行的普通语义入口与 bitmap 入口共八个方法；两个语义的显式边适配另有四个入口，使用方扩展类合计十二个方法。

## 手写指令与结果消费

`HandwrittenBitmapCollision.OneWayExecution<TOutput>.Execute` 直接读取 `CollisionLayer.AllowedCategories` 和另一方的 `Categories`，在 `IL_0070` 执行 `and`，随后 `brfalse.s` 跳过该候选。`MutualExecution<TOutput>.Execute` 在 `IL_0070`、`IL_0081` 分别执行两个方向的 `and`，各接一个拒绝分支。两者随后通过 `constrained. !TOutput` 写入实际实体对输出。

固定候选例子的三侧结果分别写入同格式的 Span 输出，身份行与独立字面期望一致。单向结果的逐实体接触次数为 3、3、2，双方结果为 2、2、0。`CountContacts` 对每个接受行的两个实体位置累加，构成后续统一 benchmark 可复用的实际消费步骤。
