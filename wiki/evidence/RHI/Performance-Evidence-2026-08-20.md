# RHI CPU Performance Evidence - 2026-08-20

This page is the current dirty-tree CPU command-construction evidence after enforcing the managed D3D12 boundary requested for the product and benchmark:

```text
Native C++ Direct
Direct C# through Silk.NET generated COM methods
Interface RHI through Silk.NET generated COM methods
```

There is no Concrete/ImplementationDiagnostic receiver in this protocol, and managed code does not read D3D12 COM vtables or emit unmanaged `calli` sites for command-list calls. The working tree is still dirty, so these reports are `FunctionalOnly` development evidence rather than clean-checkout vendor certification.

## Managed D3D12 boundary

Both the product helper in `SomeEngine.Graphics.Direct3D12` and the Direct C# baseline call methods on Silk.NET's generated `ID3D12GraphicsCommandList*` types. Examples include `DrawInstanced`, `DrawIndexedInstanced`, `Dispatch`, `SetPipelineState`, root setters, `RSSetViewports`, `OMSetRenderTargets`, `ResourceBarrier` and enhanced `Barrier`.

Two IL-shape tests fail if either helper emits `calli`; they also prove that the Draw helper calls the corresponding Silk.NET method. NativeAOT may inline a generated Silk wrapper as an ordinary compiler optimization, but repository code does not replace or bypass that wrapper.

The internal `D3D12Backend` implementation exists because it implements `IGraphicsBackend`; it is not a product receiver alternative. The product no longer exposes a benchmark-only command-timing contract. Managed benchmark calls use the ordinary `IGraphicsBackend` lifecycle, including the complete public `End` operation.

## Fixed workload and timing boundary

Every measured representative frame contains:

```text
1,025 object packets
2,050 individual public Draw requests
40 reusable materials
9 command-list Reset operations
9 native command-list Close operations
4 explicit barriers
3 persistent recording workers in the parallel workload
```

`Full` performs 107 public persistent-binding requests and 107 native root-CBV setters. `PerDrawBindings` performs 2,050 public binding requests while ordinary state suppression keeps the native root-CBV setter count at 107. No public or benchmark Draw batching is permitted.

The timer begins before object-packet generation and stops after the ninth public `End` returns, so the command-finalization work observable through the normal product API remains inside the interval.

Each adapter used:

```text
2 independent cohorts of 8 interleaved rounds (16 combined)
2,048 warm-up frames per process
2,048 measured frames per process
affinity mask 0x55
High process priority
32,768 measured samples per mode / variant / workload cell
393,216 measured performance samples per adapter
```

All reports use output identity `4F69D660B527341D446A365853AA7FA8CCD853243853FE22CAB30D0608BB6AF0` and shader-manifest identity `F0BF21538DA4D120406A725921117F8A3CD6EEACC59E3DD5826F373AA0483C99`.

## NVIDIA GeForce RTX 3070 Laptop GPU

Driver `32.0.15.7216`, adapter LUID `80559:0`.

Geometric mean of the sixteen process P50 values, in microseconds per representative frame:

| Mode / workload | Native C++ | Direct C# through Silk | Interface RHI through Silk | C# vs C++ | RHI vs C# |
|---|---:|---:|---:|---:|---:|
| Full Serial | 57.268 | 73.444 | 84.479 | +28.24% | +15.03% |
| Full Parallel | 102.240 | 108.444 | 117.951 | +6.07% | +8.77% |
| PerDrawBindings Serial | 57.046 | 72.304 | 83.014 | +26.75% | +14.81% |
| PerDrawBindings Parallel | 101.858 | 104.271 | 113.999 | +2.37% | +9.33% |

Paired absolute deltas use the median of sixteen same-round differences:

| Mode / workload | Direct C# - C++ | Sixteen-round range | Interface RHI - Direct C# | Sixteen-round range |
|---|---:|---:|---:|---:|
| Full Serial | +16.450 us | +9.750 to +24.200 | +11.300 us | +7.650 to +14.200 |
| Full Parallel | +6.800 us | -17.800 to +21.550 | +8.800 us | -10.800 to +28.200 |
| PerDrawBindings Serial | +15.650 us | +5.300 to +22.000 | +10.600 us | +6.000 to +14.100 |
| PerDrawBindings Parallel | +0.300 us | -28.400 to +24.950 | +11.000 us | -14.300 to +36.950 |

## Intel UHD Graphics

Driver `31.0.101.3222`, adapter LUID `69319:0`.

Geometric mean of the sixteen process P50 values, in microseconds per representative frame:

| Mode / workload | Native C++ | Direct C# through Silk | Interface RHI through Silk | C# vs C++ | RHI vs C# |
|---|---:|---:|---:|---:|---:|
| Full Serial | 165.442 | 187.738 | 200.087 | +13.48% | +6.58% |
| Full Parallel | 166.137 | 175.156 | 179.580 | +5.43% | +2.53% |
| PerDrawBindings Serial | 165.689 | 187.548 | 201.700 | +13.19% | +7.55% |
| PerDrawBindings Parallel | 160.817 | 174.084 | 183.488 | +8.25% | +5.40% |

