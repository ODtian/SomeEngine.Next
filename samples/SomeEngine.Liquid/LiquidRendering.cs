using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Runtime;

namespace SomeEngine.Liquid;

/// <summary>A bounded mesh reconstructed from the current liquid volumes and contact footprints.</summary>
public sealed class LiquidRendering : IApplicationColorFeature
{
    public const int AxialSegments = 16, RadialSegments = 24;
    public const uint MeshSamples = 4;
    public const int SectionVertices = (AxialSegments + 1) * (RadialSegments + 1);
    public const int CurveCount = LiquidFlow.Capacity;
    public const int MeshVertexCount = CurveCount * SectionVertices;
    public const int MeshIndexCount = CurveCount * AxialSegments * RadialSegments * 6;
    private readonly RenderWorld _world;
    private readonly LiquidExtraction _extraction;
    private readonly List<IDisposable> _resources = [];
    private readonly Dictionary<string, LiquidBuffer> _buffers = [];
    private readonly Dictionary<string, (GraphTextureId Id, Format Format)> _textures = [];
    private readonly LiquidGpuTimes _times;
    private readonly GraphTextureInput _color;
    private readonly GraphPassId _initialize, _shade;
    private readonly GraphBufferId _settings;
    private readonly Vector4[] _constants = new Vector4[16];
    private readonly int _width, _height;
    private bool _first = true;
    public string BackendName { get; }
    public string[] StageNames { get; } = ["Liquid volume mesh", "Background", "Raster surface", "Liquid shading"];

    public LiquidRendering(ApplicationRendering rendering, RenderWorld world, LiquidExtraction extraction)
    {
        (_world, _extraction, _width, _height) = (world, extraction, rendering.Width, rendering.Height);
        BackendName = rendering.Backend.GetType().Name;
        RenderGraphEdit edit = rendering.Graph.BeginEdit();
        try
        {
            _times = new(rendering, ref edit, StageNames.Length);
            _color = edit.CreateTextureInput();
            foreach (string name in new[] { "PositionVolume", "Contact", "SurfaceSize", "Footprint",
                "CurveStart", "CurveEnd", "CurveFirstTangent", "CurveLastTangent", "CurveRadius" })
                Upload(ref edit, name, LiquidFlow.Capacity * 16, 16);
            Upload(ref edit, "Obstacles", 2 * 48, 48);
            Buffer(ref edit, "MeshIndices", MeshIndexCount, 4, persistent: true);
            _settings = edit.DeclareExternalBuffer(new(256, BufferUsages.Constant), MemoryType.Upload);
            Buffer(ref edit, "MeshVertices", MeshVertexCount, 48);
            Buffer(ref edit, "MeshArguments", 4, 4, extra: BufferUsages.Indirect);
            Buffer(ref edit, "Background", _width * _height * 2, 16);
            _initialize = Compute(ref edit, rendering, "InitializeIndices", new((MeshIndexCount + 63) / 64, 1, 1), -1);
            GraphPassId mesh = Compute(ref edit, rendering, "BuildLiquidMesh", new((uint)(MeshVertexCount + 63) / 64, 1, 1), 0);
            GraphPassId background = Compute(ref edit, rendering, "ClearBackground", Pixels(), 1);
            GraphTextureId surface = Texture(ref edit, "MeshSurface", Format.R32G32B32A32Float, TextureUsages.ColorAttachment | TextureUsages.Sampled);
            GraphTextureId detail = Texture(ref edit, "MeshDetail", Format.R16G16B16A16Float, TextureUsages.ColorAttachment | TextureUsages.Sampled);
            GraphTextureId depth = Texture(ref edit, "MeshDepth", Format.D32Float, TextureUsages.DepthStencilAttachment, MeshSamples);
            GraphPassId raster = Raster(ref edit, rendering, surface, detail, depth, 2);
            _shade = Compute(ref edit, rendering, "ShadeMesh", Pixels(), 3);
            edit.OrderAfter(raster, _initialize);
            edit.OrderAfter(raster, mesh); edit.OrderAfter(raster, background);
            edit.OrderAfter(_shade, raster);
            CompletePass = _times.Resolve(ref edit, _shade);
            edit.Commit();
        }
        finally { edit.Dispose(); }
    }

    public GraphPassId CompletePass { get; }
    public void OrderAfter(ref RenderGraphEdit graph, GraphPassId colorReady) => graph.OrderAfter(_shade, colorReady);
    public double[] ReadTimes() => _times.Read();

