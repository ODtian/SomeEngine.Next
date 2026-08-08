# ECS certification evidence

This document defines the executable evidence used to decide whether the ECS is ready for a
specific production target. It is an engineering gate, not a marketing claim: passing it on one
machine does not certify every platform, content profile, filesystem, or gameplay workload.

The gate covers five independent risks:

| Risk | Evidence | Failure behavior |
| --- | --- | --- |
| Semantic drift | deterministic model-based fuzzing against an independent dictionary model | xUnit failure plus replayable/minimized JSON trace |
| Partial structural publication | command-buffer and delta rollback assertions; structural publication metrics | candidate is discarded; published state and publication count stay unchanged |
| Throughput or tail regression | fresh-World JSON benchmark samples at 100k, 500k, and 1m entities | certification profile exits non-zero on missing or exceeded gates |
| Save corruption or interrupted commit | two-generation `DurableSaveStore`, checksums, truncation/corruption and commit-cut fault tests | newest invalid generation is ignored and the other verified generation is loaded |
| Deployment incompatibility | forced NativeAOT rebuild followed by execution of the native binary | any IL20xx/IL30xx error, publish error, or semantic check returns non-zero |

## Entry points

The default solution now contains the fuzz project and the AOT smoke project. Ordinary product
validation remains:

```powershell
dotnet build SomeEngine.slnx -c Release
dotnet test SomeEngine.slnx -c Release --no-build
```

The short local evidence loop is:

```powershell
just ecs-cert-smoke
```

It runs the deterministic fuzz bank, the short benchmark profile, and the AOT scenario on the JIT.
It is suitable for a pull request, but it is not the full native/performance certification run.

The individual entry points are:

```powershell
just ecs-fuzz
just ecs-fuzz-campaign 0x8f0d3c7a52b941e1 100000
just ecs-fuzz-replay path/to/ecs-fuzz-failure.json
just ecs-perf --profile standard --output artifacts/ecs-standard.json
just ecs-aot win-x64
pwsh -NoProfile -File tests/SomeEngine.ECS.Fuzz.Tests/Invoke-LongFuzzCampaign.ps1 `
  -Seed 0x8f0d3c7a52b941e1 -Steps 100000 `
  -Evidence artifacts/ecs-fuzz-long.json
```

`global.json` pins the certification toolchain to the .NET 10.0.109 SDK feature band and rolls only
to a later patch in that band. A different SDK is a different certification environment and must
produce new evidence.

## Model-based fuzzing

`tests/SomeEngine.ECS.Fuzz.Tests` uses a repository-owned `xorshift64star-v1` PRNG and a reference
model that does not call ECS internals. Trace schema 2 keeps the frozen PRNG/replay/minimization
contract while expanding each entity's independently cloned oracle state. The fixed bank currently
executes six seeds with 160 steps per seed and covers immediate operations, successful multi-command
batches, deliberately failing batches, stale entities, deferred identities, allocator rollback,
ordinary and enableable components, enabled/disabled query sets, indexed buckets, sparse/shared
owners, dynamic-buffer inline/overflow contents, hierarchy parent/children maintenance, directed
relation identity/endpoints/adjacency, queries, and structural metrics.

Every operation is compared with the model. A failed batch additionally proves that:

- no candidate entity/component/tag/query state became visible;
- deferred handles from the rejected batch never resolve;
- `Started` and `Aborted` increase once while `Published` does not;
- the publication epoch does not move.

A dedicated control-trace test additionally proves allocator frontier, free-list order and entity
generations are restored after a failed allocator-mutating batch; that stronger identity comparison
is not redundantly executed for every generated failing batch.

For exploratory longer deterministic execution, the fail-closed `just` recipe still requires both
arguments:

```powershell
just ecs-fuzz-campaign 0x8f0d3c7a52b941e1 100000
```

That command alone is not release evidence. The ordinary xUnit project also contains the
environment-controlled campaign entry point and can report it as an ordinary passing test when no
campaign was requested. Certification therefore accepts only the separate schema-1 success
artifact emitted by the clean-worktree launcher:

```powershell
pwsh -NoProfile -File tests/SomeEngine.ECS.Fuzz.Tests/Invoke-LongFuzzCampaign.ps1 `
  -Seed 0x8f0d3c7a52b941e1 `
  -Steps 100000 `
  -Evidence artifacts/ecs-fuzz-long-8f0d3c7a52b941e1.json
