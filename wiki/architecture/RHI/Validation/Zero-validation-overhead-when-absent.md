# RHI-VAL-002 — Zero validation overhead when absent 

When the application selects `D3D12Backend` directly, no Validation registry, shader diagnostic walk,
message sink or validation dispatch is present. Product code does not contain a runtime branch that
checks whether a layer exists.

The benchmark project measures concrete and interface receiver dispatch separately, but those
measurement adapters do not enter the product assembly.
^rhi-val-002

