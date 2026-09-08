# ECS measurements

This tool measures ECS workloads and writes samples and distributions as JSON. Measurements do not determine whether engine functionality passes. There is no certification profile, baseline prerequisite, absolute budget, source cleanliness prerequisite, or external evidence manifest.

Use `just ecs-perf` for the short smoke defaults. `--profile standard` selects a larger default workload. Both profiles accept `--entity-counts`, `--warmup`, `--samples`, `--query-iterations`, and `--structural-iterations`. `--output` writes the report atomically. Invalid input or an execution failure returns a nonzero exit code; elapsed time and allocation measurements do not.

Tool tests are separate from `SomeEngine.slnx`. When explicitly requested, run their project at `tests/SomeEngine.ECS.Benchmarks.Tests/SomeEngine.ECS.Benchmarks.Tests.csproj`. These tests cover argument errors, report output and distribution calculations.

Native AOT execution is available through `just ecs-aot <rid>` or `just ecs-aot-matrix <rids>`. Those commands publish and start native executables; managed execution is not a substitute. The matrix records per-RID execution results and preserves output-file protection.

Random ECS regressions use an independent reference model. `just ecs-fuzz-campaign <seed> <steps>` selects a campaign and `just ecs-fuzz-replay <trace>` replays saved operations. Unconfigured optional campaign tests are skipped.
