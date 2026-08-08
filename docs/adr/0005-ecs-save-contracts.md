---
status: accepted
---

# Separate exact-build checkpoints from durable saves

## Context

An ECS snapshot has two distinct jobs. A crash/restart checkpoint for the same executable should use
the cheapest exact-build representation available. A player save may cross runtime and platform ABI
boundaries, but this runtime accepts it only when every stable type and 64-bit schema fingerprint is
the current registered schema. Treating native struct bytes as durable data makes a fast checkpoint
look portable even though padding, layout, endianness, runtime identity, or a changed component
definition can silently reinterpret it.

## Decision

Serialization format version 4 carries one explicit contract:

- `RawCheckpoint` is an exact-build artifact. Its header binds the payload to the current ECS and
  serialization assembly module identities, runtime/OS architecture, pointer size, and endianness.
  Every implicit raw component key is additionally bound to the actual CLR type/module/token,
  layout, size, storage kind, and reference shape. A mismatch is rejected before applying data.
- `DurableSave` accepts only an explicit stable 64-bit schema fingerprint and a canonical or custom
  codec. Implicit native-layout codecs and build-derived schema identities are rejected. Generated
  codecs encode fixed-width fields in canonical little-endian form and include field identity,
  enum shape, nested schema, schema version, and codec version in the fingerprint.

The generated packed-primitive memcpy optimization is only an implementation of the same canonical
wire image. Eligibility is proven and rechecked once during registration: the type must be a
single-declaration sequential `Pack=1` value with a continuous, supported fixed-width field layout,
and the generated layout fingerprint must match the runtime offsets. A caller-supplied byte size is
not a proof. Failure to prove the layout falls back to the generated canonical codec; it never
downgrades a durable save to native ABI bytes.

Readers use strict UTF-8 plus shared count, string, payload, topology, and estimated-allocation
budgets. Every type key contains exactly one non-zero 64-bit schema fingerprint. Unknown stable IDs,
zero or mismatched fingerprints, pre-v4 envelopes, length-prefix frames, and unversioned delta
sections fail closed. There is no 32-bit fallback, old primitive Guid/string decoder, unknown-type
skip, or runtime schema-conversion registry.

Every component, buffer value, and topology section is encoded at most once. Writers stream bytes to
the final destination through an online counter and append the measured length as a footer. Seekable
and non-seekable destinations use the same wire; no measurement encode, encoded-frame backing,
topology DTO, or mmap copy exists. `WriteWorld` and `WorldCheckpointCodec.Write` use a short
topology-exclusive admission to validate one source root, retain it for the synchronous encoder, and
publish a semantically identical copy-on-write successor. Caller-controlled output then reads the
retained root while ordinary mutations target the successor and detach only touched backing.
`World.Dispose` still waits for the explicit serialization lifetime. Same-thread World re-entry from
a codec or destination fails immediately, and every exception path releases the read root, admission,
and lifetime without advancing the topology revision or structural epoch.

Canonical `BinaryPrimitiveEncoding`, `BinaryTypeId` / `BinaryFieldKey`, inline `Digest256`, bounded
counting streams, and online hashing streams belong to `SomeEngine.Serialization` and are shared
with the asset document system. Asset `BinaryReadLimits` and ECS `SerializationReadLimits` remain
domain-specific because they meter different objects; ECS has no `BinaryLimits` projection. ECS
keeps only its World/Entity/Relation contracts, registry identity, root admission, journal
acknowledgement, and durable-slot policy. Sharing the mechanism does not make an asset document an
ECS save and does not create a second runtime object model.

World reads allocate one new World and fill its final slot, component, buffer, hierarchy, and
relation structures directly. The caller replaces its World reference only after success. On any
header, schema, codec, footer, remap, topology, cancellation, or stream failure the new World is
disposed. APIs that require an old World and a complete candidate World to coexist—`LoadInto`,
capture DTOs, checkpoint capture, and apply modes—are not part of the contract.

## Consequences

There is no serialization work with literally zero cost: bytes still have to be read and written.
Registration and layout proof happen once, raw checkpoints retain the exact-build memcpy path, and
proven packed canonical values retain the same memcpy inner loop for durable saves. Long-lived saves
pay only for the one canonical encode and the I/O they actually perform.

Version 4 is current-schema-only. A schema or wire change is a deliberate breaking data change;
conversion of already released files, if a product chooses to provide it, belongs in an explicit
offline tool outside this runtime. Shipping projects must keep golden fixtures for the one schema
they currently claim to accept and must never infer that an older file is compatible.
