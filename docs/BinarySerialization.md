# SomeEngine Binary Serialization

This document is the durable engineering contract for SomeEngine's reflection-free binary serialization, assets, packs and patches, range streaming, and ECS current-schema canonical checkpoints. C# declarations are the only authoritative schema source. The retired FlatBuffers schemas and generated runtime are not part of the product or build graph.

## Design invariants

- Every generated type is a `partial` C# type marked with `[BinarySerializable]` and implements `IBinarySerializable<T>` through compile-time generation. A deliberately handwritten contract may implement the same static interface directly.
- `[TextSerializable]` opts the same declaration into static JSON encoding through `ITextSerializable<T>`. Field identities and ordering are shared with the binary codec. See [readable asset sources](AssetSources.md) for the source envelope, payload references, and current integration coverage.
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

Supported generated shapes include primitives, enums, GUIDs, strict UTF-8 strings, byte memory, arrays, `List<T>`/`IList<T>`, `ImmutableList<T>`, `ImmutableArray<T>`, canonical `Dictionary<TKey,TValue>`/`IDictionary<TKey,TValue>`, nullable values, mutable records, nested contracts, and explicitly closed unions. Dictionary keys are emitted in canonical wire order. Collection writers snapshot `Count` once, binding the encoded count and traversal bound to the same value. An uninitialized immutable array is rejected; an empty array and a nullable array's absence remain distinct.

Nested values can come from another assembly when they provide the static serialization and view-validation interfaces. Generated types expose both; a parent schema includes the referenced codec's actual schema. This does not scan assemblies or regenerate the referenced type. The defining generator also emits `BinaryValue` for source shapes safe to copy or share without mutable reference contents. Asset declarations use this proof for external values, instead of inferring safety from incomplete private metadata. Generic encoding remains usable for mutable objects without this proof.

Generation rejects non-partial or generic declarations, inaccessible construction or setters, unsupported or open shapes, duplicate names or field keys, object cycles, invalid nested contracts, and colliding type IDs. Inherited public data participates in the schema; setters must be accessible from the generated codec, including an accessible protected setter. It does not silently fall back to reflection.

Default field order is ordinal logical-name order, independent of source paths and partial-file arrangement. `BinaryName` defines a field's encoded identity and can preserve an enum's identity when its C# namespace changes. An optional `BinaryFields` attribute declares the complete explicit order by C# member name; duplicate, missing and unknown names are diagnosed. Ordinary binary generation only processes explicit binary declarations and does not inject asset fields into the compilation.

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

Creating and reading a primitive generated view allocates zero managed bytes after warm-up. Fixed fields use generated offsets; variable layouts retain only their field offsets during initial validation. Repeated tail-field reads do not rescan a large preceding collection. Corruption, truncation, budget overflow, and use after disposal fail closed.

### Proven native layout

`NativeRaw` is never inferred from `unmanaged` alone. A type must opt in with `[BinaryNativeLayout]`. Generation then requires explicit sequential layout and packing, recursively unmanaged fields, a known size, and no implicit or tail padding. It emits `NativeLayoutProof<T>`. `NativeBlock` version 2 accepts only that proof before exposing an allocation-free typed span. A merely plausible, padded, or platform-dependent CLR layout is rejected at compile time.

### Single-encoding behavior

`BinarySerializer.TryWrite<T>` invokes `T.Write` at most once into caller-supplied storage. An undersized destination may contain the prefix already produced by that call; failure never triggers measurement or re-encoding. `Write<T>(IBufferWriter<byte>, T)` provides the same single-encoding behavior for extensible output. Span and `ReadOnlySequence<byte>` inputs share the existing reader and read limits; segmented primitives and strings do not require concatenating the input.

Writable generated classes also implement `IBinaryObjectReader<T>`. `DeserializeInto` and `BinaryDocument.ReadRootInto` decode into an existing object and reuse its writable `List<T>` / `IList<T>` instances. This lets a caller choose paged storage without a temporary complete list or an ambient allocator in the codec. These operations can partially modify the target on failure; a caller requiring atomic installation supplies a new object or an isolated branch. Types with init-only properties keep their construction-based reader.

Document and pack writers accept writable, seekable `Stream` destinations with length control. They encode roots directly while observing length and SHA-256, then backfill the existing header and directory. File publication separately performs the temporary-file flush and atomic replacement. Chunk input may be continuous memory or `ReadOnlySequence<byte>`; compression and hashes consume segments without joining them. Directory descriptors retain hashes as inline `Digest256` values and are encoded once into a stack span, written into the reserved directory, and fed to its hashers.