    public GraphTextureId Record(ref RenderGraphFrame graph, GraphTextureId color, GraphTextureId depth)
    {
        LiquidFrame data = _world.Read<LiquidRenderData>(_extraction.RenderEntity).Data;
        Vector3 target = data.Target;
        Vector3 forward = Vector3.Normalize(target - data.Camera);
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        _constants[0] = new(_width, _height, data.Count, data.Seconds);
        _constants[1] = new(AxialSegments, RadialSegments, 0, 0);
        _constants[2] = new(0, 0, 0, data.Display);
        _constants[3] = new(0, LiquidFlow.TrayFloor, LiquidFlow.TrayHalfWidth * data.Extent, LiquidFlow.TrayHalfDepth * data.Extent);
        _constants[4] = new(data.Scene == "curved" ? 1 : 0, data.PointLimit, MeshVertexCount, MeshIndexCount);
        _constants[5] = new(data.Nozzle, 0);
        _constants[6] = new(data.Camera, MathF.Tan(.32f));
        _constants[7] = new(target, data.Nozzle.X);
        _constants[8] = new(right, 0);
        _constants[9] = new(Vector3.Cross(right, forward), 0);
        _constants[10] = new(forward, 0);
        _constants[11] = data.Surface.Profile;
        _constants[12] = data.Surface.Ridges;
        _constants[13] = data.Surface.Ripple;
        _constants[14] = new(data.Material.Extinction, data.Material.ScatteringStrength);
        _constants[15] = new(data.Material.Scattering, data.Material.Roughness);
        graph.SetBufferData(_buffers["PositionVolume"].Id, MemoryMarshal.AsBytes(data.PositionVolume.AsSpan()));
        graph.SetBufferData(_buffers["Contact"].Id, MemoryMarshal.AsBytes(data.Contact.AsSpan()));
        graph.SetBufferData(_buffers["SurfaceSize"].Id, MemoryMarshal.AsBytes(data.SurfaceSize.AsSpan()));
        graph.SetBufferData(_buffers["Footprint"].Id, MemoryMarshal.AsBytes(data.Footprint.AsSpan()));
        graph.SetBufferData(_buffers["CurveStart"].Id, MemoryMarshal.AsBytes(data.CurveStart.AsSpan()));
        graph.SetBufferData(_buffers["CurveEnd"].Id, MemoryMarshal.AsBytes(data.CurveEnd.AsSpan()));
        graph.SetBufferData(_buffers["CurveFirstTangent"].Id, MemoryMarshal.AsBytes(data.CurveFirstTangent.AsSpan()));
        graph.SetBufferData(_buffers["CurveLastTangent"].Id, MemoryMarshal.AsBytes(data.CurveLastTangent.AsSpan()));
        graph.SetBufferData(_buffers["CurveRadius"].Id, MemoryMarshal.AsBytes(data.CurveRadius.AsSpan()));
        graph.SetBufferData(_buffers["Obstacles"].Id, MemoryMarshal.AsBytes(data.Obstacles.AsSpan()));
        graph.SetBufferData(_settings, MemoryMarshal.AsBytes(_constants.AsSpan()));
        graph.SetTextureInput(_color, color);
        graph.SetPassEnabled(_initialize, _first);
        _first = false;
        return color;
    }

    private void Upload(ref RenderGraphEdit edit, string name, int bytes, uint stride) =>
        _buffers.Add(name, new(edit.DeclareExternalBuffer(new((ulong)bytes, BufferUsages.ShaderRead), MemoryType.Upload), stride));

    private void Buffer(ref RenderGraphEdit edit, string name, int count, uint stride, bool persistent = false, BufferUsages extra = 0)
    {
        var description = new BufferDesc((ulong)count * stride, BufferUsages.ShaderRead | BufferUsages.ShaderWrite | extra, "Liquid " + name);
        GraphBufferId id = persistent ? edit.CreatePersistentBuffer(description) : edit.CreateTransientBuffer(description);
        var buffer = new LiquidBuffer(id, stride);
        _buffers.Add(name + "Read", buffer); _buffers.Add(name + "Write", buffer);
    }

