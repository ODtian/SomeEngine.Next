# RHI-LIFE-002 — Parent disposal and cascading teardown 

The backend owns Devices and Surfaces. A Device owns its Queues, resources, views, Pipelines,
command objects, descriptor tables, query pools and capability-created children. A Surface tracks its
Swapchains. A Swapchain therefore participates in both Device and Surface teardown, but that is an
internal parent relation rather than a public lifetime graph.

Parent registries close registration under a gate, detach an intrusive work list in one pass and
perform child Dispose outside the gate. Teardown does not repeatedly scan a hash table and does not
allocate. Independent registry drain links are used only where one object belongs to two parent
registries.

Disposing a child first unregisters it. Disposing a parent first ends every child exactly once. A
child release failure is retained and prevents unsafe native-runtime unload rather than releasing
possibly referenced native state.
^rhi-life-002

