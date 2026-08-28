# SomeEngine.Job Benchmarks

Runs allocation-light scheduler scenarios and reports p50/p95/p99 wall time.

```powershell
dotnet run --project tools\SomeEngine.Job.Benchmarks\SomeEngine.Job.Benchmarks.csproj -c Release -- --samples 20 --warmups 5 --length 1000000
```

Options:

- `--auto` uses `JobScheduleOptions.AutomaticBatchSize` for parallel jobs.
- `--workers N` selects the worker count.
- `--spin N` and `--busy-spin N` compare wait policies.
- `--counters` includes the default runtime diagnostics path.

Run benchmark processes serially. Concurrent runs compete for the same CPU workers and make tail
latency comparisons meaningless.
