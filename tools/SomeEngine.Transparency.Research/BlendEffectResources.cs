using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Transparency.Research;

public sealed class BlendEffectResources : IDisposable
{
    private readonly List<IDisposable> _resources = [];
    public readonly Dictionary<string, BlendBuffer> Buffers = [];
    public readonly Dictionary<string, ResourceBinding> ReadBindings = [];
    public Buffer Uniforms { get; }
    public ResourceBinding SettingsBinding { get; }
    public ResourceBinding CellWriteBinding { get; }
    public LinkedComputePipeline? BuildCells { get; }
    public LinkedComputePipeline? Resolve { get; }
    public Pipeline? Draw { get; }
    public ShaderReflection? DrawReflection { get; }
    public VariableLayoutReflection DrawLayout { get; }
    public uint VertexCount { get; }
    public long GeometryBytes { get; private set; }
    public long IntermediateBytes { get; }

    public BlendEffectResources(IGraphicsBackend backend, Device device, BlendEffectData data,
        BlendMethod method, Matrix4x4 projection, float xrayDistance, ComparisonShaders shaders)
    {
        var uploads = new List<(Buffer Source, Buffer Destination)>();
        Add("Bounds", data.Bounds.AsSpan(), 32);
        Add("Pattern", data.Pattern.AsSpan(), 16);
        if (data.Effect == BlendEffect.Xray) Add("Triangles", data.Triangles.AsSpan(), 96);
        if (method != BlendMethod.Forward)
        {
            if (data.Effect == BlendEffect.Xray) Add("Coverage", data.Coverage.AsSpan(), 56);
            Add("References", data.References.AsSpan(), 4);
            Add("TileWords", data.TileWords.AsSpan(), 8);
            Add("Words", data.Words.AsSpan(), 8);
        }
        Uniforms = Keep(backend.CreateBuffer(device, new(256, BufferUsages.Constant), MemoryType.Upload));
        using (MappedBuffer mapping = backend.Map(Uniforms, MapType.Write, BufferRange.Whole))
        {
            MemoryMarshal.AsBytes(data.CreateSettings(projection, xrayDistance).AsSpan()).CopyTo(mapping.Bytes);
            mapping.Flush(mapping.Range);
        }
        SettingsBinding = ResourceBinding.ConstantBuffer(Keep(backend.CreateBufferCbv(device, new(Uniforms, BufferRange.Whole))));
        ReadBindings["Settings"] = SettingsBinding;
        if (method is BlendMethod.Cluster or BlendMethod.ClusterCached)
        {
            ulong bytes = (ulong)data.Bounds.Length * 4;
            Buffer cells = Keep(backend.CreateBuffer(device, new(bytes, BufferUsages.ShaderRead | BufferUsages.ShaderWrite)));
            Buffers["CellSlices"] = new(cells, 4);
            CellWriteBinding = ResourceBinding.WritableBuffer(Keep(backend.CreateBufferUav(device,
                new(cells, BufferRange.Whole, StructureStride: 4))));
            ReadBindings["CellSlices"] = ResourceBinding.ReadOnlyBuffer(Keep(backend.CreateBufferSrv(device,
                new(cells, BufferRange.Whole, StructureStride: 4))));
            IntermediateBytes = (long)bytes;
            BuildCells = Keep(LinkedComputePipeline.Create(backend, device,
                shaders.Get("blend_effect_cells.slang", ["BuildBlendCells"]), "BuildBlendCells", "Build blend depth cells"));
        }
        string definitions = data.Effect == BlendEffect.Xray ? "#define BLEND_XRAY 1\n" : "";
        if (method is BlendMethod.Cluster or BlendMethod.ClusterCached) definitions += "#define BLEND_CLUSTERED 1\n";
        if (method == BlendMethod.Forward)
        {
            var shader = shaders.Get("blend_effect_draw.slang", ["DrawBlendEffect", "BlendEffectPixel"], definitions);
            bool xray = data.Effect == BlendEffect.Xray;
            CompareOperation comparison = projection.M43 > 0
                ? xray ? CompareOperation.Less : CompareOperation.LessOrEqual
                : xray ? CompareOperation.Greater : CompareOperation.GreaterOrEqual;
            LinkedRasterPipeline draw = Keep(LinkedRasterPipeline.Create(backend, device, shader,
                "DrawBlendEffect", "BlendEffectPixel", [Format.R16G16B16A16Float], Format.D32Float,
                rasterizer: new(Cull: CullType.None),
                depthStencil: new(DepthTest: true, DepthComparison: comparison),
                blendAttachments: [new(Enabled: true, SourceColor: BlendFactor.One,
                    DestinationColor: BlendFactor.OneMinusSourceAlpha, SourceAlpha: BlendFactor.Zero,
                    DestinationAlpha: BlendFactor.One)]));
            Draw = draw.Pipeline;
            DrawReflection = draw.Reflection;
            DrawLayout = draw.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
            VertexCount = (uint)data.Bounds.Length * (data.Effect == BlendEffect.Xray ? 3u : 6u);
        }
        else
            Resolve = Keep(LinkedComputePipeline.Create(backend, device,
                shaders.Get("blend_effect_resolve.slang", ["ResolveBlendEffect"], definitions), "ResolveBlendEffect", "Resolve blend effects"));

        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        foreach (var copy in uploads)
        {
            backend.Barrier(context, new BufferBarrier(copy.Destination, PipelineSync.None, PipelineSync.Copy,
                ResourceAccess.NoAccess, ResourceAccess.CopyDestination));
            backend.CopyBuffer(context, new(copy.Source, 0, copy.Destination, 0, copy.Destination.Info.Size));
            backend.Barrier(context, new BufferBarrier(copy.Destination, PipelineSync.Copy, PipelineSync.AllShading,
                ResourceAccess.CopyDestination, ResourceAccess.ShaderResource));
        }
        if (method == BlendMethod.ClusterCached)
        {
            Buffer cells = Buffers["CellSlices"].Buffer;
            backend.Barrier(context, new BufferBarrier(cells, PipelineSync.None, PipelineSync.ComputeShading,
                ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
            var bindings = new Dictionary<string, ResourceBinding>(ReadBindings) { ["CellSlices"] = CellWriteBinding };
            backend.SetPipeline(context, BuildCells!.Pipeline);
            backend.SetTransientParameterBindings(context, new(BuildCells.ParameterLayout,
                GpuTransparencyRenderer.Bindings(BuildCells.Reflection, bindings), []));
            backend.Dispatch(context, new(((uint)data.Bounds.Length + 63) / 64, 1, 1));
            backend.Barrier(context, new BufferBarrier(cells, PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                ResourceAccess.UnorderedAccess, ResourceAccess.ShaderResource));
        }
        using RecordedCommands recorded = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Graphics), new([], [], [recorded], [], []));
        if (backend.WaitCpu(completion, TimeSpan.FromSeconds(30)) != WaitStatus.Completed)
            throw new TimeoutException("Blend geometry upload timed out.");
        foreach (var copy in uploads) copy.Source.Dispose();

        void Add<T>(string name, Span<T> values, uint stride) where T : unmanaged
        {
            ulong bytes = (ulong)values.Length * (ulong)Marshal.SizeOf<T>();
            Buffer source = backend.CreateBuffer(device, new(bytes, BufferUsages.CopySource), MemoryType.Upload);
            Buffer buffer = Keep(backend.CreateBuffer(device, new(bytes, BufferUsages.ShaderRead | BufferUsages.CopyDestination)));
            using (MappedBuffer mapping = backend.Map(source, MapType.Write, BufferRange.Whole))
            {
                MemoryMarshal.AsBytes(values).CopyTo(mapping.Bytes);
                mapping.Flush(mapping.Range);
            }
            uploads.Add((source, buffer));
            Buffers[name] = new(buffer, stride);
            ReadBindings[name] = ResourceBinding.ReadOnlyBuffer(Keep(backend.CreateBufferSrv(device,
                new(buffer, BufferRange.Whole, StructureStride: stride))));
            GeometryBytes += (long)bytes;
        }
    }

    private T Keep<T>(T resource) where T : IDisposable { _resources.Add(resource); return resource; }
    public void Dispose() { for (int index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose(); }
}

public readonly record struct BlendBuffer(Buffer Buffer, uint Stride);
