# RHI-VAL-004 — Pipeline creation is authoritative 

Validation derives binding/export/node facts before Pipeline creation, then registers the returned
Pipeline only after the backend succeeds. Work Graph materialized entries are read from the created
Pipeline and checked against reflected S# nodes. DXR tables retain the validated Pipeline export
state.

Asynchronous creation performs the same prevalidation, awaits the backend Task, then commits object
and binding state atomically. A faulted Task leaves no registered Pipeline. Task success still means
the native Pipeline is fully usable.
^rhi-val-004

