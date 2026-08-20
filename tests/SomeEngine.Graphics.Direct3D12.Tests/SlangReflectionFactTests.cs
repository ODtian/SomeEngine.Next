using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class SlangReflectionFactTests
{
    [Fact]
    public void Public_RHI_surface_does_not_reintroduce_removed_shader_models()
    {
        string[] forbiddenNames =
        [
            "ParameterBindingContract",
            "ShaderContract",
            "ShaderInterface",
            "ShaderPackage",
            "ShaderCursor",
            "ShaderBindingCursor",
            "ComponentTypeLease",
            "ParameterBlockLayoutReflection",
            "ParameterBindingRangeReflection",
            "ParameterBindingElementReflection",
            "DescriptorDomain",
            "DescriptorPublication",
            "PipelineCompilation",
        ];
        Type[] exportedTypes =
        [
            .. typeof(IGraphicsBackend).Assembly.GetExportedTypes(),
            .. typeof(D3D12Backend).Assembly.GetExportedTypes(),
            .. typeof(ValidationLayer).Assembly.GetExportedTypes(),
        ];
        foreach (string forbiddenName in forbiddenNames)
        {
            Assert.DoesNotContain(
                exportedTypes,
                type => string.Equals(type.Name, forbiddenName, StringComparison.Ordinal));
        }

        Assert.Equal(
            typeof(Task<Pipeline>),
            typeof(IGraphicsBackend)
                .GetMethod(nameof(IGraphicsBackend.CreateComputePipelineAsync))!
                .ReturnType);
        Assert.NotNull(typeof(PipelineCreationSupport));
        Assert.Equal(
            ["Description", "Sampler"],
            typeof(StaticSamplerBinding).GetProperties()
                .Select(static property => property.Name)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public void Transient_parameter_blocks_use_the_single_authoritative_identity_map()
    {
        const string source = """
            struct Values { uint value; };
            ParameterBlock<Values> first;
            ParameterBlock<Values> second;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain() { if (first.value + second.value == 0) { } }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "associative_parameter_placement_cache", source,
            [new("computeMain", SlangStage.Compute)], "sm_6_0");
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection data = GetDataLayout(globals);
        VariableLayoutReflection[] blocks = Enumerable.Range(
                0, checked((int)data.SubObjectRangeCount))
            .Select(index => data.GetSubObjectRangeOffset(index))
            .ToArray();
        Assert.Equal(2, blocks.Length);

        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device, new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        using CommandContext context = backend.CreateCommandContext(
            device, new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(0, 0, 16));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(blocks[0], [], BitConverter.GetBytes(1u)));
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(blocks[1], [], BitConverter.GetBytes(2u)));
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(blocks[0], [], BitConverter.GetBytes(3u)));
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(blocks[1], [], BitConverter.GetBytes(4u)));
        using RecordedCommands commands = backend.End(context);
    }

    [Fact]
    public void Sibling_and_nested_parameter_blocks_bind_in_reverse_order_and_execute()
    {
        const string source = """
            struct Inner { uint value; };
            struct Outer { uint scale; ParameterBlock<Inner> nested; };
            ParameterBlock<Outer> first;
            ParameterBlock<Outer> second;
            RWStructuredBuffer<uint> outputValues;
            uint outputIndex;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[outputIndex] = first.scale + first.nested.value * 10
                    + second.scale * 100 + second.nested.value * 1000;
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "reverse_nested_parameter_blocks", source,
            [new("computeMain", SlangStage.Compute)], "sm_6_0");
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection globalData = GetDataLayout(globals);
        VariableLayoutReflection[] siblings = Enumerable.Range(
                0, checked((int)globalData.SubObjectRangeCount))
            .Where(index => (globalData.GetBindingRangeType(
                    globalData.GetSubObjectRangeBindingRangeIndex(index)) &
                SlangBindingType.BaseMask) == SlangBindingType.ParameterBlock)
            .Select(index => globalData.GetSubObjectRangeOffset(index))
            .ToArray();
        Assert.Equal(2, siblings.Length);
        VariableLayoutReflection first = siblings[0];
        VariableLayoutReflection second = siblings[1];
        VariableLayoutReflection firstNested = GetDataLayout(first).GetSubObjectRangeOffset(0);
        VariableLayoutReflection secondNested = GetDataLayout(second).GetSubObjectRangeOffset(0);
        Assert.NotEqual(first, second);
        Assert.NotEqual(firstNested, secondNested);

        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device, new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(8, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(
            device, new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);
        using PersistentParameterBindings secondBindings =
            backend.CreatePersistentParameterBindings(device, pipeline,
                new ParameterBlockBindings(second, [], BitConverter.GetBytes(3u)));
        using PersistentParameterBindings secondNestedBindings =
            backend.CreatePersistentParameterBindings(device, pipeline,
                new ParameterBlockBindings(secondNested, [], BitConverter.GetBytes(4u)));
        using PersistentParameterBindings updatedSecondNestedBindings =
            backend.CreatePersistentParameterBindings(device, pipeline,
                new ParameterBlockBindings(secondNested, [], BitConverter.GetBytes(9u)));
        backend.UpdatePersistentParameterBindings(updatedSecondNestedBindings,
            new ParameterBlockBindings(secondNested, [], BitConverter.GetBytes(7u)));
        backend.PublishDescriptors(device);
        using CommandContext context = backend.CreateCommandContext(
            device, new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(16, 0, 16));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.None,
            PipelineSync.ComputeShading, ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(globals, [ResourceBinding.WritableBuffer(outputUav)],
                BitConverter.GetBytes(0u)));
        backend.SetPersistentParameterBindings(context, secondNestedBindings);
        backend.SetPersistentParameterBindings(context, secondBindings);
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(firstNested, [], BitConverter.GetBytes(2u)));
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(first, [], BitConverter.GetBytes(1u)));
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(globals, [ResourceBinding.WritableBuffer(outputUav)],
                BitConverter.GetBytes(1u)));
        backend.SetPersistentParameterBindings(context, updatedSecondNestedBindings);
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading,
            PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 8));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Compute),
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, new BufferRange(0, 8));
        mapped.Invalidate(new BufferRange(0, 8));
        Assert.Equal([4321u, 7321u], System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
            mapped.Bytes).ToArray());
    }

    [Fact]
    public void Ordinary_constant_buffer_fast_path_requires_the_native_16_byte_shape()
    {
        const string sixteenBytes = """
            uint4 values;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID) { if (values.x == id.x) { } }
            """;
        const string thirtyTwoBytes = """
            uint4 firstValues;
            uint4 secondValues;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            { if (firstValues.x + secondValues.x == id.x) { } }
            """;
        D3D12TestShaderEntry[] entries = [new("computeMain", SlangStage.Compute)];
        using D3D12TestShaderProgram validShader = D3D12TestShaderProgram.Compile(
            "ordinary_fast_path_16", sixteenBytes, entries, "sm_6_0");
        using D3D12TestShaderProgram invalidShader = D3D12TestShaderProgram.Compile(
            "ordinary_fast_path_32", thirtyTwoBytes, entries, "sm_6_0");
        VariableLayoutReflection validLayout =
            validShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        VariableLayoutReflection invalidLayout =
            invalidShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        Assert.Equal((nuint)16, GetDataLayout(validLayout).GetSize(SlangParameterCategory.Uniform));
        Assert.Equal((nuint)32, GetDataLayout(invalidLayout).GetSize(SlangParameterCategory.Uniform));

        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline validPipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(validShader.Program, validShader.GetEntryPoint(0)));
        using Pipeline invalidPipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(invalidShader.Program, invalidShader.GetEntryPoint(0)));
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(0, 0, 32));
        backend.SetPipeline(context, validPipeline);
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(validLayout, [], new byte[16]));
        backend.SetPipeline(context, invalidPipeline);
        Assert.Throws<ArgumentException>(() => backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(invalidLayout, [], new byte[16])));
        backend.Discard(context);
    }

    [Fact]
    public void Distinct_pipeline_identity_rebinds_and_clears_previous_parameter_bindings()
    {
        const string source = """
            RWStructuredBuffer<uint> outputValues;
            uint outputIndex;
            uint outputValue;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[outputIndex] = outputValue;
            }
            """;
        D3D12TestShaderEntry[] entries = [new("computeMain", SlangStage.Compute)];
        using D3D12TestShaderProgram firstShader = D3D12TestShaderProgram.Compile(
            "distinct_pipeline_raw_identity_first", source, entries, "sm_6_0");
        using D3D12TestShaderProgram secondShader = D3D12TestShaderProgram.Compile(
            "distinct_pipeline_raw_identity_second", source, entries, "sm_6_0");
        VariableLayoutReflection firstLayout =
            firstShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        VariableLayoutReflection secondLayout =
            secondShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        Assert.NotEqual(firstLayout, secondLayout);

        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Assert.NotNull(diagnostics);
        using Pipeline firstPipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(firstShader.Program, firstShader.GetEntryPoint(0)));
        using Pipeline secondPipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(secondShader.Program, secondShader.GetEntryPoint(0)));
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(8, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(4, 0, 16));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.None,
            PipelineSync.ComputeShading, ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, firstPipeline);
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(firstLayout,
                [ResourceBinding.WritableBuffer(outputUav)],
                [.. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(11u)]));
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.SetPipeline(context, secondPipeline);
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(secondLayout,
                [ResourceBinding.WritableBuffer(outputUav)],
                [.. BitConverter.GetBytes(1u), .. BitConverter.GetBytes(22u)]));
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
        Assert.Equal([11u, 22u], System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
            mapped.Bytes).ToArray());
    }

    [Fact]
    public void Bounded_array_elements_follow_raw_range_then_element_order_for_transient_and_persistent()
    {
        const string source = """
            ByteAddressBuffer inputs[2];
            RWStructuredBuffer<uint> outputValues;
            uint outputIndex;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[outputIndex] = inputs[0].Load<uint>(0) * 100u
                    + inputs[1].Load<uint>(0);
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "bounded_array_element_execution", source,
            [new("computeMain", SlangStage.Compute)], "sm_6_0");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection reflected = GetDataLayout(layout);
        nint inputRange = Enumerable.Range(0, checked((int)reflected.BindingRangeCount))
            .Select(static index => (nint)index)
            .Single(index =>
                (reflected.GetBindingRangeType(index) & SlangBindingType.BaseMask) ==
                    SlangBindingType.RawBuffer &&
                (reflected.GetBindingRangeType(index) & SlangBindingType.MutableFlag) == 0);
        Assert.Equal((nint)2, reflected.GetBindingRangeBindingCount(inputRange));

        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        using Buffer first = CreateInput(3u);
        using Buffer second = CreateInput(7u);
        using BufferSrv firstSrv = backend.CreateBufferSrv(device,
            new BufferSrvDesc(first, BufferRange.Whole));
        using BufferSrv secondSrv = backend.CreateBufferSrv(device,
            new BufferSrvDesc(second, BufferRange.Whole));
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(8, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);

        var resources = new List<ResourceBinding>();
        for (nint range = 0; range < reflected.BindingRangeCount; range++)
        {
            if (reflected.GetBindingRangeDescriptorRangeCount(range) == 0)
                continue;
            SlangBindingType type = reflected.GetBindingRangeType(range);
            nint count = reflected.GetBindingRangeBindingCount(range);
            for (nint element = 0; element < count; element++)
            {
                resources.Add((type & SlangBindingType.MutableFlag) != 0
                    ? ResourceBinding.WritableBuffer(outputUav)
                    : element == 0
                        ? ResourceBinding.ReadOnlyBuffer(firstSrv)
                        : ResourceBinding.ReadOnlyBuffer(secondSrv));
            }
        }
        Assert.Equal(3, resources.Count);

        using PersistentParameterBindings persistent = backend.CreatePersistentParameterBindings(
            device, pipeline,
            new ParameterBlockBindings(layout, resources.ToArray(), BitConverter.GetBytes(1u)));
        backend.PublishDescriptors(device);
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(8, 0, 8));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.None,
            PipelineSync.ComputeShading, ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(layout, resources.ToArray(), BitConverter.GetBytes(0u)));
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
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(new BufferRange(0, 8));
        Assert.Equal([307u, 307u],
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray());

        Buffer CreateInput(uint value)
        {
            Buffer buffer = backend.CreateBuffer(device,
                new BufferDesc(4, BufferUsages.ShaderRead), MemoryType.Upload);
            using MappedBuffer mapped = backend.Map(buffer, MapType.Write, BufferRange.Whole);
            BitConverter.GetBytes(value).CopyTo(mapped.Bytes);
            mapped.Flush(new BufferRange(0, 4));
            return buffer;
        }
    }

    [Fact]
    public void Auto_and_manual_multi_space_layouts_share_raw_range_order_and_execute()
    {
        const string automatic = """
            Texture2D<float4> sampledTexture;
            SamplerState dynamicSampler;
            RWStructuredBuffer<uint> outputValues;
            ByteAddressBuffer inputValues;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[0] = inputValues.Load<uint>(0)
                    + asuint(sampledTexture.SampleLevel(dynamicSampler, float2(0.5), 0).x)
                    + 0xABCDu;
            }
            """;
        const string manual = """
            Texture2D<float4> sampledTexture : register(t7, space3);
            SamplerState dynamicSampler : register(s4, space2);
            RWStructuredBuffer<uint> outputValues : register(u1, space0);
            ByteAddressBuffer inputValues : register(t9, space1);
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[0] = inputValues.Load<uint>(0)
                    + asuint(sampledTexture.SampleLevel(dynamicSampler, float2(0.5), 0).x)
                    + 0xABCDu;
            }
            """;

        var (autoResult, autoOrder, _) = Execute(automatic, "auto_order");
        var (manualResult, manualOrder, manualSetCount) = Execute(manual, "manual_order");
        Assert.Equal(unchecked(0x1357u + 0x3F800000u + 0xABCDu), autoResult);
        Assert.Equal(autoResult, manualResult);
        Assert.Equal(autoOrder, manualOrder);
        Assert.True(manualSetCount >= 4);

        static (uint Result, SlangBindingType[] Order, nint DescriptorSetCount) Execute(
            string source,
            string name)
        {
            using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
                name, source, [new("computeMain", SlangStage.Compute)], "sm_6_0");
            VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
                ?? VariableLayoutReflection.Null;
            TypeLayoutReflection data = GetDataLayout(layout);
            SlangBindingType[] order = Enumerable.Range(0, checked((int)data.BindingRangeCount))
                .Select(index => data.GetBindingRangeType(index)).ToArray();
            if (name == "manual_order")
            {
                (uint Register, uint Space)[] expected =
                    [(7, 3), (4, 2), (1, 0), (9, 1)];
                Assert.Equal(expected.Length, order.Length);
                for (nint bindingRange = 0; bindingRange < order.Length; bindingRange++)
                {
                    Assert.Equal((nint)1,
                        data.GetBindingRangeDescriptorRangeCount(bindingRange));
                    nint set = data.GetBindingRangeDescriptorSetIndex(bindingRange);
                    nint descriptorRange =
                        data.GetBindingRangeFirstDescriptorRangeIndex(bindingRange);
                    SlangParameterCategory category =
                        data.GetDescriptorSetDescriptorRangeCategory(set, descriptorRange);
                    uint descriptorRegister = checked((uint)
                        data.GetDescriptorSetDescriptorRangeIndexOffset(set, descriptorRange));
                    uint setSpace = checked((uint)data.GetDescriptorSetSpaceOffset(set));
                    nint[] subObjects = Enumerable.Range(0, checked((int)data.SubObjectRangeCount))
                        .Select(static index => (nint)index)
                        .Where(index =>
                            data.GetSubObjectRangeBindingRangeIndex(index) == bindingRange)
                        .ToArray();
                    Assert.True(subObjects.Length <= 1);
                    uint leafSpace = subObjects.Length == 0 ? 0 : checked((uint)
                        data.GetSubObjectRangeOffset(subObjects[0]).GetBindingSpace(category));
                    Assert.Equal(expected[checked((int)bindingRange)].Register,
                        descriptorRegister);
                    Assert.Equal(expected[checked((int)bindingRange)].Space,
                        checked(setSpace + leafSpace));
                }
            }
            using var backend = new D3D12Backend();
            using Device device = D3D12TestSupport.CreateWarpDevice(backend);
            using Pipeline pipeline = backend.CreateComputePipeline(device,
                new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
            using Buffer output = backend.CreateBuffer(device,
                new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource),
                MemoryType.DeviceLocal);
            using BufferUav outputUav = backend.CreateBufferUav(device,
                new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
            using Buffer readback = backend.CreateBuffer(device,
                new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
            using Buffer input = backend.CreateBuffer(device,
                new BufferDesc(4, BufferUsages.ShaderRead), MemoryType.Upload);
            using (MappedBuffer inputMapping = backend.Map(input, MapType.Write, BufferRange.Whole))
            {
                BitConverter.GetBytes(0x1357u).CopyTo(inputMapping.Bytes);
                inputMapping.Flush(new BufferRange(0, 4));
            }
            using BufferSrv inputSrv = backend.CreateBufferSrv(device,
                new BufferSrvDesc(input, BufferRange.Whole));
            using Texture texture = backend.CreateTexture(device,
                new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
                    Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination));
            D3D12TestSupport.UploadSinglePixelR32Float(backend, device, texture, 1.0f);
            TextureSubresourceRange textureRange = new(0, 1, 0, 1, TextureAspects.Color);
            using TextureSrv textureSrv = backend.CreateTextureSrv(device,
                new TextureSrvDesc(texture, textureRange, Format.R32Float,
                    TextureViewDimension.Texture2D));
            using Sampler sampler = backend.CreateSampler(device,
                new SamplerDesc(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                    AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge));
            var resources = new List<ResourceBinding>();
            foreach (SlangBindingType reflected in order)
            {
                SlangBindingType type = reflected & SlangBindingType.BaseMask;
                resources.Add(type switch
                {
                    SlangBindingType.Texture => ResourceBinding.SampledTexture(textureSrv),
                    SlangBindingType.Sampler => ResourceBinding.SampledWith(sampler),
                    SlangBindingType.RawBuffer when
                        (reflected & SlangBindingType.MutableFlag) != 0 =>
                        ResourceBinding.WritableBuffer(outputUav),
                    SlangBindingType.RawBuffer => ResourceBinding.ReadOnlyBuffer(inputSrv),
                    _ => throw new InvalidOperationException($"Unexpected reflected type {reflected}."),
                });
            }
            using CommandContext context = backend.CreateCommandContext(device,
                new CommandContextDesc(QueueType.Compute, 0, 1));
            backend.Begin(context, new CommandRecordingDesc(8, 2, 8));
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.None,
                PipelineSync.ComputeShading, ResourceAccess.NoAccess,
                ResourceAccess.UnorderedAccess));
            backend.SetPipeline(context, pipeline);
            backend.SetTransientParameterBindings(context,
                new ParameterBlockBindings(layout, resources.ToArray(), []));
            backend.Dispatch(context, new DispatchArguments(1, 1, 1));
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading,
                PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 4));
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute),
                new QueueSubmitDesc([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed,
                backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            using MappedBuffer mapped = backend.Map(readback, MapType.Read, new BufferRange(0, 4));
            mapped.Invalidate(new BufferRange(0, 4));
            return (BitConverter.ToUInt32(mapped.Bytes), order, data.DescriptorSetCount);
        }
    }

    [Fact]
    public void Multi_descriptor_binding_range_expands_in_raw_descriptor_range_order_and_executes()
    {
        const string source = """
            Sampler2D combinedSampler : register(t4) : register(s3);
            RWStructuredBuffer<float4> outputValues;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[0] = combinedSampler.SampleLevel(float2(0.5), 0);
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "combined_texture_sampler_diagnostic", source,
            [new("computeMain", SlangStage.Compute)], "sm_6_0");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection data = GetDataLayout(layout);
        nint combinedRange = Enumerable.Range(0, checked((int)data.BindingRangeCount))
            .Select(static index => (nint)index)
            .Single(index => data.GetBindingRangeDescriptorRangeCount(index) > 1);
        Assert.Equal(SlangBindingType.CombinedTextureSampler,
            data.GetBindingRangeType(combinedRange) & SlangBindingType.BaseMask);

        nint setIndex = data.GetBindingRangeDescriptorSetIndex(combinedRange);
        nint firstDescriptorRange =
            data.GetBindingRangeFirstDescriptorRangeIndex(combinedRange);
        Assert.Equal(
            [SlangBindingType.CombinedTextureSampler, SlangBindingType.Sampler],
            Enumerable.Range(0, checked((int)
                    data.GetBindingRangeDescriptorRangeCount(combinedRange)))
                .Select(relative => data.GetDescriptorSetDescriptorRangeType(
                    setIndex, firstDescriptorRange + relative) & SlangBindingType.BaseMask)
                .ToArray());

        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        using Texture texture = backend.CreateTexture(device,
            new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
                Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination));
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, texture, 1.0f);
        using TextureSrv textureSrv = backend.CreateTextureSrv(device,
            new TextureSrvDesc(texture, new TextureSubresourceRange(
                    0, 1, 0, 1, TextureAspects.Color), Format.R32Float,
                TextureViewDimension.Texture2D));
        using Sampler sampler = backend.CreateSampler(device,
            new SamplerDesc(FilterType.Nearest, FilterType.Nearest,
                FilterType.Nearest, AddressType.ClampToEdge,
                AddressType.ClampToEdge, AddressType.ClampToEdge));
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(16, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32G32B32A32Float));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(16, BufferUsages.CopyDestination), MemoryType.Readback);

        var resources = new List<ResourceBinding>();
        for (nint rangeIndex = 0; rangeIndex < data.BindingRangeCount; rangeIndex++)
        {
            nint descriptorRangeCount =
                data.GetBindingRangeDescriptorRangeCount(rangeIndex);
            if (descriptorRangeCount == 0)
                continue;
            nint descriptorSet = data.GetBindingRangeDescriptorSetIndex(rangeIndex);
            nint first = data.GetBindingRangeFirstDescriptorRangeIndex(rangeIndex);
            for (nint relative = 0; relative < descriptorRangeCount; relative++)
            {
                nint descriptorRange = first + relative;
                SlangBindingType reflected =
                    data.GetDescriptorSetDescriptorRangeType(
                        descriptorSet, descriptorRange);
                SlangParameterCategory category =
                    data.GetDescriptorSetDescriptorRangeCategory(
                        descriptorSet, descriptorRange);
                nint count = data.GetDescriptorSetDescriptorRangeDescriptorCount(
                    descriptorSet, descriptorRange);
                Assert.True(count > 0);
                for (nint element = 0; element < count; element++)
                {
                    resources.Add((reflected & SlangBindingType.BaseMask) switch
                    {
                        SlangBindingType.Texture =>
                            ResourceBinding.SampledTexture(textureSrv),
                        SlangBindingType.CombinedTextureSampler when
                            category == SlangParameterCategory.ShaderResource =>
                            ResourceBinding.SampledTexture(textureSrv),
                        SlangBindingType.Sampler =>
                            ResourceBinding.SampledWith(sampler),
                        SlangBindingType.RawBuffer when
                            (reflected & SlangBindingType.MutableFlag) != 0 =>
                            ResourceBinding.WritableBuffer(outputUav),
                        _ => throw new InvalidOperationException(
                            $"Unexpected descriptor type {reflected}."),
                    });
                }
            }
        }
        Assert.Equal(
            [ResourceBindingType.TextureSrv, ResourceBindingType.Sampler,
                ResourceBindingType.BufferUav],
            resources.Select(static binding => binding.Type).ToArray());

        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(8, 2, 8));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.None,
            PipelineSync.ComputeShading, ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(layout, resources.ToArray(), []));
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.Barrier(context, new BufferBarrier(output,
            PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 16));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Compute),
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer mapped = backend.Map(
            readback, MapType.Read, new BufferRange(0, 16));
        mapped.Invalidate(new BufferRange(0, 16));
        Assert.Equal([1f, 0f, 0f, 0f],
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                mapped.Bytes).ToArray());
    }

    [Fact]
    public void Runtime_existential_subobject_requires_specialization_and_is_not_silently_omitted()
    {
        const string source = """
            [anyValueSize(32)]
            dyn interface IValue { uint getValue(); }
            struct ConcreteValue : IValue
            {
                uint value;
                uint getValue() { return value; }
            }
            IValue dynamicValue;
            RWStructuredBuffer<uint> outputValues;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            { outputValues[0] = dynamicValue.getValue(); }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "runtime_existential_subobject", source,
            [new("computeMain", SlangStage.Compute)], "sm_6_0");
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection data = GetDataLayout(globals);
        Assert.Contains(Enumerable.Range(0, checked((int)data.SubObjectRangeCount)), index =>
        {
            nint range = data.GetSubObjectRangeBindingRangeIndex(index);
            return (data.GetBindingRangeType(range) & SlangBindingType.BaseMask) ==
                SlangBindingType.ExistentialValue &&
                data.GetBindingRangeDescriptorRangeCount(range) == 0;
        });
        Assert.True(shader.Program.GetSpecializationParamCount() > 0);
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        GraphicsException error = Assert.Throws<GraphicsException>(() =>
            backend.CreateComputePipeline(device,
                new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0))));
        Assert.Equal(GraphicsError.PipelineCreation, error.Error);
        Assert.Contains("fully specialized Slang program", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Slang_rejects_parameter_block_arrays_and_distinct_scalar_occurrences_have_distinct_identities()
    {
        const string source = """
            struct Values { uint value; };
            ParameterBlock<Values> blocks[2];
            RWStructuredBuffer<uint> outputValues;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[0] = blocks[0].value + blocks[1].value;
            }
            """;
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            D3D12TestShaderProgram.Compile(
                "parameter_block_array_diagnostic", source,
                [new("computeMain", SlangStage.Compute)], "sm_6_0"));
        Assert.Contains("arrays of non-addressable type", exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Global_unbounded_resource_and_sampler_use_published_heap_indices_and_execute()
    {
        const string source = """
            Texture2D<float4> bindlessTextures[];
            SamplerState bindlessSamplers[];
            RWStructuredBuffer<uint> outputValues;
            uint textureIndex;
            uint samplerIndex;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                float sampled = bindlessTextures[textureIndex].SampleLevel(
                    bindlessSamplers[samplerIndex], float2(0.5), 0).x;
                outputValues[0] = asuint(sampled) + 0xBEEFu;
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "global_unbounded_heap_execution", source,
            [new("computeMain", SlangStage.Compute)], "sm_6_6");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection data = GetDataLayout(layout);
        Assert.Equal(2, Enumerable.Range(0, checked((int)data.BindingRangeCount))
            .Count(index => unchecked((nuint)data.GetBindingRangeBindingCount(index)) ==
                Slang.UnboundedSize));

        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using DescriptorTable textures = backend.CreateDescriptorTable(device,
            [ResourceBindingType.TextureSrv]);
        using DescriptorTable samplers = backend.CreateDescriptorTable(device,
            [ResourceBindingType.Sampler]);
        using Sampler sampler = backend.CreateSampler(device,
            new SamplerDesc(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge));
        using Texture texture = backend.CreateTexture(device,
            new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
                Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination));
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, texture, 1.0f);
        TextureSubresourceRange textureRange = new(0, 1, 0, 1, TextureAspects.Color);
        using TextureSrv textureSrv = backend.CreateTextureSrv(device,
            new TextureSrvDesc(texture, textureRange, Format.R32Float,
                TextureViewDimension.Texture2D));
        backend.WriteDescriptor(textures, 0, ResourceBinding.SampledTexture(textureSrv));
        backend.WriteDescriptor(samplers, 0, ResourceBinding.SampledWith(sampler));
        uint textureIndex = backend.GetDescriptorIndex(textures, 0).Value;
        uint samplerIndex = backend.GetDescriptorIndex(samplers, 0).Value;
        backend.PublishDescriptors(device);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        byte[] ordinary = new byte[8];
        BitConverter.GetBytes(textureIndex).CopyTo(ordinary, 0);
        BitConverter.GetBytes(samplerIndex).CopyTo(ordinary, 4);
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(1, 0, 8));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.None,
            PipelineSync.ComputeShading, ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(layout, [ResourceBinding.WritableBuffer(outputUav)], ordinary));
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading,
            PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 4));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute),
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, new BufferRange(0, 4));
        mapped.Invalidate(new BufferRange(0, 4));
        Assert.Equal(unchecked(0x3F800000u + 0xBEEFu), BitConverter.ToUInt32(mapped.Bytes));
    }

    [Fact]
    public void Compute_pipeline_description_preserves_positional_record_semantics()
    {
        ComputePipelineDesc original = new(
            Program: null!,
            Compute: EntryPointReflection.Null,
            Label: "original");
        ComputePipelineDesc changed = original with { Label = "changed" };
        (IComponentType program, EntryPointReflection compute, string? label) = changed;

        Assert.Null(program);
        Assert.Equal(EntryPointReflection.Null, compute);
        Assert.Equal("changed", label);
        Assert.True(changed.StaticSamplers.IsEmpty);
    }

    [Fact]
    public void Dxil_constant_buffer_fact_preserves_category_register_and_space()
    {
        const string source = """
            struct Values { uint value; };
            ConstantBuffer<Values> constants : register(b3, space2);
            RWStructuredBuffer<uint> outputValues;
            uint addend;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[id.x] = constants.value + addend;
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "dxil_constant_buffer_fact",
            source,
            [new("computeMain", SlangStage.Compute)],
            "sm_6_0");
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection data = GetDataLayout(globals);

        nint subObject = Enumerable.Range(0, checked((int)data.SubObjectRangeCount))
            .Select(static index => (nint)index)
            .Single(index =>
                (data.GetBindingRangeType(
                    data.GetSubObjectRangeBindingRangeIndex(index)) &
                SlangBindingType.BaseMask) == SlangBindingType.ConstantBuffer);
        nint constantRange = data.GetSubObjectRangeBindingRangeIndex(subObject);
        Assert.Equal((nint)1, data.GetBindingRangeDescriptorRangeCount(constantRange));
        nint constantSet = data.GetBindingRangeDescriptorSetIndex(constantRange);
        nint constantDescriptorRange =
            data.GetBindingRangeFirstDescriptorRangeIndex(constantRange);
        Assert.Equal(SlangBindingType.ConstantBuffer,
            data.GetDescriptorSetDescriptorRangeType(constantSet, constantDescriptorRange));
        VariableLayoutReflection constants = data.GetSubObjectRangeOffset(subObject);
        Assert.Equal(
            (nuint)3,
            constants.GetOffset(SlangParameterCategory.ConstantBuffer));
        Assert.Equal(
            (nuint)2,
            constants.GetBindingSpace(SlangParameterCategory.ConstantBuffer));

        Assert.Equal((nuint)4, GetDataLayout(constants).GetSize(SlangParameterCategory.Uniform));
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        using Buffer constantsBuffer = backend.CreateBuffer(device,
            new BufferDesc(256, BufferUsages.Constant), MemoryType.Upload);
        using (MappedBuffer mapped = backend.Map(constantsBuffer, MapType.Write,
                   new BufferRange(0, 256)))
        {
            BitConverter.GetBytes(37u).CopyTo(mapped.Bytes);
            mapped.Flush(new BufferRange(0, 4));
        }
        using BufferCbv constantsView = backend.CreateBufferCbv(device,
            new BufferCbvDesc(constantsBuffer, new BufferRange(0, 256)));
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputView = backend.CreateBufferUav(device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(4, 0, 16));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.None,
            PipelineSync.ComputeShading, ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        Assert.Throws<ArgumentException>(() => backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(constants, [], BitConverter.GetBytes(37u))));
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(globals,
                [ResourceBinding.ConstantBuffer(constantsView),
                    ResourceBinding.WritableBuffer(outputView)],
                BitConverter.GetBytes(5u)));
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading,
            PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 4));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute),
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer result = backend.Map(readback, MapType.Read, new BufferRange(0, 4));
        result.Invalidate(new BufferRange(0, 4));
        Assert.Equal(42u, BitConverter.ToUInt32(result.Bytes));
    }

    [Fact]
    public void Spirv_push_constant_category_becomes_a_root_constant_fact()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID, uniform uint value)
            {
                if (id.x == value) {}
            }
            """;
        using D3D12TestShaderProgram shader =
            D3D12TestShaderProgram.CompileForReflection(
                "spirv_push_constant_fact",
                source,
                [new("computeMain", SlangStage.Compute)],
                SlangCompileTarget.Spirv,
                "spirv_1_5");
        VariableLayoutReflection entry = shader.GetEntryPoint(0).VarLayout;

        Assert.Contains(
            Enumerable.Range(0, checked((int)entry.CategoryCount))
                .Select(index => entry.GetCategoryByIndex(checked((uint)index))),
            static category => category == SlangParameterCategory.PushConstantBuffer);
        Assert.Equal((nuint)4, GetDataLayout(entry).GetSize(SlangParameterCategory.Uniform));
        Assert.Equal((nuint)0, entry.GetOffset(SlangParameterCategory.PushConstantBuffer));
    }

    [Fact]
    public void Nested_parameter_blocks_follow_only_parameter_block_subobjects()
    {
        const string source = """
            struct InnerValues { uint value; };
            struct OuterValues
            {
                uint value;
                ParameterBlock<InnerValues> nested;
            };

            ParameterBlock<OuterValues> parameters;
            RWStructuredBuffer<uint> outputValues;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[id.x] = parameters.value + parameters.nested.value;
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "nested_parameter_block_facts",
            source,
            [new("computeMain", SlangStage.Compute)],
            "sm_6_0");
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection globalData = GetDataLayout(globals);
        SlangBindingType[] globalSubObjectTypes = Enumerable.Range(
                0,
                checked((int)globalData.SubObjectRangeCount))
            .Select(index => globalData.GetBindingRangeType(
                globalData.GetSubObjectRangeBindingRangeIndex(index)) &
                SlangBindingType.BaseMask)
            .ToArray();
        Assert.Contains(SlangBindingType.ParameterBlock, globalSubObjectTypes);
        Assert.Contains(SlangBindingType.MutableRawBuffer & SlangBindingType.BaseMask,
            globalSubObjectTypes);

        nint parameterSubObject = Enumerable.Range(
                0,
                checked((int)globalData.SubObjectRangeCount))
            .Select(static index => (nint)index)
            .Single(index =>
                (globalData.GetBindingRangeType(
                    globalData.GetSubObjectRangeBindingRangeIndex(index)) &
                    SlangBindingType.BaseMask) == SlangBindingType.ParameterBlock);
        VariableLayoutReflection parameters =
            globalData.GetSubObjectRangeOffset(parameterSubObject);
        Assert.Equal(
            (nint)0,
            globalData.GetSubObjectRangeSpaceOffset(parameterSubObject));
        TypeLayoutReflection parameterData = GetDataLayout(parameters);
        Assert.Equal((nint)1, parameterData.SubObjectRangeCount);
        nint nestedSubObject = 0;
        nint nestedRange =
            parameterData.GetSubObjectRangeBindingRangeIndex(nestedSubObject);
        Assert.Equal(
            SlangBindingType.ParameterBlock,
            parameterData.GetBindingRangeType(nestedRange) & SlangBindingType.BaseMask);
        nint nestedRangeSpace =
            parameterData.GetSubObjectRangeSpaceOffset(nestedSubObject);
        Assert.Equal((nint)0, nestedRangeSpace);
        VariableLayoutReflection nested =
            parameterData.GetSubObjectRangeOffset(nestedSubObject);
        nuint nestedVariableSpace = nested.GetOffset(
            SlangParameterCategory.SubElementRegisterSpace);
        Assert.Equal((nuint)1, nestedVariableSpace);
        Assert.Equal((nuint)0, parameters.GetOffset(SlangParameterCategory.ConstantBuffer));
        Assert.Equal((nuint)0, parameters.GetBindingSpace(SlangParameterCategory.ConstantBuffer));
        Assert.Equal((nuint)0, nested.GetOffset(SlangParameterCategory.ConstantBuffer));
        Assert.Equal((nuint)0, nested.GetBindingSpace(SlangParameterCategory.ConstantBuffer));

        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
    }

    [Fact]
    public void Graphics_varyings_are_zero_descriptor_ranges_and_do_not_enter_root_layout()
    {
        const string source = """
            float4 Tint;

            struct VertexOutput
            {
                float4 position : SV_Position;
                float4 color : COLOR0;
            };

            [shader("vertex")]
            VertexOutput vertexMain(uint vertexId : SV_VertexID)
            {
                VertexOutput result;
                result.position = float4(vertexId == 1 ? 1 : -1,
                    vertexId == 2 ? 1 : -1, 0, 1);
                result.color = float4(1, 1, 1, 1);
                return result;
            }

            [shader("fragment")]
            float4 pixelMain(VertexOutput input) : SV_Target0
            {
                return input.color * Tint;
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "graphics_varying_binding_facts",
            source,
            [new("vertexMain", SlangStage.Vertex), new("pixelMain", SlangStage.Fragment)],
            "sm_6_2");

        var varyingFacts = new List<(SlangBindingType Type, nint DescriptorRangeCount)>();
        for (int entryIndex = 0; entryIndex < 2; entryIndex++)
        {
            EntryPointReflection entryPoint = shader.GetEntryPoint(entryIndex);
            VariableLayoutReflection[] varyingLayouts =
                [entryPoint.VarLayout, entryPoint.ResultVarLayout];
            foreach (VariableLayoutReflection varyingLayout in varyingLayouts)
            {
                TypeLayoutReflection entry = GetDataLayout(varyingLayout);
                for (nint rangeIndex = 0; rangeIndex < entry.BindingRangeCount; rangeIndex++)
                {
                    SlangBindingType type =
                        entry.GetBindingRangeType(rangeIndex) & SlangBindingType.BaseMask;
                    if (type is SlangBindingType.VaryingInput or SlangBindingType.VaryingOutput)
                    {
                        varyingFacts.Add((
                            type,
                            entry.GetBindingRangeDescriptorRangeCount(rangeIndex)));
                    }
                }
            }
        }

        Assert.Contains(varyingFacts, static fact => fact.Type == SlangBindingType.VaryingInput);
        Assert.Contains(varyingFacts, static fact => fact.Type == SlangBindingType.VaryingOutput);
        Assert.All(varyingFacts, static fact => Assert.Equal((nint)0, fact.DescriptorRangeCount));

        using var backend = new ValidationLayer(new D3D12Backend());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
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
                new AttachmentFormatSignature(formats, null)));
    }

    private static TypeLayoutReflection GetDataLayout(
        VariableLayoutReflection layout)
    {
        TypeLayoutReflection result = layout.TypeLayout.UnwrapArray();
        if (result.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            result = result.ElementTypeLayout.UnwrapArray();
        return result;
    }
}
