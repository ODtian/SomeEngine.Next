# Naming research and breaking migration are complete

## What to review

检查所有新 public type/method名称的研究记录和仓库内消费者迁移，确保一次性 breaking redesign，没有 compatibility shell。

## Pass conditions

- 采用前已记录 GitHub/标准 API证据：Veldrid/LLGL 的 `DrawIndirect`、`DrawIndexedIndirect`、`DispatchIndirect`；Stride/Vulkan系的 `QueryPool`/`WriteTimestamp`；Veldrid/Vulkan系的 `Swapchain`/acquire/present术语；Unity RenderGraph参数模式及原 checkpoint 的 `History`、`Export`、`Capture`、`Replay`。
- 名称语义与 backend-neutral contract一致；不存在 `*Plan`、`*Run`、非入口点 `*Program`。
- 所有仓库内 consumer和tests迁到唯一新入口，不保留 `[Obsolete]`、双入口、转发壳。

## Fail conditions

- 未研究即发明 public命名，或复制 D3D12专属词泄漏到公共层。
- 为让旧测试编译而保留空 compatibility API、Null delegation或 CPU shadow。
- 只迁产品的一半，把 consumer修改留作后续 handoff。

## NEEDS_GRILL

研究证据显示 accepted名称会造成跨 backend语义冲突，且替代名称会改变已接受 public contract时使用 `NEEDS_GRILL:`。
