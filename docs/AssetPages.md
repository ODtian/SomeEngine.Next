# 资产编辑页与标准缓冲区

本页描述 `SomeEngine.Serialization.Containers` 中已经实现的通用存储。资产声明和业务转换仍由 Assets 层负责；这里不定义资产身份、发现、类型登记或业务对象镜像。

## 内容与使用期间

`PageArena` 将托管数组按元素类型与页尺寸分组分配。`PageBuffer<T>` 保存当前分支的索引根；`PageBuffer` 是字节特化。`Duplicate()` 只保留一个根，不复制所有页引用。修改共享内容时沿索引路径分离节点和目标页。`PageList<T>` 提供值、普通对象引用和完整元素赋值；引用存入真实的 `T[]`，不装入裸字节页。它隔离元素赋值，不深复制任意可变 CLR 对象。资产生成在声明侧检查可共享值：当前源码可检查私有状态，引用程序集需要其定义端生成的 `BinaryValue` 证明。

```csharp
using var arena = new PageArena(sharedResidency);
using var points = new PageList<Point>(arena);
points.Add(new Point(1, 2));
using var branch = points.Duplicate();
branch[0] = new Point(3, 4);

branch.Write(0, branch.Count, (start, values) =>
{
    for (int i = 0; i < values.Length; i++)
        values[i] = values[i] with { X = values[i].X * 2 };
});

readonly record struct Point(float X, float Y);
```

普通读取返回值，不创建每元素代理。`PageList<T>` 的结构修改与普通 BCL 可变集合一样需要调用方串行化；不同分支可以分别使用。底层 `PageBuffer` 在每个公共访问边界串行化操作。

批量写入口按受影响页调用同步 `PageWrite` / `PageWrite<T>`，参数是 `scoped Span`。回调期间拒绝在同一分支上读取、再次写入、复制、取得范围、重绑定或释放。回调不能跨 `await`；也没有返回可长期保留的 writable ref 或 Memory 的接口。这个边界阻止通过受支持的 C# 写法先取得可写内存、共享分支后再继续写旧内存。回调抛出异常时，已经执行的修改保留，写入期间标记会解除。

`PageAccess` 协调一个内容分支的多个缓冲区。资产的标量、引用、列表和大块共用它；批量写期间也会拒绝对兄弟字段进行复制、保存或重绑定，跨线程同样成立。回调执行时不持有外层锁，允许另一个线程及时得到拒绝，避免回调等待该线程造成死锁。

`PageRange<T>` 是一次明确的读取保留。`Retain(offset, length)` 只保留相交页并建立一次 `ReadOnlySequence<T>` 适配，后续遍历复用它。泛型范围的偏移与长度按元素计。一个小范围仍会保留其相交页及这些页所在的数组；普通 `Memory` 切片不会减小底层数组。

```csharp
using PageRange<byte> range = buffer.Retain(offset, length);
foreach (ReadOnlyMemory<byte> segment in range.Sequence)
    Consume(segment.Span);
```

关闭原分支、清理缓存或关闭竞技场不会复用仍被该范围保留的页。`PageRange.Dispose()` 结束此前取得的所有 Memory、Span 和 sequence 的允许使用期间；在此之后继续保存 CLR 引用不会阻止池复用。需要脱离期间自由保存的普通数组时，显式使用 `ToArray()`。竞技场关闭后不接受新分配，既有保留的结束仍负责归还对应使用量。

## 编码和文档

`PageBufferWriter` 实现 `IBufferWriter<byte>`。它向编码器提供可复用的 GC 托管暂存空间，在 `Advance` 时只把本次确认的范围写入页。完整编码值不会先暂存到一份连续数组。暂存与最终页分开，使调用者即使保留旧的 writable Memory，也不能修改已保留的页或另一个分支。

```csharp
using var output = new PageBufferWriter(arena);
BinarySerializer.Write(output, value);
using PageRange<byte> encoded = output.Retain();
var decoded = BinarySerializer.Deserialize<MyValue>(encoded.Sequence);
```

需要指定最终集合存储时，使用 `DeserializeInto` 或 `BinaryDocument.ReadRootInto`。可写的生成类支持这个静态入口；已有可写 `IList<T>` 会直接清空并填充，例如一个 `PageList<Point>`，不会先生成完整 List 再复制进页。目标对象本身也不会重新创建。调用方负责对失败需要隔离的状态使用新对象或独立分支；不能把此入口当成自动原子更新既有业务对象的操作。

