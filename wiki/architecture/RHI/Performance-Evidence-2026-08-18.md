# RHI CPU Performance Evidence — 2026-08-18

> **Status: SUPERSEDED on 2026-08-20.** The current managed D3D12 implementation and
> Direct C# baseline must call the generated Silk.NET COM surface. The manual COM-vtable /
> `SuppressGCTransition` D3D12 path and the Concrete/ImplementationDiagnostic receiver have
> been removed. All timings and conclusions below describe retired binaries and a retired
> four-receiver protocol; they are historical investigation data, not current closure evidence.

This page records the final current-tree CPU command-construction audit. It replaces the earlier
10,000-call microbenchmarks as the primary answer to the question “how much real wall time does the
RHI add to a representative frame?” The reports remain `FunctionalOnly` because the working tree is
dirty; they are not clean-checkout vendor certification.

## Public workload basis

The workload shape is derived from two public sample families rather than invented round numbers.

Microsoft `DirectX-Graphics-Samples`, revision
`213dd4fd4918ea009dd8f35adee1aff1f2ecaba4`, supplies the concrete scene and command-list structure:

```text
Samples/Desktop/D3D12Multithreading/src/D3D12Multithreading.cpp
Samples/Desktop/D3D12Multithreading/src/stdafx.h
Samples/Desktop/D3D12Multithreading/src/SquidRoom.h
```

The sample records with three worker contexts, one pre-list, three shadow lists, one mid-list, three
scene lists and one post-list. Its `SquidRoom.h` draw table contains 1,025 objects. The shadow and
scene passes therefore produce 2,050 draws per frame.

Khronos `Vulkan-Samples`, revision
`120a6072470a5e861cf664f5adfad4b7bf80a531`, supplies the command-buffer and threading constraints:

```text
samples/performance/command_buffer_usage
samples/performance/multithreading_render_passes
samples/performance/descriptor_management
samples/performance/pipeline_barriers
```

Those samples motivate command-buffer reuse, bounded command-list count, work distribution across a
small worker set, descriptor reuse and explicit barrier accounting. They do not define a second RHI
model.

The public source was downloaded into `artifacts/public-render-workload-research`. The extractor
materializes a canonical 1,025-byte material sequence. Its SHA-256 is:

```text
4F69D660B527341D446A365853AA7FA8CCD853243853FE22CAB30D0608BB6AF0
```

The resulting representative frame contains:

```text
1,025 object packets, 16 bytes each
40 reusable materials
107 actual material-binding emissions after source-order run suppression
2,050 draws
9 command lists
4 explicit pass barriers
3 worker threads in the parallel variant
```

The timer begins immediately before object-packet generation and ends after the ninth command list is
closed. Queue submission, GPU execution, CPU fence waits and Present are excluded. This is deliberate:
the test measures the CPU cost of constructing one frame, not GPU throughput or presentation latency.

Two variants are executed:

```text
RepresentativeFrameSerial
    all nine command lists are recorded on the caller thread

RepresentativeFrameParallel
    three persistent worker threads record shadow and scene phases;
    thread wake-up and phase joins are inside the wall-clock interval
```

## Receiver and language matrix

Every adapter runs the same five implementations:

```text
Default C# direct D3D12
    ordinary Silk.NET generated command-list calls

Optimized C# direct D3D12
    the same C# workload with the audited backend fast-call whitelist

Native C++ direct D3D12
    current MSVC Release source, rebuilt before the run

Concrete RHI
    D3D12Backend concrete receiver

Interface RHI
    the same backend through IGraphicsBackend
```

The fixed protocol is:

```text
5 process rounds
5 receiver/language implementations
5-by-5 Latin square: every implementation occupies every process position once
4,096 warm-up frames per process
4,096 measured frames per process
R-7 P50/P95/P99
```

The 4,096-frame warm-up is required. A 512-frame audit run showed a Tiered-PGO transition during the
measured serial workload; that run is superseded and is not used below.

For each adapter the matrix validates:

```text
25 raw process reports
one managed payload hash
one native executable hash
identical shader manifest
identical canonical workload identity
exact draw and barrier counts
no GPU samples
zero managed allocation in every managed measured sample
```

Each adapter therefore contains 163,840 managed measured frame samples; the two matrices contain
327,680 allocation-free managed samples in total.

## NVIDIA RTX 3070 Laptop GPU

Adapter LUID `0x13AAF:0x0`; driver `32.0.15.7216`.

Latin-square adjusted P50 wall time:

