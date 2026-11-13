# RHI-QUE-001 — Queue submission 

`QueueSubmitDesc` explicitly names Queue-completion waits, external timeline waits, one or more
`RecordedCommands`, acquired `SwapchainImage` values and external timeline signals. Every item must
belong to the selected Queue's Device and satisfy its Queue-family contract.

ValidationLayer checks submission arguments. The backend reserves managed/native retirement storage before native acceptance.
Before acceptance, failure restores every command and image to its prior state. After acceptance, the
submission holds command storage, immutable binding data and acquired-image/timeline use until its returned `QueueCompletion` retires; a
post-acceptance failure is terminal Device loss rather than a retryable partial submit.

Resource and descriptor disposal follows the Device deferred release queue, which records all
accepted queue values at disposal. ValidationLayer checks references between recording and submission.

A `RecordedCommands` payload is single-submit. A submitted swapchain image can be presented only on
the Graphics Queue that owns its swapchain.
^rhi-que-001

