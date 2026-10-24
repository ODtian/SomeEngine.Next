# Sparse buffer range measurements

```powershell
dotnet run --project benchmarks/SomeEngine.RenderGraph.Benchmarks -c Release
```

Requires Windows and a hardware Direct3D 12 adapter. The benchmark uses 64 readers, either one or 128 disjoint ranges, 16 warmup frames and 48 measured frames per case. Four repetitions alternate ordinary `Read` declarations and a shared immutable `BufferRangeSet` with `ReadRanges`.

JSON reports registration and execution median/P95 in microseconds, plus calling-thread allocated bytes. Execution includes graph analysis, submission and state commit. The GPU wait occurs outside the measured interval, and validation is disabled. These are CPU measurements of repeated buffer-read pressure; they are not GPU frame times or a whole-renderer speedup.

Avoid concurrent builds, captures and other GPU workloads when comparing results. Preserve the adapter, all repetitions and P95 values instead of selecting only the fastest sample. See the [implementation record](../../wiki/architecture/Render-Enhancement-Optimization-2026-09-05.md) for the measured range and original conditions.
