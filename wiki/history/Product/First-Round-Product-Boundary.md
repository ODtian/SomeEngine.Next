# Product Boundary

`SomeEngine.Next` 的 product boundary 是 `SomeEngine.slnx`、`harness/config.json`、coverage required assemblies 与自动 harness 一起声明的可验证代码边界。

本轮边界只包含可构建、可测试、可由 harness 自动验证的 Next 代码。旧仓库代码是 Legacy Reference；磁盘上出现旧实现不代表它属于 product boundary。

## 本轮已完成

- `SomeEngine.Core`
- `SomeEngine.Assets` as one-type assets: `Texture`, `Mesh`, `Shader`, `Material`, `MaterialInstance`, and `ClusterShaders` are simultaneously the authoritative `[BinaryContract]` roots, authoring values, and the unique values behind `AssetLoader` strong handles. Non-generic `[Asset(".suffix")]` source-generates closed `AssetType<T>` operations; `Load<T>` returns the canonical `AssetHandle<T>` before readiness, `WaitAsync` observes the shared operation, `Read` grants a scoped read, and `ReloadAsync` disposes the old backing before opening a new publication behind the same stable handle. ECS stores `AssetHandle<T>`, never `T` or a loader-local integer token. Closed generic resident slots remove runtime asset-interface/`Type` dispatch. Dependencies are strong lifetime pins released only after parent cleanup. There is no `*AssetData`, `IAssetData`, runtime wrapper, parallel IO facade, provider family, or compatibility alias. `BinaryDocument<T>` owns generic chunk IO; third-party `IAssetStorage` implementations supply immutable publication ranges. Exact TypeId/fingerprint/epoch checks finish before decode or output, and probing/fallback/migration remain absent.
- `SomeEngine.ECS`
- `SomeEngine.ECS.Systems`
- `SomeEngine.ECS.Serialization`
- `SomeEngine.ECS.SourceGen` as build-support source generator code
- `SomeEngine.Graphics` as the backend-neutral graphics contract
- `SomeEngine.Graphics.Null` as the executable validation oracle
- `SomeEngine.Graphics.Direct3D12` as the Windows native backend
- `SomeEngine.Job`
- `SomeEngine.Job.Dots`
- `SomeEngine.Render` as the backend-free render domain and [[Render-Boundaries|RenderWorld ECS]] extraction/component boundary; it consumes `SomeEngine.Assets.Schema.Mesh`, `Texture`, `Shader`, and `Material` and declares no second asset classes with those names
- `SomeEngine.Render.Cluster` as the backend-neutral [[Render-Boundaries|Cluster Renderer]] residency owner and shared Render-instance layout/prepare contributor; product preparation resolves ECS `AssetHandle<Mesh>` only through the owning `AssetLoader`, transfers the scoped read to the epoch, and holds no independent mesh mmap/source backing. `ClusterShaders` resolves all shader dependencies through the same generic loader route; Cluster pipeline construction likewise accepts shader handles rather than raw retained `Shader` objects
- `SomeEngine.RenderGraph` as the immediate render-graph compiler and execution layer
- `SomeEngine.Assets.Importers`, `SomeEngine.Generators`, `SomeEngine.AssetCook`, `SomeEngine.RenderGraph.Sample`, the `SomeEngine.Graphics.Benchmarks` executable test host, and harness projects as build-support/evidence code, not runtime product assemblies

## 本轮未完成

截至本次运行，没有单独列入 `本轮未完成` 的内容；发现属于本轮要求但未通过 harness 或 review 的项时，本轮不能结束。

## 不属于本轮

- the superseded `SomeEngine.Rhi*` legacy RHI projects and old-repository RHI/RenderGraph implementations; they remain migration evidence, not active product assemblies
- renderer execution beyond the accepted resource-owner/frame-coordination boundary, including shader/PSO assembly, descriptor binding, and RenderGraph command recording
- Cluster execution, including traversal/culling dispatch, material shading, debug visualization, and presentation; the accepted Cluster boundary stops at validated residency, RenderWorld preparation, same-epoch bindings, and completion-gated physical-resource ownership
- Runtime as a first-round product boundary; Runtime source may remain as reference material
- Editor renderer integration
- ImGui/editor window integration; backend-neutral swapchain/present contracts and the native D3D12 implementation belong to the accepted Graphics boundary
- DiligentCore and Diligent-SharpGenTools
- undeclared third-party samples; the declared RenderGraph sample and run-0004 benchmark/soak infrastructure are build-support/evidence surfaces, not runtime product assemblies

## External dependency policy

`本轮已完成` 的 external source dependencies are tracked as git submodules through `.gitmodules` and gitlink entries. The main repository must not vendor-copy submodule source trees into `external/`. DiligentCore and Diligent-SharpGenTools are not migrated into SomeEngine.Next; if present on disk they are ignored local legacy references, not submodules, product code, tests, or harness dependencies. Repo-owned binary drops are limited to explicitly declared binary assets such as the DXC runtime DLLs; generated local packages under `artifacts/` are build/restore artifacts, not `本轮已完成` product source.

Direct NuGet packages for `本轮已完成` product, build-support, and product-test projects are part of the first-round boundary catalog. A package belongs to `本轮已完成` only when `harness/config.json` pins the project, package ID, and version; project-local declarations, root build declarations, central package versions, global package references, package downloads, package version overrides, and package update items are all checked by harness.

Direct assembly references are also cataloged. `本轮已完成` direct references must be declared in `harness/config.json` with their project, assembly include, and normalized hint path. Both `Reference Include` entries and `Reference Update` hint-path overrides are checked so binary references cannot become an untracked first-round dependency path.

First-round source and product-test project declarations are platform-neutral for this boundary. Windows desktop SDKs declared through SDK attributes or SDK elements, Windows-targeted TFMs, WPF, WinForms, WinUI, WinExe output, explicit Windows targeting, and UI framework references are `不属于本轮` product/test declaration surface.

First-round MSBuild declarations are constrained to the repository-root declaration files and each project-local declaration surface. Intermediate automatic `Directory.Build.props`, `Directory.Build.targets`, or `Directory.Packages.props` files between the repository root and a first-round project directory are `不属于本轮` declaration model.

## Harness consequence

`本轮已完成` product projects must build and test. `不属于本轮` legacy projects must not appear in the first-round solution graph, product project catalog, coverage required assemblies, or active product tests. A rule that depends on an agent remembering this page is not harness; executable architecture checks enforce the boundary.

参见 [[Harness-Definition]]、[[Constraint-Direction]]、[[Render-Boundaries]]。
