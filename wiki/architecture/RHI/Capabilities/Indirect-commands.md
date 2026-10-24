# RHI-CAP-009 — Indirect commands 

`IndirectCommands` reports supported argument kinds and limits. `IndirectCommandLayoutDesc` is an
ordered native command-signature description with a fixed stride and, where root arguments require
it, an owning Pipeline.

Execution names the layout, argument/count Buffer ranges and maximum command count. The backend
validates alignment, Queue family, Pipeline type and every capability-dependent argument before
recording. D3D12 does not advertise a Work Graph indirect argument that its released native API does
not provide.
^rhi-cap-009

