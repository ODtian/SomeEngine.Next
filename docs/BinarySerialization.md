# SomeEngine Binary Serialization

This document is the durable engineering contract for SomeEngine's reflection-free binary serialization, assets, packs and patches, range streaming, and ECS current-schema canonical checkpoints. C# declarations are the only authoritative schema source. The retired FlatBuffers schemas and generated runtime are not part of the product or build graph.

## Design invariants

- Every generated type is a `partial` C# type marked with `[BinaryContract]` and implements `IBinaryContract<T>` through compile-time generation. A deliberately handwritten contract may implement the same static interface directly.
- Runtime encoding and discovery use no reflection, assembly scanning, `Activator`, expression compilation, or runtime code generation.
- Integer and floating-point primitives are canonical little-endian. GUIDs use RFC/network byte order. Booleans accept only `0` and `1`. Strings are strict UTF-8 with explicit nullable length framing.
- `ExactSchema` is the only compatibility mode. Type id, fingerprint, compatibility marker, and epoch must all match the current reader before decoding. Unknown or older schemas fail closed; there is no runtime migration or legacy envelope path.
- Untrusted lengths, counts, recursion, allocations, compression ratios, root sizes, catalogs, chunk counts, checkpoint bytes, and records are bounded before allocation or iteration.
- Borrowed `Span` or `Memory` remains valid only through an explicit owner or lease. Disposing a document invalidates its document-level chunk leases and generated root views.
- Each read has an explicit final destination. A memory mapping counts as resident CPU memory. File and HTTP ranges can write directly into caller-supplied storage. Independent asset accesses and domain objects may share immutable file bytes; CPU decoded bytes and GPU allocations have separate lifetimes and budgets.
- Binary-document offsets are signed 64-bit nonnegative values and are checked before arithmetic. A materialized root, chunk, or contiguous native block remains bounded by the CLR `int` contiguous-memory limit.
- Cross-domain binary mechanics live in `SomeEngine.Serialization`: `BinaryPrimitiveEncoding`,
  `BinaryTypeId` / `BinaryFieldKey`, the inline `Digest256` value, bounded counting streams, and
  online `HashingReadStream` / `HashingWriteStream`. Asset documents and ECS envelopes compose
  those mechanisms but retain separate read/admission limits because they meter different domain
  objects, as well as separate schemas, registries, ownership models, admission protocols, and
  failure policy. A domain must not fork another digest or hashing-stream implementation merely to
  attach its own metadata, and it must not project one domain's limits into another's.

## Compile-time contracts

`BinaryContractGenerator` processes declarations incrementally. It emits a deterministic `TypeId`, transitive schema fingerprint, dense exact-schema codecs, depth guards, generated views, and an AOT-rooted catalog entry.

The fingerprint includes logical names, member order, wire shapes, nullability, collection element shapes, union closure, and every nested contract fingerprint. Variable-length descriptor tokens are length-framed before the SHA-256 digest is truncated to 64 bits. The logical `TypeId` is the first 128 bits of SHA-256 over the logical type name.

Supported generated shapes include primitives, enums, GUIDs, strict UTF-8 strings, byte memory, arrays, `List<T>`/`IList<T>`, canonical `Dictionary<TKey,TValue>`/`IDictionary<TKey,TValue>`, nullable values, mutable records, nested contracts, and explicitly closed unions. Dictionary keys are emitted in canonical wire order. Collection writers snapshot `Count` once, binding the encoded count and traversal bound to the same value.

Generation rejects non-partial or generic declarations, inaccessible construction or setters, concrete base-class inheritance, unsupported or open shapes, duplicate names or field keys, object cycles, invalid nested contracts, and colliding type IDs. It does not silently fall back to reflection.

Each contract assembly receives an assembly-local catalog in a namespace derived from that
assembly's name. Code in the same assembly can register every generated contract explicitly:

```csharp
global::SomeEngine.GeneratedContracts.Assembly_MyProduct
    .GeneratedBinaryContractCatalog.RegisterAll(catalog);
```

The catalog type is intentionally internal: referencing two assemblies that both declare binary
contracts cannot import a colliding global catalog type. Registration is explicit and deterministic.
`BinaryContractCatalog.Freeze()` closes mutation before runtime use.

### Generated validation and views

Every generated contract exposes a bounded validator, its exact current-schema reader, a stack-only `SpanView`, and an owner-backed readonly `View`:

