# RHI-LIFE-004 — Device Lost and diagnostic delivery 

A Device has the terminal status domain `Active`, `Lost`, `Disposed`. The first native removal result
creates one retained `GraphicsException(GraphicsError.DeviceLost)`. Later operations observe that
same terminal authority.

D3D12 diagnostics may attach a structured immutable DRED report containing independent breadcrumb
and page-fault query results, copied names/contexts, page-fault address, allocation candidates and
explicit truncation flags. Failure to capture one DRED branch does not erase the other branch or the
original Device Lost exception.

Release failures are diagnostics, not an excuse to continue using the object. If complete native
cleanup cannot be proven, the backend retains the affected runtime in process quarantine rather than
unloading it unsafely.
^rhi-life-004

