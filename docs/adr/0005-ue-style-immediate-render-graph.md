---
status: accepted
---

# Keep Render Graph immediate, graph-scoped, and independent of binding transport

SomeEngine records the actual Render Graph for every render invocation, following UE RDG's setup/execute and graph-scoped resource model while adding stricter shader-effect, content-coverage, provenance, and buffer-range validation. A graph resource keeps one public identity across reads and writes; views describe exact ranges, pass-local access values carry effects and content contracts, and producer epochs remain compiler-internal. Renderer history, migration, physical resource caches, descriptor/view caches, residency, stable bindless indices, pipeline readiness, and quality fallback stay outside the graph; graph-managed resources remain explicitly declared even when bound bindlessly, while only externally managed resident read-only bindless resources may stay outside per-resource graph tracking. Public graph templates, instances, variants, and SSA write handles are rejected because they replace immediate control flow with a second persistent user model; semantic pass injection is rejected because it mixes renderer algorithms into dependency compilation. The required transparent internal compilation cache in ADR-0006 reuses immutable compiler output without changing this immediate public model.
