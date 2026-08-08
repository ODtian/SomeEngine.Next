# SomeEngine ECS benchmark runner

This executable turns the original ECS performance probes into a repeatable JSON runner. Every
warm-up and measured sample creates a fresh `World`; setup and correctness validation are outside
the timed interval. The default invocation is a short smoke run and has no external gate inputs:

```powershell
dotnet run --project benchmarks\SomeEngine.ECS.Benchmarks -c Release
```

Smoke and standard provide defaults that the corresponding CLI options can override. Certification
is deliberately immutable so a reduced workload cannot retain the certification label:

| Profile | Entities | Warm-ups | Samples | Query passes | Structural publications |
| --- | ---: | ---: | ---: | ---: | ---: |
| `smoke` | 10k | 1 | 3 | 8 | 8 |
| `standard` | 100k, 500k | 2 | 5 | 64 | 32 |
| `certification` | 100k, 500k, 1m | 3 | 100 | 128 | 64 |

For example, this explicitly exercises the million-entity path and writes the same JSON printed to
stdout to a file:

```powershell
dotnet run --project benchmarks\SomeEngine.ECS.Benchmarks -c Release -- `
  --profile standard `
  --entity-counts 1m `
  --output artifacts\ecs-perf-1m.json
```

Use `--help` for all options. Counts accept positive whole values and the `k`/`m` suffixes.

## Report semantics

The report contains p50, p95, p99, and maximum values using R-7 linear interpolation. It also keeps
every raw sample, including:

- elapsed time;
- current-thread and all-thread managed allocation;
- generation 0/1/2 collection deltas and managed-memory size;
- working set before/after/delta;
- `WorldStructuralMetrics` transaction counts/timings plus cloned archetype shells, chunk shells,
  and compiled query matches as timed deltas and per-fresh-World maxima;
- workload-specific payload bytes and update, snapshot-write, snapshot-load, durable-commit, and
  durable-load timings;
- a deterministic semantic checksum.

Structural counts and cumulative times are deltas over the timed interval. The API exposes maximum
times only as cumulative per-`World` values, so the explicitly named `worldMaximum*` fields may also
reflect that fresh world's untimed setup; they are retained as diagnostic context, not used as a
timed-scenario maximum.

Each scenario validates entity count and component aggregates after timing. Query samples also
validate the value read on every pass; structural samples require exactly one successful publication
per requested transaction. Different checksums across fresh measured samples fail the run.

Report schema 5 fixes thirteen scenarios per entity-count tier, plus one real durable-persistence
round trip at the smallest configured tier, and adds source-revision, structural-clone, and
serialization sub-metric evidence; certification rejects a baseline or
budget catalog whose scenario set differs by even one name:

- bundle spawn, read query, and single-candidate structural publication;
- source-generated parallel `IJobEntity` integration;
- row-precise `Changed<T>` plus enableable filtering;
- dynamic-buffer overflow (capped at 4,096 owners), sparse write, shared-bucket filtering, and
  indexed lookup (the latter three retain the full tier entity count);
- directed-relation high-fanout retarget/maintenance and hierarchy deep-chain-to-wide-tree
  maintenance (deterministically capped at 4,096 topology items per tier);
- multi-command `CommandBuffer` archetype churn;
- full-World snapshot write, separately timed snapshot read, and changed-component delta
  serialization;
- a mixed frame containing a source-generated parallel update, snapshot write, and snapshot load;
- one `DurableSaveStore` commit/flush/publish/reopen/load path using an actual temporary file.

The durable scenario runs once at the first tier rather than repeating identical fsync evidence for
every scale label. The topology cap isolates maintenance cost instead of silently turning the 1m tier into a second
multi-million-entity memory test. Snapshot and ordinary data scenarios still use the full profile
entity count. Setup is untimed, but validation reads the resulting owners/topology/payload and hashes
the exact serialized bytes, so an optimized-away or semantically empty run cannot pass.

`snapshotWriteMilliseconds` measures the complete `WriteWorld` call. Serialization holds topology
admission through codec traversal, caller I/O, footer work, and successful delta-journal handling.
Codecs borrow the one published backing directly; ordinary mutation and disposal wait until the
serializer releases admission. No public diagnostic currently separates admission acquisition wait
from encoding and output time, so the runner does not invent one.

The working-set metric is the process working set observed at the sample boundaries; it is not a
polling-based within-sample peak, because an in-process polling thread would perturb the benchmark.

## Absolute budgets and baselines

`--baseline` accepts a passed schema-5 report from this runner. Both the current certification run
and baseline must identify a full Git commit SHA with a clean tracked/untracked worktree in
`sourceRevision`; dirty or unavailable source identity fails before the expensive workload. It
compares the current p50 and p99
to matching scenario names. The default relative limits are 5% and 10%. Smoke/standard can override
them; certification can only tighten them. Certification also requires the baseline to have the
exact machine/runtime/OS/architecture/memory-limit/GC/Release environment, fixed workload and
scenario set, fresh Worlds, and at least 100 measured samples per scenario. The loader requires the
runner's gate to be passed with empty report/evaluation violations and the exact result scenario set.
It also requires the raw sample array and recomputes p50/p95/p99/maximum for elapsed time,
current-thread allocation, all-thread allocation, final working set, and working-set delta instead
of trusting edited summary values. Per-sample working-set deltas and aggregate GC generation counts
must also match their raw measurements. Schema 5 likewise requires the exact workload-metric
property set and recomputes every workload p50/p95/p99/maximum distribution from raw samples.

`--absolute-budgets` accepts this schema. Every field is optional outside certification. Scenario
values override the shared defaults one field at a time:

```json
{
  "schemaVersion": 1,
  "defaults": {
    "maxP50Milliseconds": 1000,
    "maxP95Milliseconds": 1200,
    "maxP99Milliseconds": 1500,
    "maxMilliseconds": 2000,
    "maxAllocatedBytesPerSample": 500000000,
    "maxTotalAllocatedBytesPerSample": 500000000,
    "maxWorkingSetBytes": 2000000000,
    "maxWorkingSetDeltaBytes": 1000000000
  },
  "scenarios": {
    "bundle-spawn-1m": {
      "maxP50Milliseconds": 750,
      "maxP99Milliseconds": 1000
    }
  }
}
```

Budgets must be calibrated on the intended fixed certification machine; the numbers above only show
the file shape and are not product budgets.

Budget schema version 1 is strict: unknown properties and unknown certification scenario keys are
rejected so a typo cannot silently disable a gate. Certification refuses to start without both
files and requires every scenario to have effective `maxP50Milliseconds`, `maxP95Milliseconds`,
`maxP99Milliseconds`, and `maxMilliseconds` values. Baseline/budget files and their schemas,
environment, configuration and scenario sets are validated before the expensive measurements.

```powershell
dotnet run --project benchmarks\SomeEngine.ECS.Benchmarks -c Release -- `
  --profile certification `
  --baseline baselines\ecs-approved.json `
  --absolute-budgets baselines\ecs-budgets.json `
  --evidence-manifest artifacts\ecs-release-prerequisites.json `
  --output artifacts\ecs-certification.json
```

