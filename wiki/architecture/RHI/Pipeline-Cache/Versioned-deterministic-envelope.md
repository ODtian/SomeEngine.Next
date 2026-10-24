# RHI-CACHE-001 — Versioned deterministic envelope 

`PipelineCache` owns one in-memory RHI envelope. Its complete public input is
`PipelineCacheDesc.Data`; `TryGetPipelineCacheData` is its complete public output. The RHI does not
choose persistence paths, file locks, eviction policy or atomic replacement. Applications own those
filesystem concerns.

The envelope is versioned, deterministic and bounded by caller policy. Parsing, serialization and
merge validate magic, schema, environment data, counts, checked ranges, hashes and decoded-size
limits before committing. Cancellation is checked through variable-size work and leaves caller
output and destination cache unchanged until the final no-throw commit.
^rhi-cache-001

