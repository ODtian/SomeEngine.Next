# Product Boundary

`SomeEngine.Next` 的 product boundary 是 `SomeEngine.slnx`、`harness/config.json`、coverage required assemblies 与自动 harness 一起声明的可验证代码边界。

本轮边界只包含可构建、可测试、可由 harness 自动验证的 Next 代码。旧仓库代码是 Legacy Reference；磁盘上出现旧实现不代表它属于 product boundary。

## 本轮已完成

- `SomeEngine.Core`
- `SomeEngine.Assets`
- `SomeEngine.ECS`
- `SomeEngine.ECS.Systems`
- `SomeEngine.ECS.Serialization`
- `SomeEngine.ECS.SourceGen` as build-support source generator code
- `SomeEngine.Job`
- `SomeEngine.Job.Dots`
- `SomeEngine.Render` as backend-free [[Render-Boundaries|Render Domain]]
- `SomeEngine.Render.Cluster` as backend-free [[Render-Boundaries|Cluster Renderer]] domain/model
- `SomeEngine.Generators` and harness projects as build-support code, not runtime product assemblies

## 本轮未完成

截至本次运行，没有单独列入 `本轮未完成` 的内容；发现属于本轮要求但未通过 harness 或 review 的项时，本轮不能结束。

## 不属于本轮

- legacy RHI, legacy D3D12/Direct3D/DXGI implementation
- RenderGraph
- Render execution
- Cluster execution
- Runtime as a first-round product boundary; Runtime source may remain as reference material
- Editor renderer integration
- ImGui/window/present integration, including windowing packages
- DiligentCore and Diligent-SharpGenTools
- samples, benchmarks, and third-party samples unless explicitly declared as product or external dependency gates

## External dependency policy

`本轮已完成` 的 external source dependencies are tracked as git submodules through `.gitmodules` and gitlink entries. The main repository must not vendor-copy submodule source trees into `external/`. DiligentCore and Diligent-SharpGenTools are not migrated into SomeEngine.Next; if present on disk they are ignored local legacy references, not submodules, product code, tests, or harness dependencies. Repo-owned binary drops are limited to explicitly declared binary assets such as the DXC runtime DLLs; generated local packages under `artifacts/` are build/restore artifacts, not `本轮已完成` product source.

Direct NuGet packages for `本轮已完成` product, build-support, and product-test projects are part of the first-round boundary catalog. A package belongs to `本轮已完成` only when `harness/config.json` pins the project, package ID, and version; project-local declarations, root build declarations, central package versions, global package references, package downloads, package version overrides, and package update items are all checked by harness.

Direct assembly references are also cataloged. `本轮已完成` direct references must be declared in `harness/config.json` with their project, assembly include, and normalized hint path. Both `Reference Include` entries and `Reference Update` hint-path overrides are checked so binary references cannot become an untracked first-round dependency path.

First-round source and product-test project declarations are platform-neutral for this boundary. Windows desktop SDKs declared through SDK attributes or SDK elements, Windows-targeted TFMs, WPF, WinForms, WinUI, WinExe output, explicit Windows targeting, and UI framework references are `不属于本轮` product/test declaration surface.

First-round MSBuild declarations are constrained to the repository-root declaration files and each project-local declaration surface. Intermediate automatic `Directory.Build.props`, `Directory.Build.targets`, or `Directory.Packages.props` files between the repository root and a first-round project directory are `不属于本轮` declaration model.

## Harness consequence

`本轮已完成` product projects must build and test. `不属于本轮` legacy projects must not appear in the first-round solution graph, product project catalog, coverage required assemblies, or active product tests. A rule that depends on an agent remembering this page is not harness; executable architecture checks enforce the boundary.

参见 [[Harness-Definition]]、[[Constraint-Direction]]、[[Render-Boundaries]]。
