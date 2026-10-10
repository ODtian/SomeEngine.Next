# RHI-PRESENT-002 — Reconfigure commit boundary 

`Reconfigure` validates the complete `SwapchainConfig` and returns `Busy` while any image prevents a
safe native resize. It builds or resizes native state before one commit. Success increments
`SwapchainInfo.Generation`, updates the immutable current configuration and invalidates every image
from the previous generation.

A rejected configuration or a preflight failure leaves the prior generation usable. Reconfigure does
not implicitly wait for arbitrary application Queue work.

After the native resize/recreation boundary, a native failure can invalidate the previous images.
The backend preserves the last published configuration/generation, reports the native error, and
returns OutOfDate from Acquire until a successful Reconfigure. D3D12 already follows this rule when
ResizeBuffers fails. Vulkan retires oldSwapchain even on creation failure and frees that retired
generation; a subsequent Reconfigure creates without the old handle.

See [VkSwapchainCreateInfoKHR, Description](https://docs.vulkan.org/refpages/latest/refpages/source/VkSwapchainCreateInfoKHR.html)
and VUID-VkSwapchainCreateInfoKHR-oldSwapchain-01933.
^rhi-present-002

