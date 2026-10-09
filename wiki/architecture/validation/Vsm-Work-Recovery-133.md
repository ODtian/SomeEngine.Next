# VSM work recovery and ticket #133

## Scope and protected sources

Implementation branch `agent/rg70-ticket-133` was created at integration `07d9462865f16049729a7a755d218247c9a373ed`, with HEAD equality and empty status checked before edits. Only its own worktree was changed. Historical root `F:/CodexProjects/2026-10-03/new-chat-2/work/rg70`, including dirty `trees/ticket-127` at `7507ba59`, remains read-only.

The later bounded-batch RT report labels its source `7507ba59 + retained batches, fixed source ranges and bounded instance work`. It does not contain a complete immutable source patch. This recovery is therefore a **documented reconstruction of the individual-view, correct-but-slow execution design**, not a claim of byte identity with that historical passed executable or new full-scene correctness acceptance.

## Source provenance

- `f97c4e5f` cherry-picks immutable `7507ba59ae4f3e7d556a471c52ac9b409aaf9629`: coarse texel-center sampling and independent source-triangle/full-receiver reference fixes. Main's hardware test routing and deleted legacy drivers are retained.
- Selected follow-up files were copied explicitly from historical `trees/ticket-127`, not from its entire dirty diff: VSM sampling/request/allocation/packed-page-HiZ/depth-completion sources; associated VSM schema/resources/feature/view data; shared unjittered geometry-view data; alternating traversal frontiers; target source offsets/ranges and separate target visible storage; target copied-pass support and its graph implementation; standard material geometry-normal handling. The recovery commit records the complete selected file contents and exact delta.
- The mask experiment was removed before content generation: no `TargetViewMask`, no mask-to-raster encoding, no automatic shared-view branch, no bypass of prior target HiZ, no mask-only hardware routing, and no hardware-window-end operation. Candidates remain 16-byte individual-view data. Material view dependence and tessellation are not replaced by a group projection.
- Mesh raster identifiers were moved to per-vertex flat outputs (including tessellation), preserving the pre-experiment mesh correction without importing the later window-expansion path.
- Reconstructed `Targets.cs` deliberately retains CPU source/view copied chains and deferred-only rediscovery **only as the before checkpoint**. Those are the architecture to remove in #133, not the delivered optimization.
- Source starts remain 64-bit during clipping; the recovered shader's `uint(FirstSourceCluster)` narrowing was corrected rather than preserved as a known defect.
- No historical binary assets or `.import.json` were copied. Default content was rebuilt from this checkout; import records now describe this generation. The immutable model remains SHA-256 `40523D69E4E2C6FC1316114008B3DE26100108A3A5ED2A5601FF1B9FF6C89A83`.

Recovery source SHA-256 (before new GPU-work implementation):

| Source | SHA-256 |
| --- | --- |
| `cluster_bvh_traverse_core.slang` | `FBD6AF839A2CFA264B50D622DC8F70358B3FDF25FEEC2F4D144AE3819A0ADF13` |
| `cluster_bvh_traverse_seed.slang` | `145C72D98E86F6AC557468A9BC357CA592637CE05F27BE5C0170BEA6D04D0A22` |
| `cluster_cull.slang` | `F90F1D1E6EAA1D4244AF5C1EEE20B18FDE47BD2678BB11C23044CB330EE597D3` |
| `cluster_view.slang` | `196AB6E9824D9774210D8CF79C7996925AD6E1DF3A97E5346C9BC5C66C5963CF` |
| `vsm_sampling.slang` | `42651F131125A1A3A559112E8120B9EABCE57E7B54CFEB6448EF206600E948F4` |
| `ClusterPipelineSystem.Targets.cs` | `CF6B88C4C0761182A6FDA76BDCBE07CCB945B00549444E560B2B584E0DDC969E` |

## Rebuilt before content

69 project assets; 496-byte view data, 16-byte candidate data; no window-end role. Generation hashes:

- content catalog: `509A9A640184DBB432658800F74BBFE2FF62C1BDA574F61EA3995A0F1A3F6259`
- Cluster settings packet: `D00D7428EE0C0DD8D8ADC9D6963CB83BD0C58C27B2F01EF3415066AE837D2E97`
- VSM settings packet: `489E1AFFE9D02874802B3B60540FC199B79DE5077BF581236E1CD956E1A70F68`

Binary assets are generated/ignored by the repository; use `Game.Build --project <owned DefaultProject> --engine <owned checkout>` at the recovery commit to reproduce them. Regenerated import records retain source and output hashes. Do not substitute a shader packet from the historical trial.

## Recovery checks, 2026-10-05

Commands executed in the owned checkout:

1. `dotnet build templates/DefaultProject/Game.App/Game.App.csproj -c Release -v minimal`: first attempt failed on the missing recovered material-range helper; the helper was added. Subsequent build passed with 0 warnings/errors.
2. `dotnet run --project templates/DefaultProject/Game.Build/Game.Build.csproj -c Release -- --project <owned templates/DefaultProject> --engine <owned checkout>`: first attempt compiled all shaders but failed because the imported enum still required the mask experiment's `HardwareRasterWindowEnds`; removed that role. Subsequent build completed all 69 assets.
3. `dotnet build templates/DefaultProject/Game.App/Game.App.csproj -c Release -v quiet`: passed, 0 warnings/errors.
4. `Game.App.exe --test-scene <owned templates/DefaultProject> --frames 4 --hidden-window --fixed-step 0.016666667 --no-vsync --no-debug-ui --vsm`: passed; default 1024-instance scene presented all four frames, geometry streaming reported no failures. An earlier command without `--test-scene` returned the application's usage response; corrected command above is the executed startup check.
5. `git diff --check`: passed.

No new three-scene correctness or performance verdict is made. The historical bounded-batch report remains the before reference (3/3 directional RT passes, severe sphere P50 about 43.55 ms); no historical evidence was edited.

## Reference access limits

Reuse retained UE5 5.8.3 citations at commit `396c9f059903aed5fec78ecd3d437a40c6415368` in historical `ue-nanite-work-review.md` and `ue5-vsm-differences.md`. No further private Epic API retrieval/reverification or recursive discovery under `rt-final-three-scenes` was attempted. Source findings do not establish millisecond savings. Per-view candidates are legitimate; level-by-level traversal is supported by the pinned reference.

## Acceptance seam and follow-ons

Tests use public `Application.RunAsync` / `SceneScript` / `SceneImage`, public graph/rendering results, and distinguishable failures. No legacy shared boolean-driver test is resurrected. Coarse directional caster-motion coverage is preserved through the scene seam. #134 owns valid mapped-page windows and capacity policy; #135 owns HiZ/cache handoff. Final three-scene performance and integrated #125/#127 acceptance remain open.
