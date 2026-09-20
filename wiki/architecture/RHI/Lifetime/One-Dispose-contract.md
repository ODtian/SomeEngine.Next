# RHI-LIFE-001 — One Dispose contract 

Every caller-disposed RHI identity has one terminal logical transition:

```text
Active -> Disposed
```

`DisposeGate` elects one releasing thread. Concurrent Dispose calls join that release and return only
after the same logical operation is complete. Reentrant Dispose on the releasing thread is ignored.
Release exceptions do not escape Dispose; the first teardown failure is retained by the owning
Device or backend diagnostics.

Immutable metadata explicitly documented by a type may remain readable after Dispose. Operations,
native access and mutable state do not.
^rhi-life-001

