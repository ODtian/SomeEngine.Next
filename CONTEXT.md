# SomeEngine.Next

SomeEngine.Next is an engine rewrite context where legacy code is a reference source, not an automatic product fact. The language below keeps migration boundaries, render boundaries, and harness boundaries explicit.

## Language

**Product Boundary**:
Code, tests, tools, and dependencies declared as part of the repository build, test, and harness gate. Anything inside this boundary must be automatically verifiable.
_Avoid_: temporary migration boundary, assumed boundary

**Legacy Reference**:
Old repository code, tests, and notes used as evidence for behaviour or terminology without becoming product code by default.
_Avoid_: source of truth, product dependency

**Legacy RHI**:
The old rendering hardware abstraction implementation currently visible in migrated or historical sources. It is not an accepted SomeEngine.Next product boundary.
_Avoid_: current RHI, existing RHI

**Next RHI**:
The accepted rendering hardware abstraction boundary for SomeEngine.Next once it is deliberately established and covered by the harness.
_Avoid_: copied RHI, legacy RHI

**Render Domain**:
Backend-free render concepts such as scene-facing data, material semantics, render-world state, and renderer-independent asset meaning.
_Avoid_: renderer runtime, render backend

**Render Execution**:
A backend-bound renderer implementation that uses accepted RHI and render-graph boundaries to produce frames.
_Avoid_: render domain, migrated render

**Cluster Renderer**:
A render implementation family built around cluster-specific visibility, paging, material planning, and rendering execution.
_Avoid_: base render, engine subsystem
