# Product Boundary

`SomeEngine.slnx` lists the product, development tools, and ordinary test projects. Project references and package declarations describe their actual dependencies. Build with `dotnet build SomeEngine.slnx`; run relevant tests with `dotnet test` or `just test`.

- `src/` contains engine and application code.
- `tests/` exercises behavior, error handling, synchronization, resource lifetime, serialization, and GPU output.
- `benchmarks/` and diagnostic tools measure performance under explicit workloads. Their measurements do not define a second product boundary.
- `assets/` contains product shaders, schemas, and content.
- `external/` contains declared third-party source dependencies; package references and lock files record binary dependencies.
- `wiki/` explains architecture and records historical measurements. Historical reports describe the revision and environment they measured.

Normal changes update existing components and their callers, remove replaced paths, and run proportionate builds and behavioral checks. Tests should allow equivalent implementations. Source spelling, private member inventories, arbitrary file/complexity limits, interview records, review manifests, and agent execution protocols are not product behavior and do not gate iteration.

The former harness solution, its analyzers and baselines, and its repository workflow skills have been removed. There is no numeric run directory or mandatory evidence report. Preserve useful test inputs and real failure reproducers in the ordinary test projects.

See [[Render-Boundaries]] and [[Render-Graph]] for rendering responsibilities.

The asset architecture graph starts at [[Asset-Files]]. [[Asset-Responsibilities]] defines data and file responsibilities, [[Asset-Import-and-Storage]] connects development tools to storage, and [[Asset-Validation]] separates behavioral requirements from dated verification records.
