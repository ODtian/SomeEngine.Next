# RHI Presentation

### RHI-PRESENT-001 — Acquired swapchain image

`Acquire` returns a `SwapchainImage` value only on success. The value carries one swapchain generation
and one acquisition sequence plus the current Texture and its initial synchronization facts.

The image lifecycle is:

```text
Acquired -> Submitted -> Presented
         -> Invalidated or DeviceLost
```

`QueueSubmitDesc.SwapchainImages` performs the Acquired-to-Submitted transition as part of submission
preflight. A failed pre-acceptance submit restores Acquired. `Present` requires the submitted image,
the owning Graphics Queue and the same Device. Stale sequence/generation values are rejected.
^rhi-present-001

### RHI-PRESENT-002 — Reconfigure commit boundary

`Reconfigure` validates the complete `SwapchainConfig` and returns `Busy` while any image prevents a
safe native resize. It builds or resizes native state before one commit. Success increments
`SwapchainInfo.Generation`, updates the immutable current configuration and invalidates every image
from the previous generation.

Failure or an unsupported configuration leaves the prior generation usable. Reconfigure does not
implicitly wait for arbitrary application Queue work.
^rhi-present-002

### RHI-PRESENT-003 — Expected status and Device Lost separation

Acquire uses exactly `Success`, `Timeout` and `OutOfDate`. Present uses exactly `Success`,
`Suboptimal`, `Occluded` and `OutOfDate`. Reconfigure uses exactly `Success`, `Busy` and
`Unsupported`.

Timeout, occlusion, mode change and out-of-date are expected presentation control flow. A native
Device removal result is captured as the Device's terminal `GraphicsException`; it is never hidden as
an ordinary status. After Device Lost, existing images become unusable and report their terminal image
state.
^rhi-present-003

### RHI-PRESENT-004 — Creation, configuration and telemetry

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
