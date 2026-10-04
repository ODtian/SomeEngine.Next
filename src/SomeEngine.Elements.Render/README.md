# SomeEngine.Elements.Render

`SurfaceRenderExtractor` transfers layout nodes with `Surface` and `LayoutRect`
through `RenderExtractionSystems`. Install it with the other extractors before
the first extraction:

```csharp
using var extraction = new RenderExtractionSystems(
    renderWorld, new SurfaceRenderExtractor());
extraction.Extract(gameWorld);
prepareSystems.ApplyExtraction(prepareScope);
Entity canvasMirror = extraction.RequireMirror(canvas);
renderWorld.ExecuteBufferRead<SurfaceInstance>(canvasMirror, instances =>
{
    foreach (SurfaceInstance instance in instances.AsSpan())
    {
        // Consume the rectangle, matrix, opacity, fill, corners, border, clip, order, and version.
    }
});
```

Each canvas mirror holds `RenderCanvas` and one ECS buffer of `SurfaceInstance`.
`RenderCanvas` carries the target, pixel size, and pixels per layout unit. Instance
rectangles use canvas coordinates. The clip rectangle covers the canvas in layout
units: `(0, 0, Size.Width / Scale, Size.Height / Scale)`.
`Matrix` transforms the rectangle's canvas-coordinate points by the node-local
`ElementTransform` around its layout origin and then `PresentationOffset` in
canvas coordinates. `Alpha` combines their opacity multipliers. Both default to
identity for nodes with ordinary layout and surface data. The coordinate rule
lives in `wiki/architecture/someui/Render-Path.md`.

The buffer follows the applied `ElementTree` children in depth-first preorder.
`DrawOrder` is the node's ordinal in that traversal, starting with the canvas at
zero and counting transparent nodes. Nested canvases have their own buffers and
traversals. `Node` identifies the source entity; render consumers read the drawing
values from this buffer.

Component write versions select changed chunks and rows. Surface and rectangle
edits, caller transforms, and presentation offsets update their existing instances. Tree membership, child order, and canvas
changes reconcile the affected canvas. An instance starts at version one and
advances only when its drawing values change; compare versions with `!=` to allow
wraparound. Equal drawing values keep their instance and buffer write versions.
Node release or removal of either drawing component removes the instance.

All source reads, including ordered hierarchy traversal, finish in the shared
extraction read window. The collected values reach the persistent ECS buffers
through the RenderPrepare update boundary and its structural transaction.
The ECS buffers hold the retained drawing data; extraction scratch belongs to the
current collection. Static frames preserve the buffer values and write versions.

The public behavior checks live in `tests/SomeEngine.Elements.Render.Tests`.
The existing Elements consumer architecture check also loads this assembly and
its dependencies to verify public assembly access.
