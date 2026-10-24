# Open Questions

## ECS 前置能力
- 公开读取某实体某组件的当前写版本。
- 按组件类型的全局最新写版本。
- 计算结束时比较再提交的写入语义（临时副本后 `Replace`，或取引用不自动推进版本、之后显式提交）。
- 查询成员版本。

## 待验证
- `State<T>` seqlock 在写入回调期间读取方的等待成本。
- 视图类型较多时的 archetype 与块占用。
- `ElementTree` 中按插入位置调整顺序的维护成本。
- 依赖检查在 1000 个视图、每个约 5 条依赖下的每帧成本。
- 结构块与列表切换时的分配量。
- 拦截器对 `[Composable]` 函数体内所有产出节点调用的覆盖与生成代码体积。

## 已验证：组件中的托管字段

2026-10-03，[#5](https://github.com/ODtian/SomeEngine.Next/issues/5)，源码基线
`7e487d9d987613e53603871558fb40aa5e99a9d2`。`IComponent` 和 `ISparseComponent`
均支持包含 `string`、实例委托与普通对象引用的 struct。视图参数可以按
[[architecture/someui/Component-Storage|Component Storage]] 的访问频率选择两类存储。

持续验收由 `tests/SomeEngine.ECS.Tests/ManagedComponentTests.cs` 的 12 个用例负责。
公共行为检查集中在 `ManagedComponentChecks.cs`，同时编入正式 ECS NativeAOT 冒烟工程。
检查使用动态创建的字符串、持有独立对象的 `Func<int>` 与另一个普通对象，并经公开 ECS
API 验证其值、引用身份和回收：

| 行为 | 表格入口 | sparse 入口 | 观测结果 |
|---|---|---|---|
| 新增与读取 | `Add`、`Read` | `AddSparse`、`ReadSparse` | 压缩 GC 后保留三种引用和值 |
| 字段引用写入 | `ExecuteQuery`、`QueryRow.ReadWrite<T>` | `ExecuteSparseWrite` | 新引用在第 0 代和全代 GC 后可读，旧引用释放 |
| 整值替换 | `Replace` | `ReplaceSparse` | 新值及引用可读，旧引用释放 |
| 克隆与复制 | `CloneEntity`、`CopyEntity` | 同左 | 浅拷贝保留引用；替换副本字段后各实体可独立修改 |
| 遍历 | `ExecuteQuery`、`QueryRow.Read<T>` | `ExecuteSparseRead` | 返回实体集合与托管字段值一致 |
| 移除 | `Remove<T>`、`ClearRemoved<T>` | `RemoveSparse<T>` | 当前组件移除，保留实体仍可读；删除事实结束后引用释放 |
| 实体销毁 | `DestroyEntity` | 同左 | 其它副本仍持有数据时保留引用，最后一个持有实体销毁后释放 |
| 扩容与组件组合变化 | 1,100 个实体，额外组件的添加与移除 | 1,100 个实体 | 全代压缩 GC 后每个实体的托管字段仍可读 |

表格组件的 `Remove<T>` 把旧值保存在公开的 `Removed<T>` 删除事实中，保留旧引用是该
生命周期的行为；`ClearRemoved<T>` 使用对应事实的 `Version` 清理后，三种引用均可回收。
sparse 组件移除直接结束其值的生命周期。GC 观测使用弱引用；创建值和读取值的辅助方法
采用 `NoInlining`，把辅助方法中的强引用限制在各自调用期间。字段写入前先执行全代 GC，
写入后再执行第 0 代 GC，覆盖已有存储对新引用的写入屏障行为。

命令：

```powershell
dotnet test tests/SomeEngine.ECS.Tests/SomeEngine.ECS.Tests.csproj -c Release --filter FullyQualifiedName~ManagedComponentTests
dotnet test tests/SomeEngine.ECS.Tests/SomeEngine.ECS.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~ManagedComponentTests
dotnet test tests/SomeEngine.ECS.Tests/SomeEngine.ECS.Tests.csproj -c Release --no-restore
just ecs-aot win-x64
```

Release 与 Debug 定向测试各为 12 通过，0 失败，0 跳过；ECS Release 全量回归为
552 通过，0 失败，0 跳过。正式 NativeAOT 冒烟工程在 `win-x64`
发布并执行成功，退出码 0，JSON 输出包含 `managedReferencesVerified: true`；其入口成功
执行同一套 12 个行为检查。运行环境为 `.NET 10.0.12`、Windows `10.0.26200`、X64。

## 已验证：NativeAOT 存储分派

2026-10-03，[#5](https://github.com/ODtian/SomeEngine.Next/issues/5)，源码基线
`7e487d9d987613e53603871558fb40aa5e99a9d2`。环境：Windows x64，.NET SDK
`10.0.112`，NativeAOT 编译器与运行包 `10.0.12`，Release，`win-x64`。

`where T : struct` 的 `default(T) is ISparseComponent` 在四种闭合值类型上均折叠为
常量。表格组件返回 `false`，sparse 组件返回 `true`；含引用字段的组件得到相同结果。
验证函数加 `NoInlining`，并在第二次发布设置
`IlcFoldIdenticalMethodBodies=false`，以逐个查看四个实例的机器码：

| 类型 | 字段 | 返回值 | 方法字节与指令 | map 中的方法长度 |
|---|---|---|---|---|
| `TableNumber : IComponent` | `int` | `false` | `33 c0 c3`：`xor eax, eax; ret` | 3 |
| `SparseNumber : ISparseComponent` | `int` | `true` | `b8 01 00 00 00 c3`：`mov eax, 1; ret` | 6 |
| `TableReferences : IComponent` | `string`、`Func<int>`、`object` | `false` | `33 c0 c3`：`xor eax, eax; ret` | 3 |
| `SparseReferences : ISparseComponent` | `string`、`Func<int>`、`object` | `true` | `b8 01 00 00 00 c3`：`mov eax, 1; ret` | 6 |

正常发布采用编译器默认的泛型方法体合并，两类结果各保留一个相同的方法体。
以上长度来自 ILC map，反汇编中的后续 `nop` 是方法对齐填充。

临时工程引用 `src/SomeEngine.ECS/SomeEngine.ECS.csproj`，入口调用以下四个泛型实例，
检查 `false, true, false, true` 并以进程退出码 0 表示通过：

```csharp
[MethodImpl(MethodImplOptions.NoInlining)]
private static bool IsSparse<T>() where T : struct => default(T) is ISparseComponent;

private record struct TableNumber(int Value) : IComponent;
private record struct SparseNumber(int Value) : ISparseComponent;
private record struct TableReferences(string Text, Func<int> Callback, object Data) : IComponent;
private record struct SparseReferences(string Text, Func<int> Callback, object Data) : ISparseComponent;
```

执行命令（临时工程名 `SomeEngine.ECS.StorageProbe`）：

```powershell
dotnet publish tools/SomeEngine.ECS.StorageProbe/SomeEngine.ECS.StorageProbe.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:IlcGenerateMapFile=true
dotnet publish tools/SomeEngine.ECS.StorageProbe/SomeEngine.ECS.StorageProbe.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishAot=true -p:IlcGenerateMapFile=true -p:IlcFoldIdenticalMethodBodies=false
./tools/SomeEngine.ECS.StorageProbe/bin/Release/net10.0/win-x64/publish/SomeEngine.ECS.StorageProbe.exe
rg 'MethodCode.*IsSparse' tools/SomeEngine.ECS.StorageProbe/obj/Release/net10.0/win-x64/native/SomeEngine.ECS.StorageProbe.map.xml
llvm-objdump --disassemble-symbols='SomeEngine_ECS_StorageProbe_StorageProbe__IsSparse<SomeEngine_ECS_StorageProbe_StorageProbe_TableNumber>,SomeEngine_ECS_StorageProbe_StorageProbe__IsSparse<SomeEngine_ECS_StorageProbe_StorageProbe_SparseNumber>,SomeEngine_ECS_StorageProbe_StorageProbe__IsSparse<SomeEngine_ECS_StorageProbe_StorageProbe_TableReferences>,SomeEngine_ECS_StorageProbe_StorageProbe__IsSparse<SomeEngine_ECS_StorageProbe_StorageProbe_SparseReferences>' tools/SomeEngine.ECS.StorageProbe/obj/Release/net10.0/win-x64/native/SomeEngine.ECS.StorageProbe.obj
```

两次原生执行均退出 0，输出为
`tableNumber=False; sparseNumber=True; tableReferences=False; sparseReferences=True`。
临时工程、map、对象文件和发布产物使用完成后一起清理；上述结论以此编译器版本与 RID
为证据范围。后续框架可以据此采用该存储判断。

## Relations
[[architecture/someui/Performance|Performance]]
