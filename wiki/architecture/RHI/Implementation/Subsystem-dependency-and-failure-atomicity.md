# RHI-IMPL-004 — Subsystem dependency and failure atomicity 

The implementation uses a small number of necessary physical mechanisms:

- `DisposeGate` for one logical release;
- parent registries for cascading teardown;
- direct native data and construction dependencies for COM and allocation lifetime;
- Device deferred releases and Queue command-storage retirement for accepted GPU work;
- sparse and presentation generations where immutable native state truly changes;
- concrete preparation helpers before native mutation.

Managed capacity, descriptor allocations, native objects and submission work storage are acquired before the
commit boundary. Failure tests cover constructor allocation, dependency retain, descriptor
publication, command preparation, Pipeline creation, Queue acceptance and teardown. Test-only fault
injection never appears in the public RHI.
^rhi-impl-004

