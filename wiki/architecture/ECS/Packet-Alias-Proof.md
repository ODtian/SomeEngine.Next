# Owner 内 packet alias proof  Parent: [[ECS-Admission-and-Ranges]] 

SG 为每个 parallel adapter建立独立于 ResourceManager 的 alias matrix：

- current-row/current-chunk direct access 可由 stable packet partition证明唯一；
- 某 family 有任一 direct packet Write 时，同 family 的 indirect whole/shard Read 或 Write 默认拒绝；
- indirect Write 只有 target-to-packet mapping 可机械验证、且所有 aliases落在同一 packet时允许；
- direct signature 中同 family 的 RO/RW重复 alias拒绝，而不是给用户两个指向同一元素的参数；
- immutable snapshot 是不同 resource identity，可用于 read-old/write-new two-pass；
- atomic、reduction、append 使用各自认证的 `ParallelWriter`；
- hierarchy parent-before-child ready-frontier 是专用 internal proof，不能泛化给普通 query。

因此 `ref Position current + ComponentLookupView<Position> all` 即使 lookup 只读，也不能 `ScheduleParallel`；用户改为 snapshot、two-pass、单线程 `Schedule/Execute` 或有证书的 partitioned lookup。

