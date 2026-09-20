# RHI-PRESENT-002 — Reconfigure commit boundary 

`Reconfigure` validates the complete `SwapchainConfig` and returns `Busy` while any image prevents a
safe native resize. It builds or resizes native state before one commit. Success increments
`SwapchainInfo.Generation`, updates the immutable current configuration and invalidates every image
from the previous generation.

Failure or an unsupported configuration leaves the prior generation usable. Reconfigure does not
implicitly wait for arbitrary application Queue work.
^rhi-present-002

