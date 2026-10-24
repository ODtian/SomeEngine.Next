# RHI-VAL-005 — Native validation follows the selected platform 

When supported and requested, the Validation Layer enables the backend's D3D12 debug layer, GPU-based
validation, synchronized Queue validation and DRED before Device creation. Native debug messages are
reported with their native severity/category/id and do not replace managed contract checks.

Device removal tests, process-destructive tests and real-window presentation tests execute in isolated
process groups so one terminal native event cannot contaminate unrelated evidence.
^rhi-val-005

