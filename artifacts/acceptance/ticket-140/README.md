# #140: ordered shadow-page reuse

Fixed Native source: UE5.8.3 `396c9f059903aed5fec78ecd3d437a40c6415368`,
`VirtualShadowMapPhysicalPageManagement.usf:29–104,250–268,385–390,460–474,693–798`.
Its REQUESTED list contains all atlas pages at frame completion. The next frame
preserves that order while moving touched pages into REQUESTED, packing remaining
LRU pages into AVAILABLE, appending EMPTY at its tail, and allocating from that
tail. Remaining AVAILABLE pages follow touched pages for the next frame.

Production uses one GPU `ShadowPageLists` buffer for the four lists and their
counts. Prepare copies the previous order into LRU before classifying requests;
compact preserves the order and appends empty pages; allocation protects current
requests; finish maintains every page for the next frame. Unrequested page-table
and completion/invalidation metadata persist until their later request or reuse.
A 256-thread uniform group performs compact/finish, retaining portable workgroup
sizes. The persistent graph expresses these phases and carries the same buffer
through the existing FinishFrame/Discard completion handling and state reset.
Dynamic pressure reads the AVAILABLE count from that buffer. Optional quality
capture derives free atlas pages from the existing page mapping data.

`RecentlyTouchedShadowsSurvivePressureAndRecycledShadowsRecover` is one public
shader-sampling behavior. A two-page pool receives A, B, then A again. Thirty-two
idle scene-frame increments plus an actual empty-request update place A/B in the
old saturated age bucket7. C then creates pressure. The independently expected
sampling observations are A valid/shadowed, B missing/lit, C valid/shadowed.
B's next request is compared with an independently initialized fresh state.

Red used retained old compiled output and old shader sources from55109557;
`red-source.json` records their SHA256 hashes and `red-fixture.txt` its body.
The actual outcome is **1 executed/1 failed/0 skipped**: A missing/lit while B
remains valid/shadowed. `red.trx` and `red.log` retain the precise first difference.
Green runs the same actions and expectations through the PageLists interfaces:
**1 executed/1 passed/0 failed/0 skipped**,49seconds (`green.trx`,`green.log`).
The fixture changes attach the new GPU list interface and initialization; its
public sampler expectation and fresh-state comparison remain the same.

Final source/fixture Release builds passed with0warnings/0errors in13.98/9.23s.
The default behavior rule check reports0violations. Existing partial/empty-pool
allocation inputs were migrated to AVAILABLE with the same completeness checks.
The root's unified final acceptance consumes the complete backend/scene workload
and rebuilds DefaultGame shader content. Integration base for delivery:612fdb72.