- primitive, enum, GUID, strict UTF-8, and byte-blob getters read directly from validated bytes;
- complex fields expose their bounded encoded slice and can be materialized explicitly;
- `BinaryContractViewOwner` invalidates all copied views when it is disposed;
- `BinaryDocumentView<TContract,TView>` retains the root range lease and applies the same invalidation rule;
- a handwritten contract may provide an equivalent view through `IBinaryCustomViewContract`.

Generated views do not expose `Materialize`. Callers either keep the one borrowed view backing or explicitly enter the exact-schema object reader; there is no view-to-object compatibility shortcut that retains both representations.

Creating and reading a primitive generated view allocates zero managed bytes after warm-up. Corruption, truncation, budget overflow, and use after disposal fail closed.

### Proven native layout

`NativeRaw` is never inferred from `unmanaged` alone. A type must opt in with `[BinaryNativeLayout]`. Generation then requires explicit sequential layout and packing, recursively unmanaged fields, a known size, and no implicit or tail padding. It emits `NativeLayoutProof<T>`. `NativeBlock` version 2 accepts only that proof before exposing an allocation-free typed span. A merely plausible, padded, or platform-dependent CLR layout is rejected at compile time.

### Single-encoding behavior

`BinaryContractSerializer.TryWrite<T>` invokes `T.Write` at most once into caller-owned final storage. An undersized destination may contain the prefix already produced by that single invocation; the serializer reports failure and never measures, retries, grows, or re-encodes the value. Document roots are encoded once directly into their final `FileStream` while length and SHA-256 are observed online. Binary-document and pack writers accept a final file stream rather than a general `Stream`, so a `MemoryStream` cannot become a second complete encoded backing. Directory descriptors retain hashes as inline `Digest256` values, are encoded once into a stack span, written directly into the reserved final directory, and fed to the directory and generation hashers from that same span. There is no per-entry encoded array, per-hash managed backing, or product API that constructs a complete encoded frame in a growable intermediate buffer.

Sequential legacy frames are not supported. `SequentialFrame`, generic migration registries, additive field framing, and compatibility readers are absent from the product API.

## Binary document wire

`BinaryDocument<T>` uses the sole current magic `SEBDOC03` and format version 3, a fixed 128-byte header, a hashed type catalog, a small root record, a sorted 96-byte chunk directory, and aligned semantic chunks. The former `SEIDX001`/`SEIDX002` envelopes and every other envelope fail closed; there is no IndexedDocument reader or compatibility path.

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | `SEBDOC03` magic |
| 8 | 2 | format version |
| 10 | 2 | header size, always 128 |
| 12 | 1 | compatibility |
| 14 | 2 | directory entry size, always 96 |
| 16 | 4 | schema epoch |
| 20 | 4 | chunk count |
| 24 | 8 | root fingerprint |
| 32 | 16 | deterministic generation GUID |
| 48 | 8 | root offset |
| 56 | 8 | root length |
| 64 | 8 | directory offset |
| 72 | 8 | directory length |
| 80 | 8 | total document length |
| 88 | 32 | root SHA-256 |
| 120 | 4 | type-catalog length |
| 124 | 4 | header metadata checksum |

Each directory entry records a semantic key, type fingerprint, stored and decoded ranges, required alignment, compression codec, decoded SHA-256, ordinal, and metadata checksum. Entries are sorted by key and searched by bounded ranges. A request reads only directory entries needed by the search and the selected chunk.

`None` and Brotli are defined codecs. Unknown codecs, malformed metadata, overlaps, offset overflow, truncation, invalid alignment, hash mismatch, excessive expansion, duplicate types, and generation changes fail closed. Generation derives from the type catalog, root hash, and every canonical directory descriptor, so content or wire changes cannot intentionally retain the same automatic generation.

## Range IO and ownership

`IRangeSource` implementations include contractually frozen borrowed memory, stable explicit-offset file handles, memory-mapped files, strong-ETag HTTP ranges, and bounded child ranges in packs.

- `MemoryRangeSource` borrows caller memory without copying; the caller must keep the borrowed bytes stable for the complete source/lease lifetime.
- `FileRangeSource` uses `RandomAccess` against one stable open handle.
- `MemoryMappedRangeSource` offers immutable read-only views, but the mapping still counts as a resident physical backing.
- `HttpRangeSource` requires a strong ETag and content length, sends `Range` and `If-Match`, requires exact `206`/`Content-Range` semantics, and rejects generation changes, excess bodies, and truncation.
- `RetainsResidentBacking` is fail-closed by default and is propagated through nested document and pack sources. File and HTTP sources report false; memory and memory-mapped sources report true.

