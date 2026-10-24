# Cluster Deform Cache Allocator Benchmark — 2026-09-01

## Decision

The sole production allocator is **size + global prefix + write**. The per-visible-cluster CAS
and four-cluster block-reserve implementations were benchmark candidates only and are absent from
the shipping shaders. Runtime does not select an allocator from workload data.

The prefix allocator won every measured workload on both graphics APIs. At one million visible
clusters its median allocation time was 30.720 µs on D3D12 and 33.232 µs on Vulkan, compared with
127,053.312 µs and 126,937.088 µs for four-cluster block reserve. The corresponding per-cluster
CAS medians were 507,555.840 µs and 509,136.256 µs.

## Test system and method

- GPU: NVIDIA GeForce RTX 3080 (discrete, GA102).
- D3D12 driver identifier: `32.0.16.1074`.
- Vulkan driver identifier: `610.74`.
- Build: Release, x64, .NET 10, Slang-generated DXIL/SPIR-V.
- Workloads: 1,000, 10,000, 100,000, and 1,000,000 visible-cluster cache requests.
- Cache requests: deterministic opaque byte counts from 64 through 304 bytes, aligned to 16 bytes.
- Sampling: 10 warm-up executions followed by 30 measured executions per API, allocator, and
  workload.
- Duration: GPU timestamp queries enclose allocator dispatches only. The tables report the median
  and interpolated P95 in microseconds.

The candidates were deliberately isolated from vertex evaluation and cache-payload generation so
the result measures allocator topology rather than material cost. Each candidate produced one
offset per visible cluster. The prefix candidate used local size/prefix, one global group-prefix,
and offset write dispatches.

## GPU duration

### D3D12

| Visible clusters | Allocator | Median µs | P95 µs |
|---:|---|---:|---:|
| 1,000 | per-cluster CAS | 248.832 | 252.467 |
| 1,000 | block reserve (4) | 53.248 | 53.248 |
| 1,000 | size + global prefix + write | 8.192 | 9.216 |
| 10,000 | per-cluster CAS | 5,027.840 | 5,588.531 |
| 10,000 | block reserve (4) | 1,010.688 | 1,011.712 |
| 10,000 | size + global prefix + write | 8.192 | 8.192 |
| 100,000 | per-cluster CAS | 50,584.576 | 50,691.533 |
| 100,000 | block reserve (4) | 12,425.216 | 12,428.288 |
| 100,000 | size + global prefix + write | 9.216 | 9.216 |
| 1,000,000 | per-cluster CAS | 507,555.840 | 508,891.750 |
| 1,000,000 | block reserve (4) | 127,053.312 | 127,149.056 |
| 1,000,000 | size + global prefix + write | 30.720 | 31.744 |

### Vulkan

| Visible clusters | Allocator | Median µs | P95 µs |
|---:|---|---:|---:|
| 1,000 | per-cluster CAS | 233.040 | 234.307 |
| 1,000 | block reserve (4) | 48.448 | 48.544 |
| 1,000 | size + global prefix + write | 11.824 | 11.968 |
| 10,000 | per-cluster CAS | 4,777.728 | 5,572.954 |
| 10,000 | block reserve (4) | 951.072 | 952.045 |
| 10,000 | size + global prefix + write | 11.232 | 11.410 |
| 100,000 | per-cluster CAS | 50,633.184 | 50,720.491 |
| 100,000 | block reserve (4) | 12,403.824 | 12,428.528 |
| 100,000 | size + global prefix + write | 13.088 | 13.362 |
| 1,000,000 | per-cluster CAS | 509,136.256 | 509,523.992 |
| 1,000,000 | block reserve (4) | 126,937.088 | 127,027.333 |
| 1,000,000 | size + global prefix + write | 33.232 | 33.606 |

## Contention, occupancy, traffic, and dispatches

The following values are exact consequences of the measured shader and workload dimensions:

- **Allocator atomics** counts global allocation-counter operations. CAS retries can only increase
  hardware atomic traffic beyond this lower bound. The prefix allocator performs none.
- **Launch occupancy** is active allocator threads divided by launched threads. It records lane
  utilization, not vendor-specific resident-warps-per-SM occupancy.
- **Global bytes** counts explicit global buffer reads, writes, and the read/write sides of each
  allocator atomic. It excludes cache-line amplification and retry traffic, so CAS contention can
  only make the hardware traffic worse.
- **Dispatches** is the number inside the timestamp interval.

| Visible clusters | Allocator | Allocator atomics | Groups | Launch occupancy | Global bytes | Dispatches |
|---:|---|---:|---:|---:|---:|---:|
| 1,000 | per-cluster CAS | 1,000 | 1,000 | 1.562% | 12,000 | 1 |
| 1,000 | block reserve (4) | 250 | 250 | 1.562% | 6,000 | 1 |
| 1,000 | prefix | 0 | 9 | 97.917% | 16,048 | 3 |
| 10,000 | per-cluster CAS | 10,000 | 10,000 | 1.562% | 120,000 | 1 |
| 10,000 | block reserve (4) | 2,500 | 2,500 | 1.562% | 60,000 | 1 |
| 10,000 | prefix | 0 | 81 | 97.685% | 160,480 | 3 |
| 100,000 | per-cluster CAS | 100,000 | 100,000 | 1.562% | 1,200,000 | 1 |
| 100,000 | block reserve (4) | 25,000 | 25,000 | 1.562% | 600,000 | 1 |
| 100,000 | prefix | 0 | 783 | 99.904% | 1,604,692 | 3 |
| 1,000,000 | per-cluster CAS | 1,000,000 | 1,000,000 | 1.562% | 12,000,000 | 1 |
| 1,000,000 | block reserve (4) | 250,000 | 250,000 | 1.562% | 6,000,000 | 1 |
| 1,000,000 | prefix | 0 | 7,815 | 99.981% | 16,046,884 | 3 |

An NVIDIA Nsight Compute hardware-counter run was also attempted with version 2025.4.1. The
installed driver denied performance-counter access with `ERR_NVGPUCTRPERM`. That restriction was
not changed because it is machine-wide policy. Consequently the portable launch-occupancy and
explicit-traffic measurements above are the decision evidence; no inaccessible vendor counter is
presented as measured data.

## Production mapping

The production implementation preserves arbitrary opaque size/alignment requests by reserving
`size + alignment - 1` bytes per visible cluster. A saturating global prefix assigns a disjoint
slot, and the final offset is aligned within that slot. This deliberately trades bounded padding
for an associative prefix operation. It provides these invariants without interpreting payloads:

- exactly one cache region per visible cluster;
- every published cache offset satisfies the evaluator-requested power-of-two alignment;
- aligned payload ranges do not overlap;
- saturated prefix arithmetic cannot wrap;
- requests outside capacity retain the invalid sentinel and generate no cache write;
- cached-cluster statistics use one reduction atomic per 256 requests, not an allocation atomic;
- software raster, hardware raster, and cached shading drop invalid work instead of evaluating an
  alternate path.

The old CAS candidates and the temporary benchmark executable are intentionally not retained in
the product tree. This report is the durable benchmark artifact.
