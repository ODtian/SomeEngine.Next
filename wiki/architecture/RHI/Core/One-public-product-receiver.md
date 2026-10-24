# RHI-CORE-002 — One public product receiver 

`IGraphicsBackend` is the only product calling surface. The selected backend object owns its native
runtime. `ValidationLayer` is an optional ordinary wrapper that takes ownership of that backend and
implements the same interface.

Resource, view and Pipeline objects are public abstract identities with backend-private sealed
implementations. Backend methods check backend ownership, concrete private type and, where required,
Device provenance before using them. Managed object provenance is never established with
`Unsafe.As`.

Generic dispatch adapters are permitted only inside the benchmark project so concrete and interface
receiver cost can be measured without becoming product concepts.
^rhi-core-002

