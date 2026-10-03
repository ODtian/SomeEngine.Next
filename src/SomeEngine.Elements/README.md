# SomeEngine.Elements

Elements use the public view and layout APIs. The caller writes a layout node's
`Surface` data through `Background(fill)`, `Corner(radius)`, and
`Border(color, width)` during a view build or computation. Each modifier returns
the same entity and preserves the other surface fields. Colors use
`SomeEngine.Core.Math.Color` in linear RGBA; radii and widths use layout units.
Equal values preserve the component's write versions when a build repeats.

The external examples and shared behavior checks live in
`tests/SomeEngine.Elements.Consumer.Tests`.
