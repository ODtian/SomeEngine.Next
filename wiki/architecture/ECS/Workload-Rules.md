# 其他 workload 规则  [[architecture/ECS-Architecture|ECS Architecture]]


| Workload | owner/access | 并行规则 |
|---|---|---|
| unmanaged entity-wise POD | topology Read + matching chunk ranges | stable chunk batches；packet alias proof |
| chunk/SIMD | matching chunk ranges | spans 仅在 generated callback 内创建 |
| dynamic buffer | topology + header/payload/allocator accesses | 每 buffer 唯一 writer；resize epoch；thread-local/slab-safe allocator |
| sparse component | stable entity/shard ranges | add/remove membership走结构事务；value write按 shard/range |
| shared component | read按 shared/query ranges | value导致 relocation时走结构事务 |
| indexed component | component range + derived index write | per-batch journal，owner release 前 deterministic publish；不能 hidden lazy stale read |
| enablement | candidate chunk bit ranges | filter先拥有完整 candidate ranges；disjoint SetEnabled可并行 |
| random lookup RO | topology Read + whole component family Read | 仅当 owner内没有同-family packet Write |
| random lookup RW | whole component family Write | parallel 默认拒绝，除非专用已证明 partition |
| relation payload | edge chunk ranges | endpoints不变时普通并行值写 |
| structural recording | private command segment | producer间完全并行，不触碰 live topology |
| structural playback | topology Exclusive | stable merge、全批验证、原子 publication |
| reduction | per-partition accumulator | fast tree 可非确定；deterministic 模式固定 partition/merge tree |
| managed reference | whole family或 object-identity resource | 默认不并行 mutable object；外部 alias必须登记 |
| thread-affine | named executor + resource owner | route/validate main、render或device executor，不等同任意 caller |
| singleton/global | dedicated whole resource | mutable singleton不能被普通 packets并行写 |
| chunk/meta component | stable chunk-meta ranges | 每 chunk唯一 writer，跨 chunk并行 |
| event stream | private producer segments | stable logical key merge；与 command相同的 producer fault规则 |
| atomic/commutative | certified provider `ParallelWriter` | 只允许声明过的 operation；determinism另行选择 |
| GPU/IO | external owner | submit 前 admission，fence 后 release |
| cross-frame | scope/domain 持续存在 | 无 frame barrier；freshness 使用 explicit handle |
| multiple Worlds | sorted composite prepare | 按 stable World identity取得 permits/sequencers并原子 seal |


## Related concepts

- [[Enablement|Enablement]]
- [[Indexed-Components|Indexed component]]
- [[Dynamic-Buffers|Dynamic buffer]]
- [[Managed-and-Shared-Aliases|Managed/shared aliases]]