`RangeLease`, `ChunkLease`, `ResidentChunkLease`, and document owners make memory lifetime explicit. A document must outlive its chunk and child-range consumers.

Streamed meshes accept only non-resident file or remote range sources. Memory, memory-mapped, and unknown wrapper sources fail closed because publishing a page or BVH would otherwise retain a second physical backing. Page and BVH reads target final publication storage directly and authenticate that storage in place.

## Packs, overlays, patches, and signatures

`AssetPackBuilder` stores complete binary asset documents as uncompressed, 4096-aligned outer chunks so each inner document remains range-addressable. Inner semantic chunks may still be compressed. Admission validates the nested header, catalog, exact root identity/hash, directory ordering/ranges, declared total length, and schema fingerprint without decoding or hashing semantic payloads a second time. Semantic chunk hashes are checked lazily by the nested document when a chunk is acquired.

`AssetPack` validates catalog identity, outer descriptors, requested contract identity, and the nested binary document. `AssetPackStorage` resolves its ordered base, DLC, and hotfix packs in explicit highest-priority-first order. Open asset byte ranges retain independent access when the storage closes.

`AssetPackBuilder(basePack)` compares complete binary-document SHA-256 values and emits only changed or newly added assets. Unchanged assets are absent. Publication writes a same-directory temporary file, flushes it, and atomically replaces the target.

Signed packs use RSA PKCS#1 v1.5 with SHA-256 over deterministic pack identity and authenticated content receipts. Signature metadata is hashed online rather than materialized as a second encoded buffer. `OpenVerifiedAsync` verifies header/catalog/root/directory identity without eagerly reading every semantic payload; modified payload bytes fail against their signed directory receipt on first acquire. Unsigned packs under a verification policy, wrong keys, modified metadata, modified nested bytes, and corrupt signatures fail closed.

## Scheduler, coalescing, telemetry, and residency

`ChunkRequestScheduler` provides bounded admission, priority ordering and promotion, deadlines, per-waiter cancellation, concurrent-load limits, in-flight key deduplication, pinned leases, decoded LRU eviction, and telemetry. A waiter deadline never cancels the shared load or another waiter. Pinned and actively awaited entries cannot be evicted.

`DocumentChunkBatchLoader` waits up to 1 ms for adjacent requests, merges stored ranges separated by at most 4 KiB, and caps one merged read at 4 MiB. Gap bytes are admitted to the stored/compressed budget. One corrupt member fails only that member; valid siblings from the same merged range can complete. Metrics include stored bytes read, decoded bytes, read amplification, deduplication, and time to first resident byte.

`ResidencyBudgetLedger` independently accounts for:

- stored/compressed bytes;
- decoded CPU bytes;
- upload-staging bytes;
- GPU bytes.

Reservations are atomic, overflow-checked, pre-admitted before expensive work, and released by idempotent ownership tokens. Undefined classes are rejected.

## Assets and offline importers

Disk content and application objects have independent lifetimes. `AssetId<T>` is a typed GUID, `Asset<T>` is access to one disk-content version, and `T` is an explicitly created object. An opened asset is not a wrapper around a ready `T`. The loader never owns a unique runtime object for each GUID.

The complete type map, public operations, limits, ownership rules and verification coverage are documented in [Assets: disk content and objects](AssetSystem.md). File-field declarations use `[Asset(typeof(T), ".suffix.asset")]`; compile-time generation connects their binary codec to internal `AssetFormat<T>`. File fields and runtime `T` are different types. Opening validates file identity/metadata without instantiating dependencies. Domain creation and updates perform the required content reads explicitly.

`AssetLoader.OpenAsync` resolves an `IAssetStorage` record, joins a bounded file read and returns independently closable `Asset<T>` access. Content versions can coexist. Closing an asset or evicting the loader's file cache does not invalidate objects that retain their own byte access. Closing the loader attempts all cleanup and does not dispose external storage or application objects. It does not wait for entire rendering epochs merely to open a new file version.

`AssetWriter` writes captured disk fields to a temporary file and atomically replaces its target. Capturing fields isolates later caller mutations. An already opened document is copied in bounded buffers with its original type catalog, compressed chunks and generation preserved; chunks are verified before the copy replaces an existing file. The separate field-copy operation is not a second encoding of the final binary document. A document duplicate decodes independent fields from validated original root bytes; it does not expose cache fields as a mutable shared object.

