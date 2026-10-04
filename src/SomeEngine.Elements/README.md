# SomeEngine.Elements

Elements use the public view and layout APIs. The caller writes a layout node's
`Surface` data through `Background(fill)`, `Corner(radius)`, and
`Border(color, width)` during a view build or computation. Each modifier returns
the same entity and preserves the other surface fields. Colors use
`SomeEngine.Core.Math.Color` in linear RGBA; radii and widths use layout units.
Equal values preserve the component's write versions when a build repeats.

`PressableFunctions.Pressable(node)` declares `Hover`, `Clicks`, and the
`Pressable` marker through `Scope.Ensure`. Read `node.Clicked()` in the caller's
scope to consume new clicks; it records a `Clicks` dependency and writes the
node's `ClickSeen` through the normal scope commit. A layout node uses its own
rectangle. A transparent composition uses its layout content's rectangles.

Queue input through `Pointer.Move`, `Press`, `Release`, and `Wheel` with a World
and Canvas entity. Positions use canvas units. Each button pairs its press and
release independently; releasing over the original target increments `Clicks`
on that target and its `Pressable` ancestors up to the Canvas. Declare
`StopsPointer` through `Scope.Ensure` to end bubbling after that node's click.
`Hover.Hovered` follows the hit target, `Pressed` follows the held target, and
`Within` follows the hit target's subtree ancestry inside the Canvas. Ancestors
can declare `Hover` through `Scope.Ensure` to observe `Within`.
Register `HitTestSystem` before game input, then `ViewRebuildSystem` and
`LayoutSystem`. Game systems query the World's single `InputCapture` component
to read the frame's pointer consumption. The input system uses the previous
layout and preserves a held UI press until release.

The external examples and shared behavior checks live in
`tests/SomeEngine.Elements.Consumer.Tests`.