| Workload | Native C++ | Default C# | Optimized C# | Concrete RHI | Interface RHI |
|---|---:|---:|---:|---:|---:|
| Serial frame | 54.837 us | 67.467 us | 64.750 us | 81.165 us | 80.734 us |
| Parallel frame | 113.717 us | 134.909 us | 127.646 us | 151.817 us | 150.112 us |

Model comparisons, including 95% confidence intervals:

| Workload | Default C# vs C++ | Optimized C# vs C++ | Concrete RHI vs optimized C# | Interface vs Concrete |
|---|---:|---:|---:|---:|
| Serial | +23.031% `[+18.801%, +27.412%]` | +18.076% `[+14.017%, +22.280%]` | +25.352% `[+21.042%, +29.815%]`, +16.415 us | -0.531% `[-3.951%, +3.010%]` |
| Parallel | +18.635% `[+13.860%, +23.610%]` | +12.249% `[+7.731%, +16.956%]` | +18.935% `[+14.149%, +23.923%]`, +24.170 us | -1.123% `[-5.102%, +3.024%]` |

Pooled distributions, 20,480 samples per implementation and workload:

| Workload / implementation | P50 | P95 | P99 |
|---|---:|---:|---:|
| Serial optimized C# | 65.6 us | 80.3 us | 105.521 us |
| Serial Concrete RHI | 81.4 us | 122.5 us | 141.5 us |
| Serial Interface RHI | 80.9 us | 119.3 us | 142.4 us |
| Parallel optimized C# | 127.7 us | 161.1 us | 186.363 us |
| Parallel Concrete RHI | 151.8 us | 193.6 us | 225.1 us |
| Parallel Interface RHI | 150.2 us | 193.905 us | 225.321 us |

## Intel UHD Graphics

Adapter LUID `0x10EC7:0x0`; driver `31.0.101.3222`.

Latin-square adjusted P50 wall time:

| Workload | Native C++ | Default C# | Optimized C# | Concrete RHI | Interface RHI |
|---|---:|---:|---:|---:|---:|
| Serial frame | 169.533 us | 204.730 us | 199.614 us | 216.329 us | 218.218 us |
| Parallel frame | 179.528 us | 215.123 us | 213.926 us | 242.142 us | 240.545 us |

Model comparisons, including 95% confidence intervals:

| Workload | Default C# vs C++ | Optimized C# vs C++ | Concrete RHI vs optimized C# | Interface vs Concrete |
|---|---:|---:|---:|---:|
| Serial | +20.762% `[+17.895%, +23.697%]` | +17.744% `[+14.949%, +20.606%]` | +8.374% `[+5.802%, +11.009%]`, +16.715 us | +0.873% `[-1.521%, +3.326%]` |
| Parallel | +19.827% `[+13.282%, +26.750%]` | +19.161% `[+12.652%, +26.045%]` | +13.189% `[+7.007%, +19.729%]`, +28.216 us | -0.659% `[-6.085%, +5.080%]` |

Pooled distributions, 20,480 samples per implementation and workload:

| Workload / implementation | P50 | P95 | P99 |
|---|---:|---:|---:|
| Serial optimized C# | 199.8 us | 261.805 us | 362.942 us |
| Serial Concrete RHI | 216.4 us | 287.6 us | 392.521 us |
| Serial Interface RHI | 218.2 us | 313.505 us | 403.4 us |
| Parallel optimized C# | 213.7 us | 276.5 us | 331.368 us |
| Parallel Concrete RHI | 241.9 us | 313.0 us | 349.3 us |
| Parallel Interface RHI | 240.7 us | 311.3 us | 348.3 us |

## What the audit found and changed

The representative workload found defects that the old repeated-call loops could not expose.

### Benchmark evidence defects

- A 512-frame warm-up measured Tiered-PGO compilation rather than stable execution. The fixed protocol
  now uses 4,096 warm-up frames.
- Interface benchmark forwarding methods were not inlined and created a benchmark-only interface
  penalty. The hot forwarding methods are now explicitly inlined. Final Interface-versus-Concrete
  intervals include zero on both adapters and both workloads.
- The first representative harness called `SetPersistentParameterBindings` once per object. The
  Microsoft sample suppresses material setters at the caller by source-order material run. All five
  implementations now emit the same 107 actual material bindings.
- Native barrier evidence previously read `vector.size()` in the same call that moved the vector;
  argument evaluation order could report zero. The count is now captured before the move.

### Product implementation defects

- Persistent binding capture used a global object lock for every command-list read. Binding now
  observes an immutable generation and retains it with a lock-free retry before native mutation.
- A failed visibility or capture check could leave the just-retained persistent generation unreleased.
  The pre-capture path now returns the retain in `finally`.