`AssetProject` imports, creates, registers, indexes and diagnoses files. `AssetEntry` is the shared directory record; source import data is consolidated in `SourceMeta`. Runtime meshes continue reading authenticated pages directly into final storage. Textures prepare coarse mip data asynchronously and feed existing device upload/completion handling without asset I/O during rendering. Shader backend selection and material parent/override data remain supported. Scene instances enter the existing Game world to Rendering world extraction path, not a loader-maintained object graph.

## Current-schema policy

`BinaryEnvelopeMetadata` exposes type, fingerprint, exact compatibility marker, epoch, and payload length without decoding the root. Inspection does not grant compatibility: every open still requires the exact compiled contract. Schema changes require producing new asset data offline; the runtime contains no recook, dump, conversion, additive, or migration path.

## ECS canonical checkpoint envelope

`WorldCheckpointCodec` wraps exactly one current `WorldSerializer` `RawCheckpoint` payload. It is an envelope, not a second World serializer.

- the only accepted magic/version is `SEWCP003`; previous envelopes fail closed;
- the fixed 128-byte header contains canonical u64 payload/total lengths, the exact registry SHA-256, the payload SHA-256, and a header-integrity prefix;
- the payload bytes are identical to one direct `WriteCheckpointWorld` encode: there is no directory, section DTO, independent component/topology implementation, or checkpoint-only native dump;
- a seekable destination reserves the header, streams the canonical payload once through the shared
  online hashing stream, then back-patches the header; topology-exclusive admission covers only
  validation and the COW root handoff, not caller-controlled output;
- a non-seekable checkpoint destination is rejected before any codec runs; the runtime never retains a whole encoded checkpoint merely to emulate seeking;
- readers check the exact envelope, bounded payload length, and registry identity before decoding directly into one new final `World`; any later payload/hash failure disposes that World;
- there is no `LoadInto`, candidate apply, checkpoint capture object, failure-atomic compatibility apply, or old-World/new-World coexistence API.

Replacing a world uses `Read` and publishes the returned replacement. Tests cover every storage path, hierarchy and relation topology, exact one-call codec behavior, admission wait/re-entry/release paths, u64 overflow and truncation, current-only rejection, and NativeAOT execution.

## Acceptance

The ordinary workflow uses the main solution and focused tools. The suspended project harness workflow is not required.

```powershell
dotnet build SomeEngine.slnx --no-restore -v minimal -m:1
dotnet test SomeEngine.slnx --no-build --no-restore --verbosity quiet -m:1
dotnet run --project tools\SomeEngine.Serialization.PerformanceGate\SomeEngine.Serialization.PerformanceGate.csproj -c Release --no-restore
dotnet publish tools\SomeEngine.Serialization.AotSmoke\SomeEngine.Serialization.AotSmoke.csproj -r win-x64 -c Release --no-restore
dotnet publish tools\SomeEngine.Assets.AotSmoke\SomeEngine.Assets.AotSmoke.csproj -r win-x64 -c Release --no-restore
dotnet publish tools\SomeEngine.ECS.AotSmoke\SomeEngine.ECS.AotSmoke.csproj -r win-x64 -c Release --no-restore
```

Acceptance requires zero-warning product builds plus the focused Serialization, Assets, pack, range-source, generated-code, and NativeAOT tests listed above. Previous-envelope inputs are retained only as fail-closed rejection tests; no compatibility fixture, converter, dump tool, or retired serializer throughput comparison is a product acceptance path.

The Assets smoke publishes a native executable and exercises independent opens, streamed Texture instances after file/Loader closure, explicit updates, and an additional format generated in the downstream smoke assembly. It does not run importers or compile shader source. Serialization and ECS have separate smoke entrypoints for their own binary and world formats.

The regression suites cover canonical/view/native behavior, corruption and truncation, read and residency budgets, compression bombs, rejection of previous/unknown envelopes and schemas, type identity, binary-document 8 GiB virtual offsets, range isolation, deterministic generations, signed packs, overlays and changed-only patches, strong-ETag HTTP, coalescing and corrupt-member isolation, 100-request deduplication, deadlines/cancellation/priority, four residency classes, mesh/texture integrity, exact final-storage ownership, ECS streaming rollback/fail-closed cases, fuzz cases, and telemetry.
