# Architecture Checks

Architecture checks turn durable Wiki constraints into executable dependency/API-shape/build rules where automation has clear value.

They must not become a second architecture source. If a rule changes, update the owning Wiki concept first, then update the check.

Checks that only diagnose programmer mistakes during development must not force equivalent shipping-path validation.

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
