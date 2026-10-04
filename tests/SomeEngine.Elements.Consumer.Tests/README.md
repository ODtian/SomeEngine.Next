# External element examples

This consumer assembly references the public Views, Layout, and Elements packages
through their repository projects. Animation, Typography, Compose, and SwiftUI
are also referenced to check assembly access across the available SomeUI layers.
It uses public ECS, view, and layout APIs to
build views, advance frames, and observe node data.

Add each built-in control's user equivalent under `Rewrites/`. Run both versions
through the same behavior cases with independently specified expected data. The
first example rewrites `Background`, `Corner`, and `Border` with
`Scope.Write<Surface>`.

`SurfaceTests` observes node identity, Surface values, layout rectangles, write
versions, and development errors. `SurfaceChecks` is compiled by these tests and
the Views NativeAOT smoke executable to share the Column behavior checks.
`AssemblyBoundaryTests` checks the public assembly access rule owned by Wiki
`architecture/someui/Assemblies`.

`TextTests` exercises text declarations, Column placement, Row reflow, theme
changes, explicit style fields, line heights, text/shape/layout write versions,
long numeric formats, zero-allocation numeric writes, and development errors.
The linked font fixtures and their reference values come from the Typography
tests. `TextChecks` shares public string, integer, and culture-specific generic
formatting checks with the Views NativeAOT executable, including 10,000 equal
integer writes measured with `GC.GetAllocatedBytesForCurrentThread`.
