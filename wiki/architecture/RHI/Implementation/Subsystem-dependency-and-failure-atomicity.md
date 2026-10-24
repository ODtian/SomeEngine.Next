# RHI-IMPL-004 — Subsystem dependency and failure atomicity 

The implementation uses a small number of necessary physical mechanisms:

- `DisposeGate` for one logical release;
- parent registries for cascading teardown;
- `NativeLease` for COM and allocation lifetime;
- command capture and Queue retirement for accepted GPU work;
- sparse and presentation generations where immutable native state truly changes;
- concrete preparation helpers before native mutation.

Managed capacity, descriptor ranges, native owner objects and rollback state are acquired before the
commit boundary. Failure tests cover constructor allocation, dependency retain, descriptor
publication, command preparation, Pipeline creation, Queue acceptance and teardown. Test-only fault
injection never appears in the public RHI.
^rhi-impl-004

