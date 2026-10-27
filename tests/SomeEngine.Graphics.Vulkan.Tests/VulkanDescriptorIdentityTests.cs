using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using SlangShaderSharp;
using Xunit;

namespace SomeEngine.Graphics.Vulkan.Tests;

[Collection("Vulkan shader compilation")]
public sealed class VulkanDescriptorIdentityTests(VulkanTestCompiler compiler)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateModuleNamesKeepTheSelectedEntryValues(bool stripNames)
    {
        ModuleShader compiled = CompileModules();
        VulkanTestShader shader = compiled.Shader;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(default, [new(QueueType.Graphics)]));
        using var objects = new TestObjects(backend, device);
        Buffer first = objects.Upload(7);
        Buffer second = objects.Upload(19);
        Buffer firstOutput = objects.Output();
        Buffer secondOutput = objects.Output();
        BufferSrv firstView = objects.Keep(backend.CreateBufferSrv(device, new(first, BufferRange.Whole, StructureStride: 4)));
        BufferSrv secondView = objects.Keep(backend.CreateBufferSrv(device, new(second, BufferRange.Whole, StructureStride: 4)));
        BufferUav firstOutputView = objects.Keep(backend.CreateBufferUav(device, new(firstOutput, BufferRange.Whole, StructureStride: 4)));
        BufferUav secondOutputView = objects.Keep(backend.CreateBufferUav(device, new(secondOutput, BufferRange.Whole, StructureStride: 4)));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        TypeLayoutReflection data = Contents(globals);
        var resources = new List<ResourceBinding>();
        for (nint range = 0; range < data.BindingRangeCount; range++)
        {
            Assert.Equal((nint)1, data.GetBindingRangeDescriptorRangeCount(range));
            string name = data.GetBindingRangeLeafVariable(range).Name;
            if (name == "SharedInput")
            {
                SlangDescriptorLocation location = SlangDescriptorLocation.FromLayout(globals, data,
                    data.GetBindingRangeDescriptorSetIndex(range), data.GetBindingRangeFirstDescriptorRangeIndex(range));
                bool firstUses = shader.IsParameterLocationUsed(0, location.Category, (nuint)location.Space, (nuint)location.Register);
                bool secondUses = shader.IsParameterLocationUsed(1, location.Category, (nuint)location.Space, (nuint)location.Register);
                Assert.NotEqual(firstUses, secondUses);
                resources.Add(ResourceBinding.ReadOnlyBuffer(firstUses ? firstView : secondView));
            }
            else
            {
                Assert.Contains(name, new[] { "ResultA", "ResultB" });
                resources.Add(ResourceBinding.WritableBuffer(name == "ResultA" ? firstOutputView : secondOutputView));
            }
        }
        Assert.Equal(4, resources.Count);
        Pipeline[] pipelines = Enumerable.Range(0, 2).Select(index => objects.Keep(backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.Entries[index],
                new ReadOnlySequence<byte>(Code(shader.GetEntryCode(index), stripNames)))))).ToArray();
        Buffer readback = objects.Keep(backend.CreateBuffer(device, new(8, BufferUsages.CopyDestination), MemoryType.Readback));
        uint[] actual = SubmitAndRead(backend, device, readback, context =>
        {
            foreach (Buffer input in new[] { first, second })
                backend.Barrier(context, new BufferBarrier(input, input.InitialSync, PipelineSync.ComputeShading,
                    input.InitialAccess, ResourceAccess.ShaderResource));
            foreach (Buffer output in new[] { firstOutput, secondOutput })
                backend.Barrier(context, new BufferBarrier(output, output.InitialSync, PipelineSync.ComputeShading,
                    output.InitialAccess, ResourceAccess.UnorderedAccess));
            foreach (Pipeline pipeline in pipelines)
            {
                backend.SetPipeline(context, pipeline);
                backend.SetTransientParameterBindings(context, new(globals, resources.ToArray(), []));
                backend.Dispatch(context, new(1, 1, 1));
            }
            foreach (Buffer output in new[] { firstOutput, secondOutput })
                backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
                    ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.CopyBuffer(context, new(firstOutput, 0, readback, 0, 4));
            backend.CopyBuffer(context, new(secondOutput, 0, readback, 4, 4));
        });
        Assert.Equal(new uint[] { 107, 219 }, actual);
        GC.KeepAlive(compiled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeparateBindlessDeclarationsSurviveNativeRelocation(bool stripNames)
    {
        VulkanTestShader shader = compiler.Compile("""
            StructuredBuffer<uint> firstValues[];
            StructuredBuffer<uint> secondValues[];
            RWStructuredBuffer<uint> Result;
            uint firstIndex;
            uint secondIndex;
            [shader("compute")][numthreads(1, 1, 1)]
            void computeMain() { Result[0] = firstValues[firstIndex][0] * 100u + secondValues[secondIndex][0]; }
            """, ("computeMain", SlangStage.Compute));
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(default, [new(QueueType.Graphics)]));
        using var objects = new TestObjects(backend, device);
        Buffer first = objects.Upload(11);
        Buffer second = objects.Upload(22);
        BufferSrv firstView = objects.Keep(backend.CreateBufferSrv(device, new(first, BufferRange.Whole, StructureStride: 4)));
        BufferSrv secondView = objects.Keep(backend.CreateBufferSrv(device, new(second, BufferRange.Whole, StructureStride: 4)));
        using DescriptorTable table = backend.CreateDescriptorTable(device,
            [new(ResourceBindingType.BufferSrv, StructureStride: 4), new(ResourceBindingType.BufferSrv, StructureStride: 4)]);
        uint firstIndex = backend.GetDescriptorIndex(table, 0).Value;
        uint secondIndex = backend.GetDescriptorIndex(table, 1).Value;
        Assert.NotEqual(firstIndex, secondIndex);
        backend.WriteDescriptor(table, 0, ResourceBinding.ReadOnlyBuffer(firstView));
        backend.WriteDescriptor(table, 1, ResourceBinding.ReadOnlyBuffer(secondView));
        backend.PublishDescriptors(device);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.Entries[0], new ReadOnlySequence<byte>(Code(shader.GetEntryCode(0), stripNames))));
        Buffer output = objects.Output();
        BufferUav outputView = objects.Keep(backend.CreateBufferUav(device, new(output, BufferRange.Whole, StructureStride: 4)));
        Buffer readback = objects.Keep(backend.CreateBuffer(device, new(4, BufferUsages.CopyDestination), MemoryType.Readback));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        TypeLayoutReflection data = Contents(globals);
        byte[] ordinary = new byte[checked((int)data.GetSize(SlangParameterCategory.Uniform))];
        var found = new HashSet<string>();
        for (uint index = 0; index < data.FieldCount; index++)
        {
            VariableLayoutReflection field = data.GetFieldByIndex(index);
            if (field.Name is not ("firstIndex" or "secondIndex")) continue;
            Assert.True(found.Add(field.Name));
            Assert.True(BitConverter.TryWriteBytes(ordinary.AsSpan(checked((int)field.GetOffset(SlangParameterCategory.Uniform)), 4),
                field.Name == "firstIndex" ? firstIndex : secondIndex));
        }
        Assert.Equal(2, found.Count);
        uint[] actual = SubmitAndRead(backend, device, readback, context =>
        {
            foreach (Buffer input in new[] { first, second })
                backend.Barrier(context, new BufferBarrier(input, input.InitialSync, PipelineSync.ComputeShading,
                    input.InitialAccess, ResourceAccess.ShaderResource));
            backend.Barrier(context, new BufferBarrier(output, output.InitialSync, PipelineSync.ComputeShading,
                output.InitialAccess, ResourceAccess.UnorderedAccess));
            backend.SetPipeline(context, pipeline);
            backend.SetTransientParameterBindings(context, new(globals, [ResourceBinding.WritableBuffer(outputView)], ordinary));
            backend.Dispatch(context, new(1, 1, 1));
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.CopyBuffer(context, new(output, 0, readback, 0, 4));
        });
        Assert.Equal(new uint[] { 1122 }, actual);
        GC.KeepAlive(shader);
    }

    [Fact]
    public void NamedAndAnonymousSameNameBindlessDeclarationsKeepTheirSourceIdentity()
    {
        VulkanTestShader shader = compiler.Compile("""
            StructuredBuffer<uint> SharedValues[];
            RWStructuredBuffer<uint> Result;
            uint firstIndex;
            uint secondIndex;
            uint ReadGlobal(uint index) { return SharedValues[index][0]; }
            [shader("compute")][numthreads(1, 1, 1)]
            void computeMain(uniform StructuredBuffer<uint> SharedValues[])
            {
                Result[0] = ReadGlobal(firstIndex) * 100u + SharedValues[secondIndex][0];
            }
            """, ("computeMain", SlangStage.Compute));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        VariableLayoutReflection entry = shader.Entries[0].VarLayout;
        SlangDescriptorLocation firstLocation = NamedLocation(globals, "SharedValues");
        SlangDescriptorLocation secondLocation = NamedLocation(entry, "SharedValues");
        Assert.Equal(firstLocation.Category, secondLocation.Category);
        Assert.Equal(firstLocation.Space, secondLocation.Space);
        Assert.True(firstLocation.Register < secondLocation.Register);
        uint anonymousId = NamedDescriptor(shader.GetEntryCode(0), firstLocation, "SharedValues");
        uint namedId = NamedDescriptor(shader.GetEntryCode(0), secondLocation, "SharedValues");
        Assert.NotEqual(anonymousId, namedId);
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(default, [new(QueueType.Graphics)]));
        using var objects = new TestObjects(backend, device);
        Buffer first = objects.Upload(11);
        Buffer second = objects.Upload(22);
        BufferSrv firstView = objects.Keep(backend.CreateBufferSrv(device, new(first, BufferRange.Whole, StructureStride: 4)));
        BufferSrv secondView = objects.Keep(backend.CreateBufferSrv(device, new(second, BufferRange.Whole, StructureStride: 4)));
        using DescriptorTable table = backend.CreateDescriptorTable(device,
            [new(ResourceBindingType.BufferSrv, StructureStride: 4), new(ResourceBindingType.BufferSrv, StructureStride: 4)]);
        uint firstIndex = backend.GetDescriptorIndex(table, 0).Value;
        uint secondIndex = backend.GetDescriptorIndex(table, 1).Value;
        Assert.NotEqual(firstIndex, secondIndex);
        backend.WriteDescriptor(table, 0, ResourceBinding.ReadOnlyBuffer(firstView));
        backend.WriteDescriptor(table, 1, ResourceBinding.ReadOnlyBuffer(secondView));
        backend.PublishDescriptors(device);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.Entries[0], new ReadOnlySequence<byte>(Code(shader.GetEntryCode(0), true, anonymousId))));
        Buffer output = objects.Output();
        BufferUav outputView = objects.Keep(backend.CreateBufferUav(device, new(output, BufferRange.Whole, StructureStride: 4)));
        Buffer readback = objects.Keep(backend.CreateBuffer(device, new(4, BufferUsages.CopyDestination), MemoryType.Readback));
        TypeLayoutReflection data = Contents(globals);
        byte[] ordinary = new byte[checked((int)data.GetSize(SlangParameterCategory.Uniform))];
        var found = new HashSet<string>();
        for (uint index = 0; index < data.FieldCount; index++)
        {
            VariableLayoutReflection field = data.GetFieldByIndex(index);
            if (field.Name is not ("firstIndex" or "secondIndex")) continue;
            Assert.True(found.Add(field.Name));
            Assert.True(BitConverter.TryWriteBytes(ordinary.AsSpan(checked((int)field.GetOffset(SlangParameterCategory.Uniform)), 4),
                field.Name == "firstIndex" ? firstIndex : secondIndex));
        }
        Assert.Equal(2, found.Count);
        uint[] actual = SubmitAndRead(backend, device, readback, context =>
        {
            foreach (Buffer input in new[] { first, second })
                backend.Barrier(context, new BufferBarrier(input, input.InitialSync, PipelineSync.ComputeShading,
                    input.InitialAccess, ResourceAccess.ShaderResource));
            backend.Barrier(context, new BufferBarrier(output, output.InitialSync, PipelineSync.ComputeShading,
                output.InitialAccess, ResourceAccess.UnorderedAccess));
            backend.SetPipeline(context, pipeline);
            backend.SetTransientParameterBindings(context, new(globals, [ResourceBinding.WritableBuffer(outputView)], ordinary));
            backend.SetTransientParameterBindings(context, new(entry, [], []));
            backend.Dispatch(context, new(1, 1, 1));
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.CopyBuffer(context, new(output, 0, readback, 0, 4));
        });
        Assert.Equal(new uint[] { 1122 }, actual);
        GC.KeepAlive(shader);
    }

    [Fact]
    public void AmbiguousNamedDescriptorWithoutItsDeclaredCoordinateIsRejected()
    {
        ModuleShader compiled = CompileModules();
        VulkanTestShader shader = compiled.Shader;
        byte[] invalid = MoveSharedInput(shader.GetEntryCode(0), 500);
        byte[] original = invalid.ToArray();
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(default, [new(QueueType.Graphics)]));
        Assert.Throws<GraphicsException>(() =>
        {
            using Pipeline pipeline = backend.CreateComputePipeline(device,
                new(shader.SlangCode, shader.Reflection, shader.Entries[0], new ReadOnlySequence<byte>(invalid)));
        });
        Assert.Equal(original, invalid);
        GC.KeepAlive(compiled);
    }

    private static ModuleShader CompileModules()
    {
        VulkanTestCompiler.Require(Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession global), "global", null);
        SessionDesc description = new()
        {
            Targets = [new() { Format = SlangCompileTarget.Spirv, Profile = global.FindProfile("glsl_460"),
                Flags = SlangTargetFlags.GenerateSpirvDirectly }],
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            CompilerOptionEntries = [new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(0)),
                new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0))],
        };
        VulkanTestCompiler.Require(global.CreateSession(description, out ISession session), "session", null);
        string[] sources =
        [
            "StructuredBuffer<uint> SharedInput; RWStructuredBuffer<uint> ResultA; [shader(\"compute\")][numthreads(1,1,1)] void entryA() { ResultA[0] = SharedInput[0] + 100u; }",
            "StructuredBuffer<uint> SharedInput; RWStructuredBuffer<uint> ResultB; [shader(\"compute\")][numthreads(1,1,1)] void entryB() { ResultB[0] = SharedInput[0] + 200u; }",
        ];
        IModule[] modules = new IModule[2];
        IEntryPoint[] entries = new IEntryPoint[2];
        for (int index = 0; index < 2; index++)
        {
            string name = "descriptor_identity_" + index;
            modules[index] = session.LoadModuleFromSource(name, Path.Combine(AppContext.BaseDirectory, name + ".slang"),
                Slang.CreateBlob(sources[index]), out _) ?? throw new InvalidDataException(name);
            VulkanTestCompiler.Require(modules[index].FindEntryPointByName(index == 0 ? "entryA" : "entryB", out entries[index]), "entry", null);
        }
        VulkanTestCompiler.Require(session.CreateCompositeComponentType([modules[0], modules[1], entries[0], entries[1]],
            out IComponentType composite, out ISlangBlob? diagnostics), "composition", diagnostics);
        VulkanTestCompiler.Require(composite.Link(out IComponentType linked, out diagnostics), "link", diagnostics);
        ShaderReflection reflection = linked.GetLayout(0, out _);
        Assert.NotEqual(ShaderReflection.Null, reflection);
        EntryPointReflection[] reflected = [reflection.GetEntryPointByIndex(0), reflection.GetEntryPointByIndex(1)];
        Assert.Equal(2, checked((int)reflection.EntryPointCount));
        byte[][] code = new byte[2][];
        for (int index = 0; index < 2; index++)
        {
            Assert.Equal(SlangStage.Compute, reflected[index].Stage);
            VulkanTestCompiler.Require(linked.GetEntryPointCode(index, 0, out ISlangBlob blob, out diagnostics), "code", diagnostics);
            code[index] = blob.Buffer.ToArray();
        }
        return new(new VulkanTestShader(global, session, modules[0], entries, composite, linked, reflection, reflected, code, []), modules);
    }

    private static byte[] Code(ReadOnlySequence<byte> code, bool stripNames, uint? onlyTarget = null)
    {
        byte[] bytes = code.ToArray();
        if (!stripNames) return bytes;
        uint[] words = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
        var result = new List<uint>(words.Take(5));
        for (int index = 5; index < words.Length;)
        {
            int count = checked((int)(words[index] >> 16));
            Assert.True(count > 0 && index <= words.Length - count);
            if ((words[index] & 0xffff) != 5 || (onlyTarget.HasValue && words[index + 1] != onlyTarget.Value))
                result.AddRange(words.AsSpan(index, count).ToArray());
            index += count;
        }
        return MemoryMarshal.AsBytes(result.ToArray().AsSpan()).ToArray();
    }

    private static SlangDescriptorLocation NamedLocation(VariableLayoutReflection root, string name)
    {
        TypeLayoutReflection data = Contents(root);
        var locations = new List<SlangDescriptorLocation>();
        for (nint range = 0; range < data.BindingRangeCount; range++)
        {
            if (data.GetBindingRangeLeafVariable(range).Name != name) continue;
            Assert.Equal((nint)1, data.GetBindingRangeDescriptorRangeCount(range));
            locations.Add(SlangDescriptorLocation.FromLayout(root, data,
                data.GetBindingRangeDescriptorSetIndex(range), data.GetBindingRangeFirstDescriptorRangeIndex(range)));
        }
        return Assert.Single(locations);
    }

    private static uint NamedDescriptor(ReadOnlySequence<byte> code, SlangDescriptorLocation location, string expectedName)
    {
        uint[] words = MemoryMarshal.Cast<byte, uint>(code.ToArray()).ToArray();
        var names = new Dictionary<uint, string>();
        var sets = new Dictionary<uint, uint>();
        var bindings = new Dictionary<uint, uint>();
        for (int index = 5; index < words.Length;)
        {
            int count = checked((int)(words[index] >> 16));
            uint opcode = words[index] & 0xffff;
            Assert.True(count > 0 && index <= words.Length - count);
            if (opcode == 5 && count >= 3)
            {
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(words.AsSpan(index + 2, count - 2));
                int end = bytes.IndexOf((byte)0);
                names[words[index + 1]] = Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
            }
            else if (opcode == 71 && count >= 4)
            {
                if (words[index + 2] == 33) bindings[words[index + 1]] = words[index + 3];
                if (words[index + 2] == 34) sets[words[index + 1]] = words[index + 3];
            }
            index += count;
        }
        uint target = Assert.Single(bindings, pair => pair.Value == location.Register &&
            sets.TryGetValue(pair.Key, out uint set) && set == location.Space &&
            names.TryGetValue(pair.Key, out string? candidateName) &&
            (candidateName == expectedName || candidateName.EndsWith("." + expectedName, StringComparison.Ordinal))).Key;
        Assert.True(names.TryGetValue(target, out string? name));
        Assert.NotNull(name);
        Assert.True(name == expectedName || name.EndsWith("." + expectedName, StringComparison.Ordinal));
        return target;
    }

    private static byte[] MoveSharedInput(ReadOnlySequence<byte> code, uint binding)
    {
        uint[] words = MemoryMarshal.Cast<byte, uint>(code.ToArray()).ToArray();
        var ids = new List<uint>();
        for (int index = 5; index < words.Length;)
        {
            int count = checked((int)(words[index] >> 16));
            Assert.True(count > 0 && index <= words.Length - count);
            if ((words[index] & 0xffff) == 5 && count >= 3)
            {
                ReadOnlySpan<byte> text = MemoryMarshal.AsBytes(words.AsSpan(index + 2, count - 2));
                int end = text.IndexOf((byte)0);
                string name = Encoding.UTF8.GetString(end < 0 ? text : text[..end]);
                if (name == "SharedInput" || name.EndsWith(".SharedInput", StringComparison.Ordinal)) ids.Add(words[index + 1]);
            }
            index += count;
        }
        uint target = Assert.Single(ids);
        bool changed = false;
        for (int index = 5; index < words.Length;)
        {
            int count = checked((int)(words[index] >> 16));
            if ((words[index] & 0xffff) == 71 && count >= 4 && words[index + 1] == target && words[index + 2] == 33)
            {
                Assert.False(changed);
                words[index + 3] = binding;
                changed = true;
            }
            index += count;
        }
        Assert.True(changed);
        return MemoryMarshal.AsBytes(words.AsSpan()).ToArray();
    }

    private static TypeLayoutReflection Contents(VariableLayoutReflection globals)
    {
        Assert.NotEqual(VariableLayoutReflection.Null, globals);
        TypeLayoutReflection data = globals.TypeLayout.UnwrapArray();
        return data.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock ? data.ElementTypeLayout.UnwrapArray() : data;
    }

    private static uint[] SubmitAndRead(IGraphicsBackend backend, Device device, Buffer readback, Action<CommandContext> record)
    {
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        record(context);
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Graphics), new([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        return MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray();
    }

    private sealed record ModuleShader(VulkanTestShader Shader, IModule[] Modules);
    private sealed class TestObjects(IGraphicsBackend backend, Device device) : IDisposable
    {
        private readonly List<IDisposable> _resources = [];
        internal T Keep<T>(T value) where T : IDisposable { _resources.Add(value); return value; }
        internal Buffer Upload(uint value)
        {
            Buffer buffer = Keep(backend.CreateBuffer(device, new(4, BufferUsages.ShaderRead), MemoryType.Upload));
            using MappedBuffer mapped = backend.Map(buffer, MapType.Write, BufferRange.Whole);
            Assert.True(BitConverter.TryWriteBytes(mapped.Bytes, value));
            mapped.Flush(mapped.Range);
            return buffer;
        }
        internal Buffer Output() => Keep(backend.CreateBuffer(device, new(4, BufferUsages.ShaderWrite | BufferUsages.CopySource)));
        public void Dispose() { for (int index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose(); }
    }
}
