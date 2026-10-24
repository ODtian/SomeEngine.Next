# RHI-CAP-008 — Work Graphs 

`WorkGraphs` reports the released tier and exact node, dispatch-grid, record and backing-memory limits
available from the runtime. Pipeline creation consumes the linked S# program and live node reflection;
it does not build a copied topology or semantic graph model.

The Pipeline exposes materialized entry-point information and backing-memory requirements. Dispatch
selects an explicit initialization mode and CPU/GPU input form. Backing storage, records, entry index,
counts and dimensions are checked against the capability and native ABI. Work Graph Pipeline creation
is available synchronously and asynchronously.
^rhi-cap-008

