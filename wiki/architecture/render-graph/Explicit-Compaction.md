# Explicit Compaction

## Definition
大范围 transient memory 重排只能由调用者在明确允许停顿或大范围变化的边界主动触发。

## Invariants
- 普通 structural commit 不自动 compact。
- compact 可以改变大量 transient placement，因此调用本身必须显式。
- 适合 loading、idle 或其它已知允许大范围重排的阶段。

## Relations
[[Transient-Placement]]