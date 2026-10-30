# RHI-PRESENT-004 — Creation, configuration and telemetry 

Presentation is requested during Device creation and confirmed by the typed `Presentation`
capability. A Surface is a backend-owned OS-window association. A Swapchain is created for a Device,
Surface, image count, image usages and `SwapchainConfig` containing dimensions, format, color space,
present type, tearing policy, maximum frame latency and optional HDR10 metadata.

`SurfaceDesc.Type` identifies the native window data. `Win32` uses an HWND and optional process
instance in `DisplayHandle`; `Xlib` uses an X11 Window in `WindowHandle` and its `Display*` in
`DisplayHandle`. The application keeps the native window and display connection alive through
swapchain and surface disposal. The standard application obtains both values from its Win32 or Xlib
window implementation before creating the same RHI surface and swapchain.

For Xlib, the application releases swapchains, the device and surfaces, closes the native window and
display connection, then disposes the backend. This keeps graphics-driver X11 callbacks available
through `XCloseDisplay`. Device recreation keeps the window and creates the replacement backend
before releasing the previous backend.

`GetSwapchainSupport(device, surface)` returns a caller-held array of the available presentation
combinations before swapchain creation. Vulkan reads its surface formats and present modes; D3D12
uses its existing device format and tearing query. Applications use these values to select their
defaults. `CreateSwapchain` continues to validate its explicit configuration against native support.

The backend validates the format/color-space/output combination and reports `SwapchainSupport` rather
than silently substituting a different public configuration. Monitor migration and resize are handled
through explicit support queries/reconfiguration.

D3D12 presentation diagnostics are exposed through the Device's `D3D12Diagnostics` capability, not a
second method on `D3D12Backend`. `GetPresentationInfo(swapchain)` returns a thread-safe snapshot with
configuration/generation, out-of-date state, Acquire/Present/Reconfigure attempt and failure counts,
last statuses, CPU durations and last submission completion.
^rhi-present-004
