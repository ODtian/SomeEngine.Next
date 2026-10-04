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

`PointerTests` injects input through `Pointer`, advances the hit-test, game input,
view, and layout stages, and observes public node data. It covers click-dependent
display, draw order, transparent compositions, previous-frame layout, per-button
presses, canvas isolation, and development errors. `PointerChecks` runs the same
caller consumption example for the built-in and user-written `Pressable` and
`Clicked` functions and shares those cases with the NativeAOT executable.

`Rewrites/Button.cs` and `Rewrites/IconButton.cs` compose controls from public
behavior, environment, layout, text, and surface APIs. `ButtonTests` applies
the same appearance, pointer, subtree-theme, nearest-theme, and development
scope cases to the library Button, user Button, and user IconButton. The glyph
and label use the same text theme and color, with four layout units between them.
`ButtonChecks` shares click consumption, held presses, label changes, and
provided appearance updates with the Views NativeAOT executable.

`DialogTests` verifies caller part order, inserted views, title omission,
right-aligned actions, TitledDialog equivalence and scope disposal, default
surface and shadow data, nearest themes, explicit transparent and zero values,
and development scope checks. `Rewrites/Dialog.cs`, `DialogTitle.cs`, and
`DialogFooter.cs` use public view, theme, surface, text, and layout APIs.
`DialogChecks` applies the same specified theme and layout results to all eight
combinations of built-in and user parts. It also supplies the shared NativeAOT
checks for built-in, user, and mixed composition.

`PointerRoutingChecks` shares child-to-card click bubbling, `StopsPointer`
changes, subtree entry and exit, sibling hover changes, held presses, and caller
output updates across the built-in and user-written behaviors. `PointerTests`
also verifies stopping at the target or an ancestor and keeping nested canvas
clicks and subtree hover inside their own tree.

`FocusTests` injects keyboard and gamepad input and observes focus-dependent
display and click consumption. `FocusChecks` shares the Grid and transparent
composition cases, node-release behavior, and a scheduled user navigation
system with the NativeAOT executable. The Grid runs with both the built-in
focus functions and the public user equivalent under `Rewrites/`.
