# Graphics / RenderGraph capability continuity

`graphics-rendergraph-capabilities.v1.json` is the accepted run `0004` continuity ledger. It is not a wish list and it is not a backend capability bitset. Each row independently records:

- the strongest level actually evidenced by the original checkpoint ZIP;
- the strongest level actually evidenced in the current `SomeEngine.Next` tree;
- the level and executable lanes required by the accepted run;
- exact source symbols, test IDs, gaps, and any accepted semantic replacement.

`graphics-rendergraph-public-api-inventory.v1.json` is the exhaustive declaration-level companion to the capability rows. It pins all 130 method declarations from the checkpoint `src/Graphics/IDevice.cs` and all 100 public type declarations found under `src/RenderGraph.Core/`. Every declaration has a unique source key, one capability ID, a non-empty disposition, and a note. Partial type declarations are intentionally separate entries because each ZIP source declaration is independently audited.

Levels are cumulative evidence labels, ordered as follows:

1. `absent` — no relevant API or implementation was found.
2. `metadata` — token, enum, descriptor field, or documentation only.
3. `public-contract` — backend-neutral callable API exists.
4. `compiler-lowering` — graph/compiler/artifact lowering exists, without an execution oracle.
5. `null-execution` — the Null oracle validates and executes the semantics.
6. `native-call` — a native API call path exists, but capability discovery, legal-state closure, or real output proof is incomplete.
7. `native-execution` — required native backend execution has a real observable-output test.
8. `renderer-consumer` — the production renderer consumes the capability.

`native-call` intentionally prevents Mesh, VRS, and DXR fragments from being reported as complete merely because a Vortice method appears in source. Work Graphs, sparse/tiled resources, and sampler feedback are likewise recorded at their actual checkpoint levels.

The original ZIP is deliberately not required in a clean checkout. Its SHA-256 and per-row evidence are checked whenever it is present; the checked-in ledger is the durable audit result. A future source artifact with different bytes must not silently replace that baseline.

When the ZIP is present, the gate re-extracts both complete public-API groups and compares their exact source-token sets with the checked-in inventory. This prevents a new ledger row from masking an omitted overload or type. The inventory disposition describes the accepted migration route; the capability row's current/required levels and executable lanes remain the implementation truth.

Mandatory rows remain red until their current level, required mappings, required test IDs, and all required lanes close. A mapping is evidence only when its file, source symbol, and test ID are present. Windows/WARP evidence may not contain an early-return or dynamic-skip path; platform selection belongs to the harness lane, not inside a passing test.

Design documents are not automatically implementation claims. Explicit claims indexed by a row are checked against the row's current level. Changing or deleting an indexed claim requires updating both the document and the ledger, and is subject to the run's review targets.

Structural capture/replay and executable replay are separate capabilities. Deterministic JSON/DOT, canonical topology validation, and corruption rejection are `compiler-lowering`; they do not become `null-execution` until replay actually reconstructs resources, records captured commands, submits them, and proves observable output equivalence.
