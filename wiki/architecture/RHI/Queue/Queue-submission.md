# RHI-QUE-001 — Queue submission 

`QueueSubmitDesc` explicitly names Queue-completion waits, external timeline waits, one or more
`RecordedCommands`, acquired `SwapchainImage` values and external timeline signals. Every item must
belong to the selected Queue's Device and satisfy its Queue-family contract.

Submission validates and reserves all managed/native retirement storage before native acceptance.
Before acceptance, failure restores every command and image to its prior state. After acceptance, the
submission owns all captured native dependencies until its returned `QueueCompletion` retires; a
post-acceptance failure is terminal Device loss rather than a retryable partial submit.

A `RecordedCommands` payload is single-submit. A submitted swapchain image can be presented only on
the Graphics Queue that owns its swapchain.
^rhi-que-001

