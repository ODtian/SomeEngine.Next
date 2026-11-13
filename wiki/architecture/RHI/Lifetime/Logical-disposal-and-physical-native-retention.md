# RHI-LIFE-003 — Resource disposal and native retention

Each Device holds a `DeferredReleases` queue. Disposing a resource records the submitted value of every device queue and transfers its native objects and descriptor allocations into that queue. `CollectCompleted` releases an entry once every recorded queue value has completed.

Native objects retain the dependencies required by their construction: placed resources retain their heap allocation, views retain their resources, and pipelines retain their native state. These dependencies retire together with the object that uses them.

ValidationLayer tracks resource references in recorded commands until submission or discard. Disposing a referenced resource during that interval reports `ResourceDisposedWhileRecorded`. Accepted GPU work remains valid through the Device release queue.

Recorded command handles carry one status: Executable, Submitted, or Discarded. Command recording reuse is requested explicitly through `CommandRecordingDesc.Repeatable`; recording data belongs to its command context.

`D3D12Diagnostics.LiveNativeObjects` reports registered native wrappers until their deferred release
finishes. The WARP behavior checks submit a copy, dispose its source, verify the copied bytes and
observe the count drop after completion collection. A separate ValidationLayer check covers disposal
between recording and submission.
^rhi-life-003
