# RHI-EVID-002 — Workload equivalence 

Performance comparisons are accepted only after every receiver produces equivalent work. The
benchmark protocol uses untimed readback hashes, command/submission counts and available native
statistics to prove equivalence before comparing timing.

The current formal receiver set is interface `IGraphicsBackend`, direct C# through the generated
Silk.NET D3D12 COM surface, and native C++ D3D12. There is no Concrete/ImplementationDiagnostic
receiver and no managed manual-vtable D3D12 variant. WARP is functional equivalence only. Hardware
developer probes are exploratory and non-gating. [[Performance-Evidence-2026-08-18]] is superseded;
the current same-source three-receiver results are recorded in [[Performance-Evidence-2026-08-20]].
^rhi-evid-002

