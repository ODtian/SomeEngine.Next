# Harness Definition

Harness 是**强制自动门禁**。

如果一条规则依赖 AI agent 记忆、解读或自报合规，它不是 harness——可能是 guidance、review 或 design policy，但不构成 grill 检查。

Harness 结果由**可执行检查**产生，存为 grill artifacts。

参见 [[Constraint-Direction]]、[[Feedforward-Feedback]]。

For a preflighted run, Step2 harness scope is bounded by the accepted `intent.md` and `harness.md`. If a proposed hard check is not grounded in those files, it needs a new grill decision instead of being folded into the current Step2.

## Gate separation

Build gate proves the accepted solution graph compiles. Product test gate runs executable tests under `tests/`. Quality analyzer gate is explicit and opt-in through the quality harness, so compiler validity and maintainability findings are not hidden inside the same signal.

## Coverage and performance separation

Coverage gate runs only the declared product test project catalog from `harness/config.json`. Tests whose configured traits are excluded from coverage, such as `Category=Performance`, still run through the product-test harness; they are excluded from coverage collection because instrumentation can change allocation and timing behaviour.

Performance-tagged product tests run in the warning bucket for the first-round boundary. Functional product tests remain in the hard product-test gate.

The performance-tag check covers public and internal xUnit method shapes, including async methods returning fully-qualified `System.Threading.Tasks.Task` or `System.Threading.Tasks.ValueTask`, so timing/allocation-sensitive tests cannot stay in the hard bucket by changing method syntax.

Declared product tests are also part of the first-round boundary. A hard product test must not require Runtime, legacy RHI/RG, Diligent/SharpGen, D3D12/Direct3D/DXGI, ImGui/window/present integration, windowing packages, or execution-shaped Cluster concepts.

The product-test boundary is checked both as source/declaration text and as compiled assemblies. Root build declarations are included so a test-only excluded dependency cannot be introduced outside the test project file. Compiled product-test assemblies are checked for excluded references and declared type/member names. Compiled domain product-test assemblies also obey the domain-specific boundary, so base Render tests cannot acquire Cluster assembly references or Cluster-facing type/member names through the build output.

## Boundary declaration surface

First-round project declaration checks include each declared `.csproj`, project-local `.props`, and project-local `.targets` file. Root `Directory.Build.props`, `Directory.Build.targets`, and `Directory.Packages.props` are also checked for source hiding, excluded first-round boundary tokens, internals exposure, and output-assembly project references.

Product and product-test declaration files cannot use explicit MSBuild imports to pull in declaration files outside that scanned surface. If a declaration affects the first-round boundary, it must live in the scanned `.csproj`, project-local `.props` / `.targets`, or root build declaration files.

Direct NuGet package references for first-round product, build-support, and product-test projects are pinned in `harness/config.json`. The check reads project-local `.props` / `.targets`, root build declarations, central `PackageVersion` declarations, `GlobalPackageReference`, `PackageDownload`, `VersionOverride`, and package `Update` items, so backend/UI/window/RHI/RG/D3D12/Diligent/Cluster-execution packages cannot be added or version-swapped by moving package declarations away from the main project file.

Direct assembly references are pinned the same way. The check reads project-local `.props` / `.targets` and root build declarations, counts both `Reference Include` and `Reference Update` entries with hint-path metadata, normalizes repository-relative and NuGet-cache hint paths, and rejects undeclared binary references.

First-round source and product-test declarations also reject UI/window platform switches. Windows desktop SDKs declared through either SDK attributes or SDK elements, Windows-targeted TFMs, WPF, WinForms, WinUI, Windows-targeting, WinExe output, and UI framework references are checked across project-local and root declaration surfaces.

Automatic build declarations are intentionally limited to the repository-root files and the project-local declaration surface. Intermediate `Directory.Build.props`, `Directory.Build.targets`, or `Directory.Packages.props` files between the repository root and a first-round project directory are rejected because they would affect MSBuild without being part of the accepted scanned surface.

Declared first-round API and required product type contracts are checked before product API/type existence checks. A contract entry must belong to an accepted first-round source project and must not require excluded backend, UI, Runtime, RG/RHI, D3D12/Diligent, or Cluster-execution type/member names.

Project-local `.props` and `.targets` project references count for undeclared/repo-external project-reference checks and layer dependency checks. A first-round dependency cannot be made acceptable by moving it out of the main `.csproj`.

Layer dependency contracts are checked as configuration, not only as project files. An excluded backend, UI, Runtime, RG/RHI, D3D12/Diligent, or Cluster-execution dependency name cannot be made acceptable by adding it to the layer contract.

Domain-specific first-round boundaries use the same root build declaration files. Base Render cannot gain Cluster, backend, UI, or execution dependencies by moving declarations into root build files, and domain-specific product-test checks treat those root declarations as part of the test boundary.

Quality analyzer opt-out, suppression, and analyzer-input removal checks use the same project declaration surface, including root build declarations and project-local `.props`/`.targets`.

Declared external source/local-package consumer checks also use that declaration surface.

Accepted Assets schema wiring is read from the same declaration surface, so schema includes cannot disappear from the main project file and avoid the first-round schema contract check. The same surface rejects `FlatSharpSchema Remove` entries that would remove accepted schema items.

Product/domain boundary source checks include text contract files under accepted product roots, not only `.cs`: schema, asset, shader, material, and YAML/JSON-style contract files are part of the same first-round boundary scan. Extension matching is case-insensitive, so uppercase or mixed-case contract file extensions cannot avoid the scan.

Compiled first-round assemblies are checked for both forbidden assembly references and forbidden declared type/member names. This keeps generated or otherwise hidden build output from reintroducing excluded architecture or domain execution names after source and declaration text appear clean.

## Review target coverage

Run-level review targets protect the parts that cannot be reduced to a stable mechanical check. For this first-round boundary they include harness weakening, temporary exceptions, and accepted classification language in addition to Runtime, Render, Cluster, Assets, product-test, material-pass, quality-split, and naming-research objectives.

Review targets must stay specific enough to decide pass/fail for this run. A passing review must not relax the accepted boundary, hide excluded concepts through renamed code, or report `不属于本轮` items as `本轮未完成`.

## First-round quality split

The hard quality bucket contains the structural, boundary, and reflection rules accepted for the first-round boundary: `SE001`, `SE002`, `SE020`, `SE021`, `SE022`, `SE023`, `SE024`, and `SE030`.

Style-oriented quality findings stay in the warning bucket for this boundary: `SE010`, `SE031`, and `SE052`.

The warning quality run suppresses the hard boundary rules and elevates only those style rule IDs inside the warning bucket, so style findings are visible without changing first-round hard status.