```

The launcher requires at least 10,000 and at most 1,000,000 steps, rejects a dirty tracked or
untracked worktree before execution, captures a full commit SHA, runs exactly the campaign test,
requires an ignored or out-of-repository evidence destination, and rechecks the worktree and HEAD
afterward. The test writes a same-directory candidate; the launcher validates its complete shape
and identity and then atomically replaces the final artifact only after the final Git checks pass.
Failure preserves any prior successful artifact and deletes the candidate. The artifact records
schema, commit, clean/passed status, PRNG, seed, actual step count, the 1,024-logical-entity model
bound, the 128-step full-verification interval, duration, batch counters, and a SHA-256 state digest.
The release prerequisite manifest hashes the same bytes it parses and rejects ordinary test counts,
sub-10k runs, incomplete/duplicate fields or artifacts, failed flags, stale commits, different
PRNG/model/verification parameters, or invalid counters and digests.

The legacy `just` recipe requires both seed and step count and accepts no trailing test-runner arguments. It also
lexically restricts the seed to decimal or `0x` hexadecimal digits and the step count to decimal
digits before either value reaches the shell; an omitted or injected argument fails, and options
such as `--list-tests` cannot turn the campaign into a zero-execution success. The step limit is
1,000,000. The long campaign generates and executes one step at a time against one shared
incremental dictionary oracle; it does not retain a complete `FuzzStep[]` or clone the oracle per
step. Each model transaction journals only touched entities/relations. Acceptance/rejection,
rollback, structural counters, and publication epoch are checked on every step; full World/model
comparison runs every 128 steps and on the final step. The generator caps historical logical
identities at 1,024 so accepted step count, not accumulated World size, controls campaign growth.

On long-campaign failure, the test first atomically writes
`fuzz-failures/ecs-long-fuzz-*.json` with PRNG, seed, requested count, failed step, exception, stack,
and stable fingerprint. It does not run online ddmin or retain a second full trace; rerun the same
seed/count to reconstruct the deterministic prefix. Ordinary bounded fuzz tests still write
`ecs-fuzz-*.json` with a minimized replay trace. Replay those exact minimized traces before accepting
a fix:

```powershell
just ecs-fuzz-replay path/to/ecs-fuzz-8f0d3c7a52b941e1-*.json
```

The ordinary replay loader selects `minimizedTrace`, validates the artifact/trace schema and frozen
PRNG identifier, and returns the test process exit code. Regenerating only from a seed is not
equivalent to replaying an ordinary ddmin trace. The fail-closed recipe single-quotes the trace path
for both PowerShell and POSIX shells and rejects quote/newline characters before interpolation.

Suggested cadence:

- every pull request: fixed seed bank;
- nightly: retained long seeds whose 10k/50k/100k wall-time and peak-memory calibration fits the
  machine's window; retain the measured wall-time and peak-memory calibration with the evidence;
- release candidate: fixed bank plus the approved long-running seed corpus.

## Performance and scale gate

`benchmarks/SomeEngine.ECS.Benchmarks` is a standalone JSON runner with no benchmark-framework
dependency. Every warm-up and measured sample receives a fresh `World`; setup and correctness
validation are outside the timed interval. It records every sample and reports R-7 p50, p95, p99,
maximum, managed allocations, GC deltas, managed memory, working-set boundaries, structural
transaction metrics, workload-specific payload/timing metrics, environment identity, and a
deterministic semantic checksum.

Profiles are deliberately different:

| Profile | Entity counts | Warm-ups | Samples | Intended use |
| --- | --- | ---: | ---: | --- |
| `smoke` | 10k | 1 | 3 | local/PR execution and schema validation |
| `standard` | 100k, 500k | 2 | 5 | nightly trend collection |
| `certification` | 100k, 500k, 1m | 3 | 100 | fixed-hardware release gate |

The certification profile requires an approved baseline report, an absolute-budget file, and a
reviewed prerequisite evidence manifest:

```powershell
dotnet run --project benchmarks/SomeEngine.ECS.Benchmarks/SomeEngine.ECS.Benchmarks.csproj `
  -c Release -- `
  --profile certification `
  --baseline baselines/ecs/<machine>/approved.json `
  --absolute-budgets baselines/ecs/<machine>/budgets.json `
  --evidence-manifest artifacts/ecs-release-prerequisites.json `
  --output artifacts/ecs-certification.json
