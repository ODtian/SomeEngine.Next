# RHI-CORE-007 — Controlled vocabulary 

A public type must correspond to a real API/GPU object, an independent state machine, independent
ownership, a type-level error prevention boundary, or behavior shared by genuinely different
backends.

The core vocabulary is intentionally small: Adapter, Device, Queue, Resource, View, Pipeline,
DescriptorTable, Parameter bindings, CommandContext, RecordedCommands, QueueCompletion, Surface,
Swapchain and DeviceCapability. Internal helpers are named for the concrete fact they store or the
specific preparation they perform; generic planning, artifact and transaction layers are not product
concepts.
^rhi-core-007

