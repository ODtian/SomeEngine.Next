# Memory Safety Guards

## Definition
防止真实越界写、非法 generated index 或固定容量 worklist 破坏内存的最小 guard。

## Invariants
- shipping 必须保留需要的 guard。
- 它不是 programmer-error validation。
- 如果算法组织已经证明写入范围安全，不额外增加无收益分支。

## Relations
[[architecture/cluster/Worklists]] · [[architecture/cluster/Overflow]]