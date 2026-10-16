# WeakValues

`SomeEngine.Core.Collections.WeakValues<TKey, T>` 按取得键共享存活的普通对象，并合并同键正在进行的完整创建。`TKey` 保存取得条件，`T` 是引用类型。业务通过普通引用持有取得的值。

## 取得与创建

`GetOrBegin(key, out completion)` 返回 `ValueTask<T>`。已有值以同步结果返回；创建中的请求取得同一个共享任务。唯一发起者同时得到 `TaskCompletionSource<T>`，并把完整创建方法返回的 `ValueTask<T>` 交给 `CompleteAsync`：

```csharp
ValueTask<Shader> result = shaders.GetOrBegin(key, out var completion);
if (completion is not null)
    result = shaders.CompleteAsync(key, completion, OpenReadCreateAsync(files, key, creationCancellation));
return await result.ConfigureAwait(false);
```

完整创建方法覆盖打开来源、读取 Block 和创建实际值，由具体业务在索引锁外直接调用。方法使用 `async ValueTask<T>` 表达异步成功、异常与取消结果。索引在同一同步范围内登记结果、移除创建工作并通知所有等待者；失败和取消后的下一次取得重新发起创建。

创建取消由发起工作的业务范围控制。等待者可对共享结果调用 `AsTask().WaitAsync(waitCancellation)` 结束自身等待；普通同步命中可直接消费 `ValueTask<T>`，保留零分配路径。具体 Shader、Texture 的完整键和创建入口见 [Asset-Objects](Assets/Asset-Objects.md)。

## 数据与清理

桶数组保存链头，连续记录数组保存键、哈希、后继和内嵌的短 `WeakGCHandle<T>`。完成记录各持有一个键和一个句柄。进行中的创建集中保存在索引的临时任务表中，任务发布后移除；数组及临时表的容量保留为可复用的高水位。

普通命中只进行键查找和 `TryGetTarget`。查询发现目标已被 GC 回收时，立即删除该条记录。每次真正发起创建，再从上次检查位置顺序检查最多四条既有记录；空闲记录也占一条检查预算。检查推进与对象 GC 分别发生在取得操作和托管回收路径上。

删除在同一把锁内摘除桶链、清空当前记录句柄、把记录接入空闲链，再释放原句柄一次。记录复用继续使用原数组容量。四条预算限制的是记录检查数量；桶链摘除的工作量随对应链长变化，扩容的工作量随数组数据量变化。空闲期间，待检查记录及其键继续由索引保存。

查询、删除、扩容及终结释放共用同步范围。公共句柄访问结束处通过 `GC.KeepAlive(this)` 维持索引的存活期。索引自身被 GC 回收时，内部终结路径统一释放其余弱句柄。业务仍持有的值继续按普通引用使用。

行为检查通过普通对象和公开入口验证复用、创建交接、失败/取消重试、回收、分批清理及增长。记录布局、四条检查预算及一次释放顺序由源码核对。命中、托管保留、原生句柄表及清理成本分别记录在 [2026-10-16 性能证据](../evidence/Core/Weak-Values-2026-10-16.md)。

.NET 的短弱句柄目标和释放行为参见 [WeakGCHandle<T>](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.weakgchandle-1?view=net-10.0)。
