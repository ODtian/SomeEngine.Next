# Combined review fixes checkpoint — #70/#125/#127/#132/#135

原始输出已移入仓库外验收归档；文中的 `artifacts/acceptance/` 路径对应归档内目录。归档身份与结果摘要见 [#70 验收记录](../../../docs/agents/acceptance-70.md)。

This is a measurable source checkpoint, **not complete acceptance or issue closure**. Standards and Spec remain separate axes. Parent owns the subsequent three-scene rebuild/reference/quality/performance gate. #79/#80/#134 were actually reopened; historical closure is not current acceptance.

## Standards

- ImGui transient uploads now use graph-managed pages whose actual native usages include Vertex/Index as well as Constant/ShaderRead/CopySource. Existing alignment rules and graph frame completion/cancellation remain authoritative. Ordinary public startup/platform UI images passed in Release and Debug (one D3D12 execution/pass and one Vulkan Presentation capability skip in each configuration). The script startup hides UI intentionally; two earlier test-harness failures are retained, not production reds.
- New byte-exact Cluster image assertions retain format-aware actual/expected/diff PNGs and raw bytes under SOMEENGINE_TEST_ARTIFACTS or artifacts/test-results. RT failure retention is separate from working content cleanup. Device-free Half/depth/dimension and shadow-retention behaviors passed. Explicit evidence requests can also retain successful captures.
- Reference content serialization now takes explicit camera, mesh placements/motion and authored lights. Tests supply actions/options; no shared suffix-decoded scene modes or reconstructed frame-63 pose. The legacy CPU image comparator remains an edge-excluded diagnostic, not acceptance.

## Spec

- Finite target tessellation resolves requested pages and qualified prior/current target HiZ; encoded policy IDs never become primary camera texture layers. Public finite linear-domain entries preserve original triangle geometry. An off-camera original-triangle comparison failed with the exact 6706 tessellation core; the corrected hardware routes passed. Mixed routes then exposed a separate real dicing error: heterogeneous patches shared one window's groupshared page cache. Dicing now uses the existing requested-page/receiver-aware writer without that incompatible cache. Same first/history/change whole-image assertions passed all five D3D12/Vulkan VS/MS/mixed rows. No culling, mesh/software route or quality gate was disabled.
- Dirty-page dispatch enumerates pages inside a band in X, tile X in Y, and page band plus tile Y in Z. Clear, HiZ initialization and reduction share this indexing and guard the actual page count. This keeps both page bands and large square tile grids inside portable dispatch dimensions. The public scene exceeds 65,535 requested/dirty pages; the other scene renders and clears accepted 4,096-pixel pages. The old large-page packing failed one image comparison; all four D3D12/Vulkan rows passed with the revised packing.
- Cluster geometry changes are written in property-specific runs; computed roots/expansions fill final graph-mapped typed property spans. Producer WriteChanged supports independent child properties. Public GPU readback and upload-range tests cover adjacent different-property edits, abandoned preparation, cancelled mapped writes, parallel query packets, and computed geometry properties. The old producer uploaded two 96-byte ranges instead of the two required 48-byte ranges; the restored property-specific producer passed all five tests.
- Raw RT visibility and color/coverage finiteness are checked before preview clamping; CSV retains raw ratios/numerators, full receivers and unchanged gates. Actual device-free nonfinite red: 1 failed; implementation green: 1 passed. Expanded output/compiler/comparison/retention suite: 11 passed.
- Corrected-score default spheres reproduced the prior numeric red exactly: 40.718478% mismatch/mean and 41.60603% false/lit. The explicit visibility material had omitted the public geometric receiver-plane argument and used mapped-normal bias unlike Standard. Matching Standard's geometric-plane call produced 1 pass: mismatch/mean 1.9470176%, false/lit 1.45302275%, missed/shadow 1.827909%, coverage 99.518406%, zero self-hit. All 135592 receivers/edges remain. This is a capture-call correction, not a native or production VSM repair.

## Completed quality capture

Selected linear captures optionally install existing graph readbacks and attach SceneFrame.Quality only after frame completion. Reported data includes current frame-used directional/local additions (not future adaptive bias), submitted view/mip resolutions, mapped/whole/receiver-only/unfinished depth populations and covered 8x8 receiver cells, requests/dirty work/allocation failures/free pages, typed geometry overflow, geometry work and streaming residency. Raster pressure version 2 means HW page area and SW batch weight64; equal numeric pressure versus older versions is not equivalent work. Per-mip populations are not per-pixel selected-mip histograms.

Public frame-input readback supports slices/Whole with completed delivery. The quality demand readback now runs after allocation consumes the published demand and before the request counter reset; the public graph readback ordering API expresses both relationships. The cancellation/frame-correspondence test reproduced the zero-demand failure, then passed with completed demand greater than zero. Both runs executed one D3D12 row and skipped the Vulkan Presentation row for adapter capability. Quality capture continues to use the graph's frame completion and optional readbacks. Matching effective quality remains an independent acceptance gate.

## Remaining gates and evidence

Public local Try sampling now covers missing and unfinished fine pages, valid fine/coarse depth, receiver-cell coverage, and incomplete filtering footprints: two WARP rows passed. Independent source-triangle reference passed both first-resumed point/spot rows after a cancelled update, and all six point-light-face rows. First output after an actually partially submitted callback failure matches a ready fresh scene for all four D3D12/Vulkan point/spot rows. These are separate result and validity observations, rather than an inference from page pressure.

Raster stages resolve material scalar bytes and offsets from the same graph inputs as shading. The authored vertex-scalar material reproduced the previous missing-resource failure, then its D3D12 and Vulkan hardware paths passed. Completed in-place patterned texture updates pass the D3D12 paths and Vulkan hardware path. The Vulkan software path with the original blob-decoded UV and SampleLevel produces device loss; all original test rows and failures are retained for the next linked bug ticket. Constant coordinates, dynamic pixel coordinates and position-derived varying coordinates each pass that same scalar-change row. Numeric UV clamping, scalar shuffles and helper inlining preserve the failure. Source probes have been restored; this remains a concrete open rendering obligation.

The 2026-10-06 user architecture decision retains directional distance selection, paired camera projection calibration and orthographic metadata. Request and sampling use that same distance rule. Local-light mip footprints and geometric receiver-plane derivatives retain their own roles. The previous screen-footprint direction policy exploration was restored before the distance build/cook; the three production files match the integration source.

Three historical native zero-result aborts retain Failed status. Recent repeated startup/shutdown evidence and the new shader device-loss records each retain their actual scope and result counts.

The remaining checkpoint's small TRX/build/cook records are archived in `artifacts/acceptance/review-fixes/remaining`. Full images, independent-reference CSV/raw captures and exploratory shader dataflow reports remain under `F:/SomeEngine.AgentWork/rg70-remaining/review-fixes-validation/{remaining,resume}`. The distance source build finished with zero warnings/errors; its compatible default content cook built 68 assets. The next frontier includes the Vulkan UV failure, the fixed-source Nanite/VSM audit findings, and final correctness/quality/performance acceptance.

Evidence: artifacts/acceptance/review-fixes contains CPU TRX/logs, including failed intermediate builds/cook. Current hardware evidence is retained at F:/SomeEngine.AgentWork/rg70-remaining/review-fixes-validation (sphere red/green, quality red/green, finite old-core red/mixed failures/five-row green, ordinary UI Release/Debug). Old-core sensitivity retained an empty core-versus-6706 diff plus full source delta. Full raw captures/CSV remain outside the repository; no failure artifacts were cleaned as working content.

The interrupted turn was resumed without resetting WIP. Measurable executable-source checkpoint **833cab9158db4ceee79fa8f1ca613a8616472cbd** was committed and verified after merging unchanged clean integration6706 (Already up to date, ancestor exit0). Final own cook68 assets; Cluster Release including Game.App0 warnings/errors; device-free suite11/11; full rule sweep24 projects/0 violations. Final quality-enabled sphere1/1 reproduces the same1.9470176% green with135592 receivers/edges unchanged; final finite five-row batch5/5 passes. Exact records live at review-fixes-validation/checkpoint; post-validation-files.csv is post-run hashes, not a fabricated pre-run freeze.

GPU and own builds/tests are RELEASED/stopped. #81 is preparing its exact833 candidate; it must rebuild/cook compatible three-scene content after shader changes, revalidate corrected references and comparable semantic quality, and only then measure. Observer-only BEFORE backport must not import production repairs. No three-scene throughput, FPS, equal-work/effective-quality acceptance or issue closure is claimed by this branch. No new production work during the parent performance window.

## Generated fixture cleanup — 2026-10-06

Removed the 69 generated catalog and compiled asset files from the retained
`ticket-135/gpu-window/local-lights/local-ray-spot-b2b6e1bf65414bc0885e258fc5cd7ebd/content`
fixture after recording each file's byte size and SHA-256 in
`F:/SomeEngine.AgentWork/rg70-remaining/removed-generated-content-hashes.csv`.
The adjacent rendering outputs, full receiver comparisons, command records and
test results remain the durable behavior evidence. The fixture is rebuilt through
the public scene setup when the regression executes.
