# Borrow/query  Parent: [[review/ECS-Validation|ECS Validation Checklist]] 

- sync foreach整个枚举持有 owner，break/exception可靠 Dispose；
- async view只在 body/batches内创建；
- enumerator copy、manual early Dispose、manual non-dispose、borrow copy和owner外 ref/span extraction全部拒绝；
- `ref T + indirect RO lookup<T>`、buffer/index/relation等同-family cross-packet self-alias在 SG负例中拒绝；
- stale World/storage/snapshot generation拒绝；
- nested subset borrow合法，upgrade/additional access拒绝；
- schedule-after-wait race不能与同步 ref/span 重叠；
- Changed writer-predecessor late-start、out-of-order completed epochs、enablement/membership变化不漏报；
- future checkpoint gate：copied `QueryCheckpointHandle`共享同一terminal lane，fault后at-least-once retry不形成永久fault chain；
- future checkpoint gate：same-scope descendant复用checkpoint、pending K consumer依赖parent再由parent child复用K时确定性拒绝，不形成scope环；
- dynamic-buffer resize使旧 element borrow失效，disjoint buffer growth仍可并行；
- indexed bulk write success/fault都发布与实际 values一致的 affected shards；
- managed object alias、named executor mismatch、mutable singleton和event producer fault按声明规则拒绝/终结。

