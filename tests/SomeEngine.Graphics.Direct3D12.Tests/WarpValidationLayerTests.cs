using SlangShaderSharp;
using Silk.NET.Direct3D12;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpValidationLayerTests
{
    [Fact]
    public void Pipeline_static_sampler_becomes_a_native_static_sampler()
    {
        const string source = """
            Texture2D<float4> inputTexture;
            SamplerState pipelineSampler : register(s3, space2);
            RWStructuredBuffer<float4> outputValues;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[id.x] = inputTexture.SampleLevel(
                    pipelineSampler,
                    float2(0.5, 0.5),
                    0);
            }
            """;
        D3D12TestShaderEntry[] entries = [new("computeMain", SlangStage.Compute)];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "pipeline_static_sampler_root_signature",
            source,
            entries,
            "sm_6_0");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;

        using var backend = new ValidationLayer(new D3D12Backend());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        StaticSamplerBinding[] staticSamplers =
        [
            StaticSampler(shader.Reflection, layout, 3, 2, new SamplerDesc(
                    FilterType.Linear,
                    FilterType.Nearest,
                    FilterType.Linear,
                    AddressType.Repeat,
                    AddressType.MirrorRepeat,
                    AddressType.ClampToBorder,
                    MipLodBias: -0.5f,
                    BorderColor: System.Numerics.Vector4.One,
                    MinimumLod: 1.25f,
                    MaximumLod: 7.5f)),
        ];
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(
                shader.Program,
                shader.GetEntryPoint(0),
                StaticSamplers: staticSamplers));
        StaticSamplerDesc[] native = D3D12Backend.GetCompiledStaticSamplers(pipeline);

        Assert.Collection(native, AssertStaticSampler);
        Assert.NotEmpty(D3D12Backend.GetSerializedRootSignature(pipeline));

        ResourceBinding[] runtimeBindings = CreateNullBindings(layout)
            .Where(static binding => binding.Type != ResourceBindingType.Sampler)
            .ToArray();
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(8, 0, 8));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(layout, runtimeBindings, []));
        backend.Discard(context);

        using PersistentParameterBindings persistent = backend.CreatePersistentParameterBindings(
            device,
            pipeline,
            new ParameterBlockBindings(layout, runtimeBindings, []));
        backend.PublishDescriptors(device);
        backend.UpdatePersistentParameterBindings(
            persistent,
            new ParameterBlockBindings(layout, runtimeBindings, []));
        backend.PublishDescriptors(device);
        backend.Begin(context, new CommandRecordingDesc(8, 0, 8));
        backend.SetPipeline(context, pipeline);
        backend.SetPersistentParameterBindings(context, persistent);
        backend.Discard(context);

        void AssertStaticSampler(StaticSamplerDesc sampler)
        {
            Assert.Equal(Filter.MinLinearMagPointMipLinear, sampler.Filter);
            Assert.Equal(TextureAddressMode.Wrap, sampler.AddressU);
            Assert.Equal(TextureAddressMode.Mirror, sampler.AddressV);
            Assert.Equal(TextureAddressMode.Border, sampler.AddressW);
            Assert.Equal(-0.5f, sampler.MipLODBias);
            Assert.Equal(1u, sampler.MaxAnisotropy);
            Assert.Equal(ComparisonFunc.Always, sampler.ComparisonFunc);
            Assert.Equal(StaticBorderColor.OpaqueWhite, sampler.BorderColor);
            Assert.Equal(1.25f, sampler.MinLOD);
            Assert.Equal(7.5f, sampler.MaxLOD);
            Assert.Equal(3u, sampler.ShaderRegister);
            Assert.Equal(2u, sampler.RegisterSpace);
            Assert.Equal(ShaderVisibility.All, sampler.ShaderVisibility);
        }
    }

    [Fact]
    public void Persistent_bindings_require_the_exact_pipeline_instance()
    {
        const string source = """
            Texture2D<float4> inputTexture;
            SamplerState firstSampler;
            SamplerState secondSampler;
            RWStructuredBuffer<float4> outputValues;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[0] = inputTexture.SampleLevel(firstSampler, 0.25, 0)
                    + inputTexture.SampleLevel(secondSampler, 0.75, 0);
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "persistent_root_token", source, [new("computeMain", SlangStage.Compute)], "sm_6_0");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection data = GetDataLayout(layout);
        nint[] samplerRanges = Enumerable.Range(0, checked((int)data.BindingRangeCount))
            .Select(static value => (nint)value)
            .Where(index => (data.GetBindingRangeType(index) & SlangBindingType.BaseMask) ==
                SlangBindingType.Sampler).ToArray();
        Assert.Equal(2, samplerRanges.Length);
        SamplerDesc state = new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
            AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge);
        StaticSamplerBinding firstStatic = new(
            data.GetBindingRangeLeafVariable(samplerRanges[0]), state);
        StaticSamplerBinding secondStatic = new(
            data.GetBindingRangeLeafVariable(samplerRanges[1]), state);
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline first = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0),
                StaticSamplers: new StaticSamplerBinding[] { firstStatic }));
        using Pipeline sameRoot = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0),
                StaticSamplers: new StaticSamplerBinding[] { firstStatic }));
        using Pipeline differentOmission = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0),
                StaticSamplers: new StaticSamplerBinding[] { secondStatic }));
        using Sampler runtimeSampler = backend.CreateSampler(device, state);
        ResourceBinding[] runtime = CreateNullBindings(layout).ToList() switch
        {
            var values => values.Where((_, ordinal) => ordinal != values.FindIndex(
                static binding => binding.Type == ResourceBindingType.Sampler)).ToArray(),
        };
        for (int ordinal = 0; ordinal < runtime.Length; ordinal++)
        {
            if (runtime[ordinal].Type == ResourceBindingType.Sampler)
                runtime[ordinal] = ResourceBinding.SampledWith(runtimeSampler);
        }
        using PersistentParameterBindings persistent = backend.CreatePersistentParameterBindings(
            device, first, new ParameterBlockBindings(layout, runtime, []));
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Compute, 0, 1));

        backend.Begin(context, new CommandRecordingDesc(8, 0, 8));
        backend.SetPipeline(context, first);
        backend.SetPersistentParameterBindings(context, persistent);
        backend.SetPipeline(context, sameRoot);
        Assert.Throws<ArgumentException>(() =>
            backend.SetPersistentParameterBindings(context, persistent));
        backend.SetPipeline(context, differentOmission);
        Assert.Throws<ArgumentException>(() =>
            backend.SetPersistentParameterBindings(context, persistent));
        backend.Discard(context);
    }

    [Fact]
    public void Static_sampler_input_order_does_not_change_raw_native_order()
    {
        const string source = """
            Texture2D<float4> inputTexture;
            SamplerState firstSampler : register(s7, space3);
            SamplerState secondSampler : register(s2, space1);
            RWStructuredBuffer<float4> outputValues;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[0] = inputTexture.SampleLevel(firstSampler, 0.25, 0)
                    + inputTexture.SampleLevel(secondSampler, 0.75, 0);
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "static_sampler_input_order", source,
            [new("computeMain", SlangStage.Compute)], "sm_6_0");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        SamplerDesc state = new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
            AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge);
        StaticSamplerBinding first = StaticSampler(shader.Reflection, layout, 7, 3, state);
        StaticSamplerBinding second = StaticSampler(shader.Reflection, layout, 2, 1, state);
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline forward = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0),
                StaticSamplers: new StaticSamplerBinding[] { first, second }));
        using Pipeline reverse = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0),
                StaticSamplers: new StaticSamplerBinding[] { second, first }));

        Assert.Equal([7u, 2u], D3D12Backend.GetCompiledStaticSamplers(forward)
            .Select(static sampler => sampler.ShaderRegister));
        Assert.Equal([7u, 2u], D3D12Backend.GetCompiledStaticSamplers(reverse)
            .Select(static sampler => sampler.ShaderRegister));
    }

    [Fact]
    public void Static_sampler_is_omitted_from_transient_and_persistent_resources()
    {
        const string source = """
            Texture2D<float4> inputTexture;
            SamplerState pipelineSampler : register(s3, space2);
            RWStructuredBuffer<float4> outputValues;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[id.x] = inputTexture.SampleLevel(
                    pipelineSampler, float2(0.5, 0.5), 0);
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "pipeline_static_sampler_binding_contract",
            source,
            [new("computeMain", SlangStage.Compute)],
            "sm_6_0");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        SamplerDesc samplerDescription = new(
            FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
            AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge);

        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(
                shader.Program,
                shader.GetEntryPoint(0),
                StaticSamplers: new StaticSamplerBinding[]
                {
                    StaticSampler(shader.Reflection, layout, 3, 2, samplerDescription),
                }));
        ResourceBinding[] reflected = CreateNullBindings(layout);
        ResourceBinding[] runtime = reflected
            .Where(static binding => binding.Type != ResourceBindingType.Sampler)
            .ToArray();
        Assert.Equal(reflected.Length - 1, runtime.Length);

        using PersistentParameterBindings validPersistent =
            backend.CreatePersistentParameterBindings(
                device,
                pipeline,
                new ParameterBlockBindings(layout, runtime, []));

        Assert.Throws<ArgumentException>(() => backend.CreatePersistentParameterBindings(
            device,
            pipeline,
            new ParameterBlockBindings(layout, reflected, [])));
        using Device otherDevice = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.Throws<ArgumentException>(() => backend.CreatePersistentParameterBindings(
            otherDevice,
            pipeline,
            new ParameterBlockBindings(layout, runtime, [])));
        backend.PublishDescriptors(device);

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));

        backend.Begin(context, new CommandRecordingDesc(8, 0, 8));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(layout, runtime, []));
        backend.Discard(context);

        backend.Begin(context, new CommandRecordingDesc(8, 0, 8));
        backend.SetPipeline(context, pipeline);
        backend.SetPersistentParameterBindings(context, validPersistent);
        backend.Discard(context);

        backend.Begin(context, new CommandRecordingDesc(8, 0, 8));
        backend.SetPipeline(context, pipeline);
        Assert.Throws<ArgumentException>(() => backend.SetTransientParameterBindings(
            context, new ParameterBlockBindings(layout, reflected, [])));

        backend.Discard(context);
        pipeline.Dispose();
        Assert.Throws<ObjectDisposedException>(() => backend.CreatePersistentParameterBindings(
            device,
            pipeline,
            new ParameterBlockBindings(layout, runtime, [])));
    }

    [Fact]
    public void Auto_layout_scalar_static_sampler_executes_without_a_runtime_sampler_descriptor()
    {
        const string source = """
            Texture2D<float4> inputTexture;
            SamplerState pipelineSampler;
            RWStructuredBuffer<uint> outputValues;
            uint outputIndex;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[outputIndex] = asuint(inputTexture.SampleLevel(
                    pipelineSampler, float2(0.5, 0.5), 0).x);
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "auto_scalar_static_sampler_execution", source,
            [new("computeMain", SlangStage.Compute)], "sm_6_0");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection data = GetDataLayout(layout);
        nint samplerRange = Enumerable.Range(0, checked((int)data.BindingRangeCount))
            .Select(static index => (nint)index)
            .Single(index => (data.GetBindingRangeType(index) & SlangBindingType.BaseMask) ==
                SlangBindingType.Sampler);
        Assert.Equal((nint)1, data.GetBindingRangeBindingCount(samplerRange));
        SamplerDesc sampler = new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
            AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge);

        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0),
                StaticSamplers: new StaticSamplerBinding[]
                {
                    new(data.GetBindingRangeLeafVariable(samplerRange), sampler),
                }));
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(8, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);
        using Texture texture = backend.CreateTexture(device,
            new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
                Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination));
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, texture, 1.0f);
        TextureSubresourceRange textureRange = new(0, 1, 0, 1, TextureAspects.Color);
        using TextureSrv textureSrv = backend.CreateTextureSrv(device,
            new TextureSrvDesc(texture, textureRange, Format.R32Float,
                TextureViewDimension.Texture2D));
        ResourceBinding[] runtime = CreateNullBindings(layout)
            .Where(static binding => binding.Type != ResourceBindingType.Sampler)
            .ToArray();
        int textureOrdinal = Array.FindIndex(runtime,
            static binding => binding.Type == ResourceBindingType.TextureSrv);
        int outputOrdinal = Array.FindIndex(runtime,
            static binding => binding.Type == ResourceBindingType.BufferUav);
        Assert.True(textureOrdinal >= 0);
        Assert.True(outputOrdinal >= 0);
        runtime[textureOrdinal] = ResourceBinding.SampledTexture(textureSrv);
        runtime[outputOrdinal] = ResourceBinding.WritableBuffer(outputUav);
        using PersistentParameterBindings persistent = backend.CreatePersistentParameterBindings(
            device, pipeline, new ParameterBlockBindings(layout, runtime, BitConverter.GetBytes(1u)));
        backend.PublishDescriptors(device);

        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(8, 0, 8));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.None,
            PipelineSync.ComputeShading, ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(layout, runtime, BitConverter.GetBytes(0u)));
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.SetPersistentParameterBindings(context, persistent);
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading,
            PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 8));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute),
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, new BufferRange(0, 8));
        mapped.Invalidate(new BufferRange(0, 8));
        Assert.Equal([0x3F800000u, 0x3F800000u], System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
            mapped.Bytes).ToArray());
    }

    [Fact]
    public void Duplicate_pipeline_static_sampler_location_is_rejected()
    {
        const string source = """
            Texture2D<float4> inputTexture;
            SamplerState pipelineSampler : register(s3, space2);
            RWStructuredBuffer<float4> outputValues;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[id.x] = inputTexture.SampleLevel(
                    pipelineSampler,
                    float2(0.5, 0.5),
                    0);
            }
            """;
        D3D12TestShaderEntry[] entries = [new("computeMain", SlangStage.Compute)];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "pipeline_static_sampler_duplicate_rejection",
            source,
            entries,
            "sm_6_0");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        GraphicsException failure = Assert.Throws<GraphicsException>(() =>
            backend.CreateComputePipeline(
                device,
                new ComputePipelineDesc(
                    shader.Program,
                    shader.GetEntryPoint(0),
                    StaticSamplers: new StaticSamplerBinding[]
                    {
                        StaticSampler(shader.Reflection, layout, 3, 2, new SamplerDesc(
                            FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                            AddressType.ClampToEdge, AddressType.ClampToEdge,
                            AddressType.ClampToEdge)),
                        StaticSampler(shader.Reflection, layout, 3, 2, new SamplerDesc(
                            FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                            AddressType.ClampToEdge, AddressType.ClampToEdge,
                            AddressType.ClampToEdge)),
                    })));

        Assert.Equal(GraphicsError.PipelineCreation, failure.Error);
        Assert.Contains("declared more than once", failure.Message);
    }

    [Fact]
    public void Pipeline_static_sampler_state_participates_in_pipeline_identity()
    {
        const string source = """
            Texture2D<float4> inputTexture;
            SamplerState pipelineSampler : register(s0);
            RWStructuredBuffer<float4> outputValues;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[id.x] = inputTexture.SampleLevel(
                    pipelineSampler, float2(0.5, 0.5), 0);
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "pipeline_static_sampler_identity",
            source,
            [new("computeMain", SlangStage.Compute)],
            "sm_6_0");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        SamplerDesc point = new(
            FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
            AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge);
        SamplerDesc linear = point with
        {
            MinFilter = FilterType.Linear,
            MagFilter = FilterType.Linear,
            MipFilter = FilterType.Linear,
        };
        using PipelineCache cache = backend.CreatePipelineCache(device, default);

        using Pipeline first = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(
                shader.Program,
                shader.GetEntryPoint(0),
                StaticSamplers: new StaticSamplerBinding[] { StaticSampler(shader.Reflection, layout, 0, 0, point) }),
            cache);
        using Pipeline second = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(
                shader.Program,
                shader.GetEntryPoint(0),
                StaticSamplers: new StaticSamplerBinding[] { StaticSampler(shader.Reflection, layout, 0, 0, linear) }));

        byte[] serializedCache = ReadPipelineCache(backend, cache);
        using PipelineCache reloaded = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(serializedCache));
        using Pipeline replayed = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(
                shader.Program,
                shader.GetEntryPoint(0),
                StaticSamplers: new StaticSamplerBinding[] { StaticSampler(shader.Reflection, layout, 0, 0, point) }),
            reloaded);
        Assert.Equal(D3D12Backend.GetSerializedRootSignature(first),
            D3D12Backend.GetSerializedRootSignature(replayed));
        Assert.Equal(serializedCache, ReadPipelineCache(backend, reloaded));
    }

    [Fact]
    public void Unbounded_sampler_range_cannot_be_made_static()
    {
        const string source = """
            Texture2D<float4> inputTexture;
            SamplerState bindlessSamplers[] : register(s0, space1);
            RWStructuredBuffer<float4> outputValues;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[id.x] = inputTexture.SampleLevel(
                    bindlessSamplers[id.x], float2(0.5, 0.5), 0);
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "pipeline_static_sampler_unbounded_rejection",
            source,
            [new("computeMain", SlangStage.Compute)],
            "sm_6_0");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection reflected = GetDataLayout(layout);
        nint samplerRange = Enumerable.Range(
                0,
                checked((int)reflected.BindingRangeCount))
            .Select(static index => (nint)index)
            .Single(index =>
                (reflected.GetBindingRangeType(index) & SlangBindingType.BaseMask) ==
                    SlangBindingType.Sampler);
        Assert.Equal(
            Slang.UnboundedSize,
            unchecked((nuint)reflected.GetBindingRangeBindingCount(samplerRange)));
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        SamplerDesc sampler = new(
            FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
            AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge);

        VariableReflection samplerDeclaration =
            reflected.GetBindingRangeLeafVariable(samplerRange);
        GraphicsException failure = Assert.Throws<GraphicsException>(() =>
            backend.CreateComputePipeline(
                device,
                new ComputePipelineDesc(
                    shader.Program,
                    shader.GetEntryPoint(0),
                    StaticSamplers: new StaticSamplerBinding[]
                    {
                        new(samplerDeclaration, sampler),
                    })));

        Assert.Equal(GraphicsError.PipelineCreation, failure.Error);
        Assert.Contains("one scalar Slang sampler", failure.Message);
    }

    [Fact]
    public void Bounded_sampler_array_cursor_is_rejected_before_d3d12_pipeline_creation()
    {
        const string source = """
            Texture2D<float4> inputTexture;
            SamplerState pipelineSamplers[3] : register(s4, space1);
            RWStructuredBuffer<float4> outputValues;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[id.x] = inputTexture.SampleLevel(
                    pipelineSamplers[id.x % 3], float2(0.5, 0.5), 0);
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "pipeline_static_sampler_bounded_array",
            source,
            [new("computeMain", SlangStage.Compute)],
            "sm_6_0");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        SamplerDesc sampler = new(
            FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
            AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge);

        TypeLayoutReflection reflected = GetDataLayout(layout);
        nint samplerRange = Enumerable.Range(0, checked((int)reflected.BindingRangeCount))
            .Select(static index => (nint)index)
            .Single(index => (reflected.GetBindingRangeType(index) &
                SlangBindingType.BaseMask) == SlangBindingType.Sampler);
        VariableReflection samplerDeclaration =
            reflected.GetBindingRangeLeafVariable(samplerRange);
        GraphicsException failure = Assert.Throws<GraphicsException>(() =>
            backend.CreateComputePipeline(
                device,
                new ComputePipelineDesc(
                    shader.Program,
                    shader.GetEntryPoint(0),
                    StaticSamplers: new StaticSamplerBinding[]
                    {
                        new(samplerDeclaration, sampler),
                    })));
        Assert.Equal(GraphicsError.PipelineCreation, failure.Error);
        Assert.Contains("only one scalar Slang sampler", failure.Message);
    }

    [Fact]
    public void Separate_sampler_ranges_can_make_the_middle_location_static()
    {
        const string source = """
            Texture2D<float4> inputTexture;
            SamplerState firstSampler : register(s4, space1);
            SamplerState middleSampler : register(s5, space1);
            SamplerState lastSampler : register(s6, space1);
            RWStructuredBuffer<float4> outputValues;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[id.x] =
                    inputTexture.SampleLevel(firstSampler, float2(0.25, 0.25), 0) +
                    inputTexture.SampleLevel(middleSampler, float2(0.5, 0.5), 0) +
                    inputTexture.SampleLevel(lastSampler, float2(0.75, 0.75), 0);
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "pipeline_static_sampler_separate_ranges",
            source,
            [new("computeMain", SlangStage.Compute)],
            "sm_6_0");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        SamplerDesc sampler = new(
            FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
            AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge);

        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(
                shader.Program,
                shader.GetEntryPoint(0),
                StaticSamplers: new StaticSamplerBinding[] { StaticSampler(shader.Reflection, layout, 5, 1, sampler) }));

        StaticSamplerDesc[] native = D3D12Backend.GetCompiledStaticSamplers(pipeline);
        Assert.Equal([5u], native.Select(static value => value.ShaderRegister));
        Assert.All(native, static value => Assert.Equal(1u, value.RegisterSpace));
    }

    [Fact]
    public void Nonrepresentable_static_sampler_border_color_is_rejected()
    {
        const string source = """
            Texture2D<float4> inputTexture;
            SamplerState pipelineSampler : register(s0);
            RWStructuredBuffer<float4> outputValues;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[id.x] = inputTexture.SampleLevel(
                    pipelineSampler, float2(0.5, 0.5), 0);
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "pipeline_static_sampler_border_rejection",
            source,
            [new("computeMain", SlangStage.Compute)],
            "sm_6_0");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        SamplerDesc sampler = new(
            FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
            AddressType.ClampToBorder, AddressType.ClampToBorder, AddressType.ClampToBorder,
            BorderColor: new System.Numerics.Vector4(0.25f));

        GraphicsException failure = Assert.Throws<GraphicsException>(() =>
            backend.CreateComputePipeline(
                device,
                new ComputePipelineDesc(
                    shader.Program,
                    shader.GetEntryPoint(0),
                    StaticSamplers: new StaticSamplerBinding[] { StaticSampler(shader.Reflection, layout, 0, 0, sampler) })));

        Assert.Equal(GraphicsError.PipelineCreation, failure.Error);
        Assert.Contains("border color", failure.Message);
    }

    [Fact]
    public void Global_static_sampler_visibility_is_all_without_physical_location_identity_matching()
    {
        const string source = """
            Texture2D<float4> inputTexture;
            SamplerState vertexSampler : register(s0);
            SamplerState pixelSampler : register(s1);

            struct VertexOutput { float4 position : SV_Position; };
            [shader("vertex")]
            VertexOutput vertexMain(uint id : SV_VertexID)
            {
                VertexOutput result;
                float offset = inputTexture.SampleLevel(
                    vertexSampler, float2(0.5, 0.5), 0).x * 0.000001;
                result.position = float4(id == 1 ? 0 : (id == 2 ? 1 : -1) + offset,
                    id == 1 ? 1 : -1, 0, 1);
                return result;
            }
            [shader("fragment")]
            float4 pixelMain(VertexOutput input) : SV_Target0
            {
                return inputTexture.SampleLevel(pixelSampler, float2(0.5, 0.5), 0);
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "pipeline_static_sampler_graphics_visibility",
            source,
            [new("vertexMain", SlangStage.Vertex), new("pixelMain", SlangStage.Fragment)],
            "sm_6_0");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        SamplerDesc sampler = new(
            FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
            AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge);
        Format[] formats = [Format.R8G8B8A8UNorm];
        BlendAttachmentState[] blends = [new(Enabled: false, WriteMask: ColorWriteMasks.All)];

        using Pipeline pipeline = backend.CreateGraphicsPipeline(
            device,
            new GraphicsPipelineDesc(
                shader.Program,
                shader.GetEntryPoint(0),
                shader.GetEntryPoint(1),
                [],
                [],
                PrimitiveTopology.TriangleList,
                StripCut.Disabled,
                new RasterizerState(Cull: CullType.None),
                new MultisampleState(SampleCount: 1),
                new DepthStencilState(),
                new BlendState(blends),
                new AttachmentFormatSignature(formats, null),
                staticSamplers: [StaticSampler(shader.Reflection, layout, 0, 0, sampler), StaticSampler(shader.Reflection, layout, 1, 0, sampler)]));

        StaticSamplerDesc[] native = D3D12Backend.GetCompiledStaticSamplers(pipeline);
        Assert.All(native,
            static sampler => Assert.Equal(ShaderVisibility.All, sampler.ShaderVisibility));
    }

    [Fact]
    public void Entry_parameter_static_samplers_follow_raw_cumulative_offsets_and_stage_identity()
    {
        const string source = """
            struct VertexOutput { float4 position : SV_Position; };
            [shader("vertex")]
            VertexOutput vertexMain(
                uniform SamplerState stageSampler : register(s0),
                uint id : SV_VertexID)
            {
                VertexOutput result;
                result.position = float4(id == 1 ? 0 : (id == 2 ? 1 : -1),
                    id == 1 ? 1 : -1, 0, 1);
                return result;
            }
            [shader("fragment")]
            float4 pixelMain(
                uniform SamplerState stageSampler : register(s0),
                VertexOutput input) : SV_Target0
            {
                return float4(1, 1, 1, 1);
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "entry_parameter_static_sampler_identity", source,
            [new("vertexMain", SlangStage.Vertex), new("pixelMain", SlangStage.Fragment)],
            "sm_6_0");
        VariableLayoutReflection vertexLayout = shader.GetEntryPoint(0).VarLayout;
        VariableLayoutReflection pixelLayout = shader.GetEntryPoint(1).VarLayout;
        Assert.NotEqual(vertexLayout, pixelLayout);
        nint vertexSampler = FindSamplerRange(vertexLayout);
        nint pixelSampler = FindSamplerRange(pixelLayout);
        VariableReflection vertexSamplerDeclaration =
            GetDataLayout(vertexLayout).GetBindingRangeLeafVariable(vertexSampler);
        VariableReflection pixelSamplerDeclaration =
            GetDataLayout(pixelLayout).GetBindingRangeLeafVariable(pixelSampler);
        uint vertexRawOffset = GetSamplerRegister(vertexLayout, vertexSampler);
        uint pixelRawOffset = GetSamplerRegister(pixelLayout, pixelSampler);
        Assert.Equal(0u, vertexRawOffset);
        Assert.Equal(1u, pixelRawOffset);
        SamplerDesc state = new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
            AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge);
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Format[] formats = [Format.R8G8B8A8UNorm];
        BlendAttachmentState[] blends = [new(Enabled: false, WriteMask: ColorWriteMasks.All)];
        using Pipeline pipeline = backend.CreateGraphicsPipeline(device,
            new GraphicsPipelineDesc(shader.Program, shader.GetEntryPoint(0),
                shader.GetEntryPoint(1), [], [], PrimitiveTopology.TriangleList,
                StripCut.Disabled, new RasterizerState(), new MultisampleState(1),
                new DepthStencilState(), new BlendState(blends),
                new AttachmentFormatSignature(formats, null),
                staticSamplers:
                [
                    new StaticSamplerBinding(vertexSamplerDeclaration, state),
                    new StaticSamplerBinding(pixelSamplerDeclaration, state),
                ]));
        StaticSamplerDesc[] native = D3D12Backend.GetCompiledStaticSamplers(pipeline);
        Assert.Equal(2, native.Length);
        Assert.Equal([vertexRawOffset, pixelRawOffset],
            native.Select(static sampler => sampler.ShaderRegister));
        Assert.Contains(native,
            static sampler => sampler.ShaderVisibility == ShaderVisibility.Vertex);
        Assert.Contains(native,
            static sampler => sampler.ShaderVisibility == ShaderVisibility.Pixel);

        static nint FindSamplerRange(VariableLayoutReflection layout)
        {
            TypeLayoutReflection data = GetDataLayout(layout);
            return Enumerable.Range(0, checked((int)data.BindingRangeCount))
                .Select(static index => (nint)index)
                .Single(index => (data.GetBindingRangeType(index) & SlangBindingType.BaseMask) ==
                    SlangBindingType.Sampler);
        }

        static uint GetSamplerRegister(
            VariableLayoutReflection layout,
            nint bindingRange)
        {
            TypeLayoutReflection data = GetDataLayout(layout);
            nint set = data.GetBindingRangeDescriptorSetIndex(bindingRange);
            nint descriptorRange = data.GetBindingRangeFirstDescriptorRangeIndex(bindingRange);
            return checked((uint)(
                layout.GetOffset(SlangParameterCategory.SamplerState) +
                unchecked((nuint)data.GetDescriptorSetDescriptorRangeIndexOffset(
                    set,
                    descriptorRange))));
        }
    }

    [Fact]
    public void Slang_parameter_layout_exposes_canonical_binding_range_order()
    {
        const string source = """
            Texture2D<float4> inputTextures[2];
            SamplerState inputSampler;
            RWStructuredBuffer<float4> outputValues;
            float multiplier;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[id.x] = inputTextures[id.x & 1]
                    .SampleLevel(inputSampler, float2(0.5, 0.5), 0) * multiplier;
            }
            """;
        D3D12TestShaderEntry[] entries = [new("computeMain", SlangStage.Compute)];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "slang_parameter_layout_authority",
            source,
            entries);
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection reflected = GetDataLayout(layout);

        Assert.Equal(4u, checked((uint)reflected.GetSize(SlangParameterCategory.Uniform)));
        Assert.Equal((nint)3, reflected.BindingRangeCount);
        Assert.Equal(SlangBindingType.Texture, reflected.GetBindingRangeType(0));
        Assert.Equal((nint)2, reflected.GetBindingRangeBindingCount(0));
        Assert.Equal(SlangBindingType.Sampler, reflected.GetBindingRangeType(1));
        Assert.Equal((nint)1, reflected.GetBindingRangeBindingCount(1));
        Assert.Equal(SlangBindingType.MutableRawBuffer, reflected.GetBindingRangeType(2));
        Assert.Equal((nint)1, reflected.GetBindingRangeBindingCount(2));
        Assert.Equal(4, CreateNullBindings(layout).Length);
    }

    [Fact]
    public void Throwing_diagnostic_sink_cannot_interrupt_backend_teardown()
    {
        D3D12ValidationOptions validation = new(
            DisableGpuBasedValidation: true,
            DisableSynchronizedQueueValidation: true);
        var direct = new D3D12Backend(new D3D12BackendOptions(validation));
        var backend = new ValidationLayer(
            direct,
            new ValidationOptions(new ThrowingValidationMessageSink()));
        Device device = D3D12TestSupport.CreateWarpDevice(backend);

        Assert.Null(Record.Exception(backend.Dispose));
        Assert.Equal(DeviceStatus.Disposed, device.Status);
        Assert.Null(Record.Exception(backend.Dispose));

        device.Dispose();
    }

    [Fact]
    public void Validated_and_direct_receivers_produce_the_same_native_copy_result()
    {
        Assert.True(OperatingSystem.IsWindows());
        byte[] source = Enumerable.Range(0, 769)
            .Select(static value => unchecked((byte)(value * 37 + 11)))
            .ToArray();

        byte[] direct;
        using (IGraphicsBackend backend = new D3D12Backend())
            direct = D3D12TestSupport.ExecuteCopyChain(backend, source);

        byte[] validated;
        using (var backend = new ValidationLayer(new D3D12Backend()))
        using (Device device = D3D12TestSupport.CreateWarpDevice(backend))
        {
            Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
            Assert.NotNull(diagnostics);
            if (!diagnostics.DebugLayerEnabled)
            {
                Assert.False(diagnostics.GpuBasedValidationEnabled);
                Assert.False(diagnostics.SynchronizedQueueValidationEnabled);
            }
            validated = D3D12TestSupport.ExecuteCopyChain(backend, device, source);
        }

        Assert.Equal(source, direct);
        Assert.Equal(direct, validated);
    }

    [Fact]
    public void Foreign_layer_resource_is_rejected_and_reported_before_command_forwarding()
    {
        var messages = new List<ValidationMessage>();
        using var validated = CreateFastLayer(messages);
        using IGraphicsBackend foreignBackend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(validated);
        using Device foreignDevice = D3D12TestSupport.CreateWarpDevice(foreignBackend);
        using Buffer destination = validated.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using Buffer foreignSource = foreignBackend.CreateBuffer(
            foreignDevice,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        using CommandContext context = validated.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));

        validated.Begin(context);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            validated.CopyBuffer(
                context,
                new BufferCopy(foreignSource, 0, destination, 0, 64)));
        validated.Discard(context);

        Assert.Contains("Validation Layer", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            messages,
            static message => message.Type == ValidationMessageType.Error &&
                              message.Area == "Ownership");
    }

    [Fact]
    public void Resource_from_another_device_in_the_same_layer_is_rejected_before_forwarding()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device firstDevice = D3D12TestSupport.CreateWarpDevice(backend);
        using Device secondDevice = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer first = backend.CreateBuffer(
            firstDevice,
            new BufferDesc(64, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using Buffer second = backend.CreateBuffer(
            secondDevice,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        using CommandContext context = backend.CreateCommandContext(
            firstDevice,
            new CommandContextDesc(QueueType.Copy, 0, 1));

        backend.Begin(context);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            backend.CopyBuffer(context, new BufferCopy(second, 0, first, 0, 64)));
        backend.Discard(context);

        Assert.Contains("another Device", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            messages,
            static message => message.Area == "Ownership" &&
                              message.Type == ValidationMessageType.Error);
    }

    [Fact]
    public void Query_lifecycle_rejects_reuse_before_resolve_and_remains_recordable()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using QueryPool pool = backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 1, Label: "timestamp pool"));
        using Buffer destination = backend.CreateBuffer(
            device,
            new BufferDesc(pool.ResultInfo.ResultStride, BufferUsages.QueryResolve),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        backend.WriteTimestamp(context, pool, 0);
        Assert.Throws<InvalidOperationException>(() => backend.WriteTimestamp(context, pool, 0));
        backend.ResolveQueries(
            context,
            pool,
            0,
            1,
            destination,
            new BufferRange(0, pool.ResultInfo.ResultStride));
        using RecordedCommands recorded = backend.End(context);
        RecordedCommands[] commands = [recorded];
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));

        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        Assert.Contains(
            messages,
            static message => message.Area == "Queries" &&
                              message.Type == ValidationMessageType.Error);
    }

    [Fact]
    public void Barrier_history_rejects_an_incorrect_local_Before_state_without_forwarding_it()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                256,
                BufferUsages.CopySource | BufferUsages.CopyDestination,
                "local barrier history"));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 2));

        backend.Begin(context);
        backend.Barrier(context, new BufferBarrier(
            buffer,
            PipelineSync.None,
            PipelineSync.Copy,
            ResourceAccess.NoAccess,
            ResourceAccess.CopyDestination));
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            backend.Barrier(context, new BufferBarrier(
                buffer,
                PipelineSync.None,
                PipelineSync.Copy,
                ResourceAccess.NoAccess,
                ResourceAccess.CopySource)));
        backend.Discard(context);

        Assert.Contains("Incorrect Before state", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            messages,
            static message => message.Area == "Barriers" &&
                              message.Type == ValidationMessageType.Error &&
                              message.Text.Contains("Tracked state", StringComparison.Ordinal));
    }

    [Fact]
    public void Split_barrier_requires_an_exact_End_and_commits_the_after_state()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                256,
                BufferUsages.CopySource | BufferUsages.CopyDestination,
                "split barrier"));
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        BufferBarrier split = new(
            buffer,
            PipelineSync.None,
            PipelineSync.Copy,
            ResourceAccess.NoAccess,
            ResourceAccess.CopyDestination,
            BarrierPhase.Begin);

        using (CommandContext context = backend.CreateCommandContext(
                   device,
                   new CommandContextDesc(QueueType.Copy, 0, 2)))
        {
            backend.Begin(context);
            backend.Barrier(context, split);
            InvalidOperationException mismatch = Assert.Throws<InvalidOperationException>(() =>
                backend.Barrier(context, split with
                {
                    AccessAfter = ResourceAccess.CopySource,
                    Phase = BarrierPhase.End,
                }));
            Assert.Contains("exact Begin transition", mismatch.Message, StringComparison.Ordinal);
            backend.Barrier(context, split with { Phase = BarrierPhase.End });
            using RecordedCommands commands = backend.End(context);
            SubmitAndWait(backend, queue, commands);
        }

        using (CommandContext context = backend.CreateCommandContext(
                   device,
                   new CommandContextDesc(QueueType.Copy, 0, 1)))
        {
            backend.Begin(context);
            backend.Barrier(context, new BufferBarrier(
                buffer,
                PipelineSync.Copy,
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                ResourceAccess.CopySource));
            using RecordedCommands commands = backend.End(context);
            SubmitAndWait(backend, queue, commands);
        }

        Assert.Contains(
            messages,
            static message => message.Area == "Barriers" &&
                              message.Text.Contains("exact Begin transition", StringComparison.Ordinal));
    }

    [Fact]
    public void Split_barrier_End_without_Begin_is_rejected_at_submission()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopyDestination, "orphan split end"));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));

        backend.Begin(context);
        backend.Barrier(context, new BufferBarrier(
            buffer,
            PipelineSync.None,
            PipelineSync.Copy,
            ResourceAccess.NoAccess,
            ResourceAccess.CopyDestination,
            BarrierPhase.End));
        using RecordedCommands commands = backend.End(context);
        RecordedCommands[] submitted = [commands];
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            backend.Submit(
                backend.GetQueue(device, QueueType.Copy),
                new QueueSubmitDesc([], [], submitted, [], [])));

        Assert.Contains("no matching submitted Begin", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Barrier_history_commits_only_at_Submit_and_preserves_a_valid_transition_chain()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                256,
                BufferUsages.CopySource | BufferUsages.CopyDestination,
                "submitted barrier history"));
        Queue queue = backend.GetQueue(device, QueueType.Copy);

        using (CommandContext rejectedContext = backend.CreateCommandContext(
                   device,
                   new CommandContextDesc(QueueType.Copy, 0, 1)))
        {
            backend.Begin(rejectedContext);
            backend.Barrier(rejectedContext, new BufferBarrier(
                buffer,
                PipelineSync.Copy,
                PipelineSync.Copy,
                ResourceAccess.CopySource,
                ResourceAccess.CopyDestination));
            using RecordedCommands rejected = backend.End(rejectedContext);
            RecordedCommands[] rejectedCommands = [rejected];
            Assert.Throws<InvalidOperationException>(() => backend.Submit(
                queue,
                new QueueSubmitDesc([], [], rejectedCommands, [], [])));
            Assert.Equal(RecordedCommandsStatus.Executable, rejected.Status);
        }

        using (CommandContext firstContext = backend.CreateCommandContext(
                   device,
                   new CommandContextDesc(QueueType.Copy, 0, 2)))
        {
            backend.Begin(firstContext);
            backend.Barrier(firstContext, new BufferBarrier(
                buffer,
                PipelineSync.None,
                PipelineSync.Copy,
                ResourceAccess.NoAccess,
                ResourceAccess.CopyDestination));
            backend.Barrier(firstContext, new BufferBarrier(
                buffer,
                PipelineSync.Copy,
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                ResourceAccess.CopySource));
            using RecordedCommands first = backend.End(firstContext);
            SubmitAndWait(backend, queue, first);
        }

        using (CommandContext secondContext = backend.CreateCommandContext(
                   device,
                   new CommandContextDesc(QueueType.Copy, 0, 1)))
        {
            backend.Begin(secondContext);
            backend.Barrier(secondContext, new BufferBarrier(
                buffer,
                PipelineSync.Copy,
                PipelineSync.Copy,
                ResourceAccess.CopySource,
                ResourceAccess.CopyDestination));
            using RecordedCommands second = backend.End(secondContext);
            SubmitAndWait(backend, queue, second);
        }

        Assert.Contains(
            messages,
            static message => message.Area == "Barriers" &&
                              message.Text.Contains("Incorrect Before state", StringComparison.Ordinal));
    }

    [Fact]
    public void Queue_handoff_requires_an_exact_acquire_and_an_explicit_matching_wait()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                256,
                BufferUsages.CopySource | BufferUsages.CopyDestination,
                "queue handoff"));
        Queue graphicsQueue = backend.GetQueue(device, QueueType.Graphics);
        Queue copyQueue = backend.GetQueue(device, QueueType.Copy);

        QueueCompletion releaseCompletion;
        using (CommandContext releaseContext = backend.CreateCommandContext(
                   device,
                   new CommandContextDesc(QueueType.Graphics, 0, 2)))
        {
            backend.Begin(releaseContext);
            backend.Barrier(releaseContext, new BufferBarrier(
                buffer,
                PipelineSync.None,
                PipelineSync.Copy,
                ResourceAccess.NoAccess,
                ResourceAccess.CopySource));
            backend.Barrier(releaseContext, new QueueRelease(
                buffer,
                null,
                PipelineSync.Copy,
                ResourceAccess.CopySource,
                null,
                QueueType.Copy));
            using RecordedCommands release = backend.End(releaseContext);
            RecordedCommands[] commands = [release];
            releaseCompletion = backend.Submit(
                graphicsQueue,
                new QueueSubmitDesc([], [], commands, [], []));
        }

        using CommandContext acquireContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        backend.Begin(acquireContext);
        backend.Barrier(acquireContext, new QueueAcquire(
            buffer,
            null,
            QueueType.Graphics,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            null));
        using RecordedCommands acquire = backend.End(acquireContext);
        RecordedCommands[] acquireCommands = [acquire];

        InvalidOperationException missingWait = Assert.Throws<InvalidOperationException>(() =>
            backend.Submit(
                copyQueue,
                new QueueSubmitDesc([], [], acquireCommands, [], [])));
        Assert.Contains("missing", missingWait.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RecordedCommandsStatus.Executable, acquire.Status);

        QueueCompletion[] waits = [releaseCompletion];
        QueueCompletion completion = backend.Submit(
            copyQueue,
            new QueueSubmitDesc(waits, [], acquireCommands, [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        Assert.Contains(
            messages,
            static message => message.Area == "Barriers" &&
                              message.Text.Contains(
                                  "QueueCompletion or ExternalTimeline wait",
                                  StringComparison.Ordinal));
    }

    [Fact]
    public void Queue_handoff_accepts_the_ExternalTimeline_signal_wait_pair_named_by_the_source_Submit()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using ExternalTimeline timeline = backend.CreateExternalTimeline(
            device,
            0,
            "handoff timeline");
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                256,
                BufferUsages.CopySource | BufferUsages.CopyDestination,
                "timeline handoff"));
        Queue graphicsQueue = backend.GetQueue(device, QueueType.Graphics);
        Queue copyQueue = backend.GetQueue(device, QueueType.Copy);

        using (CommandContext releaseContext = backend.CreateCommandContext(
                   device,
                   new CommandContextDesc(QueueType.Graphics, 0, 2)))
        {
            backend.Begin(releaseContext);
            backend.Barrier(releaseContext, new BufferBarrier(
                buffer,
                PipelineSync.None,
                PipelineSync.Copy,
                ResourceAccess.NoAccess,
                ResourceAccess.CopySource));
            backend.Barrier(releaseContext, new QueueRelease(
                buffer,
                null,
                PipelineSync.Copy,
                ResourceAccess.CopySource,
                null,
                QueueType.Copy));
            using RecordedCommands release = backend.End(releaseContext);
            RecordedCommands[] commands = [release];
            TimelineSignal[] signals = [new(timeline, 3)];
            _ = backend.Submit(
                graphicsQueue,
                new QueueSubmitDesc([], [], commands, [], signals));
        }

        using CommandContext acquireContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        backend.Begin(acquireContext);
        backend.Barrier(acquireContext, new QueueAcquire(
            buffer,
            null,
            QueueType.Graphics,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            null));
        using RecordedCommands acquire = backend.End(acquireContext);
        RecordedCommands[] acquireCommands = [acquire];
        TimelinePoint[] waits = [new(timeline, 3)];
        QueueCompletion completion = backend.Submit(
            copyQueue,
            new QueueSubmitDesc([], waits, acquireCommands, [], []));

        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(
            messages,
            static message => message.Area == "Barriers" &&
                              message.Type == ValidationMessageType.Error);
    }

    [Fact]
    public void Queue_ownership_rejects_use_on_another_Queue_without_release_and_acquire()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                256,
                BufferUsages.CopySource | BufferUsages.CopyDestination,
                "queue ownership"));
        Queue graphicsQueue = backend.GetQueue(device, QueueType.Graphics);
        Queue copyQueue = backend.GetQueue(device, QueueType.Copy);

        using (CommandContext graphicsContext = backend.CreateCommandContext(
                   device,
                   new CommandContextDesc(QueueType.Graphics, 0, 1)))
        {
            backend.Begin(graphicsContext);
            backend.Barrier(graphicsContext, new BufferBarrier(
                buffer,
                PipelineSync.None,
                PipelineSync.Copy,
                ResourceAccess.NoAccess,
                ResourceAccess.CopySource));
            using RecordedCommands commands = backend.End(graphicsContext);
            SubmitAndWait(backend, graphicsQueue, commands);
        }

        using CommandContext copyContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        backend.Begin(copyContext);
        backend.Barrier(copyContext, new BufferBarrier(
            buffer,
            PipelineSync.Copy,
            PipelineSync.Copy,
            ResourceAccess.CopySource,
            ResourceAccess.CopyDestination));
        using RecordedCommands copyCommandsValue = backend.End(copyContext);
        RecordedCommands[] copyCommands = [copyCommandsValue];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            backend.Submit(
                copyQueue,
                new QueueSubmitDesc([], [], copyCommands, [], [])));
        Assert.Contains("another Queue", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            messages,
            static message => message.Area == "Barriers" &&
                              message.Text.Contains("QueueRelease", StringComparison.Ordinal));
    }

    [Fact]
    public void Recorded_commands_retain_a_dependency_disposed_before_Queue_acceptance()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Buffer source = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopySource, "recorded source"),
            MemoryType.Upload);
        using Buffer destination = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopyDestination, "manual destination"),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));

        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(source, 0, destination, 0, 64));
        using RecordedCommands commands = backend.End(context);
        source.Dispose();
        RecordedCommands[] commandSpan = [commands];

        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Copy),
            new QueueSubmitDesc([], [], commandSpan, [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
        Assert.DoesNotContain(
            messages,
            static message => message.Area == "Retirement" &&
                              message.Type == ValidationMessageType.Error);
    }

    [Fact]
    public void Validated_receiver_has_one_idempotent_owning_root()
    {
        var messages = new List<ValidationMessage>();
        var layer = CreateFastLayer(messages);
        AdapterInfo adapter = SelectWarp(layer);
        DeviceQueueDesc[] queues = [new(QueueType.Copy)];
        Device device = layer.CreateDevice(new DeviceDesc(
            adapter.Id,
            queues,
            label: "validated generic owner"));

        layer.Dispose();
        layer.Dispose();
        device.Dispose();

        Assert.Equal(DeviceStatus.Disposed, device.Status);
        Assert.Contains(
            messages,
            static message => message.Area == "Lifetime" &&
                              message.Type == ValidationMessageType.Warning);
    }

    [Fact]
    public void Command_family_scope_pipeline_and_event_misuse_stays_in_the_validation_layer()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using CommandContext copy = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        using CommandContext graphics = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(copy);
        Assert.Throws<InvalidOperationException>(() =>
            backend.SetViewports(copy, [new Viewport(0, 0, 1, 1)]));
        Assert.Throws<InvalidOperationException>(() => backend.EndEvent(copy));
        backend.Discard(copy);

        backend.Begin(graphics);
        Assert.Throws<InvalidOperationException>(() =>
            backend.Draw(graphics, new DrawArguments(3, 1, 0, 0)));
        Assert.Throws<InvalidOperationException>(() =>
            backend.Dispatch(graphics, new DispatchArguments(1, 1, 1)));
        Assert.Throws<InvalidOperationException>(() =>
            backend.BeginRendering(graphics, new RenderingDesc([], null, 1, 1)));
        backend.Discard(graphics);

        Assert.True(
            messages.Count(static message =>
                message.Area == "Commands" &&
                message.Type == ValidationMessageType.Error) >= 5);
    }

    [Fact]
    public void Sampler_feedback_mip_region_contract_is_rejected_before_native_creation()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out SamplerFeedback? capability));
        Assert.NotNull(capability);
        Assert.True(capability.SupportedFormats.Contains(Format.R8G8B8A8UNorm));
        using Texture sampled = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                32,
                32,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled));

        Assert.Throws<InvalidOperationException>(() =>
            backend.CreateSamplerFeedbackTexture(
                device,
                new SamplerFeedbackTextureDesc(
                    sampled,
                    SamplerFeedbackType.MinimumMip,
                    32,
                    4)));

        Assert.Contains(
            messages,
            static message => message.Area == "SamplerFeedback" &&
                              message.Type == ValidationMessageType.Error);
    }

    [Fact]
    public void Unavailable_or_incompatible_shading_rate_image_is_rejected_before_forwarding()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out VariableRateShading? capability));
        Assert.NotNull(capability);
        using Texture invalidImage = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                1,
                1,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        Assert.Throws<InvalidOperationException>(() =>
            backend.SetShadingRateImage(context, invalidImage));
        backend.Discard(context);

        Assert.Contains(
            messages,
            static message => message.Area == "VariableRateShading" &&
                              message.Type == ValidationMessageType.Error);
    }

    [Fact]
    public void Parameter_binding_shape_usage_and_pipeline_compatibility_are_diagnosed_before_forwarding()
    {
        const string bindingSource = """
            struct Constants { float scale; };
            ConstantBuffer<Constants> constants;
            Texture2D<float4> inputTextures[2];
            SamplerState inputSampler;
            RWStructuredBuffer<float4> outputValues;
            float multiplier;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[id.x] = inputTextures[id.x & 1]
                    .SampleLevel(inputSampler, float2(0.5, 0.5), 0)
                    * constants.scale * multiplier;
            }
            """;
        const string otherSource = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
            }
            """;
        D3D12TestShaderEntry[] entries = [new("computeMain", SlangStage.Compute)];
        using D3D12TestShaderProgram bindingShader = D3D12TestShaderProgram.Compile(
            "rhi_validation_binding_contract",
            bindingSource,
            entries);
        using D3D12TestShaderProgram otherShader = D3D12TestShaderProgram.Compile(
            "rhi_validation_other_contract",
            otherSource,
            entries);
        VariableLayoutReflection layout =
            bindingShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        Assert.NotEqual(VariableLayoutReflection.Null, layout);
        TypeLayoutReflection reflectedLayout = GetDataLayout(layout);
        ResourceBinding[] validResources = CreateNullBindings(layout);
        uint ordinaryDataSize = checked((uint)reflectedLayout.GetSize(
            SlangParameterCategory.Uniform));
        Assert.True(validResources.Length >= 4);
        Assert.Contains(validResources,
            static binding => binding.Type == ResourceBindingType.ConstantBuffer);
        Assert.True(ordinaryDataSize > 0);
        Assert.Contains(
            Enumerable.Range(0, checked((int)reflectedLayout.BindingRangeCount)),
            index => reflectedLayout.GetBindingRangeBindingCount(index) >= 2);

        byte[] validOrdinaryData =
            new byte[checked((int)ordinaryDataSize)];
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Sampler concreteSampler = backend.CreateSampler(
            device,
            new SamplerDesc(
                FilterType.Nearest,
                FilterType.Nearest,
                FilterType.Nearest,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge));
        for (int index = 0; index < validResources.Length; index++)
        {
            if (validResources[index].Type == ResourceBindingType.Sampler)
                validResources[index] = ResourceBinding.SampledWith(concreteSampler);
        }
        using Pipeline bindingPipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(bindingShader.Program, bindingShader.GetEntryPoint(0)));
        using Pipeline otherPipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(otherShader.Program, otherShader.GetEntryPoint(0)));

        byte[] wrongOrdinaryData = new byte[checked(validOrdinaryData.Length + 1)];
        Assert.Throws<InvalidOperationException>(() => CreateAndDisposeBindings(
            backend,
            device,
            bindingPipeline,
            layout,
            validResources,
            wrongOrdinaryData));

        ResourceBinding[] missingResource = validResources[..^1];
        Assert.Throws<InvalidOperationException>(() => CreateAndDisposeBindings(
            backend,
            device,
            bindingPipeline,
            layout,
            missingResource,
            validOrdinaryData));

        ResourceBinding[] wrongType = validResources.ToArray();
        int textureOrdinal = Array.FindIndex(wrongType,
            static binding => binding.Type == ResourceBindingType.TextureSrv);
        int bufferOrdinal = Array.FindIndex(wrongType,
            static binding => binding.Type == ResourceBindingType.BufferUav);
        Assert.True(textureOrdinal >= 0 && bufferOrdinal >= 0);
        (wrongType[textureOrdinal], wrongType[bufferOrdinal]) =
            (wrongType[bufferOrdinal], wrongType[textureOrdinal]);
        Assert.Throws<InvalidOperationException>(() => CreateAndDisposeBindings(
            backend,
            device,
            bindingPipeline,
            layout,
            wrongType,
            validOrdinaryData));

        using PersistentParameterBindings persistent = backend.CreatePersistentParameterBindings(
            device,
            bindingPipeline,
            new ParameterBlockBindings(layout, validResources, validOrdinaryData));
        Assert.False(persistent.IsDisposed);

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(8, 2, 8));
        backend.SetPipeline(context, otherPipeline);
        Assert.Throws<InvalidOperationException>(() => SetTransientBindings(
            backend,
            context,
            layout,
            validResources,
            validOrdinaryData));
        Assert.Throws<InvalidOperationException>(() =>
            backend.SetPersistentParameterBindings(context, persistent));
        backend.SetPipeline(context, bindingPipeline);
        Assert.Throws<InvalidOperationException>(() => SetTransientBindings(
            backend, context, layout, wrongType, validOrdinaryData));
        SetTransientBindings(
            backend,
            context,
            layout,
            validResources,
            validOrdinaryData);
        backend.SetPersistentParameterBindings(context, persistent);
        backend.Discard(context);

        Assert.True(
            messages.Count(static message =>
                message.Area == "Bindings" &&
                message.Type == ValidationMessageType.Error) >= 6);
    }

    [Fact]
    public void Acceleration_structure_commands_are_rejected_from_bundles_before_forwarding()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out RayTracing? capability));
        Assert.NotNull(capability);
        Assert.True(capability.Serialization);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Assert.NotNull(diagnostics);
        using CommandContext bundleContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1, Bundle: true));

        backend.Begin(bundleContext);
        InvalidOperationException buildError = Assert.Throws<InvalidOperationException>(() =>
            backend.BuildAccelerationStructure(
            bundleContext,
            new AccelerationStructureBuildDesc(
                AccelerationStructureType.BottomLevel,
                AccelerationStructureBuildOptions.None,
                [],
                null!,
                null!,
                default)));
        Assert.Contains("not legal in a bundle", buildError.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => backend.CopyAccelerationStructure(
            bundleContext,
            null!,
            null!,
            AccelerationStructureCopyType.Clone));
        Assert.Throws<InvalidOperationException>(() => backend.SerializeAccelerationStructure(
            bundleContext,
            default,
            null!));
        Assert.Throws<InvalidOperationException>(() => backend.DeserializeAccelerationStructure(
            bundleContext,
            null!,
            default));
        Assert.Throws<InvalidOperationException>(() => backend.EmitAccelerationStructurePostBuildInfo(
            bundleContext,
            null!,
            AccelerationStructurePostBuildInfoType.CurrentSize,
            null!,
            0));
        using RecordedBundle bundle = backend.EndBundle(bundleContext);

        Assert.Equal(
            5,
            messages.Count(static message =>
                message.Area == "Commands" &&
                message.Type == ValidationMessageType.Error &&
                message.Text.Contains("not legal in a bundle", StringComparison.Ordinal)));
    }

    [Fact]
    public void Warm_validation_dependency_reservation_is_allocation_free()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                256,
                BufferUsages.Index | BufferUsages.CopyDestination,
                "warm reservation buffer"));
        using QueryPool pool = backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 1));
        using Buffer queryDestination = backend.CreateBuffer(
            device,
            new BufferDesc(pool.ResultInfo.ResultStride, BufferUsages.QueryResolve),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        var binding = new IndexBufferBinding(buffer, 0, 256, IndexType.UInt16);

        static void RecordIteration(
            ValidationLayer layer,
            CommandContext recording,
            Buffer trackedBuffer,
            QueryPool queryPool,
            Buffer destination,
            in IndexBufferBinding indexBinding)
        {
            layer.SetIndexBuffer(recording, indexBinding);
            layer.Barrier(recording, new BufferBarrier(
                trackedBuffer,
                PipelineSync.None,
                PipelineSync.Copy,
                ResourceAccess.NoAccess,
                ResourceAccess.CopyDestination));
            layer.Barrier(recording, new BufferBarrier(
                trackedBuffer,
                PipelineSync.Copy,
                PipelineSync.None,
                ResourceAccess.CopyDestination,
                ResourceAccess.NoAccess));
            layer.WriteTimestamp(recording, queryPool, 0);
            layer.ResolveQueries(
                recording,
                queryPool,
                0,
                1,
                destination,
                new BufferRange(0, queryPool.ResultInfo.ResultStride));
        }

        backend.Begin(context);
        for (int index = 0; index < 256; index++)
            RecordIteration(backend, context, buffer, pool, queryDestination, binding);
        backend.Discard(context);

        backend.Begin(context);
        for (int index = 0; index < 16; index++)
            backend.SetIndexBuffer(context, binding);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 128; index++)
            RecordIteration(backend, context, buffer, pool, queryDestination, binding);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        backend.Discard(context);

        Assert.Equal(0, allocated);
    }
    private static ValidationLayer CreateFastLayer(
        List<ValidationMessage> messages)
    {
        D3D12ValidationOptions validation = new(
            DisableGpuBasedValidation: true,
            DisableSynchronizedQueueValidation: true);
        var backend = new D3D12Backend(new D3D12BackendOptions(validation));
        return new ValidationLayer(
            backend,
            new ValidationOptions(new DelegateValidationMessageSink(messages.Add)));
    }

    private static ResourceBinding[] CreateNullBindings(
        VariableLayoutReflection layout)
    {
        TypeLayoutReflection reflectedLayout = GetDataLayout(layout);
        var result = new List<ResourceBinding>();
        for (nint rangeIndex = 0;
             rangeIndex < reflectedLayout.BindingRangeCount;
             rangeIndex++)
        {
            if (reflectedLayout.GetBindingRangeDescriptorRangeCount(rangeIndex) == 0)
                continue;
            nint set = reflectedLayout.GetBindingRangeDescriptorSetIndex(rangeIndex);
            nint firstDescriptorRange =
                reflectedLayout.GetBindingRangeFirstDescriptorRangeIndex(rangeIndex);
            nint descriptorRangeCount =
                reflectedLayout.GetBindingRangeDescriptorRangeCount(rangeIndex);
            for (nint relativeRange = 0;
                 relativeRange < descriptorRangeCount;
                 relativeRange++)
            {
                nint descriptorRange = firstDescriptorRange + relativeRange;
                SlangBindingType type =
                    reflectedLayout.GetDescriptorSetDescriptorRangeType(
                        set, descriptorRange);
                nint count =
                    reflectedLayout.GetDescriptorSetDescriptorRangeDescriptorCount(
                        set, descriptorRange);
                if (count < 0)
                    continue;
                for (uint element = 0; element < checked((uint)count); element++)
                    result.Add(ResourceBinding.Null(ToResourceBindingType(type)));
            }
        }
        return [.. result];
    }

    private static TypeLayoutReflection GetDataLayout(VariableLayoutReflection layout)
    {
        TypeLayoutReflection result = layout.TypeLayout.UnwrapArray();
        if (result.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
        {
            TypeLayoutReflection element = result.ElementTypeLayout.UnwrapArray();
            if (element != TypeLayoutReflection.Null)
                result = element;
        }
        return result;
    }

    private static StaticSamplerBinding StaticSampler(
        ShaderReflection reflection,
        VariableLayoutReflection block,
        uint shaderRegister,
        uint registerSpace,
        in SamplerDesc description)
    {
        TypeLayoutReflection data = GetDataLayout(block);
        for (nint rangeIndex = 0; rangeIndex < data.BindingRangeCount; rangeIndex++)
        {
            if ((data.GetBindingRangeType(rangeIndex) & SlangBindingType.BaseMask) !=
                SlangBindingType.Sampler)
                continue;
            nint set = data.GetBindingRangeDescriptorSetIndex(rangeIndex);
            nint descriptorRange = data.GetBindingRangeFirstDescriptorRangeIndex(rangeIndex);
            SlangParameterCategory category =
                data.GetDescriptorSetDescriptorRangeCategory(set, descriptorRange);
            uint first = checked((uint)(
                block.GetOffset(category) +
                unchecked((nuint)data.GetDescriptorSetDescriptorRangeIndexOffset(
                    set,
                    descriptorRange))));
            uint space = checked((uint)(
                block.GetBindingSpace(category) +
                unchecked((nuint)data.GetDescriptorSetSpaceOffset(set))));
            nint countValue = data.GetBindingRangeBindingCount(rangeIndex);
            nuint countMarker = unchecked((nuint)countValue);
            bool finite = countValue > 0 &&
                countMarker != Slang.UnknownSize &&
                countMarker != Slang.UnboundedSize &&
                countMarker <= uint.MaxValue;
            bool contains = finite && countValue == 1 && registerSpace == space &&
                shaderRegister == first;
            if (contains)
            {
                return new StaticSamplerBinding(
                    data.GetBindingRangeLeafVariable(rangeIndex),
                    description);
            }
        }
        throw new InvalidOperationException(
            $"The test layout has no sampler at s{shaderRegister}, space {registerSpace}.");
    }

    private static byte[] ReadPipelineCache(IGraphicsBackend backend, PipelineCache cache)
    {
        Assert.False(backend.TryGetPipelineCacheData(cache, [], out int required));
        byte[] result = new byte[required];
        Assert.True(backend.TryGetPipelineCacheData(cache, result, out int confirmed));
        Assert.Equal(result.Length, confirmed);
        return result;
    }

    private static ResourceBindingType ToResourceBindingType(SlangBindingType reflectedType)
    {
        SlangBindingType type = reflectedType & SlangBindingType.BaseMask;
        bool writable = (reflectedType & SlangBindingType.MutableFlag) != 0;
        return type switch
        {
            SlangBindingType.Sampler => ResourceBindingType.Sampler,
            SlangBindingType.ConstantBuffer => ResourceBindingType.ConstantBuffer,
            SlangBindingType.Texture => writable
                ? ResourceBindingType.TextureUav
                : ResourceBindingType.TextureSrv,
            SlangBindingType.TypedBuffer or SlangBindingType.RawBuffer => writable
                ? ResourceBindingType.BufferUav
                : ResourceBindingType.BufferSrv,
            SlangBindingType.RayTracingAccelerationStructure =>
                ResourceBindingType.AccelerationStructure,
            SlangBindingType.InputRenderTarget => ResourceBindingType.TextureSrv,
            _ => throw new InvalidOperationException(
                $"Unexpected Slang binding type {reflectedType}."),
        };
    }

    private static void CreateAndDisposeBindings(
        ValidationLayer backend,
        Device device,
        Pipeline pipeline,
        VariableLayoutReflection layout,
        ResourceBinding[] resources,
        byte[] ordinaryData)
    {
        using PersistentParameterBindings bindings = backend.CreatePersistentParameterBindings(
            device,
            pipeline,
            new ParameterBlockBindings(layout, resources, ordinaryData));
    }

    private static void SetTransientBindings(
        ValidationLayer backend,
        CommandContext context,
        VariableLayoutReflection layout,
        ResourceBinding[] resources,
        byte[] ordinaryData) =>
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(layout, resources, ordinaryData));

    private static AdapterInfo SelectWarp(IGraphicsBackend graphics)
    {
        AdapterEnumerationOptions options = new(
            AdapterPreference.HighPerformance,
            IncludeSoftware: true);
        _ = graphics.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        Assert.True(graphics.TryEnumerateAdapters(options, adapters, out int confirmed));
        Assert.Equal(adapters.Length, confirmed);
        return adapters.First(static adapter => !adapter.HardwareAccelerated);
    }

    private static void SubmitAndWait(
        ValidationLayer backend,
        Queue queue,
        in RecordedCommands commands)
    {
        RecordedCommands[] commandSpan = [commands];
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commandSpan, [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
    }

    private sealed class ThrowingValidationMessageSink : IValidationMessageSink
    {
        public void Report(in ValidationMessage message) =>
            throw new InvalidOperationException("The diagnostic consumer failed.");
    }
}
