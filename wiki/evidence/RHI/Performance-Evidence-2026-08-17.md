# RHI Performance Evidence — 2026-08-17

> **Direct C# versus Native C++ comparisons on this page are superseded by
> [[Performance-Evidence-2026-08-18]].** The later run rebuilds the Native C++ executable from the
> current source, repairs the native barrier-evidence bug, and measures default C#, optimized C#,
> Native C++ and Concrete RHI in one Latin-square cohort. This page remains historical evidence for
> the earlier WARP, adapter and RHI receiver runs only.

This page records measurements produced from the current working tree on 2026-08-17. The JSON reports
and their raw evidence files are the authoritative data. The worktree was dirty, so these results are
useful current evidence but are not a clean-checkout release certification.

## Adapter inventory

| Adapter | Vendor / device | LUID | Driver | Role |
|---|---:|---:|---:|---|
| NVIDIA GeForce RTX 3070 Laptop GPU | `10DE:249D` | `0x13AAF:0x0` | `32.0.15.7216` | hardware probe and fast diagnostic |
| Intel(R) UHD Graphics | `8086:9A60` | `0x10EC7:0x0` | `31.0.101.3222` | hardware probe and fast diagnostic |
| Microsoft Basic Render Driver | `1414:008C` | `0x13A1F:0x0` | `10.0.26100.8972` | WARP functional equivalence |

No AMD adapter was enumerated, and no linked-adapter performance run was executed. Those rows remain
`NOT_RUN` rather than being inferred from another vendor or from single-node metadata.

The managed benchmark executable was Release build SHA-256
`E420F69DB51424D34C6455A3CC651E99DC36F5B9D809AA83801DF0044B1E9A65` at commit
`68567b77d63ba9598b3387dfbfaa8ccbd42c18b9` with a dirty working tree. The final post-build
developer-probe and WARP cohort used managed payload SHA-256
`769567D92817B5709EDEDAAB0F619EEABDF0EDA7E6DB976C377ED62C00BF7B22`. The two four-round fast
diagnostics were captured immediately before that final rebuild with the same benchmark executable
but managed payload SHA-256
`4B5F365AE9D5800933259345DAFEC91F5362F0080E24FE2DDC6BDAA13C88DBFA`; their raw reports preserve
that separate cohort explicitly.

## Commands and report files

```text
# NVIDIA exploratory probe: all six workloads, three managed/direct-Silk receivers
SomeEngine.Graphics.Benchmarks.exe probe \
  --adapter 0x13AAF:0x0 \
  --workloads empty-submit,persistent-draw,transient-draw,state-suppression,explicit-barrier,three-queue-present \
  --variants concrete-receiver,interface-receiver,direct-silk \
  --output artifacts/graphics-benchmarks/current-nvidia-probe.json

# Intel exploratory probe
SomeEngine.Graphics.Benchmarks.exe probe \
  --adapter 0x10EC7:0x0 \
  --workloads empty-submit,persistent-draw,transient-draw,state-suppression,explicit-barrier,three-queue-present \
  --variants concrete-receiver,interface-receiver,direct-silk \
  --output artifacts/graphics-benchmarks/current-intel-probe.json

# Four-receiver WARP functional equivalence
SomeEngine.Graphics.Benchmarks.exe warp \
  --native-runner artifacts/graphics-benchmarks/native-build-msvc/SomeEngine.Graphics.NativeBenchmarks.exe \
  --output artifacts/graphics-benchmarks/current-warp-functional-with-native.json

# NVIDIA four-process hardware ordering/frequency diagnostic
SomeEngine.Graphics.Benchmarks.exe diagnose \
  --adapter 0x13AAF:0x0 \
  --native-runner artifacts/graphics-benchmarks/native-build-msvc/SomeEngine.Graphics.NativeBenchmarks.exe \
  --output artifacts/graphics-benchmarks/current-nvidia-fast-diagnostic.json

# Intel four-process hardware ordering/frequency diagnostic; completed from preserved raw evidence
SomeEngine.Graphics.Benchmarks.exe diagnose \
  --adapter 0x10EC7:0x0 \
  --native-runner artifacts/graphics-benchmarks/native-build-msvc/SomeEngine.Graphics.NativeBenchmarks.exe \
  --resume artifacts/graphics-benchmarks/raw-20260817-131218-977 \
  --output artifacts/graphics-benchmarks/current-intel-fast-diagnostic.json
```

