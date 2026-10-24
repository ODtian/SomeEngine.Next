# RHI-CAP-012 — External resources and timelines 

`ExternalResources` and `ExternalTimelines` report exact handle types and import/export support.
Imported Buffers, Textures, Heaps and timelines remain ordinary RHI identities with validated native
description and initial state.

Borrowed import leaves the input native/OS ownership with the caller; transferred import consumes the
specified native ownership exactly once. Export returns a caller-owned `ExternalHandle`. Queue waits
and signals for external timelines are explicit members of `QueueSubmitDesc`; no hidden interop
synchronization is inserted.
^rhi-cap-012
