using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Runtime;
using Buffer = SomeEngine.Graphics.Buffer;
using Shader = SomeEngine.Resources.Shader;

namespace SomeEngine.Transparency.Research;

public sealed class TileVisibilityColorFeature : IApplicationColorFeature
{
    private static readonly TextureSubresourceRange ColorRange = new(0, 1, 0, 1, TextureAspects.Color);
    private readonly List<IDisposable> _resources = [];
    private readonly GraphTextureInput _color, _depth;
    private readonly GraphPassId _firstPass;

    public TileVisibilityColorFeature(ApplicationRendering rendering, TriangleHierarchy geometry,
        TransparencyComparison raster, float colorError)
    {
        IGraphicsBackend backend = rendering.Backend;
        Device device = rendering.Device;
        var tiles = new TileVisibilityGeometry(geometry, rendering.Width, rendering.Height);
        var shaders = new ComparisonShaders();
        var uploads = new List<(Buffer Source, Buffer Destination)>();
        using RenderGraphEdit edit = rendering.Graph.BeginEdit();
        _color = edit.CreateTextureInput();
        _depth = edit.CreateTextureInput();
        var buffers = new Dictionary<string, GraphBufferSrvId>();
        buffers["TileTriangles"] = ReadBuffer(edit, tiles.Triangles.AsSpan(), 32);
        buffers["TileAssociations"] = ReadBuffer(edit, tiles.Associations.AsSpan(), 4);
        buffers["TileRanges"] = ReadBuffer(edit, tiles.Ranges.AsSpan(), 8);
        if (raster == TransparencyComparison.TileSoftware)
            buffers["Coverage"] = ReadBuffer(edit, geometry.Coverage.AsSpan(), 56);
        buffers["Colors"] = ReadBuffer(edit, geometry.Colors.AsSpan(), 16);
        var vertices = geometry.Triangles.Select((triangle, index) =>
            new ForwardTriangle(triangle.A, triangle.B, triangle.C, (uint)index, geometry.Coverage[index].ColorIndex)).ToArray();
        if (raster == TransparencyComparison.TileHardware)
            buffers["Vertices"] = ReadBuffer(edit, vertices.AsSpan(), 56);
        uint[] settings = new uint[64];
        settings[0] = (uint)rendering.Width; settings[1] = (uint)rendering.Height;
        settings[4] = (uint)tiles.TileColumns; settings[5] = (uint)tiles.TileRows;
        settings[6] = (uint)tiles.Associations.Length; settings[7] = BitConverter.SingleToUInt32Bits(colorError);
        Buffer uniforms = Keep(backend.CreateBuffer(device, new(256, BufferUsages.Constant), MemoryType.Upload));
        using (MappedBuffer mapping = backend.Map(uniforms, MapType.Write, BufferRange.Whole))
        {
            MemoryMarshal.AsBytes(settings.AsSpan()).CopyTo(mapping.Bytes);
            mapping.Flush(mapping.Range);
        }
        GraphBufferId uniformBuffer = edit.RegisterExternalBuffer(uniforms,
            [new(BufferRange.Whole, PipelineSync.AllShading, ResourceAccess.ConstantBuffer, ResourceContentState.Defined)]);
        GraphBufferCbvId settingsView = edit.CreateBufferCbv(uniformBuffer);
        GraphBufferUavId masksView = default;
        if (raster == TransparencyComparison.TileHardware)
        {
            GraphBufferId masks = edit.CreateBuffer(new((ulong)tiles.Associations.Length * 32, BufferUsages.ShaderWrite));
            masksView = edit.CreateBufferUav(masks, structureStride: 4);
        }
        GraphTextureSrvId opaqueDepth = edit.CreateTextureSrv(_depth, new(0, 1, 0, 1, TextureAspects.Depth),
            Format.D32Float, TextureViewDimension.Texture2DArray);
        GraphTextureUavId color = edit.CreateTextureUav(_color, ColorRange, Format.R16G16B16A16Float,
            TextureViewDimension.Texture2DArray);

        if (raster == TransparencyComparison.TileHardware)
        {
            LinkedComputePipeline clear = Compute("tile_visibility_coverage.slang", "ClearTileCoverage");
            _firstPass = edit.AddComputeFramePass("Clear transparency coverage", PassQueueSelection.AnyOfType(QueueType.Graphics),
                new DispatchArguments(((uint)tiles.Associations.Length * 8 + 255) / 256, 1, 1),
                new PassOptions(RepeatCommands: true), clear.Pipeline, clear.ParameterLayout, default,
                static (ref PassDefinition _, ref DispatchArguments _) => { },
                static (ref ComputePassCommandScope commands, in DispatchArguments dispatch) => commands.Dispatch(dispatch), region: rendering.Region);
            Bind(edit.GetPass(_firstPass), clear.ParameterLayout, PipelineSync.ComputeShading, GraphAccessMode.Write);
        }

        GraphPassId coveragePass = default;
        var dispatch = new DispatchArguments((uint)tiles.TileColumns, (uint)tiles.TileRows, 1);
        if (raster == TransparencyComparison.TileHardware)
        {
            Shader shader = shaders.Get("tile_visibility_draw.slang", ["DrawTransparency", "StoreTileCoverage"]);
            int target = shader.RequireSlangTarget(device.Capabilities.CompileTarget);
            ShaderReflection reflection = shader.GetSlangReflection(target);
            EntryPointReflection vertex = shader.RequireSlangEntry(target, "DrawTransparency", SlangStage.Vertex);
            EntryPointReflection pixel = shader.RequireSlangEntry(target, "StoreTileCoverage", SlangStage.Fragment);
            Pipeline pipeline = Keep(backend.CreateGraphicsPipeline(device, new(shader.SlangCode, reflection,
                vertex, shader.GetSlangEntryBytes(target, vertex), pixel, shader.GetSlangEntryBytes(target, pixel),
                [], [], PrimitiveTopology.TriangleList, StripCut.Disabled, new RasterizerState(Cull: CullType.None),
                new MultisampleState(SampleCount: 1),
                new DepthStencilState(DepthTest: true, DepthComparison: CompareOperation.GreaterOrEqual), new BlendState([]),
                new AttachmentFormatSignature([], Format.D32Float), DynamicStates.Viewport | DynamicStates.Scissor,
                vertexCodeBlob: shader.GetSlangEntryBlob(target, vertex), pixelCodeBlob: shader.GetSlangEntryBlob(target, pixel))));
            VariableLayoutReflection layout = reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
            coveragePass = edit.AddRasterFramePass("Draw transparency coverage", PassQueueSelection.AnyOfType(QueueType.Graphics),
                new RasterDimensions((uint)rendering.Width, (uint)rendering.Height, (uint)vertices.Length * 3),
                new PassOptions(RepeatCommands: true), pipeline, layout, default,
                static (ref PassDefinition pass, ref RasterDimensions dimensions) => pass.DefineRasterExtent(dimensions.Width, dimensions.Height),
                static (ref RasterPassCommandScope commands, in RasterDimensions dimensions) =>
                {
                    commands.SetViewports([new(0, 0, dimensions.Width, dimensions.Height)]);
                    commands.SetScissors([new(0, 0, (int)dimensions.Width, (int)dimensions.Height)]);
                    commands.Draw(new(dimensions.VertexCount, 1, 0, 0));
                }, region: rendering.Region);
            Bind(edit.GetPass(coveragePass), layout, PipelineSync.VertexShading | PipelineSync.PixelShading, GraphAccessMode.ReadWrite);
            GraphDepthStencilViewId depthView = edit.CreateDepthStencilView(_depth,
                new(0, 1, 0, 1, TextureAspects.Depth), Format.D32Float, TextureViewDimension.Texture2D, readOnlyDepth: true);
            edit.GetPass(coveragePass).DepthStencilAttachment(depthView, LoadType.Load, StoreType.Store,
                WriteCoverage.Partial, 0, LoadType.Discard, StoreType.Discard, WriteCoverage.Partial, 0);
            edit.OrderAfter(coveragePass, _firstPass);
        }

        LinkedComputePipeline resolve = Keep(LinkedComputePipeline.Create(backend, device,
            shaders.Get("tile_visibility_resolve.slang", ["ResolveTileVisibility"],
                raster == TransparencyComparison.TileSoftware ? "#define TILE_SOFTWARE_RASTER 1\n" : ""),
            "ResolveTileVisibility", "Resolve transparency visibility"));
        CompletePass = edit.AddComputeFramePass("Resolve transparency visibility", PassQueueSelection.AnyOfType(QueueType.Graphics),
            dispatch, new PassOptions(RepeatCommands: true), resolve.Pipeline, resolve.ParameterLayout, default,
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments dispatch) => commands.Dispatch(dispatch), region: rendering.Region);
        Bind(edit.GetPass(CompletePass), resolve.ParameterLayout, PipelineSync.ComputeShading, GraphAccessMode.Read);
        if (raster == TransparencyComparison.TileHardware) edit.OrderAfter(CompletePass, coveragePass);
        else _firstPass = CompletePass;
        edit.Commit();

        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        foreach (var upload in uploads)
        {
            backend.Barrier(context, new BufferBarrier(upload.Destination, PipelineSync.None, PipelineSync.Copy,
                ResourceAccess.NoAccess, ResourceAccess.CopyDestination));
            backend.CopyBuffer(context, new(upload.Source, 0, upload.Destination, 0, upload.Destination.Info.Size));
            backend.Barrier(context, new BufferBarrier(upload.Destination, PipelineSync.Copy, PipelineSync.AllShading,
                ResourceAccess.CopyDestination, ResourceAccess.ShaderResource));
        }
        using RecordedCommands recorded = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Graphics), new([], [], [recorded], [], []));
        if (backend.WaitCpu(completion, TimeSpan.FromSeconds(30)) != WaitStatus.Completed)
            throw new TimeoutException("Tile geometry upload timed out.");
        foreach (var upload in uploads) upload.Source.Dispose();

        LinkedComputePipeline Compute(string file, string entry)
            => Keep(LinkedComputePipeline.Create(backend, device, shaders.Get(file, [entry]), entry, entry));

        GraphBufferSrvId ReadBuffer<T>(RenderGraphEdit graph, Span<T> values, uint stride) where T : unmanaged
        {
            ulong size = (ulong)values.Length * (ulong)Marshal.SizeOf<T>();
            Buffer buffer = Keep(backend.CreateBuffer(device, new(size, BufferUsages.ShaderRead | BufferUsages.CopyDestination)));
            Buffer source = backend.CreateBuffer(device, new(size, BufferUsages.CopySource), MemoryType.Upload);
            using (MappedBuffer mapping = backend.Map(source, MapType.Write, BufferRange.Whole))
            {
                MemoryMarshal.AsBytes(values).CopyTo(mapping.Bytes);
                mapping.Flush(mapping.Range);
            }
            uploads.Add((source, buffer));
            GraphBufferId id = graph.RegisterExternalBuffer(buffer,
                [new(BufferRange.Whole, PipelineSync.AllShading, ResourceAccess.ShaderResource, ResourceContentState.Defined)]);
            return graph.CreateBufferSrv(id, structureStride: stride);
        }

        void Bind(PassDefinition pass, VariableLayoutReflection layout, PipelineSync sync, GraphAccessMode maskAccess)
        {
            TypeLayoutReflection root = layout.TypeLayout;
            if (root.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock) root = root.ElementTypeLayout;
            for (nint index = 0; index < root.BindingRangeCount; index++)
            {
                if (root.GetBindingRangeDescriptorRangeCount(index) == 0) continue;
                string name = root.GetBindingRangeLeafVariable(index).Name;
                switch (name)
                {
                    case "Settings": _ = pass.Bind(settingsView, sync); break;
                    case "CoverageMasks":
                        _ = pass.Bind(masksView, sync, maskAccess,
                            maskAccess == GraphAccessMode.Write ? WriteCoverage.Complete : WriteCoverage.Partial,
                            ResourceContentState.Defined);
                        break;
                    case "OpaqueDepth": _ = pass.Bind(opaqueDepth, sync); break;
                    case "SceneColor": _ = pass.Bind(color, sync, GraphAccessMode.ReadWrite); break;
                    default: _ = pass.Bind(buffers[name], sync); break;
                }
            }
        }
    }

    public GraphPassId CompletePass { get; }
    public void OrderAfter(ref RenderGraphEdit graph, GraphPassId colorReady) => graph.OrderAfter(_firstPass, colorReady);
    public GraphTextureId Record(ref RenderGraphFrame graph, GraphTextureId color, GraphTextureId depth)
    {
        graph.SetTextureInput(_color, color);
        graph.SetTextureInput(_depth, depth);
        return color;
    }
    private T Keep<T>(T resource) where T : IDisposable { _resources.Add(resource); return resource; }
    public void Dispose() { for (int index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ForwardTriangle(Vector4 A, Vector4 B, Vector4 C, uint TriangleIndex, uint ColorIndex);
    private readonly record struct RasterDimensions(uint Width, uint Height, uint VertexCount);
}
