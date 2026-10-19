# Final package consumer IL and NativeAOT proof

Source: `51c8ce75949cf16d7190e14028c655f526d5697b`.

Public CPU reader: `cb3aed562ed146e69b3467d2735f174a`, session `01a15090-f5bd-7a12-860a-e214325db5bb`, Exit 0. The reader checks all seven producer queue receipts before reading the package consumer. The producer records all fourteen real build/package/restore/consumer stages. `codegen-producer-queues.json` and `codegen-producer-stages.json` retain the gates.

The consumer DLL remained at `2026-10-19T22:01:17.5611514Z`, 69,120 bytes. Its NativeAOT object remained at `2026-10-19T22:01:35.5801336Z`, 25,066,602 bytes. `package-codegen-facts.json` records both input observations, exact selected symbols, commands, and output files. `package-codegen-queue-result.json`, `package-codegen-queue-stdout.log`, and `package-codegen-queue-stderr.log` retain the full reader receipt.

`package-selected-methods.il` retains the selected complete IL blocks. `package-il-targets.txt` records their successful selection counts. The selection includes the actual consumer registration, eight-position predicate and eighth-entity output, closed generic Number point access, bitmap permission dispatch, spatial leaf predicate, parallel count/fill jobs, and QueryJobAccess metadata read path.

| Native role | Complete range | Instructions | Bytes | Stack bytes |
|---|---|---:|---:|---:|
| WeightedTotal8.Matches | 0x20c30–0x20c8f | 32 | 95 | 0 |
| ComponentRelation eight positions | 0xe0740–0xe14c9 | 907 | 3,465 | 616 |
| World.ReadEntityQueryRef<Number<int>> | 0x19c1a0–0x19c593 | 300 | 1,011 | 136 |
| SpatialTraversal parallel PairArrayOutput fill | 0x1d8560–0x1d9564 | 1,057 | 4,100 | 520 |

Each Native symbol matched once before LLVM disassembly. `package-four-functions.raw.asm` preserves the four complete LLVM functions and original names; `package-native-symbols.json` preserves their exact identities. The four `package-native-*.asm` files provide the same bodies with short display names. `package-native-statistics.json` records the byte/instruction/frame observations. These four roles have distinct work scopes; their function sizes describe their respective bodies.

The producer's JIT and NativeAOT executable receipts retain the executed public behavior checks. The IL and Native files establish the compiled dispatch and binding paths for that same consumer source.
