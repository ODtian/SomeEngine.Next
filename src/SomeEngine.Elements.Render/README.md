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
        // Consume the rectangle, fill, corners, border, clip, order, and version.
    }
});
```

Each canvas mirror holds `RenderCanvas` and one ECS buffer of `SurfaceInstance`.
`RenderCanvas` carries the target, pixel size, and pixels per layout unit. Instance
rectangles use canvas coordinates. The clip rectangle covers the canvas in layout
units: `(0, 0, Size.Width / Scale, Size.Height / Scale)`.

The buffer follows the applied `ElementTree` children in depth-first preorder.
`DrawOrder` is the node's ordinal in that traversal, starting with the canvas at
zero and counting transparent nodes. Nested canvases have their own buffers and
traversals. `Node` identifies the source entity; render consumers read the drawing
values from this buffer.

Component write versions select changed chunks and rows. Surface and rectangle
edits update their existing instances. Tree membership, child order, and canvas
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

Screen canvas drawing uses `SurfaceMaterial` and `CanvasRenderer`:

```csharp
using SurfaceMaterial material = SurfaceMaterial.Create(device.Capabilities.CompileTarget);
using var canvases = new CanvasRenderer(backend, device, graph, renderWorld, material, outputFormat);

// After applying extraction, explicitly reconcile canvas membership and the target.
canvases.ReconcileGraph(screenTarget);

RenderGraphFrame frame = graph.BeginFrame();
try
{
    canvases.UpdateFrame(ref frame);
    frame.Execute(completions);
}
finally { frame.Dispose(); }
```

Provide a defined color target, such as the result of the scene's presentation pass.
`TryGetPass(canvasEntity, out pass)` exposes a drawing pass for application ordering.
Keep reconciliation between graph frames, and update drawing data once in each frame.
Dispose the drawing renderer while its graph and RenderWorld are available; the graph
retires its GPU resources after submitted work completes.

The built-in shader generates instanced quads and interprets its own instance bytes.
Static canvas frames reuse their instance buffers. Canvas size and scale are frame
values, while canvas membership and target changes use explicit graph edits.
Material pixel semantics and integration details are maintained in
[`Render-Path`](../../wiki/architecture/someui/Render-Path.md).

DefaultGame shows the panel through the test platform's `Surface panel` option.
Runtime applications using the argument entry point can enable it with `--surface-panel`.
Graph structure and D3D12/Vulkan pixel behavior are covered by
`tests/SomeEngine.RenderGraph.Tests/CanvasDrawingTests.cs`.
