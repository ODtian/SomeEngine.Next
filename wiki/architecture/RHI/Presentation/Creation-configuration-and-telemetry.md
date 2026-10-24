# RHI-PRESENT-004 — Creation, configuration and telemetry 

Presentation is requested during Device creation and confirmed by the typed `Presentation`
capability. A Surface is a backend-owned OS-window association. A Swapchain is created for a Device,
Surface, image count, image usages and `SwapchainConfig` containing dimensions, format, color space,
present type, tearing policy, maximum frame latency and optional HDR10 metadata.

The backend validates the format/color-space/output combination and reports `SwapchainSupport` rather
than silently substituting a different public configuration. Monitor migration and resize are handled
through explicit support queries/reconfiguration.

D3D12 presentation diagnostics are exposed through the Device's `D3D12Diagnostics` capability, not a
second method on `D3D12Backend`. `GetPresentationInfo(swapchain)` returns a thread-safe snapshot with
configuration/generation, out-of-date state, Acquire/Present/Reconfigure attempt and failure counts,
last statuses, CPU durations and last submission completion.
^rhi-present-004
