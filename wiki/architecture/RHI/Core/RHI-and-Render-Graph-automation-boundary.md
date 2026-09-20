# RHI-CORE-003 — RHI and Render Graph automation boundary 

The RHI executes explicit work. It does not infer render passes, synthesize barriers, insert Queue
waits, compile shaders on first use, or own frame scheduling policy.

Render Graph may derive resource lifetimes, aliases, barriers and Queue dependencies. Its compiled
result must still call ordinary RHI creation, barrier, command and submission operations. A direct
caller can express the same execution without constructing a Render Graph.
^rhi-core-003

