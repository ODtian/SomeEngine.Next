# Dynamic buffer  Parent: [[Workload-Rules]] 

buffer access拆为 chunk header、per-entity payload block和allocator arena。resize需要该 entity buffer Write + allocator access，并增加 per-buffer resize epoch；任何此前 element borrow下一次 dereference都拒绝。公开可 resize wrapper不同时导出长期 raw Span；raw Span callback拿不到 resize capability。disjoint buffers可用 per-worker slabs并行增长，同一 buffer永远只有一个 writer。

