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

`ScrollViewFunctions.ScrollView()` declares a sparse caller-content viewport with
wheel and drag scrolling. Open its `Content()` to compose the content and set its
outer size with the layout modifiers. Its descendants use the viewport bounds
for pointer hit testing and Surface clipping.

`VirtualListFunctions.VirtualList(rowHeight)` declares a sparse scrollable list.
Within its caller's build, use `Visible(list, count)` to enumerate item indices and
`Scope.Key(item.Id)` to preserve each row's entity. `VisibleRange` exposes Start,
End, and Count for its half-open range, with one buffer row at each end. Each row
supplies a layout root; the list places those roots using the shared row height
and reports the entire item count's content height. The first layout establishes
the viewport, and the next rebuild declares its visible rows. See Wiki
`architecture/someui/Lists` for frame order, padding, and the public examples.

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

`ToggleFunctions.Toggle(on)` composes pointer and focus activation with a switch
surface. Use `Toggled()` to consume a new activation and change the caller's
boolean. `SliderFunctions.Slider(value, minimum, maximum, step)` composes a
horizontal track with the public `ValueDraggable` behavior. Use `Edited(out value)`
to consume a changed number and save it in application data.

Call `ValueFunctions.ValueDraggable(node, range, direction, distance)` in a user
control's build to share range editing while choosing its own drag direction,
canvas distance, structure, and appearance. `ValueRange.Ratio` supplies the
display fraction. The external Knob example uses upward dragging and a round
surface. Step zero gives continuous dragging and 5% keyboard steps; positive
steps quantize edits relative to Minimum. Right and Up increase a focused value;
Left and Down decrease it. Gamepad directions retain focus navigation.
See Wiki Composition and Input-and-Behaviors for data writers and examples.

`Text(string)` declares a text view. `Font`, `FontSize`, `TextColor`, and
`LineHeight` write its caller-chosen `TextStyle` fields. The text view reads
omitted fields from the nearest `TextTheme` through the public environment API.
Provide a loaded `SomeEngine.Typography.Font` in that theme or the font modifier.
Sizes and line heights use pixels; zero line height uses the font metrics.
`ShapedText` exposes the final glyphs, lines, size, complete style, and version.
The version tracks glyph layout. A color edit updates the complete style and
component write version while keeping glyph layout and atlas pixels. Text draws
through the atlas, extraction and Glyphs material in `SomeEngine.Elements.Render`.

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

`Text(Loc.Key("menu.start"))` declares localized text. Put one `Locale` and
loaded `Translations` on the same entity before declaring the interface.
`world.Replace(language, new Locale { Name = "zh-CN" })` updates the texts
that read that language. Ordinary string text retains its content and shaping
versions. Default number and date declarations also read the language; an
explicit format provider supplies its own culture.

Build language-key JSON content with `Translations.Build(source, destination)`
in the content build, then load the binary directory with `Translations.Load`.
String entries use .NET composite formats. Plural entries contain `argument`,
`other`, and optional `one` fields. The content build emits English and Chinese
cardinal rule tables; English distinguishes integer one and Chinese uses other.
`Loc.Key("items", count)` supplies the positional arguments. Keep argument
memory and referenced values immutable while the view uses them.

Read the displayed string from `TextContent.Value`. `Text.Value` retains the
string declaration parameter, and `Text.LocalizedValue` holds a localized
input. `SetText` formats at the time of writing; its string overload selects
ordinary text, including an empty string. Full content examples and public API
behavior are in `wiki/architecture/someui/Text.md`.
