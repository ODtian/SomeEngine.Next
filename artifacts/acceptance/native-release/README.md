# Native release concentrated investigation evidence

**Not reproduced / no diagnosed native repair. GPU RELEASED.** See `wiki/architecture/validation/Native-Release-Investigation-70.md` for source findings, scope and remaining blocker.

Base `86555c9fbf9651d0eaf9d4916b2b571d10a58f9d`; regression-only `82f7814e`; integration merged as **0755a124862bab28d5f77ec667a696b03aacccd5**, the final executed source.

## Final result

- `final-batch/final-batch.trx`, `final-batch.log`, `final-command.json`: ordinary one-testhost class, **7 total/executed/passed, 0 failed/skipped**, Completed outcome. Outer wall167.4335518s is not GPU time.
- `final-batch/local-ray-*/`: all final raw RGBA16F, beauty BMP, submitted-frame metadata, capture/content provenance, full receiver CSV and comparison PNG/BMP/JSON. Both old/new recovery footprints and three repeated-startup comparisons remain.
- `final-comparisons.json`, `final-application-results.json`: all **11** final comparisons and **27** successful public application captures; unchanged independent gates.
- `test-totals.json`: actual completed-row counters for five GPU hosts plus device-free output checks. GPU repeated-observation total27/27, not27 distinct tests or proof of a repaired crash.
- `capture-counts.json`:99 application captures/41 comparisons across the five completed hosts. Duplicate exploratory successful capture directories were removed; all their TRX/logs/commands/counts remain.

## Failed historical runs are separate

`inherited-aborts.json` identifies/hashes the already committed two #125 and one #135 TRX failures: Failed process outcome, total/executed/results0, fatal0xC0000005 at Marshal.Release/ComObject.Finalize/GC.RunFinalizers. Those original TRX/logs remain in the copied reference-125/ticket-135 evidence roots. Rendering completion is not a passed test. No new abort or crash dump occurred here. Diagnostic commands used public `--blame-crash --blame-crash-dump-type mini`; they passed.

## Source/content/environment and preparation

- `base.txt`, `initial-status.txt`, `regression-before.diff`: exact base, clean check and test-only change before any attempted repair. There was no implementation patch.
- `inspected-source-hashes.csv`: permitted current public harness/runtime/native lifecycle source bytes examined.
- `slang-*-decompiled.txt`, `reference-package-source.csv`: small installed public SlangShaderSharp2026.17.1 API/marshaller excerpts and digests; they are not native/private source and do not diagnose an invalid reference.
- `dependency.log`, `build-baseline.log`, `build-regression.log`, `build-final.log`, `cook.log`: pinned own dependency, successful Release builds and fresh compatible68-asset content cook.
- `content-payloads.csv`, `content-recheck.json`:69 payloads incl.catalog, all hashes/sizes match. Original sphere source digest in `reference-package-source.csv`.
- `baseline-executable.csv`, `final-executable.csv`, `post-cpu-executable-check.json`: generation-specific managed/native library/runtimeconfig digests; all final test-directory bytes still match after CPU checks. No historical agent executable identity claim.
- `hardware.json`, `dotnet-info.txt`, `jit-environment.csv`: RTX3080/driver32.0.16.1074, SDK10.0.112/runtime10.0.12 and JIT environment.
- `rules-completed.log`, `rules-completed/test-rules.log`, `rules-command.json`: complete24-project rule check exit0/no violations. `rules-final*` is the earlier incomplete180s harness-timeout record, not a successful check.
- `output-cpu*`: only device-free FrameOutputTests5/5; denied standalone ReferenceShaderTests not selected.

Preparation from the owned absolute checkout:

```powershell
git submodule update --init external/Alimer.Bindings
dotnet build tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release -v minimal
dotnet run --project templates/DefaultProject/Game.Build/Game.Build.csproj -c Release -- --project F:/SomeEngine.AgentWork/rg70-remaining/trees/native-release/templates/DefaultProject --engine F:/SomeEngine.AgentWork/rg70-remaining/trees/native-release
git merge --no-edit --no-stat codex/render-graph-regions-70
```

Per-run command JSONs include actual filters, source, exits and walls. Public outputs use owned absolute paths and restored SOMEENGINE_SHADOW_EVIDENCE/SOURCE/TEST_ARTIFACTS. No list-tests, default three-scene, performance, forced GC, feature reduction, native mutation, denied-access retry, push/PR/issue closure. Parent owns next integration/full acceptance.
