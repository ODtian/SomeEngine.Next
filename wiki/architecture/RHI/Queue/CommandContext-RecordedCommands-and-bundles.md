# RHI-QUE-004 — CommandContext, RecordedCommands and bundles 

A `CommandContext` is created for one Queue type/index and may be an ordinary list or a bundle. Its
recording lifecycle is:

```text
idle -> Begin -> recording -> End/EndBundle or Discard -> reusable idle
```

`CommandRecordingDesc` supplies initial descriptor capacities and an explicit `Repeatable` choice.
The renderer selects reusable batches. Only repeatable recordings retain command argument data in
their command context; the native-call wrappers receive that context explicitly. Replaying command
data passes the native target directly, so replay has no global table or thread-local mode.

`End` returns a sealed `RecordedCommands` object. Its status is Executable, Submitted, or Discarded,
and each transition uses `Interlocked.CompareExchange`. Dispose and Submit are externally
synchronized. ValidationLayer reports concurrent or repeated use; Device loss belongs to Device.
Disposing executable commands discards their native recording. Accepted GPU work retains its
native command storage until the queue finishes. The public status remains Submitted after GPU
completion, while `QueueCompletion` supplies completion information.

Bundles are caller-disposed Device children and use the command inventory supported by the backend.
Their native command storage follows the same Device deferred release boundary as other resources.
^rhi-que-004

