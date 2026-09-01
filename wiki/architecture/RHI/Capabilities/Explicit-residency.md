# RHI-CAP-004 — Explicit residency 

`Residency` reports point-in-time local/non-local budget and usage. `ResidencyResource` is a typed
reference to a native pageable object that the backend can actually make resident.

Make-resident and eviction are explicit. A successful asynchronous make-resident request returns a
completion that must be ordered by the caller before use. Evict is synchronous and assumes the caller
has completed all Queue use. Neither operation inserts a hidden wait or changes public object
lifetime. Device Lost after native acceptance is terminal.
^rhi-cap-004

