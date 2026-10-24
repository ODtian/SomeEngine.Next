# CPU 字节缓存索引研究

2026-09-30。状态：研究结论，未修改缓存实现，也未替代现行 Asset 规范。

后续实际测量见 [2026-10-01 缓存验证](AssetByteCache-Verification-2026-10-01.md)：哈希布局降低稀疏索引空间，但比编号数组更慢；本轮混合重放中 LRU 通过减少重读取得更短总时间。固定表仍是研究参考，不作为默认实施选择。

本轮比较论文与开源代码，选定 **RocksDB HyperClockCache 的固定表版本**作为深入研究对象。没有找到同时满足全部要求、可以直接用于本项目的实现，尤其没有证据支持“每条缓存引用的内存和访问成本均不超过普通强引用”。最接近不等于完全符合。

## 需求边界

缓存负责额外保留可重新读取的 CPU 字节数组。消费者持有普通强引用；缓存淘汰只撤销缓存的保留，消费者已经取得的数据仍有效。预算约束缓存保留，不约束所有消费者持有的数组总量。

不同数据的描述和读取方式保留各自语义；缓存按字节统一计量，单条数据独立淘汰。索引应避免逐条节点对象、链表维护和遍历全部资产声明的数据。未保存且无法重新取得的唯一数据不属于可淘汰缓存。

## 筛选

| 候选 | 与需求有关的做法 | 未直接采用的原因 |
|---|---|---|
| SIEVE，NSDI 2024 | 命中设置访问标记，延迟到淘汰时处理顺序 | 论文算法仍使用双向队列；减少命中维护不等于消除节点和链接。见论文 §3.2、§4.4 |
| MemC3，NSDI 2013 | 数组桶、cuckoo 查找、CLOCK 标记 | 针对原生键值对象与 slab 分配；不是现成的 C# 强引用缓存。论文的部分容量实验另计哈希表空间，不能直接拿容量数字作总内存结论 |
| Linux XArray | 适合聚集整数编号的稀疏索引和标记遍历 | 本身不是完整预算和淘汰实现；有树节点与多层寻址，不能等同于普通数组读取 |
| BitFaster.Caching ConcurrentLru | C# 缓存，命中与队列整理分开 | 使用 ConcurrentDictionary、LruItem 类和三个 ConcurrentQueue，仍有逐条对象与额外索引结构 |
| FixedHyperClockCache | 连续记录数组同时支持查找、计费和 CLOCK 淘汰 | 最接近索引布局要求；原生引用计数、固定记录大小和预算分片仍需区别对待 |

