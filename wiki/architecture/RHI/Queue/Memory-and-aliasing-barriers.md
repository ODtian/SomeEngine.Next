# RHI-QUE-008 — Memory and aliasing barriers 

Memory and aliasing barriers are explicit commands and are never inferred from copies, rendering,
dispatch or resource creation. The backend validates Queue legality and exact resource provenance,
then lowers the public operation either to Enhanced Barriers or the legacy D3D12 path.

`AliasingBarrier.Memory` supplies the known execution and memory access scopes on both sides
of a handoff. Its two-span constructor retains the all-access handoff. RenderGraph supplies
declared scopes for buffers whose accesses all execute on the handoff Queue; initial heap
activation and the other resource cases use the all-access form. D3D12 applies these scopes
to the alias activation/deactivation and its memory barrier, while retaining the outgoing
buffer visibility request. Vulkan maps the same scopes to its memory dependency.
The current implementation is in [Commands](../../../../src/SomeEngine.Graphics/Commands/Commands.cs),
[FrameRecording](../../../../src/SomeEngine.RenderGraph/FrameRecording.cs),
[D3D12 barriers](../../../../src/SomeEngine.Graphics.Direct3D12/CommandBarriers.cs), and
[Vulkan barriers](../../../../src/SomeEngine.Graphics.Vulkan/VulkanBarriers.cs).

A Queue release/acquire pair is matched by the caller's synchronization plan. The RHI does not create
a hidden completion or wait to repair a missing handoff. Validation diagnoses mismatched split or
Queue-transfer facts before forwarding valid calls.
^rhi-que-008

