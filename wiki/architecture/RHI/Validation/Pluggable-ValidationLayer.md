# RHI-VAL-001 — Pluggable ValidationLayer 

`ValidationLayer` is a sealed non-generic `IGraphicsBackend` wrapper. Construction transfers ownership
of the wrapped backend. `ValidationOptions.Reporter` supplies an optional borrowed
`IValidationReporter`; callbacks are synchronous.
Disposing the layer closes validation state, reports live objects when configured and disposes the
wrapped backend exactly once.

The layer returns the same public object families and forwards valid operations to the same backend
methods. It does not wrap every resource in another public proxy type and does not create a second
product receiver.

Command parameter ranges, backend identity, same-device relationships, queue node visibility and
recording order are checked by this optional layer. It also tracks references while commands are
executable: disposing a referenced object reports `ResourceDisposedWhileRecorded`, and overlapping
command submission/disposal reports `ConcurrentCommandUse`.

Native command methods cast the validated command context directly. Native memory write bounds use
the shared `NativeMemoryChecks` helper. Cache/file parsing remains at its input boundary, and native
execution errors use `GraphicsException`.
^rhi-val-001