```

The certification workload is fixed at the matrix above, 128 query passes, 64 structural
publications, thirteen schema-5 scenarios per entity tier plus one real durable-file round trip at
the first tier; CLI overrides cannot reduce, reorder, or omit them. Besides the original
bundle/query/structural probes, the immutable catalog includes a
source-generated parallel Job, Changed+enableable filtering, buffer/sparse/shared/index storage,
relation and hierarchy maintenance, multi-command archetype churn, full snapshot write, separately
timed snapshot load, a mixed update/write/load frame, changed-component delta write, and
`DurableSaveStore` commit/flush/publish/reopen/load against an actual temporary file.
Relation/hierarchy topology and overflow-buffer owners are deterministically capped at 4,096
items per tier; sparse/shared/index owners, full snapshot, and ordinary data scenarios retain the
requested entity count. One hundred independent fresh samples make
the R-7 p99 name meaningful, and a mandatory maximum-time limit still gates every observed outlier.
The two relative limits may be tightened but not relaxed.

Before doing expensive work, certification resolves a full Git commit SHA and rejects any tracked
or untracked worktree change. The report records both as `sourceRevision.gitCommitSha` and
`sourceRevision.gitWorkingTreeClean`; a certification baseline must likewise identify a clean full
commit. The runner rechecks both immediately before returning the completed report and rejects any
dirty state or HEAD change observed during the run. It then validates that the baseline is a
complete passed schema-5 report
from the same machine/runtime/OS/architecture/memory limit/GC mode and Release configuration, with
the compatible workload, exact scenario set, fresh Worlds and at least 100 samples. It also strictly
requires valid report timestamps/duration, exact environment/configuration/source/result/sample
shapes, positive entity/operation counts, complete allocation/working-set/GC fields, consistent
sample ordinals and checksums, a passed gate object, empty report/evaluation violations, and a
gate-evaluation scenario set exactly matching the results. It requires the raw sample array to match
`sampleCount` and recomputes p50/p95/p99/maximum for elapsed time, current-thread allocation,
all-thread allocation, final working set, and working-set delta from those same samples. It also
requires each sample's working-set delta to equal `after - before` and each aggregate GC generation
count to equal the sum of its raw samples. Schema 5 requires the exact workload-metric fields and
likewise recomputes each payload/update/snapshot-write/load/durable-commit/durable-load distribution
from the raw samples. It
strictly validates budget schema 1 and rejects unknown properties/scenarios. Every certification scenario
must have p50, p95, p99 and maximum absolute time limits. The completed run fails when an absolute
threshold is exceeded or p50/p99 regress beyond 5%/10%. The exact budget schema is documented in
the benchmark project's README.

Certification also validates a schema-1 prerequisite evidence manifest before measurement. Each
manifest, baseline, budget, and referenced artifact is read once into a stable byte image used for
both parsing and SHA-256 binding; the parsed baseline/budget catalogs are passed directly to the
gate rather than reopening their paths. Every bound path is rehashed after measurement and before
the report is returned. `--output` may not name any bound input or artifact; successful output is
written to a same-directory candidate and atomically replaces the destination only after the final
evidence recheck. The manifest must bind
the current clean full commit, report schema, exact baseline and budget SHA-256 values, a
hashed target-machine manifest, an exact claimed-RID set backed by successful schema-2 NativeAOT
execution evidence, at least one passed clean-commit long-fuzz artifact of 10,000 or more steps, and
one or more target-filesystem power-cut entries. Each power-cut entry must explicitly mark
process-kill, physical power-cut, primary-slot recovery, and previous-slot recovery as passed and
must hash an operator-reviewed raw artifact. Missing, false, stale, mismatched, or duplicate
evidence fails configuration before the expensive workload, so no report carrying the
`certification` profile can be emitted with those prerequisites absent.

These checks prove report/index structure and internal consistency, not organizational approval,
signatures, or that a physical power interruption really occurred. The manifest binds reviewed raw
artifacts so replacement is detectable; operators remain responsible for authentic collection and
review. The completed report records the prerequisite manifest hash and normalized binding. A final
release index must hash that report as well as the prerequisite manifest because a report cannot
include its own final hash.

Do not commit arbitrary numbers copied from a developer laptop as product budgets. To approve a
target machine:

1. Record CPU model/microcode, memory, OS build, runtime/SDK, power plan, cooling state, and process
   affinity policy. Disable debuggers, background indexing, and dynamic power-saving changes.
2. Build once in Release, reboot or otherwise return the machine to its documented steady state,
   and run the standard profile until thermal behavior is stable.
3. Establish the first baseline with `standard` plus the complete certification overrides:
   `--entity-counts 100k,500k,1m --warmup 3 --samples 100 --query-iterations 128
   --structural-iterations 64`. Certification itself never runs without gates.
4. Select and review a representative approved report. Absolute limits must come from product
   frame/loading/memory budgets, not merely from the slowest observed sample plus a generous margin.
5. Produce clean-commit long-fuzz and NativeAOT evidence, and real target-filesystem process-kill
   plus physical power-cut evidence covering recovery from both slots.
6. Create the prerequisite evidence manifest with hashes of the approved baseline, budgets,
   machine manifest, long-fuzz/AOT evidence, and power-cut logs.
7. Run the fail-closed certification command and archive its JSON output for the release candidate.
8. Create a final release index that hashes both the completed report and prerequisite manifest.

The report's `environment` object is machine-readable fixed-machine evidence (machine name,
framework, OS, process/OS architecture, processor count, available-memory limit, GC mode, latency
mode, and Release configuration), and certification requires exact equality with the approved
baseline. Keep the richer operator-maintained machine manifest beside it. A schema-1 manifest should
at minimum contain `machineId`, CPU model and microcode/firmware, installed memory and configured
memory limit, OS build, power plan/governor, cooling/thermal policy, process affinity policy, SDK
feature band, and capture timestamp. The release evidence index must record the SHA-256 of that
manifest, the schema-5 report, approved baseline, budget file, prerequisite manifest, and completed
report; the benchmark runner deliberately
does not pretend it can infer firmware, cooling, or organizational approval from managed APIs.
Every raw sample and scenario aggregate also records structural-candidate counts/timings plus total
and per-World maximum cloned archetype shells, chunk shells, and compiled query matches. The
`structural-candidate-*` workload precompiles a matching query, so all three clone dimensions are
exercised rather than emitted as permanently zero placeholders.

The schema-5 `workloadMetrics` object records payload bytes plus update, snapshot-write, load,
durable-commit, and durable-load timings for every raw sample and aggregate. A zero means that the
metric is not part of that scenario. `snapshotWriteMilliseconds` is the complete `WriteWorld` call,
not an invented admission-hold measurement: the current public diagnostics do not expose separate
admission acquisition and hold durations.

Working set is sampled at scenario boundaries, not continuously. Treat it as a coarse process
boundary measurement; platform profilers remain necessary for true peak resident memory.

## Streaming World serialization and canonical state hash

Whole-World v4 holds topology-exclusive serialization admission only through validation, source-root
pinning, and publication of a semantically identical copy-on-write successor. Codecs then read the
retained source root's final backing directly while caller I/O proceeds; concurrent mutations resolve
the published successor and detach only the pages, chunks, or topology shards they touch. No encoded
World image or component-value graph is staged. A separate lifetime lease lets an already admitted
encoder complete deterministically if another thread starts `World.Dispose()`; disposal waits for
that lease before reclaiming either generation.

The admitted write plan contains only the metadata needed for deterministic traversal:
archetype/runtime bindings, the manifest, slot identity access, and sparse-membership indexing. It
does not copy component, buffer, hierarchy, or relation values. Because the canonical entity merge
indexes M sparse memberships, memory-sensitive callers must set a positive
`SerializeOptions.MaximumSparseMemberships`; zero means no explicit membership bound, and exceeding
a positive bound fails before an unbounded plan can be accepted.

Each component or buffer value is passed to its codec exactly once. A counting stream forwards those
bytes immediately to the caller's destination and writes the measured byte count as a footer. The
same footer wire is used for seekable and non-seekable destinations; there is no length-prefix
measurement pass and no retained encoded item frame. A write or destination failure can leave an
incomplete destination, which callers must discard.

Topology encoders visit the admitted retained root directly. Hierarchy traversal reads matching
`Parent<TDomain>` rows plus its ordered/unordered child shards; relation traversal reads the final
edge generation and actual ordered adjacency shards. There is no topology snapshot DTO, preview
generation, dry encoder, digest pass, or second encode. Callers can bound the one traversal with
positive
`SerializeOptions.MaximumTopologyRecords` and
`SerializeOptions.MaximumTopologyPayloadBytes`; zero means no explicit bound. Each exporter computes
and reserves its Parent/edge records, ordered-sequence headers, and ordered members without
allocating entity-sized topology copies. Byte and record budgets are checked online during that same
encode. The measured topology length is appended as a footer, and each registered topology runtime
is invoked exactly once. After any `WriteWorld` exception the destination is incomplete and must be
discarded; transactional publication belongs in `DurableSaveStore`. `WorldCheckpointCodec` adds one
fixed current-schema envelope around these exact canonical `RawCheckpoint` bytes. Its seekable-only
writer reserves and back-patches the 128-byte header during the same root-pinned output; it has no section
directory, second component/topology codec, or retained payload backing. Checkpoint registry identity
hashes every component and topology GUID, length-delimited stable name, 64-bit schema fingerprint,
and storage kind, so even an absent registered type cannot be silently renamed.

The topology reader is equally exact: section count, registry ordinal, stable name/key, and the
canonical order of Parent, edge, and ordered-sequence records must all match the current registry.
Missing, extra, aliased, reordered, or unknown sections fail before a topology codec can accept a
different logical image. Restore writes directly into the one final World backing. Hierarchy Parent
cycles are sealed in one linear pass over the final applied-parent map; its color/ordinal dictionaries
exist only inside the importer and are cleared before inverse shards are published. Relation ordered
sequence duplicate membership uses a sequence-local set that is also cleared when the same decoded
entry array is transferred into its final generation. Neither validator becomes a retained second
topology graph.

Serialization admission defines concurrency behavior: ordinary mutation waits only for the short
root handoff, then proceeds against the COW successor even if the caller stream is slow. `World.Dispose`
waits for the retained-root lifetime. Success, codec failure, destination failure, cancellation, and
disposal exceptions release the read root, any short admission, and the lifetime lease;
World/checkpoint writing itself changes neither topology revision nor structural root epoch.

Delta serialization follows the same retained-root boundary. It captures one journal sequence prefix.
After successful output it reacquires a short control-plane admission and acknowledges only through
that sequence on the current published root, preserving events appended by concurrent successor
mutations. Failed output acknowledges nothing, and overlapping snapshots may complete out of order
without deleting a newer snapshot's suffix.

Whole-World/checkpoint serialization fails closed when the admitted World contains a
registered value whose struct contains managed references. World admission freezes the stored
struct/reference shell, but it cannot freeze an object mutated through an external alias; such values
remain unsupported by this wire contract rather than creating a second backing or alias-sensitive
snapshot path.

`WorldSerializer.ComputeWorldStateHash` feeds that same canonical `WriteWorld` byte contract
directly into the shared online SHA-256 stream and returns the shared inline `Digest256` value,
without materializing either the serialized World image or a separate digest byte array. It covers
the contract marker, type manifest, entity identities/slots, component payloads, and
relation/hierarchy topology. Hash comparability therefore requires the same portable serialization
contract, schemas, and codecs; this digest is deterministic state comparison and corruption
evidence, not authentication or confidentiality.

## Durable-save publication

`DurableSaveStore` wraps the canonical `DurableSave` payload with one current version-4 file
envelope:

- monotonic 64-bit generation;
- exact payload length;
- an explicit SHA-256 corruption-detection or HMAC-SHA256 authentication kind;
- two alternating generation slots (`path` and `path.previous`).

A commit rejects `async void` writers, exposes a synchronous-only stream, and encodes each ECS value
once directly into a unique temporary file in the destination directory while computing the envelope
digest online. It bounds the payload, backpatches the fixed envelope header, calls
`Flush(flushToDisk: true)`, reopens the temporary file to verify the bytes already written, and then
atomically replaces only the older slot. Reopening verifies publication bytes; it does not invoke an
ECS codec again. The newest verified slot is never the replacement target.

Ordinary recovery inspects only each slot's fixed header, exact file length, generation, authentication
mode, and anti-rollback floor before selecting a candidate. It does not preload, mmap, copy, or hash a
complete payload. The selected file is opened once. `WorldSerializer` constructs one new final World
while the same bounded stream hashes the bytes it returns; the remainder is drained through that same
stream before the digest decision. An invalid digest disposes the candidate World and may fall back to
the older generation. Once the digest is valid, unknown schema, codec failure, or trailing semantic
data is authoritative corruption and is surfaced without falling back. Generation/path races cause a
fresh header inspection rather than reuse of stale bytes.

Supplying an authentication key requires HMAC-SHA256 envelopes; an unkeyed store requires SHA-256
envelopes. Wrong keys, keyed/unkeyed mode mismatch, and tampering reject the affected generation.
Construction transfers ownership of the exact `AuthenticationKey` array to the store; it is never
cloned, and callers must not read or mutate it afterward. The store does not generate, persist,
rotate, or securely store keys.

`DurableSaveStore` is disposable: disposal rejects self-disposal from an active store callback,
waits for already admitted synchronous operations, clears that same owned authentication-key array,
and causes later operations to throw `ObjectDisposedException`.

An OS-level `path.lock` file rejects concurrent writers. Reads remain lock-free and retry if rapid
concurrent publications change the selected generation. If every existing slot is invalid, a write
fails instead of overwriting forensic evidence. `MinimumAcceptedGeneration` rejects otherwise valid
slots below a caller-supplied anti-rollback floor, and a brand-new store starts above that floor.
Persisting and monotonically advancing the floor is the product's responsibility; the two files do
not themselves provide a trusted monotonic counter. Neither SHA-256 nor HMAC-SHA256 encrypts the
payload or provides confidentiality.

Automated tests inject exceptions after payload write, disk flush, temporary-file verification,
immediately before publication, and immediately after publication. They also cover writer
exceptions, newest-slot byte corruption, truncation, size limits, generation rotation, and World
round-trip. These tests model process interruption. Actual power-cut certification must still be
performed on every target filesystem/storage stack because flush and rename durability are OS and
filesystem contracts.

Delta output is diagnostic journal data, not an apply/mutation format. `ReadDeltaEvents` consumes and
validates the current manifest without retaining a second manifest array, then fills the one final
`DeltaEvent[]` directly. The writer's process-local `ComponentId` remains diagnostic only. Unknown
event kinds, invalid manifest indices, unversioned event sections, and every pre-v4 envelope fail
closed. There is no `ApplyDelta`, candidate apply mode, unknown-type skip, or schema migration path.

## NativeAOT gate

The AOT project is a normal project during solution builds. Native compilation is enabled only by
the explicit gate, which restores the runtime-specific compiler/runtime packs and forces a rebuild:

```powershell
dotnet restore tools/SomeEngine.ECS.AotSmoke/SomeEngine.ECS.AotSmoke.csproj `
  -r win-x64 -p:PublishAot=true -p:_IsPublishing=true

dotnet publish tools/SomeEngine.ECS.AotSmoke/SomeEngine.ECS.AotSmoke.csproj `
  -c Release -r win-x64 --self-contained true --no-restore `
  -t:Rebuild `
  -o tools/SomeEngine.ECS.AotSmoke/bin/Release/net10.0/win-x64/cert-publish `
  -p:PublishAot=true -p:SelfContained=true `
  -p:TreatWarningsAsErrors=true -p:ILLinkTreatWarningsAsErrors=true `
  -p:TrimmerSingleWarn=false

& tools/SomeEngine.ECS.AotSmoke/bin/Release/net10.0/win-x64/cert-publish/SomeEngine.ECS.AotSmoke.exe
```