    private GraphTextureId Texture(ref RenderGraphEdit edit, string name, Format format, TextureUsages usages, uint samples = 1)
    {
        GraphTextureId id = edit.CreateTransientTexture(new(TextureDimension.Texture2D, (uint)_width, (uint)_height, 1, 1, 1, samples, format, usages, label: "Liquid " + name));
        _textures.Add(name, (id, format));
        return id;
    }

    private DispatchArguments Pixels() => new((uint)(_width + 7) / 8, (uint)(_height + 7) / 8, 1);

    private GraphPassId Compute(ref RenderGraphEdit edit, ApplicationRendering rendering, string entry, DispatchArguments groups, int stage)
    {
        var pipeline = LinkedComputePipeline.Create(rendering.Backend, rendering.Device, LiquidShader.Compile(entry), entry, entry);
        _resources.Add(pipeline);
        GraphPassId pass = edit.AddComputeFramePass(entry, PassQueueSelection.AnyOfType(QueueType.Graphics),
            new LiquidDispatch(groups, _times.Queries, stage < 0 ? uint.MaxValue : (uint)stage * 2),
            new PassOptions(RepeatCommands: true), pipeline.Pipeline, pipeline.ParameterLayout, default,
            static (ref PassDefinition definition, ref LiquidDispatch data) =>
            { if (data.First != uint.MaxValue) definition.Write(data.Pool, new(data.First, 2)); },
            static (ref ComputePassCommandScope commands, in LiquidDispatch data) =>
            {
                if (data.First != uint.MaxValue) commands.WriteTimestamp(data.Pool, data.First);
                commands.Dispatch(data.Groups);
                if (data.First != uint.MaxValue) commands.WriteTimestamp(data.Pool, data.First + 1);
            });
        Bind(ref edit, pass, pipeline.ParameterLayout, entry, PipelineSync.ComputeShading);
        return pass;
    }

    private GraphPassId Raster(ref RenderGraphEdit edit, ApplicationRendering rendering, GraphTextureId target, GraphTextureId detail, GraphTextureId depth, int stage)
    {
        const string pixel = "LiquidMeshPixel";
        const Format format = Format.R32G32B32A32Float;
        var pipeline = LinkedRasterPipeline.Create(rendering.Backend, rendering.Device,
            LiquidShader.Compile("DrawLiquidMesh", pixel), "DrawLiquidMesh", pixel, [format, Format.R16G16B16A16Float], Format.D32Float,
            rasterizer: new(Cull: CullType.None), depthStencil: new(DepthTest: true, DepthWrite: true),
            blendAttachments: [], sampleCount: MeshSamples);
        _resources.Add(pipeline);
        var indirect = rendering.Backend.CreateIndirectCommandLayout(rendering.Device, new([new(IndirectArgumentType.Draw)], 16, label: "Liquid surface draw"));
        _resources.Add(indirect);
        GraphTextureId samples = Texture(ref edit, pixel + "Samples", format, TextureUsages.ColorAttachment, MeshSamples);
        GraphColorAttachmentViewId color = edit.CreateColorAttachmentView(samples, new(0, 1, 0, 1, TextureAspects.Color), format, TextureViewDimension.Texture2DMultisampled);
        GraphColorAttachmentViewId resolve = edit.CreateColorAttachmentView(target, new(0, 1, 0, 1, TextureAspects.Color), format, TextureViewDimension.Texture2D);
        GraphTextureId extraSamples = Texture(ref edit, "MeshDetailSamples", Format.R16G16B16A16Float, TextureUsages.ColorAttachment, MeshSamples);
        GraphColorAttachmentViewId extra = edit.CreateColorAttachmentView(extraSamples, new(0, 1, 0, 1, TextureAspects.Color), Format.R16G16B16A16Float, TextureViewDimension.Texture2DMultisampled);
        GraphColorAttachmentViewId extraResolve = edit.CreateColorAttachmentView(detail, new(0, 1, 0, 1, TextureAspects.Color), Format.R16G16B16A16Float, TextureViewDimension.Texture2D);
        GraphDepthStencilViewId depthView = edit.CreateDepthStencilView(depth, new(0, 1, 0, 1, TextureAspects.Depth), Format.D32Float, TextureViewDimension.Texture2DMultisampled);
        VariableLayoutReflection layout = pipeline.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        GraphPassId pass = edit.AddRasterFramePass(pixel, PassQueueSelection.AnyOfType(QueueType.Graphics),
            new LiquidDraw(color, resolve, extra, extraResolve, depthView, _buffers["MeshArgumentsRead"].Id, indirect, _times.Queries, (uint)stage * 2, _width, _height),
            new PassOptions(RepeatCommands: true), pipeline.Pipeline, layout, default,
            static (ref PassDefinition definition, ref LiquidDraw data) =>
            {
                definition.Write(data.Pool, new(data.First, 2));
                definition.Read(data.Arguments, BufferRange.Whole, PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument);
                definition.ColorAttachment(0, data.Color, LoadType.Clear, StoreType.Discard, WriteCoverage.Complete, Vector4.Zero, data.Resolve);
                definition.ColorAttachment(1, data.Extra, LoadType.Clear, StoreType.Discard, WriteCoverage.Complete, Vector4.Zero, data.ExtraResolve);
                definition.DepthStencilAttachment(data.Depth, LoadType.Clear, StoreType.Store, WriteCoverage.Complete, 1,
                    LoadType.Discard, StoreType.Discard, WriteCoverage.Partial, 0);
            },
            static (ref RasterPassCommandScope commands, in LiquidDraw data) =>
            {
                commands.WriteTimestamp(data.Pool, data.First);
                commands.SetViewports([new Viewport(0, 0, data.Width, data.Height)]);
                commands.SetScissors([new ScissorRect(0, 0, data.Width, data.Height)]);
                commands.ExecuteIndirect(data.Indirect, new(commands.GetBuffer(data.Arguments), BufferRange.Whole), 1);
                commands.WriteTimestamp(data.Pool, data.First + 1);
            });
        Bind(ref edit, pass, layout, pixel, PipelineSync.VertexShading | PipelineSync.PixelShading);
        return pass;
    }

