# RHI-IMPL-005 — Ordinary acceptance work 

For an RHI change to be complete:

1. Debug and Release builds pass with the quality analyzer enabled and no baseline suppression.
2. `git diff --check` passes.
3. Graphics, D3D12, benchmark-gate and Render Graph tests pass; destructive groups run in isolation.
4. Affected hardware capabilities execute on available hardware or are recorded `NOT_RUN` with cause.
5. Performance workloads prove equivalent work before timing and distinguish probe, diagnostic and
   certification evidence.
6. Wiki contract pages describe the current API rather than a proposed or deleted model.
7. New source/tests are classified for commit, temporary artifacts are classified separately, and a
   clean recursive checkout can reproduce restore/build/test.

No historical report or uncommitted implementation is sufficient by itself.
^rhi-impl-005
