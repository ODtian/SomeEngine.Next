# RHI-PRESENT-001 — Acquired swapchain image 

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

