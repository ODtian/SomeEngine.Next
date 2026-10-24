# Fault、cancel 与 cleanup  [[architecture/ECS-Architecture|ECS Architecture]]


- `RequireSuccess` predecessor fault/cancel：body 不运行，pins释放，semantic successor fault/cancel。
- `AfterCompletion` predecessor fault/cancel：等待 scope terminal后仍运行，用于 retry/cleanup而非读取有效结果。
- hazard predecessor fault：successor 等其 WorkReleased 后继续。
- cancel before admission：不进入 frontier。
- cancel while waiting hazard：owner 从 frontier 释放并终结；later owners继续。
- cancel while running：只能协作式，body 返回前不能释放内存。
- child fault：parent WorkReleased 不受影响，parent ScopeCompleted fault。
- generic cleanup/release 设计为 nonthrowing；内部 scheduler failure记录为 owner fault但 terminalization必须继续。
- structural root swap开始后 cancellation延后；retire/observer fault隔离报告。若检测到已发布 root自身损坏，World进入 Poisoned/Fatal并拒绝 access，不能继续暴露可能不一致状态。

