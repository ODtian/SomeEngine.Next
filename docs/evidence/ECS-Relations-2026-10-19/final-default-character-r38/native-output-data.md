# Default and character output data, source 51c8ce

Source: `51c8ce75949cf16d7190e14028c655f526d5697b`.

Public CPU reader `b39adbd26d0f4b9286e617902b4ace71` ran at 2026-10-19T22:54:43.9966769Z–22:54:49.6506943Z and returned Exit 0. Each of the four actual symbols matched once before the single LLVM disassembly. The benchmark object remained at 2026-10-19T21:35:48.1647902Z, 26,164,795 bytes. `native-reader-facts.json` records the compiled source check, object observations, exact symbols and command. The original result, stdout and stderr are retained as `native-reader-result.json`, `native-reader-stdout.log` and `native-reader-stderr.log`.

## Selected work scopes

| Work | Relation function | Handwritten function |
| --- | --- | --- |
| Default pair traversal | ComponentPairsWithin Required/Where, 0x1d3280–0x1d35db, 187 instructions / 859 bytes | PairExecution.Execute, 0x15e300–0x15e657, 186 instructions / 855 bytes |
| Default frame / entry clear | 440 / 352 bytes | 440 / 352 bytes |
| Character buckets and pairs | KeyPairsWithin core, 0x20a900–0x20adf9, 288 instructions / 1,273 bytes; key collection is the call at 0x20a99c | MemberExecution.Execute, 0x164c40–0x1653a2, 449 instructions / 1,890 bytes; key collection is included in this body |
| Character frame / entry clear | 312 / 112 bytes for the selected core | 424 / 224 bytes for the selected callback including collection |

Ranges have an exclusive end. Character whole-function counts describe their listed scopes. Pair behavior is compared below at corresponding bucket, membership and output boundaries.

## Actual pair work

| Corresponding work | Relation instructions | Handwritten instructions |
| --- | --- | --- |
| Default row iteration | 0x1d335d / 0x1d351c and alternate calls target 0xd820; chunk advance targets 0x154b60; column binding targets 0x1612c0 | 0x15e3dd / 0x15e599 and alternate calls target the same 0xd820; chunk advance and binding target the same 0x154b60 / 0x1612c0 |
| Default remaining-row value | Each outer row copies 96 bytes through 0x1d3451 → BulkMove 0x87eb0 | Each outer row copies 96 bytes through 0x15e4d1 → the same BulkMove |
| Default component predicate | 0x1d3478 / 0x1d347d test the two Kind fields with the dynamic short circuit | 0x15e4f8 / 0x15e4fd test the same fields with the same short circuit |
| Default identity order | 0x1d34b1–0x1d34bf compare Index then Generation; reverse output joins at 0x1d34ca | 0x15e531–0x15e53f compare Index then Generation; reverse output joins at 0x15e54a |
| Default accepted output base | **0x1d34ca `movq (%rbx), %r10`**, followed by Count at r10+0x10, capacity at r10+0x8 and span pointer at r10+0 | **0x15e54a directly reads Count at rbx+0x10**, then capacity at rbx+0x8 and span pointer at rbx+0 |
| Default accepted output block | 0x1d34ca–0x1d34f9: 12 instructions / 47 bytes | 0x15e54a–0x15e576: 11 instructions / 44 bytes |
| Character first membership | 0x20ac24–0x20ac5f load QueryData's World, State and last version, then call World.ContainsEntityQuery 0x6a60 | 0x16514a–0x165186 load the same three fields and call the same 0x6a60 |
| Character second membership | 0x20ad4b–0x20ad7d uses the same QueryData fields and 0x6a60 | 0x16526b–0x16529d uses the same fields and 0x6a60 |
| Character accepted output base | **0x20ad8c `movq (%rbx), %rax`**, then Count / capacity / span pointer | **0x1652aa `leaq 0x10(%rbx), %rax`**, then Count / capacity / span pointer |
| Character result data | 0x20ada4–0x20adb1 write the two Index/Generation identities | 0x1652cc–0x1652e1 write the same four identity fields |
| Character bucket acceptance | 0x20abcc compare, 0x20abcf `setne`, 0x20abd3 `movzbl`, 0x20abd7 `test`, then 0x20abe4 conditional jump | 0x1650fe compare, then 0x165112 conditional jump |

The source-level early pair assignment in KeyPairsWithin is represented by registers in these functions. The accepted output uses four scalar identity stores in each implementation. The actual differences above are the terminal output address load and the relation-side bucket boolean materialization. The timing contribution of each instruction remains a measurement question; the final series and its handwritten control remain the timing evidence.

## Proposed single product change

`IQueryRelationOutput.cs` currently makes QueryIdentityOutput contain a RelationOutputReference to the caller's TOutput. `RelationQuery.EnumerateQuery` constructs that reference terminal. Thus the private pair cores receive a reference to an output adapter whose field contains the address of the actual output. The highlighted loads follow that data shape.

The proposed implementation changes this terminal to hold the active TOutput value. `EnumerateQuery` constructs it from the caller's value and writes its Output back once in a finally block. This terminal contains output data; the existing downstream reference adapters continue to share it throughout the callback. QueryData and row borrows remain in their existing callback scopes.

This is one data-holding change across `IQueryRelationOutput.cs` and `RelationQuery.cs`. It gives the pair cores a direct output-value base and keeps the early-stop, mutable-state and exception writeback at the outer enumeration boundary. The handwritten pairers already keep one active output value in their execution data and copy it back at the outer finally boundary. The implementation and the next same-source Native read can therefore check one precise target: direct Count/capacity/span addressing at the relation terminal, followed by the existing paired measurements.

## Complete retained evidence

`native-four-functions.raw.asm` retains all four original LLVM bodies. `native-DefaultGeneric.asm`, `native-DefaultHand.asm`, `native-KeyGeneric.asm` and `native-KeyHand.asm` retain complete bodies with shortened display names. `native-symbols.json` preserves the full symbol identities. `native-function-statistics.json` records whole-function statistics. `source-files.txt` preserves the eight relevant source files at Source51c with line numbers.
