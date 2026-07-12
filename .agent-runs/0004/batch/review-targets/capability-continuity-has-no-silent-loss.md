# Capability continuity has no silent loss

## What to review

逐项对照原始 ZIP baseline、`harness/capabilities/graphics-rendergraph-capabilities.v1.json` 与 `harness/capabilities/graphics-rendergraph-public-api-inventory.v1.json`，确认所有 public/API、Null、D3D12、RG、test、tooling、utility 和 renderer evidence 都有当前映射或明确 recorded gap，并检查 manifest 更新是否由代码和可执行测试支持。

## Pass conditions

- 每个 ZIP capability 只有一个稳定 ID，baseline level 与 evidence 可追溯。
- 原 `src/Graphics/IDevice.cs` 130 个 method declaration 与 `src/RenderGraph.Core/` 100 个 public type declaration 全部逐符号存在；source key、inventory id 均唯一，capability id 存在，disposition 与说明非空。
- mandatory 项的 current level 不低于 accepted target，所有映射文件、符号、required lane 和 test ID 均存在。
- replacement 明确保留或提高可观察语义；删除/降级引用已完成 grill/ADR。
- 当前新增的 generational lifetime、native requirements、exact hazards、transparent cache、shader-effect admission 同样在 ledger 中受保护。
- capture structural envelope 只按 `compiler-lowering` 计；独立 executable replay 必须实际重建、提交和比较输出后才能记为 `null-execution`。
- renderer adapter、capture diff/minimize、fuzzer、profile scheduling、Slang direct runtime、hot reload、async/disk pipeline tooling，以及 bind-group/bindless/residency/breadcrumb utility 不因本 run 不实现而从账本消失。

## Fail conditions

- ZIP 项消失、合并后无法追踪，或用“当前消费者没用”解释删除。
- inventory 仅覆盖代表性方法/类型、遗漏 overload/partial declaration，或多个 declaration 共享一个无法追踪的笼统条目。
- 修改 baseline/target 让失败消失，却没有新的原始证据或已接受决策。
- 把 token、文档、interface cast、stub 或只验证“不抛异常”的测试计为 execution。
- 把 structural replay 的拓扑验证写成 executable replay，或用删除 capture command payload 要求关闭红灯。

## NEEDS_GRILL

原始证据证明 accepted scope 对某 capability 的 baseline 或 required target 判断错误，并且修正会实质扩大或缩小本 run 范围时使用 `NEEDS_GRILL:`。
