# RHI-PRESENT-003 — Expected status and Device Lost separation 

Acquire uses exactly `Success`, `Timeout` and `OutOfDate`. Present uses exactly `Success`,
`Suboptimal`, `Occluded` and `OutOfDate`. Reconfigure uses exactly `Success`, `Busy` and
`Unsupported`.

Timeout, occlusion, mode change and out-of-date are expected presentation control flow. A native
Device removal result is captured as the Device's terminal `GraphicsException`; it is never hidden as
an ordinary status. After Device Lost, existing images become unusable and report their terminal image
state.
^rhi-present-003

