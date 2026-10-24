# RHI-CAP-011 — Linked adapters 

`LinkedAdapters` reports node count and masks for resource creation, resource visibility, Queues and
Pipelines. Device creation selects an enabled-node mask. Every node-specific Queue, resource,
descriptor and Pipeline operation validates that mask and does not infer cross-node visibility.

Single-node Devices use the same API with one enabled bit. Multi-node behavior requires real hardware
evidence; metadata tests alone do not certify linked-adapter execution.
^rhi-cap-011

