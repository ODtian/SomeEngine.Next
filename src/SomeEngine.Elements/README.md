# SomeEngine.Elements

Elements use the public view and layout APIs. The caller writes a layout node's
`Surface` data through `Background(fill)`, `Corner(radius)`,
`Border(color, width)`, and `Shadow(shadow)` during a view build or computation. Each modifier returns
the same entity and preserves the other surface fields. Colors use
`SomeEngine.Core.Math.Color` in linear RGBA; radii and widths use layout units.
Equal values preserve the component's write versions when a build repeats.
`DropShadow` carries a linear RGBA color, a canvas offset, and a blur radius in
layout units. Its default value is transparent with zero offset and radius.

During a build, `node.Background(color).Animate(SurfaceChannels.Fill, Spec.Tween(0.15f))`
declares inline property animation on the interface clock. Later caller writes
become animation targets after the enclosing build succeeds. Repeating the same
target continues the existing progress; a changed target starts from the presented
color. `Channel<T>.Of<TComponent>` and `TrackSystem<T, TOps>` provide the same path
for user value types.

`Flip.Enable(node, spec)` declares presentation data for same-entity layout
transitions. Applications can also use `Flip.Enable(world, node, spec)` and add
`FlipSystem` after their animation and layout systems. `ElementTransform` holds
the caller's node-local matrix and opacity; start with `ElementTransform.Identity`.
FLIP writes `PresentationOffset`, which the extractor combines with layout and
the caller transform. The detailed field and frame rules live in
`wiki/architecture/someui/Animation.md`.

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

Built-in content and controls expose `AccessibleRole` and `AccessibleName` on
their view entities. Applications read them through the public World component
API. User components declare their own role and label with
`node.Accessible(Role.Button, "Open inventory")` in a build or computation;
the modifier returns the same entity. The default role table, name sources, and
writing rules live in
[`Accessibility`](../../wiki/architecture/someui/Accessibility.md).

`Image(AssetId<TextureAsset> source, ImageFit fit = ImageFit.Contain)` is a layout
view. Install `new ImageTextures(resolve)` in `LayoutSystem`; the resolver returns
the application's ready `SomeEngine.Resources.Texture` for that asset identity.
The application controls acquisition and texture lifetime. Image's natural size
uses texture width and height. During acquisition it uses the unified missing
texture's 1×1 size; a Box can supply a fixed drawing area. Ready resource changes
enter the layout data stage and continue through the shared canvas extraction.
`ImageFit` supports `Stretch`, `Contain` and `Cover`; fitting applies within Box
padding, while `LayoutRect` retains the allocated outer rectangle.
The material and fitting semantics live in the Wiki
[`Render-Path`](../../wiki/architecture/someui/Render-Path.md).

`FocusFunctions.Focusable(node)` declares the `Focusable` marker and `Clicks`.
Read `node.Focused()` in a caller scope to observe focus through a presence
dependency. Queue keys through `Keyboard.Key(world, key, pressed)`, directions
through `Gamepad.Navigate(world, direction)`, and activation through
`Gamepad.Confirm(world)`. On ordinary focusable nodes, arrow keys navigate;
Enter and Space activate. Each key
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

`TextEditingFunctions.TextEditing(node)` declares `EditableText`, `Focusable`,
and `Clicks` while preserving their values. Queue committed characters through
`Keyboard.Text(world, text)` and IME candidates through
`Keyboard.Composition(world, text, caret)`. The default focus system calls the
stateless `TextEditingSystem` at each event's position in the same input buffer.
The focused editor receives text and its arrow, Home, End, Backspace, Delete,
Enter, and Space keys; gamepad navigation continues to move focus.

Read `EditableText` in the caller's scope to draw text, selection, and caret.
`Caret` and `SelectionStart` are UTF-16 offsets at complete text element
boundaries. Shift extends the selection; typing replaces it, and deletion
removes the selection or one whole text element. Up and Down use the current
text element column across explicit LF or CRLF lines; Home and End use the
whole string. Empty character input preserves existing data.

`Composition` and `CompositionCaret` provide the candidate text and its UTF-16
caret separately from committed `Text`. Display candidates over the selection;
`Keyboard.Text` commits the result and clears the candidates. An empty
composition or a focus transfer ends candidate presentation. During composition,
the input method handles editor keys through subsequent candidate and text
events. Read `node.Edited(out text)` to consume new committed content changes,
using the same count comparison as `Clicked()`. Its output is the current
committed text on every read. Custom navigation systems can call
`TextEditingSystem.HandleInput` and `CancelComposition` through the public API.
See Wiki `architecture/someui/Input-and-Behaviors` for complete input rules.

Declare `SingleLineText` with `Scope.Ensure` on an editor to filter CR and LF
from committed and composition input. The composition caret follows the
filtered string. An input containing only line breaks retains the editor's
selection and text, following the existing empty-input rule.

`TextFieldFunctions.TextField(text, placeholder)` composes a single-line editor
from the public text editing, focus, Text, Surface, and layout APIs. Read
`field.Edited(out string text)` and pass the accepted text back to the field.
Equal accepted text retains the caret, selection, and IME candidates; a changed
parameter replaces the editor's text and moves both selection ends to its end.
The field displays a placeholder for empty text and draws the focused caret
and selection using the nearest TextTheme. Its natural-width text shares the
ScrollView viewport and clipping path. See Wiki Composition for the defaults
and the external rewrite.

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

`DialogFunctions.Dialog()` and `DialogActionsFunctions.DialogActions()` declare
sparse containers accepting caller content. Dialog places parts vertically and
writes its own themed Surface. Actions places its children horizontally at the
right edge. Their resolved Flex data uses the public `ILayout` measurement API.
`DialogTitleFunctions.DialogTitle(text)` composes a Text with dialog title size
and color, retaining the nearest text theme's font and line height.

Each part reads the nearest `DialogTheme`. Provide its `Fill`, `CornerRadius`,
`Shadow`, `Gap`, `TitleColor`, `TitleSize`, and `ActionsGap` properties to style a
subtree. Omitted properties use the complete defaults documented in Composition;
explicit transparent colors and zero corner radius or gaps remain caller data.
The caller supplies Box dimensions and padding through the usual modifiers.

`DialogFunctions.TitledDialog(title)` is an ordinary composition function
returning an open `ParentScope` after declaring the dialog and its title:

```csharp
using (var dialog = DialogFunctions.TitledDialog("Settings"))
{
    dialog.Entity.Size(360, float.NaN).Padding(24);
    TextFunctions.Text("Content");
    using (DialogActionsFunctions.DialogActions().Content())
        ButtonFunctions.Button("OK");
}
```

The caller controls part order, optional titles, and inserted views. User parts
can read the same theme and mix with built-in parts through the same public APIs.

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
