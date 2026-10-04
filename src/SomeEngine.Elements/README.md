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

`FocusFunctions.Focusable(node)` declares the `Focusable` marker and `Clicks`.
Read `node.Focused()` in a caller scope to observe focus through a presence
dependency. Queue keys through `Keyboard.Key(world, key, pressed)`, directions
through `Gamepad.Navigate(world, direction)`, and activation through
`Gamepad.Confirm(world)`. Arrow keys navigate; Enter and Space activate. Each key
press is one action, including platform repeats; releases preserve focus and
click counts.

Register `FocusNavigationSystem` beside `HitTestSystem`, before game input and
view rebuilds. It uses the previous layout to choose the nearest rectangle
center in the requested direction within the focused canvas. The first
direction selects the top-left center; direction boundaries retain focus.
Transparent compositions use their layout content's bounding rectangle.
Confirmation increments the focused node's `Clicks`; `Clicked()` consumes those
clicks through the existing caller scope. The World has one sparse `Focused`
marker, and `InputCapture.Keyboard` indicates focus consumption.

`FocusInput` is a public event buffer on the World's `InputCapture` entity.
Disable the default navigation system and schedule a user system to consume
that buffer and implement custom navigation and activation. The default system
uses `World.EntityDestroying` to transfer focus to the nearest focusable
ElementTree ancestor during node release. A parent-chain endpoint clears focus.

`ButtonFunctions.Button(label)` declares a sparse `Button` view. Import
`ButtonFunctions` statically to write `if (Button("OK").Clicked())` in a build.
The button reads its own `Hover` and the nearest `ButtonTheme`, composes a Row
with Surface and padding, and gives its Text the theme's text color. Font,
size, and line height continue to come from `TextTheme`.

Provide a `ButtonTheme` on an ancestor to style its entire subtree. Its writable
properties are `Fill`, `HoverFill`, `PressedFill`, `TextColor`, `Padding`, and
`CornerRadius`. The default value supplies a complete style; omitted properties
use the button defaults and explicit transparent colors and zero spacing remain
caller data. See Wiki `architecture/someui/Composition` for values and examples.

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