| Report | SHA-256 |
|---|---|
| `current-nvidia-probe.json` | `C93BD5255D25A9803465BD00F0EAE2D8E800891D3C0F0847EDA13EC3C2C92D25` |
| `current-intel-probe.json` | `8EFD16505EA12C9D13968B79F2AC357566D10AB00175E11F3EF6B7634EB08059` |
| `current-warp-functional-with-native.json` | `679855F808E33C6F056F119630379DF73CF33A90FA4D6F6C4AA088E4604D99CD` |
| `current-nvidia-fast-diagnostic.json` | `BBDD18D1FBD136714EC9C4E4EBB3200C77225EDC40ACF92ACD8558B170C3B46D` |
| `current-intel-fast-diagnostic.json` | `D81D92F9AF52478DA98060ED68CE0D54E2FE5642D40D6E232C6503FDFD56B2DE` |

The native runner used SHA-256
`3FA9F000C762C243BF6C0E82BA6328C8655D6F75E4BE5E7742EC96DF54980D13`, reported Release,
commit `68567b77d63ba9598b3387dfbfaa8ccbd42c18b9`, and toolchain
`MSVC 194134123 / native D3D12`. A rebuild from current native source was attempted, but the installed
Visual Studio instance did not contain `Microsoft.VisualStudio.Component.VC.Tools.x86.x64`; CMake
therefore had no C++ compiler. This prevents treating the native binary as newly reproduced evidence,
although it passed the current v3 protocol and WARP equivalence checks.

## WARP functional equivalence

`current-warp-functional-with-native.json` completed successfully. Concrete receiver, interface
receiver, direct Silk.NET D3D12 and native C++ executed all reduced-count workloads and produced
matching equivalence evidence.

WARP timing is intentionally not hardware performance evidence. Its purpose here is to prove that the
four receiver implementations perform the same logical work under the current protocol.

## Developer probes

Each probe used 64 warm-up frames, 256 measured frames, one process, 1,000 draw/state calls and 1,000
barriers where applicable. Values below are CPU P50 microseconds per call. Percentages compare the
candidate with ConcreteReceiver in the same run.

### NVIDIA RTX 3070 Laptop GPU

| Workload | Concrete | Interface | Interface delta | Direct Silk | Direct Silk delta |
|---|---:|---:|---:|---:|---:|
| Empty submit | 1.30000 | 1.20000 | -7.692% | 1.00000 | -23.077% |
| Persistent draw | 0.11200 | 0.12440 | +11.071% | 0.05655 | -49.509% |
| Transient binding draw | 0.47440 | 0.35340 | -25.506% | 0.11780 | -75.169% |
| State suppression | 0.26030 | 0.27925 | +7.280% | 0.06035 | -76.815% |
| Explicit barrier | 0.33015 | 0.32255 | -2.302% | 0.22525 | -31.773% |
| Three-Queue present | 422.300 | 509.900 | +20.744% | 370.750 | -12.207% |

### Intel UHD Graphics

| Workload | Concrete | Interface | Interface delta | Direct Silk | Direct Silk delta |
|---|---:|---:|---:|---:|---:|
| Empty submit | 1.80000 | 1.70000 | -5.556% | 1.50000 | -16.667% |
| Persistent draw | 0.14065 | 0.14800 | +5.226% | 0.10310 | -26.697% |
| Transient binding draw | 0.18255 | 0.20520 | +12.408% | 0.15920 | -12.791% |
| State suppression | 0.18895 | 0.18460 | -2.302% | 0.08555 | -54.723% |
| Explicit barrier | 0.14395 | 0.14255 | -0.973% | 0.09895 | -31.261% |
| Three-Queue present | 201.350 | 210.200 | +4.395% | 166.700 | -17.209% |

All probe workloads produced matching output evidence. The direction and size of interface/concrete
deltas vary by adapter and workload. A one-process probe therefore does not support restoring a
public generic receiver or claiming a stable dispatch advantage.

## NVIDIA fast diagnostic

The fast diagnostic used four interleaved process rounds, all four receivers, 512 warm-up frames,
1,024 measured frames and the three 10,000-call draw workloads. It completed with
`functionalOnly`, as required: this profile can reveal ordering/frequency bias but cannot certify a
vendor.

