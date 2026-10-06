# Supported common configuration: source and quality attribution

This diagnostic uses AtlasSize4096/PageSize128 (1024 page layers), with the other current configured shadow fields retained, on native production7b03495c, cbb28b28 and2a55ca90. Historical sampler, clipmap policy, resource limits and renderer state belong to their original source commits. Current native defaults use8192 and have separate three-scene quality/performance evidence.

## Actual discovery and execution

| Renderer | Test-material host | Discovered / executed / passed / failed / skipped | Command wall seconds |
| --- | --- | --- | ---: |
| pre71 7b03495c | 7f01dfff | 3 / 3 / 0 / 3 / 0 | 284.4144566 |
| recent fast cbb28b28 | 40f0e2c3 | 3 / 3 / 0 / 3 / 0 | 287.2137520 |
| current 2a55ca90 | 652e880b | 3 / 3 / 3 / 0 / 0 | 300.2595874 |

These are independent raw/beauty correctness hosts. Their wall times include two64-frame captures and source-triangle hardware-RT validation for each scene, with original TAA/async/mixed rendering and moving spheres. The performance platform retains its separate60-second feedback constraint.

## Complete receiver results

The aggregate includes boundary pixels, geometric shadow/light receivers and coverage errors. All nine captures keep the original thresholds: mismatch, mean visibility error, false-shadow/lit and missed-shadow/shadowed fractions≤2%; coverage≥98%; at least32shadowed/512lit pixels; self-hits0.

| Renderer / scene | Receivers | Mismatch and mean % | False/lit % | Missed/shadowed % | Coverage % |
| --- | ---: | ---: | ---: | ---: | ---: |
| pre71 / spheres | 135592 | 24.40852 | 21.159977 | 56.97726 | 97.93646 |
| pre71 / Sponza | 488646 | 50.31986 | 4.0033925 | 53.293 | 100 |
| pre71 / Bistro | 790033 | 28.843224 | 9.486024 | 50.807583 | 99.99481 |
| cbb / spheres | 135592 | 44.328573 | 42.718107 | 29.02363 | 97.93646 |
| cbb / Sponza | 488646 | 14.159535 | 2.3918575 | 14.914922 | 100 |
| cbb / Bistro | 790033 | 5.1690247 | 6.94007 | 3.155997 | 99.99886 |
| current / spheres | 135592 | 1.9470176 | 1.45302275 | 1.827909 | 99.518406 |
| current / Sponza | 488646 | .10805369 | .8922816 | .057712704 | 100 |
| current / Bistro | 790033 | 1.7658755 | 1.8698264 | 1.6467834 | 99.999493 |

Every scene reports self-hits0. Sphere coverage also fails on both historical sources. Historical/common matching requires a qualifying quality baseline; these historical numeric failures are retained as actual source/quality attribution.

## Submitted data and material scope

All versions captured scene/submitted frame63/63,1.0500001scene seconds,1280×720, jitter(-.4375,.3888889), RTX3080/driver32.0.16.1074. Camera/view/jittered and unjittered projections, lights, configured shadows, temporal settings, raster edge threshold4, cluster capacity1048576 and source geometry match across each corresponding scene. Independent imports produce different imported mesh asset identifiers; all1024/1/1532submitted world-transform multisets match. The oracle verifies each imported transform against original source geometry.

The first historical hosts included the latervsm_material.slang header, absent from7b/cbb. Each discovered/executed3tests and failed3fixture shader compilations after real beauty capture. The preserved *-include-failure directories record that compatibility history. The corrected historical TEST MATERIAL declares the same uniform fields and resource bindings as its own ordinary standard_material.slang16–38 and calls its originalvsm_sampling.slang public sampler. The exact test-material diff is retained.

The normative raw visibility fixture uses triangle geometric normal for receiver bias. Historical ordinary beauty uses mappedNormal*NormalBias (standard_material.slang154–175); both actual beauty and raw outputs are retained with their distinct call scopes. The fixture comment about the ordinary geometric plane refers to the current-source rule. Historical directional selection remains projected shortest-axis footprint/user-only view bias (vsm_sampling.slang444–462 and VirtualShadowMap.cs312). Current2a55 remains camera-distance selection/projection-density calibration with OrthographicReceiver. These policy/call differences explain why a configured-field match still needs measured quality correspondence.

supported-common-quality.json retains full aggregate/interior/boundary comparisons, unchanged thresholds and individually computed gates; exact commands/discovery/results/source/config identities; capture and CSV SHA256 hashes; frame-data and transform correspondence. Selected-mip and whole/partial-validity histograms, original historical requested demand and pressure-unit equivalence retain separate diagnostic obligations. The current default measured-window typed quality/GPU evidence retains267/253/244complete samples at its own8192configuration.

The fixed five NONTRACE reports stay native cost attribution: native7b/cbb and current2a55 complete all three scenes; shared8192old hosts reject4096pageHiZ array layers at their original D3D12 capability boundary. The common4096correctness result now supplies a concrete supported-capacity check and six historical raw quality failures. Final native quality/performance and new native-alignment fixes are assessed at their actual integrated source checkpoint.

Full raw/BMP/PNG/CSV and original comparison.json files remain at F:/SomeEngine.AgentWork/rg70-remaining/final-measurement-81/supported-common, with the small reviewable subset committed under artifacts/acceptance/performance-81/final-2a55/supported-common.
