# Descriptor rollover replays active state

## What to review

审查 shader-visible descriptor heap rollover 是否由 active binding state 和 CPU descriptor source驱动，而不是把旧容量魔数增大或依赖 bindless。

## Pass conditions

- resource/sampler heap exhaustion 会分配新 heap、切换 command-list heaps、重新 materialize并重绑所有 active tables。
- 旧 heap 在引用它的 command completion 后才复用/销毁；多个 command context彼此隔离。
- 超过 4096 resource 和 256 sampler 的压力测试跨 rollover 边界校验真实输出与 descriptor provenance。

## Fail conditions

- 固定大数组、无限增长、容量不足直接抛异常仍是正常终态。
- rollover 后只重放最近一次 write，漏掉仍 active 的 group/table。
- heap、CPU descriptor、view/resource 生命周期与 GPU completion 脱节。

## NEEDS_GRILL

只有当 public binding model必须实质改变 accepted traditional/bindless边界时使用 `NEEDS_GRILL:`。