- The recording retained both the public persistent-binding wrapper and the immutable physical data.
  The wrapper was redundant; the data already owns descriptors, resources, ordinary-data storage and
  swapchain facts. Only the physical generation is retained now.
- Different persistent objects with equal bytes were deep-compared during state suppression. Public
  binding identity is object/generation identity; the cross-object deep comparison was removed.
- The same immutable generation was retained repeatedly within one command recording. A recording now
  detects an already captured generation and avoids a second atomic retain, visibility scan and
  ownership insertion.
- The public XML incorrectly said Pipeline and PersistentParameterBindings were wholly externally
  synchronized. Pipelines now explicitly permit concurrent immutable binding. Persistent bindings
  permit concurrent binding and generation publication; every bind observes one immutable generation.

Targeted execution tests prove that recorded commands remain valid after the public persistent
wrapper is disposed, and that four command-recording threads can capture a shared binding while a
fifth thread publishes 256 updates.

## Wrapper-library audit

The generated `ID3D12GraphicsCommandList.DrawInstanced` source was inspected for these exact package
revisions:

| Binding | Package / revision | Generated call convention | Automatic GC-transition suppression |
|---|---|---|---|
| Silk.NET Direct3D12 | `2.23.0` / `94605142f7b7bd6e69c9201e8e721d245c69eb7e` | `Stdcall` | No |
| Vortice.Win32 Direct3D12 | `2.5.0` / `dfaa17aa7a1ee2fba13a915171a69eacd4e716ac` | `MemberFunction` | No |
| TerraFX Interop Windows | `10.0.26100.6` / `7d3e679e74be9da3584b4d4c4689e10422cc485f` | `MemberFunction` | No |
| Hexa.NET D3D12 | `1.0.6` / `7fd99140db50c6fc52f128e13bc201f18ea95d60` | `Stdcall` | No |

`MemberFunction` selects the COM member ABI; it does not suppress the CLR unmanaged transition.
Changing the one-to-one binding library therefore does not remove this cost. The backend-private,
audited fast-call whitelist remains the correct design for short, non-blocking command-list methods.
Queue execution, waits, Present and object creation retain the ordinary transition.

The optimized C# path remains slower than Native C++ in the full frame-construction workload. That
remaining difference belongs to the C#/.NET execution path—JIT code generation, managed-loop
safepoints and related runtime work—not to the RHI layer.

## Interpretation and closure

The measured RHI cost is real and is not dismissed as “only a few nanoseconds per call.” For the
entire representative CPU frame, Concrete RHI adds:

```text
Serial
    NVIDIA +16.415 us
    Intel  +16.715 us

Parallel critical path
    NVIDIA +24.170 us
    Intel  +28.216 us
```

The near-identical serial absolute increment on two very different driver baselines is important. The
RHI does not multiply all driver work by one fixed percentage; it adds a bounded amount of provenance,
recording-state, ownership-generation, state-suppression and failure-boundary work. Its percentage is
therefore 25.4% on the faster NVIDIA serial baseline but 8.4% on the slower Intel serial baseline.

The final total Concrete RHI P50 remains:

```text
NVIDIA 81.165 us serial, 151.817 us parallel
Intel  216.329 us serial, 242.142 us parallel
```

No receiver-dispatch penalty remains, no measured managed allocation remains, and the audit found no
remaining redundant lock, wrapper retain, deep equality or duplicate generation capture in the
measured path. The residual RHI increment corresponds to the public safety and physical-lifetime
contract that Direct D3D12 deliberately omits.

The former closure based on these results is withdrawn. Current closure requires newly built Native
C++, Direct C# through Silk.NET, and Interface RHI through Silk.NET binaries, with the same workload,
native-Close boundary, allocation evidence, and hardware protocol. No performance conclusion from
this page may be carried forward without that remeasurement.

## Evidence files

| Evidence | SHA-256 |
|---|---|
| `representative-frame-nvidia-20260818-final/summary.json` | `FA53BC8A38FFE3BF77E79F5FCA2BD754E78B2F81FE026E6720617408B0276DCF` |
| `representative-frame-intel-20260818-final/summary.json` | `28EBF40F3862C649829B44ED88D9F2767855AAEF56817BD11BA9B6939C15C7B9` |
| `d3d12-calling-convention-audit-20260818.json` | `6AA730705687531200044C024C4A3185F4F561F79DC03051FB893E9CF156759E` |

Execution cohort:

```text
Managed payload SHA-256
F8187B0AA23FB5B3151D088AF7CA87E609B68EB1189D941247F105A00BE1937D

Native C++ executable SHA-256
69233EF0375B23A9D0B0DA5D7B1B41186002AE7BC8081F20452D77338B379930
```
