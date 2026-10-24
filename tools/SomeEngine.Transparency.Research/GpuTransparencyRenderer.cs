using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using Shader = SomeEngine.Resources.Shader;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Transparency.Research;

public sealed class GpuTransparencyRenderer : ITransparencyRenderer
{
    private static readonly TextureSubresourceRange ColorRange = new(0, 1, 0, 1, TextureAspects.Color);
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly List<IDisposable> _resources = [];
    private readonly List<(Buffer Upload, Buffer Destination)> _geometryUploads = [];
    private readonly Buffer _geometry, _uniforms;
    private readonly Texture _depth, _output;
    private readonly Buffer? _results;
    private readonly LinkedComputePipeline _resolve;
    private readonly LinkedComputePipeline _background;
    private readonly Shader _resolveShader;
    private readonly Shader _backgroundShader;
    private readonly ResourceBinding[] _resolveBindings;
    private readonly ResourceBinding[] _backgroundBindings;
    private readonly CommandContext _context;
    private readonly QueryPool _queries;
    private readonly Buffer _queryReadback;
    private readonly ulong _frequency;
    private bool _rendered;

    public GpuTransparencyRenderer(IGraphicsBackend backend, Device device, int width, int height,
        TriangleHierarchy geometry, float colorError, bool inspect = false)
    {
        (_backend, _device, Width, Height) = (backend, device, width, height);
        _frequency = backend.CalibrateTimestamps(backend.GetQueue(device, QueueType.Graphics)).QueueFrequency;
        var data = new TransparencyGeometry(geometry);
        GeometryBytes = data.Bytes.Length;
        _geometry = Upload(data.Bytes.AsSpan(), BufferUsages.ShaderRead);
        _uniforms = Keep(backend.CreateBuffer(device, new(256, BufferUsages.Constant), MemoryType.Upload));
        using (MappedBuffer mapping = backend.Map(_uniforms, MapType.Write, BufferRange.Whole))
        {
            MemoryMarshal.AsBytes(data.CreateSettings(width, height, geometry, colorError).AsSpan()).CopyTo(mapping.Bytes);
            mapping.Flush(mapping.Range);
        }
        _depth = Texture(Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination | TextureUsages.ColorAttachment);
        _output = Texture(Format.R16G16B16A16Float, TextureUsages.Storage | TextureUsages.CopySource);
        _resolveShader = ResearchShader.Compile("resolve.slang", ["ResolveTransparency"], inspect,
            definitions: geometry.SeparatedCoverage ? "#define TRANSPARENCY_SEPARATED_COVERAGE 1\n" : "");
        _resolve = Keep(LinkedComputePipeline.Create(backend, device, _resolveShader, "ResolveTransparency", "Resolve transparency"));
        var bindings = new Dictionary<string, ResourceBinding>
        {
            ["Geometry"] = ResourceBinding.ReadOnlyBuffer(Keep(backend.CreateBufferSrv(device, new(_geometry, BufferRange.Whole)))),
            ["Settings"] = ResourceBinding.ConstantBuffer(Keep(backend.CreateBufferCbv(device, new(_uniforms, BufferRange.Whole)))),
            ["OpaqueDepth"] = Sample(_depth, Format.R32Float, TextureViewDimension.Texture2DArray),
            ["SceneColor"] = ResourceBinding.StorageTexture(Keep(backend.CreateTextureUav(device,
                new(_output, ColorRange, Format.R16G16B16A16Float, TextureViewDimension.Texture2DArray)))),
        };
        if (inspect)
        {
            _results = Keep(backend.CreateBuffer(device, new((ulong)width * (ulong)height * 16,
                BufferUsages.ShaderWrite | BufferUsages.CopySource)));
            bindings["Results"] = ResourceBinding.WritableBuffer(Keep(backend.CreateBufferUav(device,
                new(_results, BufferRange.Whole, StructureStride: 16))));
        }
        _resolveBindings = Bindings(_resolve.Reflection, bindings);
        _backgroundShader = ResearchShader.Compile("comparison_background.slang", ["FillBackground"]);
        _background = Keep(LinkedComputePipeline.Create(backend, device, _backgroundShader, "FillBackground", "Background"));
        _backgroundBindings = Bindings(_background.Reflection, bindings);
        _context = Keep(backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1)));
        _queries = Keep(backend.CreateQueryPool(device, new(QueryType.Timestamp, QueueType.Graphics, 2)));
        _queryReadback = Keep(backend.CreateBuffer(device, new(16, BufferUsages.QueryResolve), MemoryType.Readback));
        Initialize();
    }

    public int Width { get; }
    public int Height { get; }
    public long GeometryBytes { get; }
    public long IntermediateBytes => 0;
    public Vector4[] ReadColors() => Read().Colors;

    public double Render()
    {
        _backend.Begin(_context);
        _backend.ResetQueries(_context, _queries, 0, 2);
        Record(_queries, 0);
        _backend.ResolveQueries(_context, _queries, 0, 2, _queryReadback, BufferRange.Whole);
        Submit();
        using MappedBuffer mapping = _backend.Map(_queryReadback, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        ReadOnlySpan<ulong> timestamps = MemoryMarshal.Cast<byte, ulong>(mapping.Bytes);
        return (timestamps[1] - timestamps[0]) * 1000.0 / _frequency;
    }

    public TransparencyTimes Measure(int warmupFrames, int samples)
    {
        using QueryPool queries = _backend.CreateQueryPool(_device, new(QueryType.Timestamp, QueueType.Graphics, (uint)samples * 2));
        using Buffer readback = _backend.CreateBuffer(_device, new((ulong)samples * 16, BufferUsages.QueryResolve), MemoryType.Readback);
        _backend.Begin(_context);
        _backend.ResetQueries(_context, queries, 0, (uint)samples * 2);
        for (int frame = 0; frame < warmupFrames; frame++) Record(null, 0);
        for (int frame = 0; frame < samples; frame++) Record(queries, (uint)frame * 2);
        _backend.ResolveQueries(_context, queries, 0, (uint)samples * 2, readback, BufferRange.Whole);
        Submit();
        using MappedBuffer mapping = _backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        ReadOnlySpan<ulong> values = MemoryMarshal.Cast<byte, ulong>(mapping.Bytes);
        var times = new double[samples];
        for (int frame = 0; frame < samples; frame++) times[frame] = (values[frame * 2 + 1] - values[frame * 2]) * 1000.0 / _frequency;
        return new(times, [], [], times);
    }

    private void Record(QueryPool? queries, uint firstQuery)
    {
        _backend.Barrier(_context, new TextureBarrier(_output, ColorRange,
            _rendered ? PipelineSync.ComputeShading : PipelineSync.None, PipelineSync.ComputeShading,
            _rendered ? ResourceAccess.UnorderedAccess : ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess,
            _rendered ? TextureLayout.UnorderedAccess : TextureLayout.Undefined, TextureLayout.UnorderedAccess));
        _backend.SetPipeline(_context, _background.Pipeline);
        _backend.SetTransientParameterBindings(_context, new(_background.ParameterLayout, _backgroundBindings, []));
        _backend.Dispatch(_context, new((uint)(Width + 7) / 8, (uint)(Height + 7) / 8, 1));
        if (_results is not null)
            _backend.Barrier(_context, new BufferBarrier(_results,
                _rendered ? PipelineSync.ComputeShading : PipelineSync.None, PipelineSync.ComputeShading,
                _rendered ? ResourceAccess.UnorderedAccess : ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
        _backend.Barrier(_context, new TextureBarrier(_output, ColorRange,
            PipelineSync.ComputeShading, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess,
            TextureLayout.UnorderedAccess, TextureLayout.UnorderedAccess));
        if (queries is not null) _backend.WriteTimestamp(_context, queries, firstQuery);
        _backend.SetPipeline(_context, _resolve.Pipeline);
        _backend.SetTransientParameterBindings(_context, new(_resolve.ParameterLayout, _resolveBindings, []));
        _backend.Dispatch(_context, new((uint)(Width + 7) / 8, (uint)(Height + 7) / 8, 1));
        if (queries is not null) _backend.WriteTimestamp(_context, queries, firstQuery + 1);
        _rendered = true;
    }

    public (Vector4[] Colors, uint[] Results) Read()
    {
        uint pitch = ((uint)Width * 8 + 255) & ~255u;
        using Buffer colors = _backend.CreateBuffer(_device, new((ulong)pitch * (uint)Height, BufferUsages.CopyDestination), MemoryType.Readback);
        using Buffer results = _backend.CreateBuffer(_device, new((ulong)Width * (ulong)Height * 16, BufferUsages.CopyDestination), MemoryType.Readback);
        _backend.Begin(_context);
        _backend.Barrier(_context, new TextureBarrier(_output, ColorRange, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource, TextureLayout.UnorderedAccess, TextureLayout.CopySource));
        _backend.CopyTextureToBuffer(_context, new(colors, 0, pitch, (uint)Height, _output, 0, 0, TextureAspects.Color,
            0, 0, 0, (uint)Width, (uint)Height, 1));
        if (_results is not null)
        {
            _backend.Barrier(_context, new BufferBarrier(_results, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            _backend.CopyBuffer(_context, new(_results, 0, results, 0, (ulong)Width * (ulong)Height * 16));
            _backend.Barrier(_context, new BufferBarrier(_results, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        }
        _backend.Barrier(_context, new TextureBarrier(_output, ColorRange, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess, TextureLayout.CopySource, TextureLayout.UnorderedAccess));
        Submit();
        using MappedBuffer colorMapping = _backend.Map(colors, MapType.Read, BufferRange.Whole);
        colorMapping.Invalidate(colorMapping.Range);
        var values = new Vector4[Width * Height];
        for (int row = 0; row < Height; row++)
        {
            ReadOnlySpan<Half> pixels = MemoryMarshal.Cast<byte, Half>(colorMapping.Bytes.Slice(row * (int)pitch, Width * 8));
            for (int column = 0; column < Width; column++)
                values[row * Width + column] = new((float)pixels[column * 4], (float)pixels[column * 4 + 1],
                    (float)pixels[column * 4 + 2], (float)pixels[column * 4 + 3]);
        }
        using MappedBuffer resultMapping = _backend.Map(results, MapType.Read, BufferRange.Whole);
        resultMapping.Invalidate(resultMapping.Range);
        return (values, _results is null ? [] : MemoryMarshal.Cast<byte, uint>(resultMapping.Bytes).ToArray());
    }

    private void Initialize()
    {
        _backend.Begin(_context);
        foreach (var copy in _geometryUploads)
        {
            _backend.Barrier(_context, new BufferBarrier(copy.Destination, PipelineSync.None, PipelineSync.Copy,
                ResourceAccess.NoAccess, ResourceAccess.CopyDestination));
            _backend.CopyBuffer(_context, new(copy.Upload, 0, copy.Destination, 0, copy.Destination.Info.Size));
            _backend.Barrier(_context, new BufferBarrier(copy.Destination, PipelineSync.Copy,
                PipelineSync.VertexShading | PipelineSync.ComputeShading, ResourceAccess.CopyDestination, ResourceAccess.ShaderResource));
        }
        foreach (var (texture, color) in new[] { (_depth, Vector4.Zero) })
        {
            _backend.Barrier(_context, new TextureBarrier(texture, ColorRange, PipelineSync.None, PipelineSync.Copy,
                ResourceAccess.NoAccess, ResourceAccess.CopyDestination, TextureLayout.Undefined, TextureLayout.CopyDestination));
            _backend.ClearTexture(_context, texture, ColorRange, color);
            _backend.Barrier(_context, new TextureBarrier(texture, ColorRange, PipelineSync.Copy,
                PipelineSync.VertexShading | PipelineSync.PixelShading | PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.ShaderResource, TextureLayout.CopyDestination, TextureLayout.ShaderResource));
        }
        Submit();
        foreach (var copy in _geometryUploads) copy.Upload.Dispose();
        _geometryUploads.Clear();
    }

    private void Submit()
    {
        using RecordedCommands commands = _backend.End(_context);
        QueueCompletion completion = _backend.Submit(_backend.GetQueue(_device, QueueType.Graphics), new([], [], [commands], [], []));
        if (_backend.WaitCpu(completion, TimeSpan.FromSeconds(30)) != WaitStatus.Completed)
            throw new TimeoutException("Transparency rendering timed out.");
    }

    private Buffer Upload<T>(Span<T> values, BufferUsages usages) where T : unmanaged
    {
        ulong bytes = (ulong)values.Length * (ulong)Marshal.SizeOf<T>();
        Buffer upload = _backend.CreateBuffer(_device, new(bytes, BufferUsages.CopySource), MemoryType.Upload);
        Buffer buffer = Keep(_backend.CreateBuffer(_device, new(bytes, usages | BufferUsages.CopyDestination), MemoryType.DeviceLocal));
        _geometryUploads.Add((upload, buffer));
        using MappedBuffer mapping = _backend.Map(upload, MapType.Write, BufferRange.Whole);
        MemoryMarshal.AsBytes(values).CopyTo(mapping.Bytes);
        mapping.Flush(mapping.Range);
        return buffer;
    }

    private Texture Texture(Format format, TextureUsages usages) => Keep(_backend.CreateTexture(_device,
        new(TextureDimension.Texture2D, (uint)Width, (uint)Height, 1, 1, 1, 1, format, usages)));

    private ResourceBinding Sample(Texture texture, Format format, TextureViewDimension dimension) =>
        ResourceBinding.SampledTexture(Keep(_backend.CreateTextureSrv(_device, new(texture, ColorRange, format, dimension))));

    public static ResourceBinding[] Bindings(ShaderReflection reflection, IReadOnlyDictionary<string, ResourceBinding> values)
    {
        TypeLayoutReflection root = (reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).TypeLayout;
        if (root.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock) root = root.ElementTypeLayout;
        var bindings = new List<ResourceBinding>();
        for (nint index = 0; index < root.BindingRangeCount; index++)
            if (root.GetBindingRangeDescriptorRangeCount(index) > 0)
                bindings.Add(values[root.GetBindingRangeLeafVariable(index).Name]);
        return bindings.ToArray();
    }

    private T Keep<T>(T resource) where T : IDisposable { _resources.Add(resource); return resource; }
    public void Dispose() { for (int index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose(); }
}
