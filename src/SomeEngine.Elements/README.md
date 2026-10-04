# SomeEngine.Elements

Elements use the public view and layout APIs. The caller writes a layout node's
`Surface` data through `Background(fill)`, `Corner(radius)`, and
`Border(color, width)` during a view build or computation. Each modifier returns
the same entity and preserves the other surface fields. Colors use
`SomeEngine.Core.Math.Color` in linear RGBA; radii and widths use layout units.
Equal values preserve the component's write versions when a build repeats.

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

The external examples and shared behavior checks live in
`tests/SomeEngine.Elements.Consumer.Tests`.
