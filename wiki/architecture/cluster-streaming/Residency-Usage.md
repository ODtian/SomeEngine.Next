# Residency Usage

Usage feedback 表示已经 resident 的 page 是否近期被使用，用于 eviction policy；它与缺页请求无关。

## Invariants
- fault 负责请求缺页，usage 负责 residency/eviction policy。
- usage 可以延迟读取。
- quality、time、memory budget 由 Cluster 上层提供；streaming 不因 queue overflow 临时改变 LOD 算法。