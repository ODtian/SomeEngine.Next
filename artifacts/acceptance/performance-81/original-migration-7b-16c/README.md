# Original pre71-to-migration-only source cost evidence

The retained historical pair is7b03495c2440664f5c2d08d6912be4edf7445630→16c3a15bcf6870b0eac59581aa06d8df35e6b307. Both runs used the recorded legacy three-scene content,1280×720,1024moving spheres/static Sponza+Bistro, fixed1/60,Release/.NET10.0.12,tieringfalse,RTX3080/driver32.0.16.1074,TAA/VSM/async/mixed rendering and the same native shadow settings(16kvirtual/4096atlas/P128,dynamicresolutionfalse,depth.0005,normal.01). Source/content/scene-list/settings hashes and original commands are retained in comparison-verification.json and comparison-sources.json.

## Actual retained NONTRACE results

| Scene | 7b P50 / P95 ms | 7b samples | 16c P50 / P95 ms | 16c samples |
| --- | --- | ---: | --- | ---: |
| 1024 spheres | 3.6857 / 4.1674 | 541 | 6.1104 / 7.1601 | 338 |
| Sponza | 3.0761 / 3.3469 | 650 | 3.1319 / 3.4466 | 635 |
| Amazon Bistro | 6.2082 / 6.8579 | 322 | 6.1758 / 7.1474 | 320 |

Both original reports record Complete=true, all three scenesMeasured, tracefalse and errornull. Exact outer44.732503/44.1161108seconds, each≤60; content build38.3677811/46.0500679seconds was recorded separately. The original standard32ready/128warm/3seconds warm/2seconds sample gates applied. These are observed historical source costs. measurement-window.json records CPU utilization29%→19% for7b and34%→38% for16c, with separately higher load during other historical runs; that load constrains causal attribution.

## Separate retained CPU and calibrated GPU diagnosis

Latest retained trace commands/report paths identify complete three-scene runs, outer50.1416994/44.6283760seconds. Per-scene analyzers report integritycomplete, dropped/scope-errors/issues0; all exact sample ranges and thread/category/call/inclusive/self costs are retained. The historical measurement-window also contains the earlier baseline trace exit1, preserving its actual failed-attempt history.

| Scene / source | CPU samples | Cluster.Update | Prepare | ReconcileGraph | ExecuteFrame | CPU fence wait |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| spheres / 7b | 518 | .265195 | .296986 | .007327 | 1.162722 | 1.834643 |
| spheres / 16c | 328 | .232310 | .251026 | .012305 | 1.180506 | 4.147963 |
| Sponza / 7b | 629 | .573467 | .155716 | .016735 | 1.830965 | .474704 |
| Sponza / 16c | 647 | .432008 | .108011 | .013095 | 1.551705 | .816203 |
| Bistro / 7b | 315 | 1.568320 | .161861 | .009754 | 3.100430 | 2.486692 |
| Bistro / 16c | 322 | 1.055064 | .106069 | .010808 | 2.354964 | 3.262446 |

CPU costs are inclusive milliseconds per measured frame. Nested scopes, thread work and CPU waiting retain their individual meanings.

Historical calibrated GPU reports independently record Complete=true/all threeMeasured, outer46.3070371/43.7672666seconds. GPU cross-queue spanP50 sphere/Sponza/Bistro is6.475872/5.088236/10.873956ms(7b) and3.964788/5.080048/10.120228ms(16c). Those runs used two fresh timestamp marker lists per original submission, calibrated queue clocks and recorded waits/signals. Spans include scheduling gaps; per-queue interval sums are retained separately. The new current per-frame observer has a different marker layout and its own267/253/244frame calibration proof.

## Scope alongside the current acceptance

The original pair supplies the migration-only16c source requirement with actual commands, full reports, CPU/GPU scopes and content identities. Legacy native settings/content belong to this pair. The later9e→833 severe regression pair uses corrected raw three-scene RT and a different content generation; current2a55 has fresh full-default independent RT3/3 and no-trace all-three feedback. Supported4096common raw/beauty validation independently passes current2a55 and records six numeric historical7b/cbb failures, with the sampler/material policy differences described in final-2a55/supported-common.

These source-cost observations keep their generation, machine-load and historical-quality scope. Full matched effective-quality/performance acceptance continues at the final integrated production source with its independent raw, native behavior and measured-window evidence.

Original records remain at F:/CodexProjects/2026-10-03/new-chat-2/work/rg70. source-cost-attribution.json retains source-path/file-size/SHA256 identity and normalized per-scene values. The recorded measurement-cleanup.json establishes removal of the historical application directories after evidence retention; fresh acceptance apps are separately owned and versioned.
