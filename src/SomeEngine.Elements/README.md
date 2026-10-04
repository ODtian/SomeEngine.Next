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

`DraggableFunctions.Draggable(node)`, `ScrollableFunctions.Scrollable(node)`,
and `SelectableFunctions.Selectable(node)` declare their components through
`Scope.Ensure`. The pointer stage writes `Drag` and toggles `Selected` on each
click. A primary-button drag starts with its first movement, reports the frame's
`Delta`, and keeps the latest gesture's `Total` and its start `Count`.
Wheel and drag input produce `ScrollInput`; layout writes the entire
`ScrollOffset`, clamps it to the measured content range, and offsets children.
Transparent compositions provide a viewport when they declare scrolling.
Each function returns the original entity and preserves behavior data when
declared again. See the Wiki Input-and-Behaviors and Layout pages for units,
button handling, nested viewports, and the shared public user examples.

`Text(string)` declares a text view. `Font`, `FontSize`, `TextColor`, and
`LineHeight` write its caller-chosen `TextStyle` fields. The text view reads
omitted fields from the nearest `TextTheme` through the public environment API.
Provide a loaded `SomeEngine.Typography.Font` in that theme or the font modifier.
Sizes and line heights use pixels; zero line height uses the font metrics.
`ShapedText` exposes the final glyphs, lines, size, complete style, and version.

```csharp
var shaping = new TextShapingSystem();
systems.Add(new ViewRebuildSystem());
systems.Add(shaping);
systems.Add(new LayoutSystem(shaping));
```

Layout supplies the content width to that same shaping system before measuring
each text node. The shaping system retains its buffers, commits the final result
after canvas layout, and disposes the buffers when nodes are released or the
system is destroyed. Consume glyph and line ranges before their buffers are
reused, and dispose fonts after their shaping and atlas users finish.

`Text(42, "D4")` and `Text(12.5m, "F2", CultureInfo.InvariantCulture)` format
view parameters after matching the node. `node.SetText(world, value, format,
provider)` updates an existing text node. Matching formatted characters retain
the string and write version and allocate zero managed bytes in steady use.
Long formats reuse pooled character storage; strings are created for changed
characters. Public behavior checks run in the consumer tests and the Views
NativeAOT smoke executable.
