# RHI-QUE-004 — CommandContext, RecordedCommands and bundles 

A `CommandContext` is created for one Queue type/index and may be an ordinary list or a bundle. Its
recording lifecycle is:

```text
idle -> Begin -> recording -> End/EndBundle or Discard -> reusable idle
```

`CommandRecordingDesc` supplies initial capacities for resource descriptors, sampler descriptors and
captured resources. These are preparation hints, not logical limits.

`End` returns a value-type `RecordedCommands` handle with a sequence-protected payload. Its states
cover executable, submitting, submitted, completed, discarded, Device Lost and caller-disposed
outcomes. Copies share the same terminal sequence. Disposing an unsubmitted handle discards its
native payload; disposing after acceptance does not release GPU dependencies early.

Bundles are caller-disposed Device children and may contain only the command inventory permitted by
the backend. `ExecuteBundle` captures the bundle and its native dependencies.
^rhi-que-004