| Workload / metric | Geometric mean (us/frame) | Receiver spread | Position spread | Round drift | Residual RMS |
|---|---:|---:|---:|---:|---:|
| Persistent draw / CPU | 193.901 | 24.952% | 6.876% | 16.823% | 2.915% |
| Persistent draw / GPU | 242.768 | 16.281% | 8.454% | 7.515% | 4.975% |
| Transient draw / CPU | 446.150 | 60.471% | 4.866% | 11.027% | 1.688% |
| Transient draw / GPU | 307.216 | 1.653% | 1.002% | 2.423% | 0.664% |
| State suppression / CPU | 287.758 | 109.447% | 9.953% | 17.254% | 6.344% |
| State suppression / GPU | 245.781 | 11.304% | 6.165% | 4.537% | 2.069% |

Paired Interface-versus-Concrete CPU deltas changed with process position:

- Persistent draw: `-3.680%` to `-8.409%`.
- Transient draw: `+5.039%`, then `-2.186%`, `-0.307%`, `-4.003%`.
- State suppression: `+17.576%`, then `-24.462%`, `-20.810%`, `-8.097%`.

The diagnostic demonstrates substantial order/frequency sensitivity in CPU results, especially for
state suppression. It does not establish a product-dispatch policy. GPU transient-draw variance was
small across receivers, which is consistent with equivalent encoded work.

## Intel fast diagnostic

The Intel run used the same four-round, four-receiver 512/1,024-frame diagnostic. The original
controller process reached its 10-minute command limit after all sixteen raw process reports had been
written. Re-running the controller with `--resume` reused that complete raw set and produced
`current-intel-fast-diagnostic.json`; no partial result is treated as the final report.

| Workload / metric | Geometric mean (us/frame) | Receiver spread | Position spread | Round drift | Residual RMS |
|---|---:|---:|---:|---:|---:|
| Persistent draw / CPU | 490.425 | 32.795% | 2.874% | 1.479% | 1.168% |
| Persistent draw / GPU | 6475.205 | 1.198% | 1.057% | 0.509% | 0.471% |
| Transient draw / CPU | 938.394 | 72.715% | 2.413% | 3.645% | 1.317% |
| Transient draw / GPU | 7453.424 | 0.905% | 0.349% | 0.435% | 0.366% |
| State suppression / CPU | 585.886 | 60.046% | 3.596% | 2.528% | 1.202% |
| State suppression / GPU | 6474.898 | 1.514% | 0.536% | 0.428% | 0.500% |

Paired Interface-versus-Concrete CPU deltas were narrower than the NVIDIA run but still changed with
round and position:

- Persistent draw: `-1.492%` to `-4.914%`.
- Transient draw: `-7.074%`, `+1.949%`, `-2.732%`, `+0.609%`.
- State suppression: `-1.131%`, `+0.464%`, `+7.101%`, `-1.290%`.

GPU receiver spread stayed at or below `1.514%` for all three workloads. CPU receiver spread remained
large because the receiver implementations deliberately perform different amounts of host work; the
small position/round effects show that this Intel run was materially more stable than the NVIDIA
diagnostic, but it remains a non-certifying profile.

## Conclusions and next evidence

Current evidence supports these limited conclusions:

1. All tested receivers perform equivalent work on WARP, NVIDIA and Intel for the selected workloads.
2. Interface dispatch overhead is not stable in sign or magnitude across adapters, workloads and
   process positions; no product generic receiver is justified by this data.
3. Direct Silk is usually faster in the CPU microbenchmarks, quantifying backend abstraction and
   validation/capture work rather than proving incorrect RHI behavior.
4. Both fast diagnostics confirm that a one-process probe must remain exploratory: NVIDIA showed
   substantial order/frequency sensitivity, while Intel was more stable but still changed paired
   interface/concrete direction for transient and state-suppression work.

Still required for formal acceptance:

- install/recover the MSVC C++ workload and rebuild the native runner from current source;
- run the fixed five-process 8,192/16,384-frame vendor-certification protocol from a clean Release
  checkout;
- run equivalent formal certification on available Intel hardware and record AMD as `NOT_RUN` until
  hardware exists;
- add or execute dedicated descriptor-update, Pipeline warm-up and allocator-pressure performance
  workloads rather than inferring them from draw tests;
- preserve raw reports, executable hashes and environment cohort data with the final delivery.