Sequential legacy frames are not supported. `SequentialFrame`, generic migration registries, additive field framing, and compatibility readers are absent from the product API.

## Binary document wire

`BinaryDocument<T>` uses the current magic `SEBDOC04` and format version 4, a fixed 128-byte header, a hashed type catalog, a small root record, a sorted 96-byte chunk directory, and aligned semantic chunks. Type-catalog entry zero explicitly identifies the root; remaining entries are sorted by type ID. Matching a secondary entry's schema cannot substitute another root type. Earlier document versions fail closed; affected asset products must be regenerated.

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | `SEBDOC04` magic |
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

`RangeLease`, `ChunkLease`, `ResidentChunkLease`, and document access objects make memory lifetime explicit. Document-level ranges require their document lifetime. A page-backed scheduler retains its document until queued work has actually stopped; explicitly retained page ranges and branches independently outlive the document and cache.

Document decoding does not reject a source merely because it retains memory or a mapping. An explicit target may hold a necessary decoded representation, and sources that cannot guarantee immutable borrowed ranges use explicit target reads. Source storage and decoded storage remain separate accounting responsibilities.

The existing mesh consumer's source restrictions are separately scheduled for migration in A25. They are not a general binary-document restriction.

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

Disk content and application objects have independent lifetimes. The directory identifies content, an asset selects editable content, and explicitly created business objects retain what they need for use. The loader does not maintain one unique business object per GUID.

Asset identity and references are separate from strongly typed Block content. All asset families use independent asset declarations and explicit Block-to-object conversion; the former generator that mirrored business types into field roots and nested asset classes has been removed. Block encoding reuses the generic interfaces above. Current architecture and verification status are maintained in the [original Wiki](../wiki/architecture/Asset-Files.md); page APIs are documented in [AssetPages.md](AssetPages.md).

`AssetLoader.OpenSourceAsync` resolves an `IAssetStorage` record, joins a bounded file read and returns independently closable binary-document access. Content versions can coexist. Closing the loader attempts all cleanup and does not dispose external storage or application objects.

Asset and Block saving use the existing document writer and file replacement. The binary layer copies unchanged chunks from their original source with bounded memory and preserves their stored representation, including unknown or unopened Blocks. Asset branch duplication uses shared content indexes; it does not serialize the asset to make a copy.

`AssetProject` imports, creates, registers, indexes and diagnoses files. `AssetEntry` is the shared directory record. Source identity, settings and element identities live in `.meta`; derived inputs and output verification live in `.import.json`. Source, asset and dependency indexes are saved together in one `catalog.json`, without the three old index files. Explicit mesh streaming reads authenticated pages directly into final storage; already usable meshes do not query Asset on ordinary access. Textures acquire selected mip data asynchronously before use and feed existing device upload/completion handling. Shader backend selection and material parent/override data remain supported. Scene instances enter the existing Game world to Rendering world extraction path, not a loader-maintained object graph.

## Current-schema policy

`BinaryEnvelopeMetadata` exposes root type, schema, compatibility marker, epoch, generation and payload length without decoding business values. Non-generic `BinaryDocument.InspectAsync` works without an installed business type. Typed reads still require the expected compiled schema. Description, object and view opening share header/catalog checks and external package authentication. Schema changes require producing new asset data offline; there is no implicit migration reader.

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

The Assets smoke publishes a native executable and exercises independent opens, usable Texture instances after file/Loader closure, explicit updates, downstream formats, generated Microsoft DI registration, typed importer settings, dependency deduplication, competing Block producers, multiple Blocks on one mesh, and persistence. It also replaces a catalog while a reader retains the preceding file, including a Unicode directory. This executes the static Windows file call rather than inferring AOT support from source inspection. Slang source compilation remains in the build tools; prebuilt shader bytecode is consumed by the native smoke. Serialization and ECS have separate native smoke entrypoints.

The regression suites cover canonical/view/native behavior, corruption and truncation, read and residency budgets, compression bombs, rejection of previous/unknown envelopes and schemas, type identity, binary-document 8 GiB virtual offsets, range isolation, deterministic generations, signed packs, overlays and changed-only patches, strong-ETag HTTP, coalescing and corrupt-member isolation, 100-request deduplication, deadlines/cancellation/priority, four residency classes, mesh/texture integrity, exact final-storage ownership, ECS streaming rollback/fail-closed cases, fuzz cases, and telemetry.