    private void Bind(ref RenderGraphEdit edit, GraphPassId passId, VariableLayoutReflection layout, string entry, PipelineSync sync)
    {
        PassDefinition pass = edit.GetPass(passId);
        TypeLayoutReflection root = layout.TypeLayout;
        if (root.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock) root = root.ElementTypeLayout;
        for (nint binding = 0; binding < root.BindingRangeCount; binding++)
        {
            if (root.GetBindingRangeDescriptorRangeCount(binding) == 0) continue;
            string name = root.GetBindingRangeLeafVariable(binding).Name;
            if (name == "Settings") { pass.Bind(edit.CreateBufferCbv(_settings, BufferRange.Whole), sync); continue; }
            if (name == "SceneColor")
            {
                pass.Bind(edit.CreateTextureUav(_color, new(0, 1, 0, 1, TextureAspects.Color), Format.R16G16B16A16Float, TextureViewDimension.Texture2DArray),
                    sync, GraphAccessMode.Write, WriteCoverage.Complete);
                continue;
            }
            if (_textures.TryGetValue(name, out var texture))
            {
                pass.Bind(edit.CreateTextureSrv(texture.Id, new(0, 1, 0, 1, TextureAspects.Color), texture.Format, TextureViewDimension.Texture2D), sync);
                continue;
            }
            LiquidBuffer buffer = _buffers[name];
            if (name.EndsWith("Write", StringComparison.Ordinal))
            {
                bool update = name == "SprayWrite" && entry == "UpdateSpray" ||
                    name.StartsWith("Trail", StringComparison.Ordinal) && entry == "DepositContactTrails";
                pass.Bind(edit.CreateBufferUav(buffer.Id, BufferRange.Whole, structureStride: buffer.Stride), sync,
                    update ? GraphAccessMode.ReadWrite : GraphAccessMode.Write, update ? WriteCoverage.Partial : WriteCoverage.Complete);
            }
            else pass.Bind(edit.CreateBufferSrv(buffer.Id, BufferRange.Whole, structureStride: buffer.Stride), sync);
        }
    }

    public void Dispose()
    {
        _times.Dispose();
        for (int index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose();
    }
    private readonly record struct LiquidBuffer(GraphBufferId Id, uint Stride);
    private readonly record struct LiquidDispatch(DispatchArguments Groups, GraphQueryPoolId Pool, uint First);
    private readonly record struct LiquidDraw(GraphColorAttachmentViewId Color, GraphColorAttachmentViewId Resolve,
        GraphColorAttachmentViewId Extra, GraphColorAttachmentViewId ExtraResolve, GraphDepthStencilViewId Depth,
        GraphBufferId Arguments, IndirectCommandLayout Indirect, GraphQueryPoolId Pool, uint First, int Width, int Height);
}
