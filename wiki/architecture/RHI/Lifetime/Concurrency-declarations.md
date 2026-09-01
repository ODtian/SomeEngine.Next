# RHI-LIFE-007 — Concurrency declarations 

Public XML declares one default for every exported type:

- immutable values, Device metadata, capabilities and borrowed Queue identities are thread-safe;
- mutable caller-owned resources and recording objects are externally synchronized;
- immutable Pipeline state may be bound concurrently from independent recording contexts;
- `PersistentParameterBindings` may be bound concurrently while updates publish immutable
  generations; each binding operation observes and retains exactly one generation before native
  mutation;
- concurrent Dispose calls are always safe and join one release;
- Queue submission and completion queries are internally synchronized;
- normal operation racing with Dispose is invalid unless a type states a stronger rule.

A method-specific lock may serialize a native Queue, descriptor publisher, allocator or Pipeline
compiler without changing the public concurrency model. Diagnostic snapshots are immutable and may
be read concurrently.
^rhi-life-007
