# SomeEngine.Elements

Elements use the public view and layout APIs. The caller writes a layout node's
`Surface` data through `Background(fill)`, `Corner(radius)`, and
`Border(color, width)` during a view build or computation. Each modifier returns
the same entity and preserves the other surface fields. Colors use
`SomeEngine.Core.Math.Color` in linear RGBA; radii and widths use layout units.
Equal values preserve the component's write versions when a build repeats.

The external examples and shared behavior checks live in
`tests/SomeEngine.Elements.Consumer.Tests`.

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
