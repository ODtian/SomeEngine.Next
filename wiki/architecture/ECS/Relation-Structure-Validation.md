# Relation/structure  Parent: [[review/ECS-Validation|ECS Validation Checklist]] 

- first/last edge、exclusive uniqueness、source/target destroy；
- outgoing/incoming generation borrow与并发 publication；
- `Parallel/UniquePair/UniqueSource/UniqueTarget/OneToOne` create与retarget collision matrix；同pair parallel edges保持不同identity、payload和lifetime；
- directed source/target role与undirected incident graph；undirected reversed-pair uniqueness、parallel edges、degree-one、self-edge和single-edge canonical truth；
- undirected endpoint slot与payload A/B association在create/retarget/serialization后保持；禁止用两条directed edges模拟或暴露undirected source/target API；
- directed outgoing/incoming与undirected两端incident order完全独立；同一edge两端位置可不同；edge-global order API不存在；
- ordered adjacency create/retarget placements、anchor/block/permutation与policy conversion；unordered instrumentation为零order-key bytes、零renumber、零ordered inner-loop dispatch且枚举无稳定承诺；
- single-edge destroy/retarget只接受`RelationEdge<T>`；endpoint-pair旧API在public reference与binary API shape中均不存在；explicit `DestroyAll...` bulk无歧义；
- direct edge query一edge一row，outgoing/incoming endpoint semi-join不重复endpoint；adjacency borrow与payload component access使用独立resource declarations；
- relation topology通过protected endpoint/order components、hierarchy topology通过public Parent与read-only derived Children的Added/Changed/Removed + 当前保守system query window消费；未来per-consumer checkpoint只可收窄重复，public API与metadata中仍不存在RelationChanges、HierarchyChanges、ParentDelta或其他topology delta stream；
- stable-key parallel command merge重复运行 byte-for-byte一致，无 key recorder在 deterministic mode拒绝；
- stable sequential candidate replay覆盖 SetParent/Destroy/DestroySubtree/Reorder/deferred create/relation retarget全部组合；
- prepare/hash/equality/allocation fault后 old root不变；一次structural transaction的root swap原子发布其prepared canonical/derived/journal/epoch，同时允许另行声明的Deferred Parent在maintenance前领先Children；
- producer/descendant fault使 buffer Failed并丢弃 partial segments；
- deferred identity mapping、single playback、Dispose不改 live World。

