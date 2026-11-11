# ECS measurements

This tool measures ECS workloads and writes samples and distributions as JSON. Measurements do not determine whether engine functionality passes. Each run uses the target chosen for the workload and the shared queue records its command, configuration and exit status.

Use `just ecs-perf` for the short smoke defaults. `--profile standard` selects a larger default workload. Both profiles accept `--entity-counts`, `--warmup`, `--samples`, `--query-iterations`, and `--structural-iterations`. `--output` writes the report atomically. Invalid input or an execution failure returns a nonzero exit code; the shared queue can terminate ordinary measurements at the first sample that misses their declared target.

Tool tests belong to the Performance category in `SomeEngine.slnx`. Run them with `dotnet test tests/SomeEngine.ECS.Benchmarks.Tests/SomeEngine.ECS.Benchmarks.Tests.csproj --filter Category=Performance`. These tests start the command-line tool and verify argument errors, report output and reported distributions through its exit code and JSON data.

Native AOT execution is available through `just ecs-aot <rid>` or `just ecs-aot-matrix <rids>`. Those commands publish and start native executables; managed execution is not a substitute. The matrix records per-RID execution results and preserves output-file protection.

Random ECS regressions use an independent reference model. `just ecs-fuzz-campaign <seed> <steps>` selects a campaign and `just ecs-fuzz-replay <trace>` replays saved operations. Unconfigured optional campaign tests are skipped.

The current report schema is 7. Samples contain elapsed time, allocations, garbage collections, working set and the actual payload/update/write/read measurements. Structural changes run through native component and command operations; the former COW candidate and cloned-storage counters have been removed from this report.

Queries use QueryScope and chunk spans. Spawn setup uses concrete IBundleWriter structs. Raw checkpoints use WorldSerializer.Save/ReadWorld; durable writes use BeginWrite/Commit and BeginRead. Space, key, bitmap and handwritten traversal share Relationships data and the same ExecuteRanges partition stages.
