# Immediate RenderGraph restores temporal, export, and capture semantics

## What to review

审查 temporal/history、export/extraction、capture JSON/DOT/replay 和旧 variant语义是否融入当前 frozen canonical graph、compiler、invocation 与 transparent cache。

## Pass conditions

- history offset参与 canonical identity、dependency/liveness/barrier/queue/physical-slice resolution，旧 slice只读，reset/resize使旧 generation失效。
- export是 authoritative culling root；persistent physical ownership只在成功 GPU completion后发布，失败不泄露半成品。
- capture来自 frozen/compiled truth，JSON/DOT稳定且版本化；structural replay拒绝不兼容 compiler/device envelope且只按 `compiler-lowering` 计。独立 executable replay 必须重建资源、记录/提交捕获命令并在 Null 比较可观察输出。
- retained variant的可观察能力由 immediate canonical recording + transparent cache覆盖，cache on/off与hit/miss输出一致。

## Fail conditions

- 每次 invocation重建 temporal/persistent物理资源，或复用仍在 flight 的 history slice。
- export只保活资源名而不保活 producer/final state/queue ownership。
- capture含裸 native pointer、非确定字典顺序或只序列化统计摘要。
- 仅验证 capture topology/canonical signature，却把 `rg.executable-capture-replay` 提升到 `null-execution`。
- 恢复独立 retained compiler/cache。

## NEEDS_GRILL

若 consumer 必须重新公开 retained graph作为正确性语义，而 accepted immediate replacement无法表达时使用 `NEEDS_GRILL:`。