来源：[SIEVE 论文](https://www.usenix.org/system/files/nsdi24-zhang-yazhuo.pdf)、[MemC3 论文](https://www.usenix.org/system/files/conference/nsdi13/nsdi13-final197.pdf)、[XArray 文档](https://docs.kernel.org/core-api/xarray.html)、[BitFaster 核心](https://raw.githubusercontent.com/bitfaster/BitFaster.Caching/main/BitFaster.Caching/Lru/ConcurrentLruCore.cs)、[BitFaster 条目](https://raw.githubusercontent.com/bitfaster/BitFaster.Caching/main/BitFaster.Caching/Lru/LruItem.cs)。

## 深入对象与版本

RocksDB 检查版本固定为 `31675ca6d02ce5741f210646665258e21a7ccf4d`。固定表与自动扩容表不能混称为同一种索引布局。

固定表使用开放寻址和双重哈希，记录存在一个预分配数组中。自动扩容表虽然也把记录放在数组中，查找仍沿整数链接组成的链进行，不能宣称消除了链式寻址。见 [clock_cache.h](https://raw.githubusercontent.com/facebook/rocksdb/31675ca6d02ce5741f210646665258e21a7ccf4d/cache/clock_cache.h) 中 FixedHyperClockTable、AutoHyperClockTable 的定义。

正常固定表记录不逐条分配；插入失败的部分路径可以生成独立分配的记录，所以不能把它概括成所有情况下零条目分配。见 [clock_cache.cc](https://raw.githubusercontent.com/facebook/rocksdb/31675ca6d02ce5741f210646665258e21a7ccf4d/cache/clock_cache.cc) 的 StandaloneInsert。

## 索引与访问

固定记录包含 16 字节键、数据指针、释放函数辅助信息、字节计费、原子状态、探测位移计数等；记录按 64 字节对齐，代码用 static_assert 确认大小为 64 字节。记录之间没有 LRU 的前后链接。见 [记录定义](https://raw.githubusercontent.com/facebook/rocksdb/31675ca6d02ce5741f210646665258e21a7ccf4d/cache/clock_cache.h) 与 [构造和大小检查](https://raw.githubusercontent.com/facebook/rocksdb/31675ca6d02ce5741f210646665258e21a7ccf4d/cache/clock_cache.cc)。

Lookup 根据键探测数组、取得原子读引用并比较键。Release 通常做一次原子计数更新；访问热度与引用计数共用状态字，不需要在每次命中时移动链表节点。淘汰递减未被引用记录的热度，热度耗尽后才回收。上述路径见 [Lookup、Release、ClockUpdate](https://raw.githubusercontent.com/facebook/rocksdb/31675ca6d02ce5741f210646665258e21a7ccf4d/cache/clock_cache.cc)。

这里的“热度维护便宜”利用了原生缓存本来就需要的引用计数。C# 普通强引用没有对应的访问计数，不能声称移植后这项维护免费。

## 容量与内存计算

设 C 为固定表容量，R 为当前缓存条目数，N 为所有资产声明的单位数。固定表记录内存为 64C 字节，主体索引空间是 Θ(C)，与 N 无直接关系；它也不是 Θ(R)，空表仍保留预分配容量。

根据所检查版本的 CalcHashBits，启用完整元数据计费且忽略极小缓存的特殊处理时，估算表大小为：

```text
需要的记录位置数 = ceil(预算 / (0.7 × 预计平均数据字节数 + 64))
C = 将需要的记录位置数向上取整为 2 的幂
固定记录数组内存 = 64 × C
```

例如预算 512 MiB、预计平均单位 64 KiB：需要 11687 个位置，向上取整为 16384，记录数组为 1 MiB。即使所有资产声明了 100 万个 tile，该表也不会因此变成 100 万个位置。这是源码公式计算，不是引擎性能测试，也没有把原有资产描述、全局对象、任务、数据分配头计入这个 1 MiB。见 [CalcHashBits 和表构造](https://raw.githubusercontent.com/facebook/rocksdb/31675ca6d02ce5741f210646665258e21a7ccf4d/cache/clock_cache.cc)。

预计平均大小错误时，表的条目数量限制可能先于字节预算生效。大单位占主导的缓存可以摊薄索引费用；不能由此推出小单位同样便宜。64 字节记录也远大于一个 8 字节强引用。

查找在散列分布和负载合适时平均 O(1)，最坏 O(C)。CLOCK 候选扫描访问本表，避免遍历全部 N 个声明单位；扫描仍可能检查整个 C。删除还要沿该键的探测路径调整位移计数，不能把一次回收写成严格 O(1)。这些是从 FindSlot、Evict、Rollback 推导的复杂度，不是实测延迟结论。

## 预算与外部引用

默认元数据计费策略包含缓存管理空间；预算本身可以配置为严格或允许超额。原版将容量均分给散列分片，分片之间没有自动借用容量。配置 num_shard_bits = 0 可以得到一个共享表和预算。见 [配置](https://raw.githubusercontent.com/facebook/rocksdb/31675ca6d02ce5741f210646665258e21a7ccf4d/include/rocksdb/cache.h) 与 [分片容量计算](https://raw.githubusercontent.com/facebook/rocksdb/31675ca6d02ce5741f210646665258e21a7ccf4d/cache/sharded_cache.cc)。不能把默认分片配置说成完全可互借的全局预算池。

原生接口通过 Ref/Release 保证寿命；Erase 后，已有引用对应的数据继续保留。普通 CLOCK 淘汰跳过仍被引用的记录；它没有直接实现“消费者继续持有数组，但缓存记录可立即撤销并复用”的 C# 语义。见 [Cache 接口](https://raw.githubusercontent.com/facebook/rocksdb/31675ca6d02ce5741f210646665258e21a7ccf4d/include/rocksdb/advanced_cache.h) 和 ClockUpdate。

预算紧张时的额外回收工作也是明确的工程问题，配置中有回收工作限制；严格容量设置与该限制存在取舍。不能同时默认插入总成功、容量永不超额、回收工作极小。见 [eviction_effort_cap](https://raw.githubusercontent.com/facebook/rocksdb/31675ca6d02ce5741f210646665258e21a7ccf4d/include/rocksdb/cache.h)。

## 对本项目的判断

以下是根据上述实现作出的适配判断，尚不是完成的移植设计。

- 值得验证的是按缓存容量分配的统一驻留表，而不是先为每个 texture 的全部 tile 分配缓存引用数组。已加载的消费者继续直接访问自己持有的数据；只有重新取得数据时进入缓存查找。
- Texture、Mesh 等语义仍由资产描述解释；缓存只接受不可变字节来源身份、单位编号和字节数组。需要先设计不会混淆版本、不会破坏未修改数据共享的键绑定，不能把短哈希当成唯一身份。
- C# 可以让消费者直接持有 byte[]，缓存记录清掉自己的引用后，数组按普通 GC 语义继续存活。消费者不应持有可被复用的缓存记录；GC 也不会替代键与数组一致读取所需的同步。
- 原版的原生引用计数、释放函数、64 字节对齐和独立分配的回退记录不能原样认定为本项目必需开销。去掉这些字段后，并发读取、记录复用和热度维护仍需设计与验证。
- 缓存属于可选保留：读出的数据无法加入缓存时，应继续返回给调用者。这是本项目语义推导，不是直接照搬原生 Insert 失败行为。

因此，先前“每个 texture 一个完整索引数组”的默认推荐缺少依据；本轮选择研究固定全局驻留表，是因为它把缓存管理规模限制到缓存容量。若要求仍严格保持每条引用的内存和访问成本不增加，该实现不能通过验收。现在没有依据承诺另一个有键查找、热度更新和并发复用的实现能通过同一要求。

原项目的工程记录还指出，仅换成预分配开放寻址表没有解决其主要成本，锁和 LRU 维护更关键。这来自特定 RocksDB 工作负载，不能作为本引擎的性能结果。见 [HyperClockCache 开发记录](https://github.com/facebook/rocksdb/issues/10306)。

本轮只做资料阅读、固定版本源码核对和容量算术，没有引入依赖、修改引擎实现或运行场景性能验收。网络源码在进程内读取，没有留下临时源码或探针文件。
