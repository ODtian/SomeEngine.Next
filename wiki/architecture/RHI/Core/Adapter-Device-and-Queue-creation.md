# RHI-CORE-010 — Adapter, Device and Queue creation 

Adapter enumeration returns immutable identity, memory and driver facts. A Device request supplies an
Adapter, explicit Queue families/counts, required and optional feature requests, an enabled node mask
and a diagnostic label.

`DeviceFeatures` is only a creation request. Required requests must be satisfied or creation fails;
optional requests are enabled only when available. After creation, `Device.Capabilities` contains the
core limits/format table and `TryGetCapability<TCapability>` is the sole authority for optional
features.

Queues are Device-owned borrowed identities selected by type and index. Callers do not Dispose a
Queue; Device teardown invalidates submission and native access.
^rhi-core-010