通用 `BinaryDataReader` 保留连续 Span 快路径，并支持跨段整数、GUID、UTF-8 和长度前缀。要求连续返回的 `ReadBytes` 若跨段，会只复制本次请求范围并计入读取限制；普通分段读取不拼接完整输入。

`BinaryDocumentWriter.AddChunk` 接受 `ReadOnlySequence<byte>`，现有摘要和 Brotli 写入直接消费各段。写入器借用输入，调用方应把读取保留延续到实际异步写入完成。仍沿用现有文档与包格式、回填、未修改块复制及文件原子替换；分页不定义另一种文件容器。

`PageBufferStream` 为已有 Stream 编码器提供可读写、可定位的分页输入输出。它借用调用方保留的 `PageBuffer`，关闭流不关闭缓冲区；`Length` 表示已提交内容，底层缓冲区可以含额外容量，取得结果时使用 `Slice(0, stream.Length)`。ECS World 编码和 Slang 已编译字节码的读写使用原有编码器，通过该适配直接消费页。

`PageBufferMap` 用共享平衡索引组织以 `ulong` 标识的多个字节缓冲区。分支只保留根；修改先处理共享祖先，再取得目标缓冲区的可写页。它不维护另一套页池或预算。`Asset.Chunks` 用它保存明确提供或取得用于编辑的大块；冷读仍走 `AssetFile` 和既有调度器。保存期间保留所有已修改块的读取范围，未修改的来源块由原文档写入器复制。

## 取得、取消和预算

```csharp
await using var cache = ChunkRequestScheduler.CreateForDocument(document, arena, decodedBudgetBytes);
using var acquired = await cache.AcquireAsync(chunkKey, cancellationToken: cancellationToken);
using PageRange<byte> range = acquired.RetainPages();
using PageBuffer editable = acquired.DuplicatePages();
```

这是现有调度器的页目标接入：每个调度器保留所属文档，按其范围去重，延用优先级、取消隔离、失败重试和淘汰。不同文档与版本不共用裸块键。单段可通过既有 `Memory` 读取；多页内容通过 `RetainPages` 取得 sequence，不隐式拼接。

调度器先共同申请压缩预算和最终解码容量，再把解码预留分配到最终页，期间不释放再重新申请。页按实际尺寸档位计入既有 `ResidencyBudgetLedger.DecodedCpu`，不会由调度器和竞技场重复计费。缓存删除一项时，仍被 `PageRange` 或编辑分支保留的页继续计费，最后一个使用者结束才释放。普通编辑申请额外页时也使用同一账本；若回收清洁缓存后当前页已经独占，可以原地写入。没有空间且仍需要隔离时明确失败，不丢弃未保存内容。

取消等待者只结束该等待；底层读取可能仍继续。关闭调度器会等待真实读取结束后释放目标页，即使某个来源无法即时取消已开始的内存访问。

竞技场统计区分活跃页容量与整个托管数组的保留：`ActiveBytes` 是活跃页容量，`AllocatedBytes` 包含未使用和因少量活跃页仍被保留的容量，`IdleBytes` 是两者差值。`BackingBytes` 统计实际元素数组占用的元素字节，不包含 CLR 对象头；当元素尺寸不能整除页容量时，两种统计可能不同。`RequestedBytes` 累计页申请的请求长度；`CopiedBytes` 只统计 COW 页复制，不包含调用方显式复制或编码器暂存复制。空闲数组复用不作为活跃解码内容重复收费，保留量另行测量并受空竞技场复用限制约束。

## 当前验证与测量范围

2026-09-12 的测试覆盖：不同尺寸与复用后的初始化、共享祖先下的 COW、局部首写和独占后续写、跨页值、结构插入删除、写回调重入、可写别名隔离、范围保留、预算失败、标准编码接入、分页与连续文档输出字节一致，以及调度去重、取消、重试、缓存关闭和版本隔离。

现有 `SomeEngine.Serialization.Benchmarks` 覆盖页参数、分支、局部首写、顺序读取、随机跨页读取、批量写、普通值集合与小范围长期保留负载。加入类型化页和 `PageAccess` 后重新运行：128 B 至 16 MiB 的字节缓冲区分支分配均为 **104 B**，首写只复制目标页；旧版 72 B 不再代表当前实现。4 / 16 / 64 KiB 页的 2 MiB 批量 COW 中位数本次分别为约 1935 / 522 / 1240 μs，不能据单次结果冻结参数。256 KiB 数组配置下保留 1 字节仍可能保留 256 KiB。混合生命周期、异步周转和实际资产负载尚未完成，当前默认参数不是 A11 的最终验收结论。
