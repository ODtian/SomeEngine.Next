using System.Buffers;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using SomeEngine.Render.Assets;

namespace SomeEngine.Render;

/// <summary>Creates and retains each pipeline description once for a renderer's device.</summary>
public sealed class RenderPipelines : IDisposable
{
    private readonly object _gate = new();
    private readonly IGraphicsBackend _backend;
    private readonly PipelineCache _cache;
    private readonly string _cachePath;
    private readonly List<(PipelineDescription Description, Task<Pipeline> Pipeline)> _pipelines = [];
    private bool _disposed;

    public RenderPipelines(IGraphicsBackend backend, Device device, string cacheDirectory = "Library/PipelineCache")
    {
        _backend = backend;
        Device = device;
        AdapterInfo adapter = device.Adapter;
        string backendName = device.Capabilities.CompileTarget == SlangCompileTarget.Spirv ? "vulkan" : "d3d12";
        _cachePath = Path.Combine(cacheDirectory,
            $"{backendName}-{adapter.VendorId:x4}-{adapter.DeviceId:x4}-{adapter.DriverVersion}.bin");
        byte[] bytes = File.Exists(_cachePath) ? File.ReadAllBytes(_cachePath) : [];
        try
        {
            _cache = backend.CreatePipelineCache(device, new PipelineCacheDesc(bytes, "Renderer pipelines"));
        }
        catch (GraphicsException exception) when (exception.InnerException is InvalidDataException)
        {
            _cache = backend.CreatePipelineCache(device, new PipelineCacheDesc(label: "Renderer pipelines"));
        }
    }

    public Device Device { get; }

