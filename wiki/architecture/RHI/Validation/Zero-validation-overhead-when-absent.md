# RHI-VAL-002 — Zero validation overhead when absent 

When the application selects `D3D12Backend` directly, no Validation registry, shader diagnostic walk,
reporter or validation dispatch is present. Product code does not contain a runtime branch that
checks whether a layer exists.

The native encoders use direct casts and immutable layout data. Description, capability, binding,
resource identity and command-order checks live at `ValidationLayer` entry points. The native
backends retain native API error handling, cache and imported-handle input checks, compiler-result
checks and `NativeMemoryChecks` around unsafe memory writes.

Each native recording context holds its own explicit `Repeatable` command data. Native-call
wrappers receive the context as an argument, so recording and replay use instance state. The Device
deferred release queue records accepted submission values when a resource is disposed.

The benchmark project measures concrete and interface receiver dispatch separately, but those
measurement adapters do not enter the product assembly.
^rhi-val-002

