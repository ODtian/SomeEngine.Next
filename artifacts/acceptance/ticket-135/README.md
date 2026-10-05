# #135 CPU/source checkpoint — not GPU acceptance

Owned branch `agent/rg70-ticket-135`, clean exact starting base `1667bd94c435e91c60006e4c504d3ab2abc0f2da`.

- Regression-only candidate: `d6085c7db41397db09c2e53cf143de5547fe88a3`. It is not an observed red.
- Production source/shaders/schema/content removal: `c8d427183ea2b15870bfa52b593bf6761b25eb3d`.
- Checked source: `fe15c1cd306e8765ebb45402ed151e7a192bc339`; sole follow-up fixes the inherited D3D12 sloped sampling fixture's compiler-instance call. No production shader changed after c8d42718.
- `git merge --no-edit codex/render-graph-regions-70` returned `Already up to date` before final checks, at integration tip `1667bd94c435e91c60006e4c504d3ab2abc0f2da`. No integration or protected tree was edited.

Durable explanation: `wiki/architecture/validation/Vsm-Depth-Coverage-135.md`. `version.json` records the exact source and 22 selected source/content/configuration SHA-256 values. Generated packets/catalog and application binaries are build products in this owned checkout, not committed executable acceptance.

## Actual retained commands and results

PowerShell, only this owned tree; SDK 10.0.112. Commands with logs below exited 0.

```powershell
git submodule update --init external/Alimer.Bindings
dotnet run --project templates/DefaultProject/Game.Build/Game.Build.csproj -c Release -- --project "$PWD/templates/DefaultProject" --engine "$PWD"
dotnet build tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj -c Release -v minimal
dotnet build tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release -v minimal
dotnet build tests/SomeEngine.Graphics.Direct3D12.Tests/SomeEngine.Graphics.Direct3D12.Tests.csproj -c Release -v minimal
pwsh -NoProfile -File tools/Check-TestRules.ps1 -ResultsDirectory artifacts/acceptance/ticket-135/rules
git diff --check
```

| Record | Actual result |
| --- | --- |
| content-build.log | 68 assets from source, including current shaders; final of two successful coherent cooks retained |
| runtime-release-build.log | 0 warnings / 0 errors; final production source after integration merge |
| cluster-release-build.log | 0 warnings / 0 errors; includes Game.App, final production source after integration merge |
| d3d12-release-build.log | 0 warnings / 0 errors; final fixture follow-up source after integration merge |
| test-rules.log and rules/ | 23 projects / 0 violations; final checked source |
| version.json | Production/checkpoint/integration/baseline identities plus source/content hashes |
| Test discovery/execution | **0 discovered, 0 executed, 0 passed, 0 failed, 0 skipped — no tests/list-tests run** |

Initial missing pinned Alimer/MeshOptimizer build failed with 2 warnings/8 errors; own submodule initialization resolved it. Initial D3D12 project build failed with 0 warnings/1 CS0103 error in an inherited bare ExecuteSource call; unchanged-behavior fixture correction resolved it. These compile failures are **not rendering/TDD reds**. Final logs replace repeated intermediate logs; no temporary script/probe or non-acceptance experiment was retained.

## Hardware readiness and blockers

**READY for serialized validation, BLOCKED pending parent authorization after reference-125 releases and its approved integration.** No adapter/device discovery, theory discovery, rendering execution, RT, trace, calibrated GPU or three-scene performance run occurred. New public full-image filtered receiver-expansion behavior is prepared; its baseline sensitivity and green are unobserved. Existing #133/#134 transition/failure cases and #125's independent truth must be rerun on the final combined source.

The proposed first hardware filter is `FullyQualifiedName~VsmCoverageImageTests`, through Runtime's public startup/SceneScript/SceneImage seam. This is a future command selection, not a discovered count or a pass. All independent reference/capture/local-light fixtures remain reference-125's work; they were not edited or recreated here. No denied access was retried, no issue was closed, no push or PR was made.

Keep all independent receivers and gates: mean/mismatch/false/missed ≤2%, coverage ≥98%, ≥32 shadowed / ≥512 lit, zero unexpected self-hit. Beauty/RT, cancellation/partial failure, local missing/unfinished/coarse/recovery, alpha/geometry/material/streaming and supported backend/raster output coverage remain unverified at this checkpoint. Final matched complete three-scene measurements and Standards/Spec remain open; no speedup or equal-effective-quality claim is made. #135 acceptance, #125/#127/#81 and parent gates are not closed by these CPU checks.
