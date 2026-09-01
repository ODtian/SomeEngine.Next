# RHI-LIFE-003 — Logical disposal and physical native retention 

Public wrapper lifetime and physical native lifetime are separate facts. `NativeLease` owns or
borrows one COM reference and may retain only dependencies required by the native object:

- a placed resource retains its Heap allocation;
- a view retains the resource whose descriptor it names;
- a Pipeline retains native root/state objects and its Slang program ownership;
- persistent bindings, indirect layouts and shader tables retain the Pipeline state they use;
- a command recording that binds persistent parameters retains the exact immutable published
  parameter-data generation, not the mutable public wrapper; the same generation is retained once per
  recording even when selected by several material runs;
- RecordedCommands retain captured native objects;
- sparse mapping generations retain mapped Heaps;
- swapchain image generations retain their back buffers.

When `Submit` is accepted, command and presentation dependencies remain retained until the Queue
completion retires them. Caller disposal can end a wrapper before that completion without invalidating
accepted GPU work. There is no public switch that lets a caller disable this correctness rule.

Native constructors are failure-atomic: a failed dependency retain or managed owner construction
returns every already-acquired COM reference and suballocation.
^rhi-life-003

