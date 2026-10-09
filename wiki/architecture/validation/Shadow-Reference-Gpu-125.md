# Public linear capture and concentrated local RT validation (#125)

原始输出已移入仓库外验收归档；文中的 `artifacts/acceptance/` 路径对应归档内目录。归档身份与结果摘要见 [#70 验收记录](../../../docs/agents/acceptance-70.md)。

## Result and boundary

**Public selected-frame raw capture and six targeted local-light cases are validated on the delivered #134 integration. #125 remains open; the whole focused acceptance scope remains partial. GPU was explicitly released after the final six isolated runs.** No final three-scene RT/beauty/backend/performance run occurred, and no #135 production acceptance is implied.

Initial capture/reference delivery: `3d9c1999cccd2abe335d59903d777484325eabdd`. Integration advanced to `1667bd94c435e91c60006e4c504d3ab2abc0f2da`; merged without conflict as `0a7a3d7721ae301245b900d30f4b1295b5b1d42e` before the GPU cases. All final local cases execute immutable source **`478bafa9915d1e717c4ec74486bf5007fab1bdb5`**. The later documentation/evidence commit does not change that executable code.

No Cluster/VSM production chain, Runtime ShadowImageTests, or protected checkout was edited by these observation corrections. See [capture design](Shadow-Reference-Capture-125.md) for the public API and initial CPU evidence.

## Actual selected-frame capture

Owned Release Game.App, source `0a7a3d77…`:

```text
--test-scene <owned DefaultProject> --frames 4 --capture-frame 2
--hidden-window --fixed-step 0.016666667 --no-vsync --no-debug-ui --no-vsm
--frame-output <owned proof>/selected-beauty.bmp
--linear-frame-output <owned proof>/selected-color.rgba16
```

Exit 0; four frames completed within the 60-second hang bound (14.085 s outer wall time). Scene/submitted frame **2/2** captured, not the final frame 3. 1280x720; raw byte count **7,372,800**; 1024 current transforms; jitter **(0.25, -0.3888889)** pixels; scene time `.033333335`. TAA/async/motion remain on; synchronous/forced-HW/mesh-disable flags remain false. Beauty, raw color, metadata, command, logs and file hashes are retained. VSM was disabled only for this initial capture-path isolation; the local-light cases below enable VSM.

This verifies format/copy/readback compatibility by public output, without re-reading denied resource source. It does not time default overlap or substitute for performance evidence.

**Invalid wrapper attempt retained separately:** the first shell wrapper used `IO.Path.GetFullPath` after PowerShell Set-Location and resolved the application/output root against `F:/SomeEngine.Next`. That process exited 1 in .352 s; log writes failed. Its output is not accepted evidence. No protected source edit, protected-tree retrieval, cleanup or replay was performed. Subsequent commands use explicit absolute owned paths. `invalid-wrapper-attempt.json` records the incident; device creation in that invalid attempt is unknown.

## Final local results — one fresh testhost per row

Actual discovery listed six rows. Each final run selected/executed exactly one row, exited 0, and reported one pass/no failure/no skip: **6 executed / 6 passed / 0 failed / 0 skipped**, all source `478bafa9…`, D3D12/RTX 3080 driver `32.0.16.1074`. In-process public Application startup retains default TAA/async/available mesh/mixed raster; no feature-disabling diagnostic route was used.

All fractions below use **all** eligible source receivers; boundaries remain in the denominator. Mean error happens to equal mismatch fraction in these hard-shadow outputs, but is independently reported and gated.

| Final case | Shadowed / lit | Mismatch and mean | False / lit | Missed / shadowed | Coverage / self-hit |
| --- | ---: | ---: | ---: | ---: | ---: |
| point | 3839 / 38812 | .1078521% | .0566835% | .6251628% | 100% / 0 |
| spot | 3839 / 38812 | .11957516% | .07471916% | .57306592% | 100% / 0 |
| point-range | 2894 / 37846 | .26018654% | .1664641% | 1.4858328% | 100% / 0 |
| spot-cone | 3839 / 18938 | .2151293% | .13200971% | .6251628% | 100% / 0 |
| point moving/canceled-refresh, new footprint | 4449 / 38149 | .12911404% | .086502923% | .49449313% | 100% / 0 |
| spot moving/canceled-refresh, new footprint | 4449 / 38149 | .15493685% | .10223073% | .60687796% | 100% / 0 |

Both recovery rows also pass the **old frame-31 footprint** with the corresponding base point/spot counts/fractions above. Final scene/submitted indices are **63/62** after canceled frame 32. Raw old/new controls and separate full comparisons are retained. The pressure configuration is virtual4096/atlas1024/page128/one light, no dynamic resolution: **configured 64-page pressure**, not proof of a specific resident mip.

Point-range has **1915 independently out-of-range** pixels; spot-cone has **20075 independently out-of-cone** pixels. Point fixtures exercise three source dominant-axis faces (+X, -Y, -Z), not all six. Range boundary/interior counts and errors remain separate; e.g. point-range boundary mismatch is 105/2897 (3.6244%) and is not filtered from the .2602% aggregate.

**Unchanged gates:** pixel error >.15; aggregate mismatch and mean <=2%; false/lit and missed/shadowed <=2%; receiver coverage >=98%; >=32 shadowed/>=512 lit; zero unexpected self-hit. No receivers or failed cases were removed to obtain these final passes.

## Test-proven observation/reference corrections

1. **Finite selection in the public fixture.** First four local rows failed Startup before rendering: the helper supplied SceneScript.FrameCount but omitted the CLI finite frame argument required by the raw destination parser. Commit `160c47182628552d350fd3376755527dbe43220a` supplies the same public `--frames` value and records cooked fixture payload/catalog hashes per capture. This is not a VSM failure.
2. **Authored fixture sample adequacy.** Range 7 excluded the whole caster footprint: actual independent reference had zero shadowed receivers. Recovery's old x=2 caster target moved its new footprint off the receiver, also yielding zero shadowed receivers. Commit `bccb3f0d04893de5e254275660a1ac3f23793316` uses range9.3 across the receiver/shadow boundary; `abb28111defa399deabf19d594bdf854d02a2469` moves the caster to x=-1 and generates both old/new comparisons before applying assertions. The same >=32 shadowed gate remains.
3. **Half color-ratio precision, with no receiver filter.** At range9.3/intensity30, missed shadow was 95/2894 (3.282654%). Public CSV showed 221/250 errors were indeterminate raw direct-color ratios, including 75/95 missed-shadow errors. Commit `2fc83ef7ddfd606808701627ee22f0f549d25b35` raises only that authored light's intensity to300, improving direct radiance against the ambient Half quantization step. Range, geometry, receiver population, VSM quality and thresholds are unchanged; intensity cancels in the isolated linear visibility ratio. Final reference populations remain exactly **2894 shadowed / 37846 lit**, with 43 missed shadows (1.4858%). Remaining indeterminate receivers still get full error, never exclusion. This does not validate arbitrary production-color ratios or replace a direct local visibility-mask material.
4. **False self-hit identity.** Before `478bafa9…`, both recovery rows passed numerical/coverage gates but reported self-hit=1. The independent TLAS custom InstanceID is a mesh geometry-table offset shared by caster/receiver instances of the same mesh. Comparing it plus primitive ID conflated distinct instances. The fix compares actual RT `CommittedInstanceIndex()` while retaining geometry/primitive equality and the zero-self-hit gate. Visibility populations/errors remain unchanged; old/new self-hit counts are zero. This corrects the reference's classification, not a production shadow bias.

These reds are observation/fixture/reference reds. They are **not** the required wrong-production-version default numerical red for final #125 acceptance.

## Aborted runs and infrastructure blocker

Two runs crashed in the native testhost with `0xC0000005`, `Marshal.Release` / `ComObject.Finalize` / `GC.RunFinalizers`: a four-row batch and an isolated point-range attempt. Both report zero completed result rows and are retained as **aborted**, not pass/skip or VSM numerical failure. Native ownership/root cause was not inspected or patched in this #125 window. Final isolated runs succeeded, but the original multi-row suite is not proven reliable. A later [native release investigation](Native-Release-Investigation-70.md) inspected permitted current lifetime paths and completed a new seven-case same-batch run on merged #135 source; the fault did not reproduce, so no root cause, repair or native red→green claim follows. These two original aborted runs remain failures. This remains a separate Runtime/RHI/tooling blocker for general unattended acceptance, not a diagnosed #135 cache defect.

There is **no observed production cache/missing-page numeric failure** to assign to #135 from the corrected six cases. #135 still owes the exact missing/fallback/no-resident/recovery obligations; successful pressure output cannot establish a resident-state transition without appropriate public evidence.

## Evidence, content and reproduction

Committed evidence root: `artifacts/acceptance/reference-125/gpu-window/`.

- `selected-capture-*`, beauty/raw/sidecar and hashes;
- `local-light-discovery.log` (six actual rows);
- final `point-final`, `spot-final`, `spot-cone-final`, `point-range`, `point-recovery`, `spot-recovery` `.trx/.log` pairs;
- `isolated-final-baselines.json`, `isolated-after-identity-fix.json`, `final-reference-summary.json`;
- startup/range/sample/self-hit red TRX/logs, CSV/radiance check and native aborted TRX/logs;
- per-capture `.capture.json` with exact public arguments, scene action frames, settings, source and cooked content payload/catalog SHA-256; metadata/raw/beauty/CSV/actual-expected-error PNG/BMP for completed references;
- content cook/build logs, payload/geometry/application hashes and final environment/runtime records.

Owned source-compatible content was cooked after #134 merge, not copied from a historical cooked catalog. Base catalog SHA-256 **6F0BE0AAFB3896B019BEECBD48B65D42BDA0842B104F850BAD870DD06FF5296A**; original ico_sphere.glb **40523D69E4E2C6FC1316114008B3DE26100108A3A5ED2A5601FF1B9FF6C89A83**. All base payload hashes are in `content-files-134.csv`; each derived light/ambient World and material mesh is hashed before capture. Cook-generated tracked import metadata was restored only in this owned checkout and is not part of the observation delta. Temporary fixture content was removed by its public-test cleanup; retained files suffice to identify/rebuild it through the same fixture.

The six final `isolated-*.json` records contain actual filters, source, outcome/counters and outer walls (35.7–40.4 seconds each). These are correctness runs, **not throughput measurements**. Final source is reproducible by the source commits above. After GPU release, final filtered CPU output checks passed **5/5**; Cluster/Runtime Release builds each had **0 warnings / 0 errors**; the default rule checker completed **24 projects / 0 violations**. The 69 cooked base payload hashes were rechecked after validation with **zero changes**; catalog/model hashes also still match. Example point-range command:

```powershell
# Set/restore SOMEENGINE_SHADOW_EVIDENCE, SOMEENGINE_SHADOW_SOURCE,
# SOMEENGINE_TEST_ARTIFACTS to owned absolute paths/source before each run.
dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~LocalLightShadowsMatchIndependentSourceTriangles&DisplayName~point-range' --results-directory <owned-proof> --logger 'trx;LogFileName=point-range.trx'
```

Six final filters and commands are durable JSON/TRX/log inputs, not invented future names. The full class discovery filter is `FullyQualifiedName~LocalLightRayShadowTests`. Re-run hardware only in a separately granted serialized window; GPU has been released.

## Remaining limits and authorization

Every previous access denial remains respected: no retry of private issue reads, denied worktree/source inspection, the denied standalone CPU reference shader compiler, Epic access or historical denied evidence. The reference pipeline did execute through the explicitly authorized public RT cases; the separate denied CPU/SPIR-V test did **not** run. Source format/copy compatibility was established by public output, not by revealing denied source.

Still required: direct point/spot numerical visibility material where color ratios are unsuitable; exact fine-miss/coarse-fallback/no-resident-mip fixtures and recovery; all-six cube faces, alpha/custom-deformation/streaming and backend matrices; effective adaptive work/LOD/timing diagnostics; stable native testhost execution; final wrong-version default red and post-#135 actual default three-scene RT/beauty/backend/performance with matched source/content/quality. Legacy edge-filtered CPU beauty comparison remains diagnostic. No Issue close/push/PR and no final performance verdict were made.
