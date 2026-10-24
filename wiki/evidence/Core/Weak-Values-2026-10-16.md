# WeakValues 性能与行为证据（2026-10-16）

对应 [#218](https://github.com/ODtian/SomeEngine.Next/issues/218) 与 [WeakValues 使用及清理说明](../../architecture/Weak-Values.md)。实现基线为 `f6c0f95d`，测量使用本次 `WeakValues<TKey, T>` 实现。

## 条件与命令

Intel Xeon E5-2698B v3，16 核、32 线程；Windows x64；.NET SDK 10.0.113，.NET 10.0.13；Release，`DOTNET_TieredCompilation=0`。四种命中路径采用同样的锁、4,096 个 int 键、同一组业务对象、每轮 1,048,576 次伪随机访问，预热后各取 9 轮。读取对象中的整数并累加以维持实际消费。

通过公共 FIFO 队列提交构建与运行。任务 `a16465ff14794cbbb313ad02d587a18e` 在 2026-10-16 20:09:28–20:09:37（Asia/Shanghai）完成，退出码 0：

```powershell
$env:DOTNET_TieredCompilation = '0'
& F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1 dotnet run --project tools/SomeEngine.Core.Benchmarks/SomeEngine.Core.Benchmarks.csproj -c Release
```

队列入口复用本机主工作区工具，命令的工作目录及编译产物均在 `asset210-values/SomeEngine.Next`。测量程序保存在 `tools/SomeEngine.Core.Benchmarks/WeakValuesMeasurements.cs`，测量结果整理到本页后清理队列临时输出。

## 存活命中

| 取得方式 | 中位耗时 | 最小–最大 | 9 轮命中托管分配 |
| --- | ---: | ---: | ---: |
| 强引用 Dictionary | 25.79 ns | 24.98–26.21 ns | 0 B |
| WeakReference Dictionary | 38.36 ns | 37.55–40.20 ns | 0 B |
| 内嵌 WeakGCHandle Dictionary | 26.63 ns | 26.23–27.48 ns | 0 B |
| WeakValues 公开 GetOrBegin | 30.28 ns | 29.72–31.83 ns | 0 B |

`WeakValues` 的计时包括自身锁、完整键查找、`TryGetTarget` 和同步 `ValueTask<T>` 取得。其它三组采用同样的独占锁口径。此前 #210 原型的连续记录索引命中中位为 29.71 ns，本次引擎实现采用相同记录数、访问次数、轮数及 JIT 配置。

## 托管与原生费用

全部对象在测量前已建立，并由独立数组保活。字典构造及填充的托管分配分别为：强引用 136,240 B，WeakReference 234,544 B，直接 WeakGCHandle 136,240 B。WeakReference 包装产生 98,304 B，即每条 24 B。

短 `WeakGCHandle<T>` 在 x64 上为 8 B，直接存入记录。int 键记录为 24 B，桶项为 4 B；4,096 容量的两份数组合计 114,736 B（包含数组头和对齐）。`WeakValues` 完整构造与逐个创建的累计托管分配为 622,960 B，完成 GC 后托管保留增加 115,120 B。累计分配包含逐次创建的临时共享任务及扩容过程中替换的数组，GC 后保留包括最终数组、索引对象、同步对象及空创建表的可复用容量。

原生句柄表单独测量：先建立 65,536 个句柄的托管数组及同一目标对象，再记录句柄创建前后的进程私有内存。句柄循环托管分配为 0 B，进程私有内存增加 557,056 B；创建耗时 3.039 ms，统一释放耗时 4.199 ms。该私有内存差值反映本次进程的原生句柄表页提交及相关原生记账，按整段记录。句柄数组的托管费用在计时前发生。对象本身的业务数据由独立普通引用维持。

## 四条分批检查与扩容

每组先保存 65,536 个不同对象，分别保留全部、前一半或清空普通引用，然后执行 GC 和终结等待。单次 `GetOrBegin` 包含一次真正创建的登记及最多四条检查。其后的 `CompleteAsync` 单独测量发布和可能的数组扩容。各组 9 轮：

| 初始状态 | 创建登记中位 / 最大 | 登记平均托管分配 | 发布中位 / 最大 | 发布平均托管分配 |
| --- | ---: | ---: | ---: | ---: |
| 全部存活 | 4.7 / 6.1 μs | 96 B | 616.2 / 806.5 μs | 3,670,064 B |
| 一半已回收 | 14.7 / 754.6 μs | 96 B | 4.1 / 5.2 μs | 0 B |
| 全部已回收 | 10.8 / 17.5 μs | 96 B | 3.5 / 5.8 μs | 0 B |

全部存活组的第 65,537 条值触发容量从 65,536 增至 131,072。3,670,064 B 对应新记录数组和桶数组。两组回收样本复用了检查释放的记录；发布部分保持 0 B 托管分配，短弱句柄继续计入原生句柄表。

一次发布之后，再测量 32,767 次不同键的完整登记和发布；与前一次合计 32,768 个创建批次，覆盖全部原始记录的检查。后续批次合计中位耗时：全部存活 7.359 ms，一半回收 9.054 ms，全部回收 10.883 ms。该工作量同时包含共享任务登记、句柄释放、新弱句柄创建、记录复用和新记录插入；它表达持续取得时的完整清理推进费用。单批的四条预算约束检查数量，桶链删除、原生句柄释放和系统调度共同进入实际耗时。

## 行为检查

```powershell
dotnet test tests/SomeEngine.Core.Tests/SomeEngine.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~WeakValuesTests -v minimal
```

9 项通过，行为规则检查 0 违规。公开行为包括：存活值保持同一引用；32 个并发读取共享完整打开/读取/创建；异常和取消统一传播并可再次创建；等待者取消后共享创建继续；GC 后重新创建；后续创建释放旧键并复用记录；128 个 Guid 在增长后保持各自的值；索引整体回收后普通对象引用继续可用。

清理行为的旧键采用普通对象和外部弱引用观察，桶链冲突通过相同哈希的测试键产生；断言由公开取得结果和旧键的可回收性表达。同步范围、句柄清空/释放顺序、四条预算包含空记录和访问期保活按实现核对。
