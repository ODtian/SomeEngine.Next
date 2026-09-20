# Determinism  [[architecture/ECS-Architecture|ECS Architecture]]


- 当前不把网络同步、lockstep、rollback或跨平台任意 simulation bitwise equality作为产品要求。
- ECS 必须保持 synchronization-ready：stable logical entity/producer keys、canonical serialization、replayable structural journal、deterministic structural ordering和显式 fixed-partition/fixed-merge execution points从现在起就是硬边界。
- 未来同步层只增加 ordered input stream、state hash/snapshot、rollback storage、deterministic math/FP policy和sync-specific systems；它不能要求替换 Resource Owner、Semantic Dependency、QueryHandle、Relation Edge、Parent/Children或Structural Transaction模型。
- coordinator 的 stable pipeline key/order稳定；并发外部 callers 的 admission order不承诺确定。
- independent entity outputs 和 hierarchy subtree outputs必须 order-independent。
- parallel commands按 coordinator/caller提供的 stable logical key和parent-derived producer path merge；deterministic mode拒绝无 key 的并发 external recorder。
- ordered hierarchy/relation views按其canonical semantic sequence枚举；unordered live views明确无顺序承诺。canonical serialization、state hash、stable error selection和显式sorted/debug view在scratch按stable logical id排序，不把稳定枚举成本转嫁给unordered hot path。
- floating reduction默认不宣称 deterministic；replay/network模式使用固定 partition tree。
- 多 fault 聚合按 logical job id排序，不按到达时间。

为避免阻塞未来同步，canonical state和journal禁止包含 worker id、线程时序、内存地址、dictionary enumeration order或未捕获的 ambient clock/random input。scheduler可自由并行和work-steal，但其 timing不可进入逻辑 identity或serialized state。

