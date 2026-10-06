# #81/#127 final2a55 feedback and remaining comparison obligations

Production source2a55ca9006e586072d11ea01193662deee567d4f contains executable18dce456; final68-asset/three-scene content was freshly cooked/imported from the same frozen original inputs. DefaultGame1280×720, moving1024 spheres/static Sponza+Bistro, fixed1/60, Release/.NET10.0.12, tieringfalse, RTX3080 driver32.0.16.1074, TAA/VSM/async+availablemesh default; original32ready/128warm/3sec warm/2sec sample and45sec platform/60sec feedback gates.

## Actual fixed-order NONTRACE feedback

One controlled CPU/GPU-idle sequence, one feedback per source; individual commands/logs/results retained. Source-only config adapters change four caller-setting selection lines and keep their original rendering policy. All source/binary/config/scene hashes passed before starting.

| Run | Source | Outcome | Outer seconds | P50 sphere/Sponza/Bistro ms |
| --- | --- | --- | ---: | --- |
| Original pre71 native defaults | 7b03495c | Complete/3Measured/exit0 | 45.4038 | 3.8708 /2.6993 /6.8406 |
| Recent fast native defaults | cbb28b28 | Complete/3Measured/exit0 | 45.0862 | 3.7834 /1.7850 /3.0865 |
| Pre71 with final8192 configuration | 1f33d2fd on7b | Startup NativeFailure/0samples/0reports/exit1 | 9.9332 | unavailable |
| Recent fast with final8192 configuration | c1904b34 oncbb | Startup NativeFailure/0samples/0reports/exit1 | 10.4487 | unavailable |
| Final production defaults | 2a55ca90 | Complete/3Measured/exit0 | 45.5927 | 7.2373 /7.4106 /7.6573 |

Final P95ms8.0208/8.3522/8.5725,278/270/263 samples. Native historical and current workloads retain their different settings/policies: old4096atlas/bias0/.0005(pre71)or.00002(cbb) depth/.01normal versus current8192/-2/.000001/.001. Pre71 native dynamicresolutionfalse; cbb/currenttrue. Historical directional selection uses projected shortest-axis receiver footprint and user-only viewbias;9e/current uses camera-distance selection with projection-density calibration. These native timings are source cost attribution; full matched-quality/performance acceptance remains dependent on the supported shared comparison and the original correctness/diagnostic obligations.

The retained original7b→migration-only16c source-cost pair, exact original commands/settings/content identities and separate complete CPU/GPU diagnosis are also committed at ../original-migration-7b-16c. Its legacy-generation quality and actual machine-load scope are retained alongside the fresh native and supported-common observations.

## Proved old capability boundary and separate supported diagnostic

Both7b/cbb VirtualShadowFeature constructors allocate pageHiZ as64×64/R32Float/7mips Texture2D array with ArrayLayers=settings.MaxPhysicalPages. Shared8192atlas/Page128 requires4096 layers. Their D3D12Device advertises MaximumTextureArrayLayers2048; GetTextureMemoryRequirements reaches native rejected allocation(SizeInBytes=ulong.MaxValue), raising GraphicsError.NativeFailure at graphcommit. Logs retain exact source stack locations. The renderer was retained at its own commit; capacity changes were not imported into the old source.

shared-supported-shadow-settings.json is a separate diagnostic4096atlas/max1024 config with the other final configured quality fields retained. Independent raw/actual-quality checks precede any supported-common cost comparison. Native final defaults and their full-quality results remain recorded separately.

The supported common4096independent raw/beauty hosts are now complete: current2a55 3discovered/3executed/3passed; historical7b andcbb each3discovered/3executed/3numeric failures, with the complete unchanged receiver gates. Sphere/Sponza/Bistro all-receiver mismatch percentages are24.40852/50.31986/28.843224(pre71),44.328573/14.159535/5.1690247(cbb), and1.9470176/.10805369/1.7658755(current). The corresponding raw/beauty data, fixture compatibility history, exact historical sampler/material call differences and qualifying-baseline obligation are recorded in supported-common/README.md and supported-common-quality.json.

## Actual final independent reference and beauty

3 discovered/3executed/3passed/0failed/0skip; standard automatic test rules0violations. Captured actual scene/submitted63/63, scene1.0500001s, jitter(-.4375,.3888889), full raw linear visibility beforeTAA/tone map plus actual beauty.

| Scene | Receivers | mismatch AND mean % | false/lit % | missed/shadow % | coverage % | selfhit |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| spheres | 135592 | 1.9470176 | 1.45302275 | 1.827909 | 99.518406 | 0 |
| Sponza | 488646 | .10723509 | .8719253 | .05814827 | 100 | 0 |
| Bistro | 790033 | 1.765496 | 1.868874 | 1.6470537 | 99.999493 | 0 |

All receivers/edges and the original2%/98%/.15-error gates retained. Bistro beauty produced an independent actual source-material RGB issue#152: original/source19 sign emissionred100 and importedred100, while exact projected source sign geometry includes823 high-energyblue pixels in the raw preTAA output (Bmax100,Rmax.06064). See Bistro-Emissive-152.md/source JSON. Authoredblue stringlight121 is separately attributed. Directional visibility/reference passes retain their own scope.

## Separate calibrated GPU and CPU diagnosis

Disposable observer8a6b495b retains original backend/device/context/cache/repeated payloads/waits/signals/completions, with bounded first/last queue markers and one8-byte used-bias readback. Every original frame/sample remains represented by exact IDs; typed geometry is delivered at the existing completed graph readback. Immutable actual view definitions are referenced by each frame.

Complete three-scene Trace feedback, exit0, outer47.9446sec,267/253/244 samples; every quality/geometry/GPU/trace/submitted frame join complete,0missing. GPU queue-spanP50ms7.80894/9.27977/11.13104; mean10.18688/11.44323/11.98427. Spans include submit/queue/dependency gaps, excludePresent/otherprocesses, and carry no busy-time sum interpretation. Actual directional view biases.23659372/.7156656/.7156656 with applied directional/local additions0 across the sampled windows.

Independent CPU trace analysis: complete,0dropped/misuse/issues; exact measured IDs348..614/353..605/317..560. Per-frame inclusive Cluster.Update .2186/.3468/1.3291ms, Prepare .2659/.0838/.1244, Reconcile .01063/.01025/.01248, ExecuteFrame1.4545/1.6417/3.2468, CpuFenceWait4.7226/4.8498/3.6646. Nested/thread scopes remain separate; CPU fence waiting is recorded as CPU time.

Old9e observer8eff has complete local51/175/48 joins but a global incomplete Bistro sample window; its exact false/exit1 report is retained. Earlier capability/startup/budget failures are retained under their own names. Old full calibrated feedback remains an explicit#81 gap. Old whole/partial validity, original-demand and v2-pressure equivalence and per-pixel selected-mip fields retain their unavailable scope. The final-source result demonstrates complete current diagnosis, preserving the earlier global gap.

Full raw/BMP/PNG/CSV/trace/binaries/content hash manifests are retained externally atF:/SomeEngine.AgentWork/rg70-remaining/{historical-refresh-81,final-measurement-81}. Source-compatible owned apps/inputs preserve historical and current generation identity. This committed subset contains reviewable small commands/results/validation tables and the independent color-attribution evidence.
