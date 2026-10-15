using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Transparency.Research;

public sealed class BlendEffectRenderer : ITransparencyRenderer
{
    private static readonly TextureSubresourceRange ColorRange = new(0, 1, 0, 1, TextureAspects.Color);
    private static readonly TextureSubresourceRange DepthRange = new(0, 1, 0, 1, TextureAspects.Depth);
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly BlendEffectData _data;
    private readonly BlendMethod _method;
    private readonly List<IDisposable> _resources = [];
    private readonly BlendEffectResources _effect;
    private readonly CommandContext _context;
    private readonly Texture _color, _depth;
    private readonly ColorAttachmentView _colorView;
    private readonly DepthStencilView _depthReadView;
    private readonly LinkedComputePipeline _background;
    private readonly ResourceBinding[] _backgroundBindings, _effectBindings, _cellBindings;
    private readonly ulong _frequency;
    private bool _rendered;

    public BlendEffectRenderer(IGraphicsBackend backend, Device device, BlendEffectData data,
        BlendMethod method, float xrayDistance, ComparisonShaders shaders)
    {
        (_backend, _device, _data, _method) = (backend, device, data, method);
        _effect = Keep(new BlendEffectResources(backend, device, data, method, BlendEffectData.Projection, xrayDistance, shaders));
        _context = Keep(backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1)));
        _frequency = backend.CalibrateTimestamps(backend.GetQueue(device, QueueType.Graphics)).QueueFrequency;
        _color = Texture(Format.R16G16B16A16Float, TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopySource);
        _depth = Texture(Format.D32Float, TextureUsages.DepthStencilAttachment | TextureUsages.Sampled);
        _colorView = Keep(backend.CreateColorAttachmentView(device, new(_color, ColorRange, Format.R16G16B16A16Float, TextureViewDimension.Texture2D)));
        _depthReadView = Keep(backend.CreateDepthStencilView(device,
            new(_depth, DepthRange, Format.D32Float, TextureViewDimension.Texture2D, ReadOnlyDepth: true)));
        var bindings = new Dictionary<string, ResourceBinding>(_effect.ReadBindings)
        {
            ["SceneColor"] = ResourceBinding.StorageTexture(Keep(backend.CreateTextureUav(device,
                new(_color, ColorRange, Format.R16G16B16A16Float, TextureViewDimension.Texture2DArray)))),
            ["OpaqueDepth"] = ResourceBinding.SampledTexture(Keep(backend.CreateTextureSrv(device,
                new(_depth, DepthRange, Format.D32Float, TextureViewDimension.Texture2DArray)))),
        };
        _background = Keep(LinkedComputePipeline.Create(backend, device,
            shaders.Get("blend_effect_background.slang", ["FillBlendBackground"]), "FillBlendBackground", "Blend background"));
        _backgroundBindings = GpuTransparencyRenderer.Bindings(_background.Reflection, bindings);
        _effectBindings = GpuTransparencyRenderer.Bindings(_effect.DrawReflection ?? _effect.Resolve!.Reflection, bindings);
        bindings["CellSlices"] = _effect.CellWriteBinding;
        _cellBindings = _effect.BuildCells is { } build ? GpuTransparencyRenderer.Bindings(build.Reflection, bindings) : [];
        InitializeDepth();
    }

    public long GeometryBytes => _effect.GeometryBytes + 256;
    public long IntermediateBytes => _effect.IntermediateBytes;
    public double Render() => Measure(0, 1).TotalMs[0];

    private void InitializeDepth()
    {
        using DepthStencilView view = _backend.CreateDepthStencilView(_device,
            new(_depth, DepthRange, Format.D32Float, TextureViewDimension.Texture2D));
        _backend.Begin(_context);
        _backend.Barrier(_context, new TextureBarrier(_depth, DepthRange, PipelineSync.None, PipelineSync.DepthStencil,
            ResourceAccess.NoAccess, ResourceAccess.DepthStencilWrite, TextureLayout.Undefined, TextureLayout.DepthStencilWrite));
        for (uint row = 0; row < 4; row++)
        for (uint column = 0; column < 4; column++)
        {
            float depth = 3 + (row * 4 + column) * 3.5f;
            Matrix4x4 projection = BlendEffectData.Projection;
            float deviceDepth = (-depth * projection.M33 + projection.M43) / (-depth * projection.M34 + projection.M44);
            _backend.BeginRendering(_context, new([], new(view, LoadType.Clear, StoreType.Store,
                LoadType.Discard, StoreType.Discard, deviceDepth), (uint)_data.Width / 4, (uint)_data.Height / 4,
                x: column * (uint)_data.Width / 4, y: row * (uint)_data.Height / 4));
            _backend.EndRendering(_context);
        }
        bool depthTest = _method == BlendMethod.Forward;
        _backend.Barrier(_context, new TextureBarrier(_depth, DepthRange, PipelineSync.DepthStencil,
            PipelineSync.PixelShading | PipelineSync.ComputeShading | (depthTest ? PipelineSync.DepthStencil : PipelineSync.None),
            ResourceAccess.DepthStencilWrite, ResourceAccess.ShaderResource | (depthTest ? ResourceAccess.DepthStencilRead : ResourceAccess.NoAccess),
            TextureLayout.DepthStencilWrite, depthTest ? TextureLayout.DepthStencilRead : TextureLayout.ShaderResource));
        Submit();
    }

    public TransparencyTimes Measure(int warmupFrames, int samples)
    {
        using QueryPool queries = _backend.CreateQueryPool(_device, new(QueryType.Timestamp, QueueType.Graphics, (uint)samples * 3));
        using Buffer readback = _backend.CreateBuffer(_device, new((ulong)samples * 24, BufferUsages.QueryResolve), MemoryType.Readback);
        _backend.Begin(_context);
        _backend.ResetQueries(_context, queries, 0, (uint)samples * 3);
        for (int frame = 0; frame < warmupFrames; frame++) Record(null, 0);
        for (int frame = 0; frame < samples; frame++) Record(queries, (uint)frame * 3);
        _backend.ResolveQueries(_context, queries, 0, (uint)samples * 3, readback, BufferRange.Whole);
        Submit();
        using MappedBuffer mapping = _backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        ReadOnlySpan<ulong> values = MemoryMarshal.Cast<byte, ulong>(mapping.Bytes);
        var total = new double[samples];
        var build = new double[samples];
        var shade = new double[samples];
        for (int frame = 0; frame < samples; frame++)
        {
            int query = frame * 3;
            total[frame] = (values[query + 2] - values[query]) * 1000.0 / _frequency;
            build[frame] = (values[query + 1] - values[query]) * 1000.0 / _frequency;
            shade[frame] = (values[query + 2] - values[query + 1]) * 1000.0 / _frequency;
        }
        return new(total, build, [], shade);
    }

    private void Record(QueryPool? queries, uint query)
    {
        bool forward = _method == BlendMethod.Forward;
        bool depthTest = forward;
        PipelineSync previous = forward ? PipelineSync.RenderTarget : PipelineSync.ComputeShading;
        ResourceAccess previousAccess = forward ? ResourceAccess.RenderTarget : ResourceAccess.UnorderedAccess;
        TextureLayout previousLayout = forward ? TextureLayout.RenderTarget : TextureLayout.UnorderedAccess;
        _backend.Barrier(_context, new TextureBarrier(_color, ColorRange,
            _rendered ? previous : PipelineSync.None, PipelineSync.ComputeShading,
            _rendered ? previousAccess : ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess,
            _rendered ? previousLayout : TextureLayout.Undefined, TextureLayout.UnorderedAccess));
        Run(_background, _backgroundBindings, new((uint)(_data.Width + 7) / 8, (uint)(_data.Height + 7) / 8, 1));
        if (queries is not null) _backend.WriteTimestamp(_context, queries, query);
        if (_method == BlendMethod.Cluster && _effect.BuildCells is { } build)
        {
            Buffer cells = _effect.Buffers["CellSlices"].Buffer;
            _backend.Barrier(_context, new BufferBarrier(cells, _rendered ? PipelineSync.ComputeShading : PipelineSync.None,
                PipelineSync.ComputeShading, _rendered ? ResourceAccess.ShaderResource : ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
            Run(build, _cellBindings, new(((uint)_data.Bounds.Length + 63) / 64, 1, 1));
            _backend.Barrier(_context, new BufferBarrier(cells, PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                ResourceAccess.UnorderedAccess, ResourceAccess.ShaderResource));
        }
        if (queries is not null) _backend.WriteTimestamp(_context, queries, query + 1);
        _backend.Barrier(_context, new TextureBarrier(_color, ColorRange, PipelineSync.ComputeShading,
            forward ? PipelineSync.RenderTarget : PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess,
            forward ? ResourceAccess.RenderTarget : ResourceAccess.UnorderedAccess, TextureLayout.UnorderedAccess,
            forward ? TextureLayout.RenderTarget : TextureLayout.UnorderedAccess));
        if (forward)
        {
            _backend.SetPipeline(_context, _effect.Draw!);
            _backend.SetViewports(_context, [new(0, 0, _data.Width, _data.Height)]);
            _backend.SetScissors(_context, [new(0, 0, _data.Width, _data.Height)]);
            _backend.SetTransientParameterBindings(_context, new(_effect.DrawLayout, _effectBindings, []));
            _backend.BeginRendering(_context, new([new(_colorView, LoadType.Load, StoreType.Store, Vector4.Zero)],
                depthTest ? new(_depthReadView, LoadType.Load, StoreType.Store, LoadType.Discard, StoreType.Discard) : null,
                (uint)_data.Width, (uint)_data.Height));
            _backend.Draw(_context, new(_effect.VertexCount, 1, 0, 0));
            _backend.EndRendering(_context);
        }
        else Run(_effect.Resolve!, _effectBindings, new((uint)_data.TileColumns, (uint)_data.TileRows, 1));
        if (queries is not null) _backend.WriteTimestamp(_context, queries, query + 2);
        _rendered = true;
    }

    private void Run(LinkedComputePipeline pipeline, ResourceBinding[] bindings, DispatchArguments dispatch)
    {
        _backend.SetPipeline(_context, pipeline.Pipeline);
        _backend.SetTransientParameterBindings(_context, new(pipeline.ParameterLayout, bindings, []));
        _backend.Dispatch(_context, dispatch);
    }

    public Vector4[] ReadColors()
    {
        uint pitch = ((uint)_data.Width * 8 + 255) & ~255u;
        using Buffer readback = _backend.CreateBuffer(_device, new((ulong)pitch * (uint)_data.Height, BufferUsages.CopyDestination), MemoryType.Readback);
        PipelineSync shading = _method == BlendMethod.Forward ? PipelineSync.RenderTarget : PipelineSync.ComputeShading;
        ResourceAccess access = _method == BlendMethod.Forward ? ResourceAccess.RenderTarget : ResourceAccess.UnorderedAccess;
        TextureLayout layout = _method == BlendMethod.Forward ? TextureLayout.RenderTarget : TextureLayout.UnorderedAccess;
        _backend.Begin(_context);
        _backend.Barrier(_context, new TextureBarrier(_color, ColorRange, shading, PipelineSync.Copy,
            access, ResourceAccess.CopySource, layout, TextureLayout.CopySource));
        _backend.CopyTextureToBuffer(_context, new(readback, 0, pitch, (uint)_data.Height, _color, 0, 0,
            TextureAspects.Color, 0, 0, 0, (uint)_data.Width, (uint)_data.Height, 1));
        _backend.Barrier(_context, new TextureBarrier(_color, ColorRange, PipelineSync.Copy, shading,
            ResourceAccess.CopySource, access, TextureLayout.CopySource, layout));
        Submit();
        using MappedBuffer mapping = _backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        var colors = new Vector4[_data.Width * _data.Height];
        for (int row = 0; row < _data.Height; row++)
        {
            ReadOnlySpan<Half> values = MemoryMarshal.Cast<byte, Half>(mapping.Bytes.Slice(row * (int)pitch, _data.Width * 8));
            for (int column = 0; column < _data.Width; column++)
                colors[row * _data.Width + column] = new((float)values[column * 4], (float)values[column * 4 + 1],
                    (float)values[column * 4 + 2], (float)values[column * 4 + 3]);
        }
        return colors;
    }

    private Texture Texture(Format format, TextureUsages usages) => Keep(_backend.CreateTexture(_device,
        new(TextureDimension.Texture2D, (uint)_data.Width, (uint)_data.Height, 1, 1, 1, 1, format, usages)));
    private T Keep<T>(T resource) where T : IDisposable { _resources.Add(resource); return resource; }
    private void Submit()
    {
        using RecordedCommands commands = _backend.End(_context);
        QueueCompletion completion = _backend.Submit(_backend.GetQueue(_device, QueueType.Graphics), new([], [], [commands], [], []));
        if (_backend.WaitCpu(completion, TimeSpan.FromSeconds(30)) != WaitStatus.Completed)
            throw new TimeoutException("Blend effect rendering timed out.");
    }
    public void Dispose() { for (int index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose(); }
}