    /// <summary>Returns the shared pipeline task. The renderer retains the returned pipeline.</summary>
    public Task<Pipeline> CreateCompute(in ComputePipelineDesc desc)
    {
        PipelineDescription description = PipelineDescription.Compute(desc);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Task<Pipeline>? existing = Find(description);
            if (existing is not null)
                return existing;
            Task<Pipeline> pipeline = _backend.CreateComputePipelineAsync(Device, desc, _cache);
            _pipelines.Add((description, pipeline));
            return pipeline;
        }
    }

    /// <summary>Returns the shared pipeline task. The renderer retains the returned pipeline.</summary>
    public Task<Pipeline> CreateGraphics(in GraphicsPipelineDesc desc)
    {
        PipelineDescription description = PipelineDescription.Graphics(desc);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Task<Pipeline>? existing = Find(description);
            if (existing is not null)
                return existing;
            Task<Pipeline> pipeline = _backend.CreateGraphicsPipelineAsync(Device, desc, _cache);
            _pipelines.Add((description, pipeline));
            return pipeline;
        }
    }

    /// <summary>Returns the shared pipeline task. The renderer retains the returned pipeline.</summary>
    public Task<Pipeline> CreateMesh(in MeshPipelineDesc desc)
    {
        PipelineDescription description = PipelineDescription.Mesh(desc);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Task<Pipeline>? existing = Find(description);
            if (existing is not null)
                return existing;
            Task<Pipeline> pipeline = _backend.CreateMeshPipelineAsync(Device, desc, _cache);
            _pipelines.Add((description, pipeline));
            return pipeline;
        }
    }

    private Task<Pipeline>? Find(PipelineDescription description)
    {
        foreach (var entry in _pipelines)
            if (entry.Description.Matches(description))
                return entry.Pipeline;
        return null;
    }

    public Task<Pipeline> CreateWithShader(Pipeline pipeline, Shader shader)
    {
        PipelineDescription description;
        lock (_gate)
            description = _pipelines.First(entry => entry.Pipeline.IsCompletedSuccessfully &&
                ReferenceEquals(entry.Pipeline.Result, pipeline)).Description;
        return description.CreateWithShader(this, shader);
    }

    /// <summary>Creates a replacement for a compute pipeline built from the supplied previous shader.</summary>
    public LinkedComputePipeline? CreateComputeWithShader(Pipeline pipeline, Shader previous, Shader next)
    {
        PipelineDescription description;
        lock (_gate)
            description = _pipelines.First(entry => entry.Pipeline.IsCompletedSuccessfully &&
                ReferenceEquals(entry.Pipeline.Result, pipeline)).Description;
        if (!description.IsComputeShader(previous)) return null;
        int target = next.RequireSlangTarget(Device.Capabilities.CompileTarget);
        EntryPointReflection entryPoint = next.RequireSlangEntry(target, description.ComputeEntry.Name, SlangStage.Compute);
        return LinkedComputePipeline.Create(this, next, entryPoint, pipeline.Label ?? entryPoint.Name);
    }

    public void Dispose()
    {
        Task<Pipeline>[] pipelines;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            pipelines = _pipelines.Select(entry => entry.Pipeline).ToArray();
            _pipelines.Clear();
        }
        try
        {
            ((Task)Task.WhenAll(pipelines)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();
            _backend.TryGetPipelineCacheData(_cache, [], out int requiredBytes);
            byte[] bytes = new byte[requiredBytes];
            if (_backend.TryGetPipelineCacheData(_cache, bytes, out _))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                File.WriteAllBytes(_cachePath, bytes);
            }
        }
        finally
        {
            foreach (Task<Pipeline> pipeline in pipelines)
                if (pipeline.IsCompletedSuccessfully)
                    pipeline.Result.Dispose();
            _cache.Dispose();
        }
    }

    private sealed class PipelineDescription
    {
        private PipelineType _type;
        private SlangShaderData _slangCode = null!;
        private ShaderReflection _reflection;
        private EntryPointReflection _firstEntry, _secondEntry, _thirdEntry;
        private byte[] _firstCode = [], _secondCode = [], _thirdCode = [];
        private StaticSamplerBinding[] _samplers = [];
        private VertexBufferLayout[] _vertexBuffers = [];
        private VertexAttribute[] _vertexAttributes = [];
        private PrimitiveTopology _topology;
        private StripCut _stripCut;
        private RasterizerState _rasterizer;
        private MultisampleState _multisample;
        private DepthStencilState _depthStencil;
        private BlendAttachmentState[] _blendAttachments = [];
        private bool _independentBlend;
        private LogicOperation? _logicOperation;
        private Format[] _colorFormats = [];
        private Format? _depthFormat;
        private uint _samples;
        private DynamicStates _dynamicStates;
        private bool _hasStreamOutput;
        private StreamOutputElement[] _outputElements = [];
        private uint[] _outputStrides = [];
        private uint? _rasterizedStream;

        internal EntryPointReflection ComputeEntry => _firstEntry;
        internal bool IsComputeShader(Shader shader) =>
            _type == PipelineType.Compute && ReferenceEquals(_slangCode.Code, shader.SlangCode.Code);

        internal static PipelineDescription Compute(in ComputePipelineDesc desc) => new()
        {
            _type = PipelineType.Compute,
            _slangCode = desc.SlangCode,
            _reflection = desc.Reflection,
            _firstEntry = desc.Compute,
            _firstCode = CodeBytes(desc.Code, desc.CodeBlob),
            _samplers = desc.StaticSamplers.ToArray(),
        };

        internal static PipelineDescription Graphics(in GraphicsPipelineDesc desc)
        {
            var result = new PipelineDescription
            {
                _type = PipelineType.Graphics,
                _slangCode = desc.SlangCode,
                _reflection = desc.Reflection,
                _firstEntry = desc.Vertex,
                _secondEntry = desc.Pixel,
                _firstCode = CodeBytes(desc.VertexCode, desc.VertexCodeBlob),
                _secondCode = CodeBytes(desc.PixelCode, desc.PixelCodeBlob),
                _samplers = desc.StaticSamplers.ToArray(),
                _vertexBuffers = desc.VertexBuffers.ToArray(),
                _vertexAttributes = desc.VertexAttributes.ToArray(),
                _topology = desc.Topology,
                _stripCut = desc.StripCut,
                _hasStreamOutput = desc.HasStreamOutput,
                _outputElements = desc.StreamOutput.Elements.ToArray(),
                _outputStrides = desc.StreamOutput.BufferStrides.ToArray(),
                _rasterizedStream = desc.StreamOutput.RasterizedStreamIndex,
            };
            result.SetRasterState(desc.Rasterizer, desc.Multisample, desc.DepthStencil,
                desc.Blend, desc.Attachments, desc.DynamicStates);
            return result;
        }

        internal static PipelineDescription Mesh(in MeshPipelineDesc desc)
        {
            var result = new PipelineDescription
            {
                _type = PipelineType.Mesh,
                _slangCode = desc.SlangCode,
                _reflection = desc.Reflection,
                _firstEntry = desc.Mesh,
                _secondEntry = desc.Amplification,
                _thirdEntry = desc.Pixel,
                _firstCode = CodeBytes(desc.MeshCode, desc.MeshCodeBlob),
                _secondCode = CodeBytes(desc.AmplificationCode, desc.AmplificationCodeBlob),
                _thirdCode = CodeBytes(desc.PixelCode, desc.PixelCodeBlob),
                _samplers = desc.StaticSamplers.ToArray(),
            };
            result.SetRasterState(desc.Rasterizer, desc.Multisample, desc.DepthStencil,
                desc.Blend, desc.Attachments, desc.DynamicStates);
            return result;
        }

        private void SetRasterState(in RasterizerState rasterizer, in MultisampleState multisample,
            in DepthStencilState depthStencil, in BlendState blend, in AttachmentFormatSignature attachments,
            DynamicStates dynamicStates)
        {
            _rasterizer = rasterizer;
            _multisample = multisample;
            _depthStencil = depthStencil;
            _blendAttachments = blend.Attachments.ToArray();
            _independentBlend = blend.IndependentBlend;
            _logicOperation = blend.LogicOperation;
            _colorFormats = attachments.ColorFormats.ToArray();
            _depthFormat = attachments.DepthStencilFormat;
            _samples = attachments.SampleCount;
            _dynamicStates = dynamicStates;
        }

        private static byte[] CodeBytes(ReadOnlySequence<byte> code, ISlangBlob? blob) =>
            blob is null ? code.ToArray() : blob.Buffer.ToArray();

        internal Task<Pipeline> CreateWithShader(RenderPipelines pipelines, Shader shader)
        {
            int target = shader.RequireSlangTarget(pipelines.Device.Capabilities.CompileTarget);
            ShaderReflection reflection = shader.GetSlangReflection(target);
            EntryPointReflection first = Entry(_firstEntry), second = Entry(_secondEntry), third = Entry(_thirdEntry);
            var blend = new BlendState(_blendAttachments, _independentBlend, _logicOperation);
            var attachments = new AttachmentFormatSignature(_colorFormats, _depthFormat, _samples);
            return _type switch
            {
                PipelineType.Compute => pipelines.CreateCompute(new(shader.SlangCode, reflection, first,
                    Bytes(first), StaticSamplers: _samplers, CodeBlob: Blob(first))),
                PipelineType.Mesh => pipelines.CreateMesh(new(shader.SlangCode, reflection,
                    first, Bytes(first), second, Bytes(second), third, Bytes(third),
                    _rasterizer, _multisample, _depthStencil, blend, attachments, _dynamicStates,
                    staticSamplers: _samplers, meshCodeBlob: Blob(first), amplificationCodeBlob: Blob(second), pixelCodeBlob: Blob(third))),
                _ => _hasStreamOutput
                    ? pipelines.CreateGraphics(new(shader.SlangCode, reflection, first, Bytes(first), second, Bytes(second),
                        _vertexBuffers, _vertexAttributes, _topology, _stripCut, _rasterizer, _multisample,
                        _depthStencil, blend, attachments, new StreamOutputState(_outputElements, _outputStrides, _rasterizedStream),
                        _dynamicStates, staticSamplers: _samplers, vertexCodeBlob: Blob(first), pixelCodeBlob: Blob(second)))
                    : pipelines.CreateGraphics(new(shader.SlangCode, reflection, first, Bytes(first), second, Bytes(second),
                        _vertexBuffers, _vertexAttributes, _topology, _stripCut, _rasterizer, _multisample,
                        _depthStencil, blend, attachments, _dynamicStates, staticSamplers: _samplers,
                        vertexCodeBlob: Blob(first), pixelCodeBlob: Blob(second))),
            };

            EntryPointReflection Entry(EntryPointReflection before) => before == EntryPointReflection.Null
                ? EntryPointReflection.Null : shader.RequireSlangEntry(target, before.Name, before.Stage);
            ReadOnlySequence<byte> Bytes(EntryPointReflection entry) => entry == EntryPointReflection.Null
                ? ReadOnlySequence<byte>.Empty : shader.GetSlangEntryBytes(target, entry);
            ISlangBlob? Blob(EntryPointReflection entry) => entry == EntryPointReflection.Null
                ? null : shader.GetSlangEntryBlob(target, entry);
        }

        internal bool Matches(PipelineDescription other) =>
            _type == other._type && ReferenceEquals(_slangCode.Code, other._slangCode.Code) &&
            _reflection.Equals(other._reflection) && _firstEntry.Equals(other._firstEntry) &&
            _secondEntry.Equals(other._secondEntry) && _thirdEntry.Equals(other._thirdEntry) &&
            _firstCode.AsSpan().SequenceEqual(other._firstCode) &&
            _secondCode.AsSpan().SequenceEqual(other._secondCode) &&
            _thirdCode.AsSpan().SequenceEqual(other._thirdCode) &&
            _samplers.AsSpan().SequenceEqual(other._samplers) &&
            _vertexBuffers.AsSpan().SequenceEqual(other._vertexBuffers) &&
            _vertexAttributes.AsSpan().SequenceEqual(other._vertexAttributes) &&
            _topology == other._topology && _stripCut == other._stripCut &&
            _rasterizer == other._rasterizer && _multisample == other._multisample &&
            _depthStencil == other._depthStencil && _independentBlend == other._independentBlend &&
            _logicOperation == other._logicOperation &&
            _blendAttachments.AsSpan().SequenceEqual(other._blendAttachments) &&
            _colorFormats.AsSpan().SequenceEqual(other._colorFormats) &&
            _depthFormat == other._depthFormat && _samples == other._samples &&
            _dynamicStates == other._dynamicStates && _hasStreamOutput == other._hasStreamOutput &&
            _outputElements.AsSpan().SequenceEqual(other._outputElements) &&
            _outputStrides.AsSpan().SequenceEqual(other._outputStrides) &&
            _rasterizedStream == other._rasterizedStream;
    }
}