Paired absolute deltas:

| Mode / workload | Direct C# - C++ | Sixteen-round range | Interface RHI - Direct C# | Sixteen-round range |
|---|---:|---:|---:|---:|
| Full Serial | +22.925 us | +7.100 to +29.500 | +12.450 us | +8.700 to +16.400 |
| Full Parallel | +9.700 us | -6.950 to +27.000 | +7.450 us | -35.550 to +27.050 |
| PerDrawBindings Serial | +21.700 us | +19.950 to +24.300 | +13.450 us | +8.450 to +18.900 |
| PerDrawBindings Parallel | +13.775 us | +1.900 to +24.000 | +11.450 us | -11.550 to +22.250 |

Both adapters' parallel cells contain scheduler/driver noise and negative same-round differences. Intel Full RHI spans `-35.55` to `+27.05 us`; NVIDIA PerDraw Direct C# spans `-28.4` to `+24.95 us`. The raw rounds are retained. The paired median is the robust primary summary for these cells; the observed ranges must not be presented as narrow confidence claims.

## Allocation evidence

Performance binaries compile out per-frame allocation measurement so that the C# / C++ timing comparison does not charge only managed receivers for observation. Separate allocation-enabled NativeAOT binaries execute the identical Full and PerDrawBindings command shapes.

| Adapter | Managed measured samples | Samples with allocation |
|---|---:|---:|
| NVIDIA | 32,768 | 0 |
| Intel | 32,768 | 0 |

Both cohorts include Direct C# and Interface RHI, Serial and Parallel, and both binding-request modes.

## Interpretation

### C# relative to C++

The current comparison deliberately includes the real Silk.NET boundary. The remaining Direct C# overhead is the combined cost of Silk-generated COM calls, CLR / NativeAOT call-site behavior, managed loop and worker control, and compiler/code-layout differences. This frame-level protocol does not isolate a standalone "Silk wrapper cost," and no manual vtable path is used to manufacture a lower C# baseline.

### RHI relative to Direct C#

For the canonical Full workload, the paired median Interface RHI increment is approximately `7.45-12.45 us` across the two adapters and Serial/Parallel variants. This is the actual product path, including interface dispatch, context/provenance checks, recording lifetime, rendering state and persistent-generation capture. Because the Concrete receiver was removed, this evidence does not claim a separately measured interface-dispatch component.

The additional 1,943 redundant public binding requests in PerDrawBindings change the paired RHI minus Direct C# median by `-0.7` to `+4.0 us`, depending on adapter and workload. The small negative cell is measurement noise rather than a speedup claim. The exact-identity early return therefore prevents those requests from becoming the dominant frame cost, while still charging the public call shape that a less perfectly sorted application would use.

### Simple statement

```text
C++ is fastest because it has neither the managed/Silk/CLR call-site work nor the RHI contract.
Direct C# pays the real Silk.NET + managed-runtime boundary.
Interface RHI then pays about another 7.5-12.5 us in the canonical frame for product safety,
recording/lifetime and state management.
```

## Evidence identity

Performance managed payload SHA-256:

```text
Full             FB503D234721BA5948CC16780D0F3CDFA8D8D3D3C186C95CC97D095397FF2647
PerDrawBindings  F40D8A0931453B21FA94ABCA0A5C0F784041F2B33C5566422BFE386F9CEF8E26
```

Allocation-evidence managed payload SHA-256:

```text
Full             A86BF50BFECD719B7C42657BBA3F4AB95A629778DA04E682C39B4B7E0EBC3E23
PerDrawBindings  38F86EB293E7993BC953402593EB1EC4FEFFDE383DBAD8313153EFD6B7121FD0
```

Native executable SHA-256:

```text
Full             B64AC96D52114788C784CAF6DC771AA6BD3565D9AE015696CF922D5DD5D68437
PerDrawBindings  699A9C8875C14E72322E091EF9F3A8E085D3651CD25FF05AA633A39F70B6D218
```

Raw and aggregate evidence:

```text
artifacts/graphics-benchmarks/real-call-silk-nvidia-20260820-v2/
artifacts/graphics-benchmarks/real-call-silk-nvidia-20260820-v3/
artifacts/graphics-benchmarks/real-call-silk-nvidia-20260820-combined/
artifacts/graphics-benchmarks/real-call-silk-intel-20260820-v2/
artifacts/graphics-benchmarks/real-call-silk-intel-20260820-v3/
artifacts/graphics-benchmarks/real-call-silk-intel-20260820-combined/
artifacts/graphics-benchmarks/real-call-silk-allocation-nvidia-20260820-v2/
artifacts/graphics-benchmarks/real-call-silk-allocation-intel-20260820-v2/
```

## Closure status

The Silk.NET boundary, removal of the Concrete benchmark receiver, same-workload execution, native Close timing, and zero-allocation evidence are closed for the current dirty tree. Overall RHI completion is not closed: the tracked/untracked work tree still requires safe classification and a clean-checkout reproduction of build, tests and this measurement protocol.
