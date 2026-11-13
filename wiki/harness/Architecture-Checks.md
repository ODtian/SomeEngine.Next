# Architecture Checks

Architecture checks turn durable Wiki constraints into executable dependency/API-shape/build rules where automation has clear value.

They must not become a second architecture source. If a rule changes, update the owning Wiki concept first, then update the check.

Checks that only diagnose programmer mistakes during development must not force equivalent shipping-path validation.

## Rendering refactor rules

The approved rendering rules ARCH-01 through ARCH-22 are defined in [Render Refactor](../../reviews/Render-Refactor-Plan-2026-11-08.md). Compiled metadata checks live in `tests/SomeEngine.Render.Tests/Architecture/RenderArchitectureChecks.cs`; C# syntax checks live in `tools/ArchitectureRules` and run at the shared test entry point. Rule failures identify the rule, file and source location.

RenderGraph owns one persistent schedule and all GPU completion and resource boundary data. Frame resources hold upload/readback pages, transient placement, recorded command data and submissions. Both upload paths use `UploadPage` in `UploadPages.cs`. View cache keys consist of resource identities and view descriptions; the view row carries its label. Parallel buffer writes use explicitly created `BufferWriter` objects, merged at commit.

The same metadata checks cover the work-item shape requirements: frame recording uses schedule versions, recorded commands expose the three submission statuses, mesh registration batches share one allocation lock, and Cluster consumes material changes through `TakeChanges` and `Apply`. Views reach geometry through RenderWorld components.

## SomeUI public assembly access

[[architecture/someui/Assemblies|Assemblies]] owns the public API boundary for Elements,
its rendering layer, the frontends, and their consumers.
`AssemblyBoundaryTests.ElementLayerUsesPublicAssemblyAccess` reads the compiled
assembly attributes across the external consumer's engine dependencies. An
`InternalsVisibleTo` grant to one of those public layers fails the check and
reports the granting assembly and recipient.

```powershell
dotnet test tests/SomeEngine.Elements.Consumer.Tests/SomeEngine.Elements.Consumer.Tests.csproj --filter FullyQualifiedName~AssemblyBoundaryTests
```
