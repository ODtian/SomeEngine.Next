# SomeEngine.Job Benchmarks

Runs allocation-light scheduler scenarios and reports p50/p95/p99 wall time.

```powershell
& tools/Invoke-Benchmark.ps1 dotnet run --project tools/SomeEngine.Job.Benchmarks/SomeEngine.Job.Benchmarks.csproj -c Release -- --samples 20 --warmups 5 --length 1000000
```

Options:

- `--auto` uses `JobScheduleOptions.AutomaticBatchSize` for parallel jobs.
- `--workers N` selects the worker count.
- `--spin N` and `--busy-spin N` compare wait policies.
- `--counters` includes the default runtime diagnostics path.
- `--synchronous` compares `ScheduleParallel(...).Complete()` and `ExecuteParallel(...)` with the
  same typed work, length and batch size. Each sample covers `--calls N` complete calls (default
  128), including queue submission, calling-thread work, worker completion and batch return.
  It reports p50/p95 microseconds per call and p50 process-wide managed bytes per call after the
  configured warmups. The allocation count includes worker threads.
- `--dependencies --workers 0` measures complete `CombineDependencies(...).Complete()` calls for
  1, 16, 256, 4,096 and 16,384 distinct predecessors, with one and two copies of each input.
  Input jobs are submitted before timing; zero workers keep them ready until the measured call
  executes their bodies and joins the dependency result. Each row reports the same p50/p95 time
  and process-wide managed allocation boundary.

Run benchmark processes serially. Concurrent runs compete for the same CPU workers and make tail
latency comparisons meaningless.
