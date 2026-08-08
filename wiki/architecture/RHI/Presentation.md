# RHI Presentation Contract

### RHI-PRESENT-001 — Acquired swapchain image

Successful acquire returns one sequence-bound `SwapchainImage`. All copies share one state machine:
Acquired has one Submit right, successful submission produces Submitted with one Present right, and
successful Present produces Presented. No copy can repeat either right. Every payload/status getter
validates the exact acquire sequence and swapchain generation. The value carries the acquired Texture
plus its immutable initial access/layout defined by
[[Queue-and-Commands#RHI-QUE-007 — Canonical initial synchronization facts]]. Reconfigure
invalidation, Device loss and Swapchain disposal make all copies unable to expose a newly reused
internal image slot. Acquire timeout follows
[[Queue-and-Commands#RHI-QUE-003 — CPU wait domain]].
The default SwapchainImage is invalid: every payload/status getter and any use in Submit/Present
throws `InvalidOperationException`. Sequence values never wrap or make an old copied value current.

`SwapchainImage` does not implement Dispose. A portable Dispose could neither return an acquired
Vulkan image on every supported implementation nor do so without hiding Present/Queue work; pretending
otherwise would violate the common Dispose contract. Successful Present consumes the current
sequence. A frame that cannot be presented must explicitly reconfigure or dispose the Swapchain as
required by its error path; dropping a value does not release an acquisition or make a later slot
valid.
^rhi-present-001

### RHI-PRESENT-002 — Reconfigure commit boundary

Busy, Unsupported and caller-parameter rejection occur before releasing old back-buffer references
and preserve the old generation. Once native resize is attempted, Reconfigure has crossed its commit
boundary: all old Texture/View wrappers and acquired `SwapchainImage` values are immediately
invalidated on both success and failure. Success increments generation and rebuilds wrappers. A
normal HRESULT leaves the swapchain exactly OutOfDate and throws; device removal transitions Device
and Swapchain to DeviceLost, invalidates every acquired `SwapchainImage`, and throws. No
post-boundary failure revives or ambiguously preserves the old generation.
^rhi-present-002

### RHI-PRESENT-003 — Status and device-loss separation

Acquire/Present/Reconfigure statuses contain only expected presentation outcomes. Invalid timeout or
configuration input is a standard argument exception. Device removal updates terminal object state
and throws `GraphicsException(DeviceLost)` rather than adding DeviceLost to each operation status.
Present consumes only the current `SwapchainImage` returned for the same swapchain generation and
accepted Submit. Its precondition is an explicit caller/Render-Graph-authored transition to
`TextureLayout.Present` with no outstanding resource access; Present never inserts or repairs that
transition. A zero-timeout acquire that cannot obtain an image returns Timeout, the same status used
when any finite deadline expires; there is one unavailability/deadline status rather than two
synonymous branches.

Argument, ownership, generation, image-state and Present-layout checks finish before invoking the
native Present call and preserve the one Present right when they reject the call. Invocation of
native Present consumes that right regardless of whether the native result is Success, Suboptimal,
Occluded or OutOfDate; the same `SwapchainImage` can never be presented again. OutOfDate requires
Reconfigure or Swapchain replacement. Device removal invalidates the image and throws
`GraphicsException(DeviceLost)`. No status after native invocation restores Submitted state.
^rhi-present-003

### RHI-PRESENT-004 — Creation, configuration and expected statuses

`SwapchainDesc` is the complete creation input: Surface, image count/usages, initial
`SwapchainConfig` and Label. `SwapchainConfig` is exactly the native-reconfigurable subset: width,
height, Format, color space, present mode/tearing policy and maximum frame latency. Width/height zero
means the Surface's current drawable extent at the Reconfigure call; other invalid values throw.
Changing Device, Surface or image usages requires a new Swapchain and is not disguised as
Reconfigure. `SwapchainInfo` reports the resolved current config, image count, generation and
presentation support snapshot.

`SwapchainAcquireOptions` contains only timeout and whether prior contents must be preserved. Acquire
returns Success with a SwapchainImage, Timeout when its valid deadline expires, or OutOfDate when the
native presentation target must be reconfigured; it never returns a default image on Success.
Present returns Success, Suboptimal, Occluded or OutOfDate. Reconfigure returns Success, Busy before
the commit boundary when an acquired/submitted image still prevents resize, or Unsupported when the
requested format/color-space/present combination is not in the immutable support snapshot. Invalid
arguments throw, rare native failures throw, and DeviceLost is always terminal/exceptional. Status
values do not overlap in meaning.
^rhi-present-004