Use the corresponding RID and executable path on Linux/macOS. `-t:Rebuild` is mandatory: an
incremental publish may reuse native intermediates and hide a newly introduced analysis problem.

`just ecs-aot <rid>` performs both commands and then executes the produced host-compatible native
binary. The recipe uses a fixed `cert-publish` directory, forces the NativeAOT/self-contained and
warning-as-error properties, and accepts no trailing MSBuild arguments, so callers cannot redirect
the current output or execute a stale default-publish artifact. The RID is restricted before shell
interpolation to supported Windows, Linux, Linux-musl, and macOS x64/arm64 forms. Its final relative executable path
uses `.exe` only on Windows and is directly executable by the default shell on Linux/macOS.
`just ecs-aot-build <rid>` has the same non-overridable build inputs but is publish-only and must not
be counted as certification evidence.

For a configurable host-compatible RID set, run:

```powershell
just ecs-aot-matrix win-x64 artifacts/ecs-aot-win.json
```

Use a comma-separated list when one host can genuinely execute more than one claimed RID. The
matrix recipe validates the entire list before shell interpolation, rejects duplicates, and then—
before any restore, publish, or native execution—requires an empty tracked/untracked Git status and
captures a full commit SHA. An evidence destination inside the repository must be git-ignored and
must not be a tracked source path; the validated JSON is written through a same-directory temporary
file and atomically moved into place so interruption cannot leave a partial evidence document. It invokes the
full build-and-execute `ecs-aot` gate for every RID, stops on the first failure, hashes each exact
native executable, rechecks that both worktree and HEAD stayed unchanged, and only then writes a
schema-2 evidence document. Its fields are `schemaVersion`, `createdUtc`, full `commitSha`,
`clean: true`, `sdkVersion`, `machineName`, `hostFramework`,
`hostOperatingSystem`, and an exact `results` array containing `rid`, `executed`, `exitCode`, and
`executableSha256`. A publish-only cross-RID artifact is never marked executed. Run separate matrices
on the corresponding Windows/Linux/macOS and architecture hosts, then require the union of their RID
sets to equal—not merely contain—the release's configured claimed-RID list.
The native executable does more than start. It verifies:

