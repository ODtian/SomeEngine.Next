# RHI-VAL-001 — Pluggable ValidationLayer 

`ValidationLayer` is a sealed non-generic `IGraphicsBackend` wrapper. Construction transfers ownership
of the wrapped backend. The optional message sink is borrowed and callbacks are synchronous.
Disposing the layer closes validation state, reports live objects when configured and disposes the
wrapped backend exactly once.

The layer returns the same public object families and forwards valid operations to the same backend
methods. It does not wrap every resource in another public proxy type and does not create a second
product receiver.
^rhi-val-001

