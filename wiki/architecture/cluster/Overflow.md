# Overflow

## Definition
Overflow 是 fixed-capacity work 被拒绝，不是视觉正确性恢复信号。

## Invariants
- 可以 drop work、漏 geometry、产生错误 pixel 或闪烁。
- 不 coarse fallback、不 split/retry、不 rerun、不 inline recompute、不动态降低质量。
- 只保留防止越界所需 guard。
- flag/count/high-water 可以延迟读回，用于未来 frame 调整配置。
- overflow 不形成 same-frame host decision → GPU rerun。
- page-fault queue overflow 可以丢 fault；未来 traversal 再遇到 nonresident page 会重新 fault。

## Relations
[[Worklists]] · [[architecture/rendering/Delayed-Feedback]]