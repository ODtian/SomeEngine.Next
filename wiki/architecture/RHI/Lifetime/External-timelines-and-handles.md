# RHI-LIFE-005 — External timelines and handles 

An `ExternalTimeline` is a caller-disposed Device resource backed by an importable/exportable native
timeline. `TimelinePoint` and `TimelineSignal` are pure values used explicitly in `QueueSubmitDesc`.
The RHI does not insert implicit external waits or signals.

`ExternalHandle` owns exactly the returned OS handle and closes it once. Import borrows the supplied
handle value synchronously unless the import API explicitly states transfer. Exported handles do not
extend the logical lifetime of the public resource object.
^rhi-life-005

