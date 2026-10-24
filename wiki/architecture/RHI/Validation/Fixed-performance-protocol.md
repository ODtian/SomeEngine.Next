# RHI-EVID-003 — Fixed performance protocol 

The benchmark schema is `someengine.graphics.performance/v3`. Formal vendor certification fixes:

```text
8,192 warm-up frames
16,384 measured frames
5 interleaved process rounds
10,000 draw/state calls
4,096 barriers
Interface RHI, Direct Silk and Native C++ receivers
```

A three-process 512/1,024-frame fast diagnostic may expose order, frequency or variance bias but can
never certify performance. A one-process 64/256-frame developer probe may select workloads/managed
variants and can never emit a certification PASS.

The protocol records adapter LUID/vendor/device/driver, CPU affinity and priority, power mode,
Agility SDK, validation/DRED state, build hashes, commit/dirty state, execution order, fresh samples,
R-7 percentiles, paired deltas and output equivalence.

High-water command workloads additionally require zero managed allocation, reflection traversal,
string lookup, runtime shader compilation, PSO creation, hidden wait and hidden barrier. Growth frames
are measured separately rather than hidden in warm-up.

The representative CPU-frame schema is
`someengine.graphics.representative-cpu-frame/v1`. It is a separate non-rendering audit that fixes:

```text
1,025 object packets
2,050 draws
40 reusable materials
107 actual material-binding emissions
9 command lists
4 explicit pass barriers
3 persistent recording workers in the parallel variant
2,048 warm-up frames
2,048 measured frames
8 interleaved process rounds; each consecutive 3-round block rotates the 3 treatments
Direct C# through Silk.NET, Native C++, and Interface RHI through Silk.NET
```

Its wall-clock interval begins before object-packet generation and ends after the ninth command list
is closed. It contains no Queue submission, GPU wait or Present. Every implementation consumes the
same source-derived material sequence and must report the same operation counts and workload identity.
This audit is the authority for RHI CPU frame-construction overhead; the older repeated-call loops
remain attribution microbenchmarks only.

## Managed benchmark inputs and reports

`SomeEngine.Graphics.Benchmarks` exposes command-line configuration through `BenchmarkOptions.Parse`,
sample and report values through `FrameSample`, `WorkloadRun` and `WorkloadMeasurements`, and report
aggregation through `BenchmarkOutput.Complete` and `MetricDistribution`. Managed benchmark callers
can construct and inspect the same reports consumed by the command-line tool. Frame samples attribute
allocation ticks to intervals that recorded managed allocation, and report post-close cleanup separately.

`RepresentativeFrameProfile` exposes worker ranges and object-packet generation for callers producing
the shared managed/native workload content. Benchmark runners keep command recording and state
suppression within their implementations; viewport comparison uses the public `Viewport` value equality.
^rhi-evid-003