Certification also requires a schema-1 prerequisite evidence manifest. The runner reads each
manifest/baseline/budget/artifact once for both parsing and SHA-256 binding, passes those exact
parsed gate catalogs into the run, and rehashes every bound path before returning the report.
`--output` cannot overwrite any bound input or referenced artifact; a successful report is written
to a same-directory candidate and atomically replaces the destination after the final evidence
recheck. The runner verifies that it
binds the current clean full commit, report schema, exact baseline and budget SHA-256 values, a
hashed target-machine manifest, an exact claimed-RID set backed by successful schema-2 NativeAOT
execution evidence, at least one passed `xorshift64star-v1` long-fuzz artifact with 10,000 or more
executed steps, and target-filesystem process-kill plus physical power-cut declarations covering
recovery from both durable slots. Every referenced artifact is opened and hash-checked before the
expensive benchmark begins. Missing, false, stale, mismatched, or duplicate evidence is a
configuration failure and no certification report is emitted.

The prerequisite manifest is an index of reviewed evidence, not a replacement for signatures or
operator review. Its essential shape is:

```json
{
  "schemaVersion": 1,
  "commitSha": "<clean-full-git-sha>",
  "benchmarkReportSchemaVersion": 5,
  "approvedBaselineSha256": "<sha256-of-baseline>",
  "absoluteBudgetsSha256": "<sha256-of-budget-file>",
  "machine": {
    "machineId": "<reviewed-target-id>",
    "artifact": { "path": "machine.json", "sha256": "<sha256>" }
  },
  "claimedRids": ["<rid>"],
  "aotEvidence": [
    { "path": "aot.json", "sha256": "<sha256>" }
  ],
  "longFuzzEvidence": [
    { "path": "fuzz-long.json", "sha256": "<sha256>" }
  ],
  "powerCutEvidence": [
    {
      "targetFilesystem": "<target-filesystem-and-storage-stack>",
      "artifact": { "path": "power-cut-log", "sha256": "<sha256>" },
      "processKillPassed": true,
      "powerCutPassed": true,
      "primarySlotRecoveryPassed": true,
      "previousSlotRecoveryPassed": true
    }
  ]
}
```

The emitted report records the manifest hash and normalized prerequisite binding. A final release
index must then hash that completed report and the prerequisite manifest; the report cannot hash
itself.

Exit codes are `0` for pass/help, `1` for execution or file errors, `2` for invalid configuration,
and `3` for a completed run that violated a gate.

The 100-sample minimum is intentional. With only ten observations, an R-7 value named p99 is mostly
an interpolation between the two slowest samples and is not credible tail evidence. Certification
also requires a maximum-time budget so a single observed outlier cannot be hidden by a percentile.
Use `standard` with explicit certification-sized overrides while establishing the first approved
baseline:

```powershell
dotnet run --project benchmarks\SomeEngine.ECS.Benchmarks -c Release -- `
  --profile standard `
  --entity-counts 100k,500k,1m `
  --warmup 3 `
  --samples 100 `
  --query-iterations 128 `
  --structural-iterations 64 `
  --output artifacts\ecs-baseline-candidate.json
```

An output path equal to the manifest, approved baseline, budget, machine artifact, AOT artifact,
long-fuzz artifact, or power-cut artifact is rejected during certification.