- a 17-byte packed `byte/int/float/long` source-generated component retains and selects
  `RawCanonical` layout proof, and preserves every field bit-for-bit through both formats;
- a concrete generated `IJobEntity` executes in parallel and mutates the expected rows;
- an `async void` `IJob` is rejected before its body or scheduler accounting runs;
- command-buffer publication and deferred identities;
- hierarchy creation, lookup, durable serialization, and reconstruction;
- relation direct-edge destruction and endpoint-driven incident-edge destruction through typed
  dispatch;
- canonical durable save and exact-build checkpoint round-trips;
- the two-generation file store reaches generation two and reloads the World;
- structural and Job completion metrics are non-zero.

The certified AOT boundary is intentionally explicit. Closed, concrete generated `IJobEntity`
component shapes receive a Roslyn field-graph proof that includes compiler-generated auto-property
backing fields. Native-sized handles, including nested `nint`/`nuint`, are rejected. The three
certificate factories are internal; generated code reaches them through private compiler-supported
`UnsafeAccessor` bridges whose generic constraints exactly match the target, so the supported public
API cannot mark a component alias-free. This closed path avoids runtime reflection. Certification
covers documented public ECS/Job APIs and code emitted by the official source generator;
`internal`/`private` is an API boundary, not an in-process security boundary. Deliberately invoking
non-public members through another `UnsafeAccessor` or reflection, unsafe/native memory mutation,
runtime patching, or equivalent accessibility bypass is unsupported and can violate ECS scheduling
and alias-safety invariants. The engine does not attempt to sandbox hostile code running in its own
process. Generic/unresolved
direct-storage shapes and the manual typed Job access APIs still use the runtime shape classifier;
they are supported on the JIT but are not claimed as NativeAOT-certified. If a future product needs
those APIs under AOT, add an explicit generated certificate surface rather than suppressing trim
warnings or globally preserving reflection metadata.

## Release evidence and acceptance

A release candidate passes this gate only when all of the following are attached to the candidate:

1. Release solution build and tests, including all fuzz and serialization fault tests.
2. Fixed and approved long-seed fuzz success artifacts from the clean-worktree launcher, with any
   failure trace retained and replayed.
3. A schema-5 certification benchmark report from the named target machine, with its approved
   baseline, absolute budgets, and validated prerequisite evidence binding.
4. A warning-free forced NativeAOT rebuild for every claimed RID and successful execution of each
   produced native binary.
5. Target-filesystem process-kill and power-cut save testing, including recovery from both slots.
6. The commit SHA, SDK/runtime versions, machine manifest, runner schema versions, prerequisite
   manifest, completed-report hash, and all referenced artifact hashes.

Until target hardware budgets and power-cut evidence exist, the repository has an executable
certification system and local evidence, but it must not be described as universally “AAA
certified.”
