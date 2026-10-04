# SomeEngine.Elements.Render

`CanvasRenderExtractor` transfers layout nodes with `Surface`, `Image` and `LayoutRect`
through `RenderExtractionSystems`. Install it with the other extractors before
the first extraction:

```csharp
using var extraction = new RenderExtractionSystems(
    renderWorld, new CanvasRenderExtractor());
extraction.Extract(gameWorld);
prepareSystems.ApplyExtraction(prepareScope);
Entity canvasMirror = extraction.RequireMirror(canvas);
renderWorld.ExecuteBufferRead<SurfaceInstance>(canvasMirror, instances =>
{
    foreach (SurfaceInstance instance in instances.AsSpan())
    {
        // Consume the rectangle, fill, corners, border, shadow, clip, order, and version.
    }
});
```

Each canvas mirror holds `RenderCanvas` and one ECS buffer of `SurfaceInstance`.
Images occupy an `ImageInstance` buffer on the same mirror. `SurfaceRenderExtractor`
keeps the original entry point for the shared canvas extractor.
`RenderCanvas` carries the target, pixel size, and pixels per layout unit. Instance
rectangles use canvas coordinates. The clip rectangle covers the canvas in layout
units: `(0, 0, Size.Width / Scale, Size.Height / Scale)`, intersected with each
ancestor scroll viewport's LayoutRect. Nested viewports intersect their bounds
in tree order. Viewport rectangle changes and scrolling declaration changes
refresh descendant clip rectangles through the same extraction stage.

The buffer follows the applied `ElementTree` children in depth-first preorder.
`DrawOrder` is the node's ordinal in that traversal, starting with the canvas at
zero and counting transparent nodes. Nested canvases have their own buffers and
traversals. `Node` identifies the source entity; render consumers read the drawing
values from this buffer.
The `Shadow` init property carries the surface's `DropShadow` color, offset,
and blur radius through the same extraction path. Creation, edits, and clearing
to the transparent default update that same instance for the material to use.
The existing nine-argument constructor and deconstruction remain available.
The built-in Surface material expands its shader-generated rectangle to include
the shifted shadow and blur radius, then composites the shadow behind the fill
and border with the same clip rectangle. Positive blur radii use a smoothstep
distance transition; zero radius retains the one-pixel edge coverage.

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

Image views refer to `AssetId<TextureAsset>` and use `ImageTextures` with
`LayoutSystem` to receive ready application textures. Drawing includes the image
material and the application's device resource manager:

```csharp
using ImageMaterial images = ImageMaterial.Create(device.Capabilities.CompileTarget);
using var canvases = new CanvasRenderer(backend, device, graph, renderWorld,
    material, outputFormat, images, renderDevice);
```

The canvas pass merges surfaces, glyphs and images by tree order. One node draws
its surface, glyphs and image in that order. Each material interprets its own bytes and supplies
the buffer range starting at each batch. Consecutive instances using the same
material and texture draw together. Image textures use RenderDevice's normal
upload and view methods; the unified missing texture covers acquisition time.
Texture bindings belong to each graph frame and retain the canvas pass identity.

`ImageTests` covers intrinsic size, fitted rectangles and UV through public APIs.
`ImageDrawingTests` uses the complete view, layout, extraction and canvas path to
read pixels on D3D12 WARP and Vulkan with graphics validation enabled.
Text uses a CPU atlas entity and the same extraction stage:

```csharp
Entity textAtlas = gameWorld.CreateEntity(new TextAtlas(new GlyphAtlas()));
using var extraction = new RenderExtractionSystems(renderWorld,
    new SurfaceRenderExtractor(), new GlyphRenderExtractor(textAtlas));
using GlyphsMaterial glyphs = GlyphsMaterial.Create(device.Capabilities.CompileTarget);
using var canvases = new CanvasRenderer(backend, device, graph, renderWorld,
    material, outputFormat, glyphs);
```

Register `TextShapingSystem` with the layout system before extraction. The canvas
mirror receives `GlyphInstance` and `CanvasGlyphAtlas`; the atlas mirror receives
`RenderGlyphAtlas`. Glyph rectangles combine the text node's rectangle, padding,
shaped baseline and scaled atlas offset. Whitespace keeps its advance in shaping.

Atlas pixels are detached during extraction. `RenderGlyphAtlas` holds immutable
page data, the latest changed rectangles and a version. GPU textures retain one
single-channel page each: initialize the whole page, then copy changed rectangles
on successive versions. Whole page data also allows a renderer to catch up after
several extractions. Keep caller-supplied page memories read-only while consumed.

Surface and glyph draws share the persistent canvas pass and follow the tree's
order. Glyphs sample an SDF and calculate coverage in target pixels, then use the
same premultiplied alpha blend as Surface. A color edit keeps the shaped glyphs,
layout rectangles, atlas pixels and atlas version; only drawing colors advance.
`ElementShaders` holds all three built-in shader resources by name and target for the application's
lifetime, while pipelines, instance buffers and GPU pages retire with graph work.

`GlyphExtractionTests` drives public World frames and extraction; `GlyphDrawingTests`
compares D3D12 WARP and Vulkan pixel readback against an independent glyph outline
image. The screen panel example displays both English and Chinese text.

Provide both optional materials to compose surfaces, text and images in one canvas pass:

```csharp
using var canvases = new CanvasRenderer(backend, device, graph, renderWorld,
    material, outputFormat, glyphs, images, renderDevice);
```

`CanvasCompositionTests` reads known colors after interleaved surface, glyph and
image draws, concurrent atlas/texture updates, canceled frames and GC. The same
cases run on screen and world canvases on D3D12 WARP and Vulkan, including depth.
For a world canvas, provide `CanvasTarget.WorldSpace(targetEntity)` and a target
`WorldTransform` in the Game world. `Canvas.DepthTest` chooses scene depth testing.
Extraction supplies `RenderCanvas.Transform` and `DepthTest` alongside the same
Surface instance buffer. The target transform places the canvas origin and controls
world size; layout X follows local +X and layout Y follows local -Y. `Scale` converts
layout units to target pixels before the world transform is applied.

Pass the scene resources and its RenderWorld `RenderView` entity when reconciling:

```csharp
canvases.ReconcileGraph(screenTarget, new WorldCanvasTarget(sceneColor, sceneDepth, viewEntity));
```

World canvases use the view's viewport and projection. The default depth format is
D32Float with LessOrEqual to match the current scene pipeline. Configure `DepthFormat` and
`DepthComparison` on `WorldCanvasTarget` to match the scene. Frame updates carry
target and view matrices through the existing graph constants. Screen canvases
sharing the color target compose after world canvases.

DefaultGame's `World nameplates` option adds numbered Surface plates to selected
moving spheres. Runtime's argument entry point enables it with `--world-nameplates`.
`WorldCanvasDrawingTests.cs` checks the projected and depth-tested pixels on D3D12
WARP and Vulkan.
