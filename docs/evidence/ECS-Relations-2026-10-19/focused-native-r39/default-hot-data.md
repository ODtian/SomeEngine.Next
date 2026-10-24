# Default hot data, source 393a9fbf

Source: `393a9fbf009aa2e8269636fdf532551271a85bc5`. This analysis uses the two complete Default bodies retained by Native reader `c0b4357d7bd14e01b9a3e96f942f8bf2`.

Public CPU saved-text analysis `6030587f62f841caaf3daaa6d585e694` returned Exit 0 at 2026-10-19T23:21:57.2701215Z–23:21:59.2687793Z. It records registers, stack offsets, immediates and actual external call addresses. Internal branch destinations are matched by instruction index; original addresses and branch widths remain in the retained data. Its predecessor `c72123534bac420f9deeab61e8ecc63e` ended with a PowerShell missing-brace parse error. Both commands, outputs and receipts are retained with `default-analysis` and `default-analysis-before-parser-fix` prefixes.

| Corresponding work | Relation Default | Handwritten Default |
| --- | --- | --- |
| Complete hot traversal | 0x1d171a–0x1d19a0, 137 instructions, 646 bytes | 0x15e33a–0x15e5bb, 137 instructions, 641 bytes |
| Hot start mod32 / mod64 | 26 / 26 | 26 / 58 |
| Previous chunk and first call argument | 0x1d171a loads rsp+0x140 into RSI; 0x1d1732 loads the same address into RDX | 0x15e33a loads rsp+0x140 into RDX; 0x15e342 copies RDX into RSI |
| Component bounds in the inner traversal | 0x1d1840 compare; 0x1d1848 conditional throw branch | 0x15e45b compare; 0x15e463 conditional throw branch |
| Kind short circuit | 0x1d185d first Kind; 0x1d1862 second Kind | 0x15e478 first Kind; 0x15e47d second Kind |
| Entity row bounds | 0x1d1881 compare; 0x1d1884 conditional throw branch | 0x15e49c compare; 0x15e49f conditional throw branch |
| Entity order | 0x1d1896–0x1d18a4 Index then Generation | 0x15e4b1–0x15e4bf Index then Generation |
| Output block | 0x1d18af–0x1d18db, direct RBX fields, 11 instructions / 44 bytes | 0x15e4ca–0x15e4f6, direct RBX fields, 11 instructions / 44 bytes |
| Inner row advance | 0x1d18fe to 0xd7e0 | 0x15e519 to the same 0xd7e0 |
| Inner chunk advance | 0x1d190f to 0x154ae0 | 0x15e52a to the same 0x154ae0 |
| Inner column bind | 0x1d1978 to 0x161240 | 0x15e593 to the same 0x161240 |
| Remaining-row copy per outer | 0x1d1836, 96 bytes through 0x87e30 | 0x15e451, 96 bytes through the same 0x87e30 |

The script records ten textual differences. Four are the first four instructions above: one relation-side stack reload replaces a handwritten register copy and the argument preparation is scheduled accordingly. Six are branches to each function's completion or local throw block. The corresponding branches all use the same encoded widths. After those roles are paired, the inner candidate operations, operands, branch structure and external calls correspond.

The five-byte hot size difference comes from the outer stack-load/register-copy shape. This is one additional memory read per outer iteration. It is a concrete outer difference; the final large-dynamic90 median ratio 1.015612619 and p95 ratio 1.014383809 remain timing observations of the complete measured path.

## Shared component-row work

At `ComponentRowEnumerator.cs:35` and `:39`, successful `_rows.MoveNext(out row)` is followed by a component Span index. `QueryRowCursor.MoveNext` has already selected a row below the same chunk's Count, and BindColumns obtains component arrays from that chunk. The component bounds branch above repeats that established range fact. The next minimal shared change uses the bound nonempty column's first reference plus the valid RowIndex, while preserving optional-component presence, access validation and member filtering. Both independent pairers continue to use the same public ReadRows operation.

`QueryRow.Entity` supplies the separate entity bounds check listed in the table. Its costs and the component-column check are recorded as distinct operations.

The finite Mutual path uses FixedCandidatePairs and two shared point reads. Its previously retained point/permission/output evidence belongs to that path; the Default ReadRows bounds work belongs to component-row enumeration.

Files: `default-hot-comparison.json` contains both complete hot regions with all original operands, original addresses, normalized branch correspondences and each branch's 32/64-byte placement. `default-hot-operation-differences.json` retains the ten textual differences. `default-branch-width-differences.json` records zero differing branch widths. `default-shared-row-source.txt` retains the fixed-source ComponentRowEnumerator and QueryRow. `default-analysis-facts.json` records the normalization scope and source.
