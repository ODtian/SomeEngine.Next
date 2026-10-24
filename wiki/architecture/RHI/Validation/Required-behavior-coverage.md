# RHI-EVID-001 — Required behavior coverage 

The software evidence matrix covers:

- public value equality and XML lifetime/concurrency declarations;
- strict non-D3D12 conformance for resource, descriptor, command, Pipeline, query and presentation;
- WARP resource/view/Pipeline/descriptor/barrier/copy/render/query execution;
- submission preflight, completion retirement and Device/Surface teardown races;
- mapping sequence, allocator reuse and failure atomicity;
- Pipeline cache, asynchronous creation and Slang ownership after caller release;
- static sampler GPU output;
- DXR, sparse, sampler feedback, Work Graph and indirect-command legality/execution where available;
- Device Lost, DRED, quarantine, presentation and process-destructive paths.

A feature without the required hardware is recorded as `NOT_RUN` with the reason. It is never reported
as PASS from metadata inspection alone.
^rhi-evid-001

