using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    private const uint RootLayoutSchemaVersion = 3;

    private enum ParameterHeap : byte
    {
        Resource,
        Sampler,
    }

    [Flags]
    private enum ReflectedStages : ushort
    {
        None = 0,
        Vertex = 1 << 0,
        Hull = 1 << 1,
        Domain = 1 << 2,
        Geometry = 1 << 3,
        Pixel = 1 << 4,
        Compute = 1 << 5,
        Amplification = 1 << 6,
        Mesh = 1 << 7,
        RayTracing = 1 << 8,
        All = ushort.MaxValue,
    }

    private enum RootBindingRole : byte
    {
        RootConstants,
        OrdinaryConstantBuffer,
        Resource,
        Sampler,
    }

    private readonly record struct ParameterLeaf(
        ResourceBindingType Type,
        ParameterHeap Heap,
        DescriptorRangeType RangeType,
        uint Binding,
        uint Space,
        uint DescriptorCount,
        bool Unbounded,
        uint HeapOffset);

    private readonly record struct ImmutableSamplerRange(
        VariableLayoutReflection Field,
        ImmutableSamplerReflection State,
        uint Binding,
        uint Space,
        uint Count);

    private sealed class D3D12ParameterBlockShape
    {
        private D3D12ParameterBlockShape(
            ParameterBlockLayoutReflection reflection,
            ParameterLeaf[] leaves,
            ImmutableSamplerRange[] immutableSamplers,
            uint resourceDescriptorCount,
            uint samplerDescriptorCount)
        {
            Reflection = reflection;
            Leaves = leaves;
            ImmutableSamplers = immutableSamplers;
            ResourceDescriptorCount = resourceDescriptorCount;
            SamplerDescriptorCount = samplerDescriptorCount;
        }

        internal ParameterBlockLayoutReflection Reflection { get; }
        internal ParameterLeaf[] Leaves { get; }
        internal ImmutableSamplerRange[] ImmutableSamplers { get; }
        internal uint ResourceDescriptorCount { get; }
        internal uint SamplerDescriptorCount { get; }
        internal uint OrdinaryDataSize => Reflection.OrdinaryDataSize;
        internal SlangBindingType OrdinaryDataBindingType =>
            Reflection.OrdinaryDataBindingType;
        internal bool UsesRootConstants => OrdinaryDataBindingType is
            SlangBindingType.InlineUniformData or SlangBindingType.PushConstant;
        internal bool UsesOrdinaryConstantBuffer =>
            OrdinaryDataBindingType == SlangBindingType.ConstantBuffer;
        internal uint OrdinaryConstantCount =>
            checked((OrdinaryDataSize + sizeof(uint) - 1) / sizeof(uint));
        internal uint OrdinaryRegister => Reflection.OrdinaryDataBindingIndex;
        internal uint OrdinarySpace => Reflection.OrdinaryDataBindingSpace;

        internal static D3D12ParameterBlockShape Compile(
            VariableLayoutReflection layout)
        {
            ParameterBlockLayoutReflection reflection =
                ParameterBlockLayoutReflection.Reflect(layout);
            if (reflection.OrdinaryDataSize != 0 &&
                reflection.OrdinaryDataBindingType is not (
                    SlangBindingType.ConstantBuffer or
                    SlangBindingType.InlineUniformData or
                    SlangBindingType.PushConstant))
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang produced unsupported ordinary-data binding type " +
                    $"{reflection.OrdinaryDataBindingType} for layout '{layout.Name}'.");
            }
            ReadOnlySpan<ParameterBindingRangeReflection> reflectedRanges =
                reflection.BindingRanges;
            List<ParameterLeaf> leaves = [];
            List<ImmutableSamplerRange> immutableSamplers = [];
            uint resources = 0;
            uint samplers = 0;
            for (int index = 0; index < reflectedRanges.Length; index++)
            {
                ref readonly ParameterBindingRangeReflection source =
                    ref reflectedRanges[index];
                if (source.ImmutableSampler is ImmutableSamplerReflection immutableSampler)
                {
                    if (source.Field.TypeLayout.IsArray || source.BindingCount != 1)
                    {
                        throw new GraphicsException(
                            GraphicsError.PipelineCreation,
                            $"D3D12 immutable sampler '{source.Field.Name}' must be a " +
                            "scalar sampler. Static samplers cannot represent an indexed " +
                            $"Slang sampler range of count {source.BindingCount}.");
                    }
                    immutableSamplers.Add(new ImmutableSamplerRange(
                        source.Field,
                        immutableSampler,
                        source.BindingIndex,
                        source.BindingSpace,
                        source.BindingCount));
                    continue;
                }
                (ResourceBindingType type, ParameterHeap heap, DescriptorRangeType rangeType) =
                    ToNativeBinding(source);
                uint offset = heap == ParameterHeap.Resource ? resources : samplers;
                leaves.Add(new ParameterLeaf(
                    type,
                    heap,
                    rangeType,
                    source.BindingIndex,
                    source.BindingSpace,
                    source.BindingCount,
                    source.IsUnbounded,
                    offset));
                if (!source.IsUnbounded)
                {
                    if (heap == ParameterHeap.Resource)
                        resources = checked(resources + source.BindingCount);
                    else
                        samplers = checked(samplers + source.BindingCount);
                }
            }

            return new D3D12ParameterBlockShape(
                reflection,
                [.. leaves],
                [.. immutableSamplers],
                resources,
                samplers);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void RequireMaterializationShape(
            ReadOnlySpan<ResourceBinding> bindings,
            ReadOnlySpan<byte> ordinaryData)
        {
            if (ordinaryData.Length != OrdinaryDataSize)
                ThrowOrdinaryDataSize(ordinaryData.Length);
            if (bindings.Length != Reflection.BoundedResourceCount)
                ThrowBoundedResourceCount(bindings.Length);
            if (Leaves.Length != 0)
                RequireBindingTypes(bindings);
        }

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void ThrowOrdinaryDataSize(int actual)
        {
            throw new ArgumentException(
                $"The Slang parameter layout requires exactly {OrdinaryDataSize} " +
                $"ordinary-data bytes; the supplied packet has {actual} bytes.",
                "ordinaryData");
        }

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void ThrowBoundedResourceCount(int actual)
        {
            throw new ArgumentException(
                $"The Slang parameter layout requires exactly " +
                $"{Reflection.BoundedResourceCount} bounded resource bindings; the supplied " +
                $"packet has {actual} bindings.",
                "bindings");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void RequireBindingTypes(ReadOnlySpan<ResourceBinding> bindings)
        {
            int ordinal = 0;
            foreach (ParameterLeaf leaf in Leaves)
            {
                if (leaf.Unbounded)
                    continue;
                for (uint element = 0; element < leaf.DescriptorCount; element++)
                {
                    ref readonly ResourceBinding binding = ref bindings[ordinal];
                    if (binding.Type != leaf.Type || binding.ArrayElement != element)
                    {
                        throw new ArgumentException(
                            $"Resource binding {ordinal} must be {leaf.Type} array element " +
                            $"{element}; the supplied binding is {binding.Type} element " +
                            $"{binding.ArrayElement}.",
                            nameof(bindings));
                    }
                    ordinal++;
                }
            }
        }

        private static (
            ResourceBindingType Type,
            ParameterHeap Heap,
            DescriptorRangeType RangeType) ToNativeBinding(
                in ParameterBindingRangeReflection source)
        {
            SlangBindingType type = source.Type & SlangBindingType.BaseMask;
            bool writable = (source.Type & SlangBindingType.MutableFlag) != 0;
            return type switch
            {
                SlangBindingType.Sampler =>
                    (ResourceBindingType.Sampler, ParameterHeap.Sampler,
                        DescriptorRangeType.Sampler),
                SlangBindingType.Texture => writable
                    ? (ResourceBindingType.TextureUav, ParameterHeap.Resource,
                        DescriptorRangeType.Uav)
                    : (ResourceBindingType.TextureSrv, ParameterHeap.Resource,
                        DescriptorRangeType.Srv),
                SlangBindingType.TypedBuffer or SlangBindingType.RawBuffer => writable
                    ? (ResourceBindingType.BufferUav, ParameterHeap.Resource,
                        DescriptorRangeType.Uav)
                    : (ResourceBindingType.BufferSrv, ParameterHeap.Resource,
                        DescriptorRangeType.Srv),
                SlangBindingType.RayTracingAccelerationStructure =>
                    (ResourceBindingType.AccelerationStructure, ParameterHeap.Resource,
                        DescriptorRangeType.Srv),
                _ => throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang produced unsupported binding type {source.Type} for field " +
                    $"'{source.Field.Name}'."),
            };
        }
    }

    private readonly record struct RootBindingKey(
        RootBindingRole Role,
        uint Space,
        DescriptorRangeType RangeType,
        uint Register,
        uint Count,
        bool Unbounded);

    private sealed class RootDeclaration
    {
        internal RootDeclaration(RootBindingKey key, ReflectedStages stages)
        {
            Key = key;
            Stages = stages;
        }

        internal RootBindingKey Key { get; }
        internal ReflectedStages Stages { get; set; }
        internal uint RootParameterIndex { get; set; }
        internal uint RootArgumentOffset { get; set; }
    }

    private readonly record struct ImmutableSamplerKey(uint Space, uint Register);

    private sealed class ImmutableSamplerDeclaration
    {
        internal ImmutableSamplerDeclaration(
            ImmutableSamplerKey key,
            ImmutableSamplerReflection state,
            ReflectedStages stages,
            string fieldName)
        {
            Key = key;
            State = state;
            Stages = stages;
            FieldName = fieldName;
        }

        internal ImmutableSamplerKey Key { get; }
        internal ImmutableSamplerReflection State { get; }
        internal ReflectedStages Stages { get; set; }
        internal string FieldName { get; }
    }

    private readonly record struct BlockLeafBinding(
        uint RootParameterIndex,
        uint RootArgumentOffset,
        ParameterHeap Heap,
        ResourceBindingType Type,
        uint HeapOffset,
        uint DescriptorCount,
        bool Unbounded);

    private readonly record struct OrdinaryRootBinding(
        uint RootParameterIndex,
        uint RootArgumentOffset,
        bool UsesRootConstants,
        uint ConstantCount);

    private sealed class D3D12ParameterBlockLayout
    {
        internal D3D12ParameterBlockLayout(
            D3D12ParameterBlockShape shape,
            BlockLeafBinding[] leaves,
            OrdinaryRootBinding? ordinaryRoot)
        {
            Shape = shape;
            Leaves = leaves;
            OrdinaryRoot = ordinaryRoot;
            OrdinaryConstantBuffer16RootParameter =
                leaves.Length == 0 &&
                shape.OrdinaryDataSize == 16 &&
                ordinaryRoot is { UsesRootConstants: false } ordinary
                    ? checked((int)ordinary.RootParameterIndex)
                    : -1;
        }

        internal D3D12ParameterBlockShape Shape { get; }
        internal BlockLeafBinding[] Leaves { get; }
        internal OrdinaryRootBinding? OrdinaryRoot { get; }
        internal int OrdinaryConstantBuffer16RootParameter { get; }
    }

    private readonly record struct DefaultRootTable(
        uint RootParameterIndex,
        ParameterHeap Heap);

    private sealed class D3D12RootLayout
    {
        private readonly NativeLease _native;
        private readonly Dictionary<VariableLayoutReflection, D3D12ParameterBlockLayout> _blocks;
        private int _released;

        internal D3D12RootLayout(
            ID3D12RootSignature* native,
            Dictionary<VariableLayoutReflection, D3D12ParameterBlockLayout> blocks,
            DefaultRootTable[] defaults,
            StaticSamplerDesc[] staticSamplers,
            byte[] serialized,
            uint rootArgumentSize)
        {
            _native = new NativeLease((IUnknown*)native, ownsReference: true);
            _blocks = blocks;
            DefaultTables = defaults;
            StaticSamplers = staticSamplers;
            Serialized = serialized;
            RootArgumentSize = rootArgumentSize;
        }

        internal ID3D12RootSignature* Native =>
            (ID3D12RootSignature*)_native.Pointer;
        internal NativeLease NativeLifetime => _native;
        internal DefaultRootTable[] DefaultTables { get; }
        internal StaticSamplerDesc[] StaticSamplers { get; }
        internal byte[] Serialized { get; }
        internal uint RootArgumentSize { get; }

        internal bool TryGetBlock(
            VariableLayoutReflection layout,
            out D3D12ParameterBlockLayout block) =>
            _blocks.TryGetValue(layout, out block!);

        internal D3D12ParameterBlockLayout GetBlock(VariableLayoutReflection layout)
        {
            if (!_blocks.TryGetValue(layout, out D3D12ParameterBlockLayout? block))
            {
                throw new ArgumentException(
                    "The Slang parameter layout is not part of the current Pipeline.",
                    nameof(layout));
            }
            return block;
        }

        internal void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _native.Release();
        }
    }

    private sealed class D3D12RootLayoutBuilder
    {
        private readonly D3D12Backend _backend;
        private readonly D3D12Device _device;
        private readonly PipelineType _pipelineType;
        private readonly bool _allowInputAssembler;
        private readonly bool _allowStreamOutput;
        private readonly bool _localRootSignature;
        private readonly Dictionary<RootBindingKey, RootDeclaration> _declarations = [];
        private readonly Dictionary<ImmutableSamplerKey, ImmutableSamplerDeclaration>
            _immutableSamplers = [];
        private readonly Dictionary<VariableLayoutReflection, BlockCandidate> _blocks = [];

        private D3D12RootLayoutBuilder(
            D3D12Backend backend,
            D3D12Device device,
            PipelineType pipelineType,
            bool allowInputAssembler,
            bool allowStreamOutput,
            bool localRootSignature)
        {
            _backend = backend;
            _device = device;
            _pipelineType = pipelineType;
            _allowInputAssembler = allowInputAssembler;
            _allowStreamOutput = allowStreamOutput;
            _localRootSignature = localRootSignature;
        }

        internal static D3D12RootLayout Compile(
            D3D12Backend backend,
            D3D12Device device,
            IComponentType program,
            ShaderReflection reflection,
            ReadOnlySpan<EntryPointReflection> entryPoints,
            PipelineType pipelineType,
            bool allowInputAssembler,
            bool allowStreamOutput)
        {
            return CompileCore(
                backend,
                device,
                program,
                reflection,
                entryPoints,
                pipelineType,
                allowInputAssembler,
                allowStreamOutput,
                includeGlobal: true,
                includeEntries: true,
                localRootSignature: false);
        }

        internal static D3D12RootLayout CompileGlobal(
            D3D12Backend backend,
            D3D12Device device,
            IComponentType program,
            ShaderReflection reflection,
            PipelineType pipelineType) =>
            CompileCore(
                backend,
                device,
                program,
                reflection,
                ReadOnlySpan<EntryPointReflection>.Empty,
                pipelineType,
                allowInputAssembler: false,
                allowStreamOutput: false,
                includeGlobal: true,
                includeEntries: false,
                localRootSignature: false);

        internal static D3D12RootLayout CompileLocal(
            D3D12Backend backend,
            D3D12Device device,
            IComponentType program,
            ShaderReflection reflection,
            ReadOnlySpan<EntryPointReflection> entryPoints,
            PipelineType pipelineType) =>
            CompileCore(
                backend,
                device,
                program,
                reflection,
                entryPoints,
                pipelineType,
                allowInputAssembler: false,
                allowStreamOutput: false,
                includeGlobal: false,
                includeEntries: true,
                localRootSignature: true);

        private static D3D12RootLayout CompileCore(
            D3D12Backend backend,
            D3D12Device device,
            IComponentType program,
            ShaderReflection reflection,
            ReadOnlySpan<EntryPointReflection> entryPoints,
            PipelineType pipelineType,
            bool allowInputAssembler,
            bool allowStreamOutput,
            bool includeGlobal,
            bool includeEntries,
            bool localRootSignature)
        {
            D3D12RootLayoutBuilder builder = new(
                backend,
                device,
                pipelineType,
                allowInputAssembler,
                allowStreamOutput,
                localRootSignature);

            if (includeGlobal &&
                reflection.GetGlobalParamsVarLayout() is VariableLayoutReflection global &&
                global != VariableLayoutReflection.Null)
            {
                builder.AddBlock(global, ReflectedStages.All);
            }
            if (includeEntries)
            {
                foreach (EntryPointReflection entryPoint in entryPoints)
                {
                    ReflectedStages stage = ToReflectedStage(entryPoint.Stage);
                    VariableLayoutReflection container = entryPoint.VarLayout;
                    if (container != VariableLayoutReflection.Null)
                        builder.AddBlock(container, stage);
                }
            }
            return builder.Materialize(program);
        }

        private void AddBlock(VariableLayoutReflection layout, ReflectedStages stages)
        {
            if (layout == VariableLayoutReflection.Null)
                return;

            D3D12ParameterBlockShape shape = D3D12ParameterBlockShape.Compile(layout);
            RootBindingKey? ordinary = null;
            if (shape.OrdinaryDataSize != 0)
            {
                RootBindingRole role = shape.UsesRootConstants
                    ? RootBindingRole.RootConstants
                    : RootBindingRole.OrdinaryConstantBuffer;
                ordinary = new RootBindingKey(
                    role,
                    shape.OrdinarySpace,
                    DescriptorRangeType.Cbv,
                    shape.OrdinaryRegister,
                    shape.UsesRootConstants ? shape.OrdinaryConstantCount : 1,
                    false);
                AddDeclaration(ordinary.Value, stages);
            }

            RootBindingKey[] leaves = new RootBindingKey[shape.Leaves.Length];
            for (int index = 0; index < shape.Leaves.Length; index++)
            {
                ParameterLeaf leaf = shape.Leaves[index];
                RootBindingKey key = new(
                    leaf.Heap == ParameterHeap.Sampler
                        ? RootBindingRole.Sampler
                        : RootBindingRole.Resource,
                    leaf.Space,
                    leaf.RangeType,
                    leaf.Binding,
                    leaf.Unbounded ? uint.MaxValue : leaf.DescriptorCount,
                    leaf.Unbounded);
                leaves[index] = key;
                AddDeclaration(key, stages);
            }

            foreach (ImmutableSamplerRange range in shape.ImmutableSamplers)
            {
                for (uint element = 0; element < range.Count; element++)
                {
                    AddImmutableSampler(
                        new ImmutableSamplerKey(
                            range.Space,
                            checked(range.Binding + element)),
                        range.State,
                        stages,
                        range.Field.Name);
                }
            }

            if (_blocks.TryGetValue(layout, out BlockCandidate? existing))
            {
                existing.Stages |= stages;
                foreach (VariableLayoutReflection child in shape.Reflection.ChildParameterBlocks)
                    AddBlock(child, stages);
                return;
            }
            _blocks.Add(layout, new BlockCandidate(shape, leaves, ordinary, stages));
            foreach (VariableLayoutReflection child in shape.Reflection.ChildParameterBlocks)
                AddBlock(child, stages);
        }

        private void AddDeclaration(RootBindingKey key, ReflectedStages stages)
        {
            if (key.Role == RootBindingRole.Sampler)
            {
                foreach (ImmutableSamplerKey immutable in _immutableSamplers.Keys)
                {
                    if (SamplerRangeContains(key, immutable))
                    {
                        throw new GraphicsException(
                            GraphicsError.NativeFailure,
                            $"Slang maps runtime and immutable samplers to s" +
                            $"{immutable.Register}, space {immutable.Space}.");
                    }
                }
            }
            if (_declarations.TryGetValue(key, out RootDeclaration? existing))
                existing.Stages |= stages;
            else
                _declarations.Add(key, new RootDeclaration(key, stages));
        }

        private void AddImmutableSampler(
            ImmutableSamplerKey key,
            ImmutableSamplerReflection state,
            ReflectedStages stages,
            string fieldName)
        {
            foreach (RootBindingKey declaration in _declarations.Keys)
            {
                if (SamplerRangeContains(declaration, key))
                {
                    throw new GraphicsException(
                        GraphicsError.NativeFailure,
                        $"Slang maps immutable sampler '{fieldName}' and a runtime " +
                        $"sampler to s{key.Register}, space {key.Space}.");
                }
            }

            if (_immutableSamplers.TryGetValue(
                    key,
                    out ImmutableSamplerDeclaration? existing))
            {
                if (existing.State != state)
                {
                    throw new GraphicsException(
                        GraphicsError.NativeFailure,
                        $"Slang immutable samplers '{existing.FieldName}' and " +
                        $"'{fieldName}' map conflicting states to s{key.Register}, " +
                        $"space {key.Space}.");
                }
                existing.Stages |= stages;
                return;
            }

            _immutableSamplers.Add(
                key,
                new ImmutableSamplerDeclaration(key, state, stages, fieldName));
        }

        private static bool SamplerRangeContains(
            in RootBindingKey declaration,
            in ImmutableSamplerKey sampler)
        {
            if (declaration.Role != RootBindingRole.Sampler ||
                declaration.Space != sampler.Space ||
                sampler.Register < declaration.Register)
                return false;
            return declaration.Unbounded ||
                sampler.Register - declaration.Register < declaration.Count;
        }

        private D3D12RootLayout Materialize(IComponentType program)
        {
            RootDeclaration[] declarations = [.. _declarations.Values
                .OrderBy(static value => value.Key.Role)
                .ThenBy(static value => value.Key.Space)
                .ThenBy(static value => value.Key.RangeType)
                .ThenBy(static value => value.Key.Register)
                .ThenBy(static value => value.Stages)
                .ThenBy(static value => value.Key.Unbounded)
                .ThenBy(static value => value.Key.Count)];
            for (int index = 0; index < declarations.Length; index++)
            {
                RootDeclaration declaration = declarations[index];
                declaration.RootParameterIndex = checked((uint)index);
                declaration.RootArgumentOffset = index == 0
                    ? 0
                    : checked(
                        declarations[index - 1].RootArgumentOffset +
                        RootArgumentByteSize(declarations[index - 1].Key));
            }

            ImmutableSamplerDeclaration[] immutableDeclarations =
                [.. _immutableSamplers.Values
                    .OrderBy(static value => value.Key.Space)
                    .ThenBy(static value => value.Key.Register)];
            StaticSamplerDesc[] staticSamplers = new StaticSamplerDesc[
                immutableDeclarations.Length];
            for (int index = 0; index < staticSamplers.Length; index++)
                staticSamplers[index] = ToNativeStaticSampler(immutableDeclarations[index]);

            RootParameter1[] parameters = new RootParameter1[declarations.Length];
            DescriptorRange1[] ranges = new DescriptorRange1[declarations.Length];
            fixed (DescriptorRange1* rangePointer = ranges)
            {
                for (int index = 0; index < declarations.Length; index++)
                {
                    RootDeclaration declaration = declarations[index];
                    ShaderVisibility visibility = ToShaderVisibility(declaration.Stages);
                    if (declaration.Key.Role == RootBindingRole.RootConstants)
                    {
                        parameters[index] = new RootParameter1(
                            RootParameterType.Type32BitConstants,
                            shaderVisibility: visibility,
                            constants: new RootConstants(
                                declaration.Key.Register,
                                declaration.Key.Space,
                                declaration.Key.Count));
                        continue;
                    }
                    if (declaration.Key.Role == RootBindingRole.OrdinaryConstantBuffer)
                    {
                        parameters[index] = new RootParameter1(
                            RootParameterType.TypeCbv,
                            shaderVisibility: visibility,
                            descriptor: new RootDescriptor1(
                                declaration.Key.Register,
                                declaration.Key.Space,
                                RootDescriptorFlags.DataStatic));
                        continue;
                    }

                    DescriptorRangeFlags flags = declaration.Key.Role == RootBindingRole.Sampler
                        ? DescriptorRangeFlags.None
                        : DescriptorRangeFlags.DataVolatile;
                    ranges[index] = new DescriptorRange1(
                        declaration.Key.RangeType,
                        declaration.Key.Count,
                        declaration.Key.Register,
                        declaration.Key.Space,
                        flags,
                        0);
                    parameters[index] = new RootParameter1(
                        RootParameterType.TypeDescriptorTable,
                        shaderVisibility: visibility,
                        descriptorTable: new RootDescriptorTable1(1, rangePointer + index));
                }

                RootSignatureFlags rootFlags = RootFlags();
                ID3D10Blob* serializedBlob = null;
                ID3D10Blob* errorBlob = null;
                byte[] serialized;
                ID3D12RootSignature* rootSignature = null;
                fixed (RootParameter1* parameterPointer = parameters)
                fixed (StaticSamplerDesc* staticSamplerPointer = staticSamplers)
                {
                    RootSignatureDesc1 description = new(
                        checked((uint)parameters.Length),
                        parameterPointer,
                        checked((uint)staticSamplers.Length),
                        staticSamplers.Length == 0 ? null : staticSamplerPointer,
                        rootFlags);
                    VersionedRootSignatureDesc versioned = new(
                        D3DRootSignatureVersion.Version11,
                        desc11: description);
                    int result = _backend._d3d12.SerializeVersionedRootSignature(
                        &versioned,
                        &serializedBlob,
                        &errorBlob);
                    if (result < 0)
                    {
                        string detail = BlobText(errorBlob);
                        ReleaseBlob(serializedBlob);
                        ReleaseBlob(errorBlob);
                        throw new GraphicsException(
                            GraphicsError.NativeFailure,
                            string.IsNullOrWhiteSpace(detail)
                                ? "D3D12 root-signature serialization failed."
                                : $"D3D12 root-signature serialization failed: {detail}",
                            result);
                    }

                    try
                    {
                        serialized = new ReadOnlySpan<byte>(
                            serializedBlob->GetBufferPointer(),
                            checked((int)serializedBlob->GetBufferSize())).ToArray();
                        Guid iid = ID3D12RootSignature.Guid;
                        NativeCall.ThrowIfFailed(
                            _device.Native->CreateRootSignature(
                                _device.EnabledNodeMask,
                                serializedBlob->GetBufferPointer(),
                                serializedBlob->GetBufferSize(),
                                &iid,
                                (void**)&rootSignature),
                            "ID3D12Device::CreateRootSignature");
                    }
                    finally
                    {
                        ReleaseBlob(serializedBlob);
                        ReleaseBlob(errorBlob);
                    }
                }

                try
                {
                    Dictionary<VariableLayoutReflection, D3D12ParameterBlockLayout> blocks = [];
                    foreach ((VariableLayoutReflection layout, BlockCandidate candidate) in _blocks)
                    {
                        BlockLeafBinding[] leafBindings =
                            new BlockLeafBinding[candidate.LeafKeys.Length];
                        for (int index = 0; index < leafBindings.Length; index++)
                        {
                            ParameterLeaf leaf = candidate.Shape.Leaves[index];
                            RootDeclaration declaration = _declarations[candidate.LeafKeys[index]];
                            leafBindings[index] = new BlockLeafBinding(
                                declaration.RootParameterIndex,
                                declaration.RootArgumentOffset,
                                leaf.Heap,
                                leaf.Type,
                                leaf.HeapOffset,
                                leaf.DescriptorCount,
                                leaf.Unbounded);
                        }
                        OrdinaryRootBinding? ordinaryRoot = null;
                        if (candidate.OrdinaryKey is RootBindingKey ordinary)
                        {
                            RootDeclaration declaration = _declarations[ordinary];
                            ordinaryRoot = new OrdinaryRootBinding(
                                declaration.RootParameterIndex,
                                declaration.RootArgumentOffset,
                                ordinary.Role == RootBindingRole.RootConstants,
                                ordinary.Role == RootBindingRole.RootConstants
                                    ? ordinary.Count
                                    : 0);
                        }
                        blocks.Add(
                            layout,
                            new D3D12ParameterBlockLayout(
                                candidate.Shape,
                                leafBindings,
                                ordinaryRoot));
                    }

                    DefaultRootTable[] defaults = declarations
                        .Where(static declaration => declaration.Key.Unbounded)
                        .Select(static declaration => new DefaultRootTable(
                            declaration.RootParameterIndex,
                            declaration.Key.Role == RootBindingRole.Sampler
                                ? ParameterHeap.Sampler
                                : ParameterHeap.Resource))
                        .ToArray();
                    uint rootArgumentSize = _localRootSignature && declarations.Length != 0
                        ? checked(
                            declarations[^1].RootArgumentOffset +
                            RootArgumentByteSize(declarations[^1].Key))
                        : 0;
                    return new D3D12RootLayout(
                        rootSignature,
                        blocks,
                        defaults,
                        staticSamplers,
                        serialized,
                        rootArgumentSize);
                }
                catch
                {
                    _ = rootSignature->Release();
                    throw;
                }
            }
        }

        private static uint RootArgumentByteSize(in RootBindingKey key) =>
            key.Role == RootBindingRole.RootConstants
                ? checked(key.Count * sizeof(uint))
                : sizeof(ulong);

        private static StaticSamplerDesc ToNativeStaticSampler(
            ImmutableSamplerDeclaration declaration)
        {
            ImmutableSamplerReflection state = declaration.State;
            return new StaticSamplerDesc
            {
                Filter = ToNativeStaticFilter(state),
                AddressU = ToNativeStaticAddress(state.AddressU),
                AddressV = ToNativeStaticAddress(state.AddressV),
                AddressW = ToNativeStaticAddress(state.AddressW),
                MipLODBias = state.MipLodBias,
                MaxAnisotropy = state.MaximumAnisotropy,
                ComparisonFunc = ToNativeStaticComparison(state.Comparison),
                BorderColor = state.BorderColor switch
                {
                    SlangStaticSamplerBorderColor.TransparentBlack =>
                        StaticBorderColor.TransparentBlack,
                    SlangStaticSamplerBorderColor.OpaqueBlack =>
                        StaticBorderColor.OpaqueBlack,
                    SlangStaticSamplerBorderColor.OpaqueWhite =>
                        StaticBorderColor.OpaqueWhite,
                    _ => throw new ArgumentOutOfRangeException(nameof(declaration)),
                },
                MinLOD = state.MinimumLod,
                MaxLOD = state.MaximumLod,
                ShaderRegister = declaration.Key.Register,
                RegisterSpace = declaration.Key.Space,
                ShaderVisibility = ToShaderVisibility(declaration.Stages),
            };
        }

        private static Filter ToNativeStaticFilter(
            in ImmutableSamplerReflection state)
        {
            bool comparison = state.Comparison != SlangSamplerComparisonMode.None;
            if (state.MaximumAnisotropy > 1)
                return comparison ? Filter.ComparisonAnisotropic : Filter.Anisotropic;

            return (state.MinFilter, state.MagFilter, state.MipFilter, comparison) switch
            {
                (SlangSamplerFilterMode.Nearest, SlangSamplerFilterMode.Nearest,
                    SlangSamplerFilterMode.Nearest, false) => Filter.MinMagMipPoint,
                (SlangSamplerFilterMode.Nearest, SlangSamplerFilterMode.Nearest,
                    SlangSamplerFilterMode.Linear, false) => Filter.MinMagPointMipLinear,
                (SlangSamplerFilterMode.Nearest, SlangSamplerFilterMode.Linear,
                    SlangSamplerFilterMode.Nearest, false) => Filter.MinPointMagLinearMipPoint,
                (SlangSamplerFilterMode.Nearest, SlangSamplerFilterMode.Linear,
                    SlangSamplerFilterMode.Linear, false) => Filter.MinPointMagMipLinear,
                (SlangSamplerFilterMode.Linear, SlangSamplerFilterMode.Nearest,
                    SlangSamplerFilterMode.Nearest, false) => Filter.MinLinearMagMipPoint,
                (SlangSamplerFilterMode.Linear, SlangSamplerFilterMode.Nearest,
                    SlangSamplerFilterMode.Linear, false) => Filter.MinLinearMagPointMipLinear,
                (SlangSamplerFilterMode.Linear, SlangSamplerFilterMode.Linear,
                    SlangSamplerFilterMode.Nearest, false) => Filter.MinMagLinearMipPoint,
                (SlangSamplerFilterMode.Linear, SlangSamplerFilterMode.Linear,
                    SlangSamplerFilterMode.Linear, false) => Filter.MinMagMipLinear,
                (SlangSamplerFilterMode.Nearest, SlangSamplerFilterMode.Nearest,
                    SlangSamplerFilterMode.Nearest, true) => Filter.ComparisonMinMagMipPoint,
                (SlangSamplerFilterMode.Nearest, SlangSamplerFilterMode.Nearest,
                    SlangSamplerFilterMode.Linear, true) =>
                    Filter.ComparisonMinMagPointMipLinear,
                (SlangSamplerFilterMode.Nearest, SlangSamplerFilterMode.Linear,
                    SlangSamplerFilterMode.Nearest, true) =>
                    Filter.ComparisonMinPointMagLinearMipPoint,
                (SlangSamplerFilterMode.Nearest, SlangSamplerFilterMode.Linear,
                    SlangSamplerFilterMode.Linear, true) =>
                    Filter.ComparisonMinPointMagMipLinear,
                (SlangSamplerFilterMode.Linear, SlangSamplerFilterMode.Nearest,
                    SlangSamplerFilterMode.Nearest, true) =>
                    Filter.ComparisonMinLinearMagMipPoint,
                (SlangSamplerFilterMode.Linear, SlangSamplerFilterMode.Nearest,
                    SlangSamplerFilterMode.Linear, true) =>
                    Filter.ComparisonMinLinearMagPointMipLinear,
                (SlangSamplerFilterMode.Linear, SlangSamplerFilterMode.Linear,
                    SlangSamplerFilterMode.Nearest, true) =>
                    Filter.ComparisonMinMagLinearMipPoint,
                (SlangSamplerFilterMode.Linear, SlangSamplerFilterMode.Linear,
                    SlangSamplerFilterMode.Linear, true) => Filter.ComparisonMinMagMipLinear,
                _ => throw new ArgumentOutOfRangeException(nameof(state)),
            };
        }

        private static TextureAddressMode ToNativeStaticAddress(
            SlangSamplerAddressMode address) => address switch
            {
                SlangSamplerAddressMode.Repeat => TextureAddressMode.Wrap,
                SlangSamplerAddressMode.MirrorRepeat => TextureAddressMode.Mirror,
                SlangSamplerAddressMode.ClampToEdge => TextureAddressMode.Clamp,
                SlangSamplerAddressMode.ClampToBorder => TextureAddressMode.Border,
                SlangSamplerAddressMode.MirrorOnce => TextureAddressMode.MirrorOnce,
                _ => throw new ArgumentOutOfRangeException(nameof(address)),
            };

        private static ComparisonFunc ToNativeStaticComparison(
            SlangSamplerComparisonMode comparison) => comparison switch
            {
                SlangSamplerComparisonMode.None => ComparisonFunc.Always,
                SlangSamplerComparisonMode.Never => ComparisonFunc.Never,
                SlangSamplerComparisonMode.Less => ComparisonFunc.Less,
                SlangSamplerComparisonMode.Equal => ComparisonFunc.Equal,
                SlangSamplerComparisonMode.LessOrEqual => ComparisonFunc.LessEqual,
                SlangSamplerComparisonMode.Greater => ComparisonFunc.Greater,
                SlangSamplerComparisonMode.NotEqual => ComparisonFunc.NotEqual,
                SlangSamplerComparisonMode.GreaterOrEqual => ComparisonFunc.GreaterEqual,
                SlangSamplerComparisonMode.Always => ComparisonFunc.Always,
                _ => throw new ArgumentOutOfRangeException(nameof(comparison)),
            };

        private RootSignatureFlags RootFlags()
        {
            if (_localRootSignature)
                return RootSignatureFlags.LocalRootSignature;

            RootSignatureFlags result = RootSignatureFlags.None;
            if (_allowInputAssembler)
                result |= RootSignatureFlags.AllowInputAssemblerInputLayout;
            if (_allowStreamOutput)
                result |= RootSignatureFlags.AllowStreamOutput;

            if (_pipelineType == PipelineType.Compute)
            {
                result |= RootSignatureFlags.DenyVertexShaderRootAccess |
                    RootSignatureFlags.DenyHullShaderRootAccess |
                    RootSignatureFlags.DenyDomainShaderRootAccess |
                    RootSignatureFlags.DenyGeometryShaderRootAccess |
                    RootSignatureFlags.DenyPixelShaderRootAccess |
                    RootSignatureFlags.DenyAmplificationShaderRootAccess |
                    RootSignatureFlags.DenyMeshShaderRootAccess;
            }
            else if (_pipelineType == PipelineType.Graphics)
            {
                result |= RootSignatureFlags.DenyHullShaderRootAccess |
                    RootSignatureFlags.DenyDomainShaderRootAccess |
                    RootSignatureFlags.DenyGeometryShaderRootAccess |
                    RootSignatureFlags.DenyAmplificationShaderRootAccess |
                    RootSignatureFlags.DenyMeshShaderRootAccess;
            }
            else if (_pipelineType == PipelineType.Mesh)
            {
                result |= RootSignatureFlags.DenyVertexShaderRootAccess |
                    RootSignatureFlags.DenyHullShaderRootAccess |
                    RootSignatureFlags.DenyDomainShaderRootAccess |
                    RootSignatureFlags.DenyGeometryShaderRootAccess;
            }
            return result;
        }

        private sealed class BlockCandidate
        {
            internal BlockCandidate(
                D3D12ParameterBlockShape shape,
                RootBindingKey[] leafKeys,
                RootBindingKey? ordinaryKey,
                ReflectedStages stages)
            {
                Shape = shape;
                LeafKeys = leafKeys;
                OrdinaryKey = ordinaryKey;
                Stages = stages;
            }

            internal D3D12ParameterBlockShape Shape { get; }
            internal RootBindingKey[] LeafKeys { get; }
            internal RootBindingKey? OrdinaryKey { get; }
            internal ReflectedStages Stages { get; set; }
        }
    }

    private static ReflectedStages ToReflectedStage(SlangStage stage) => stage switch
    {
        SlangStage.Vertex => ReflectedStages.Vertex,
        SlangStage.Hull => ReflectedStages.Hull,
        SlangStage.Domain => ReflectedStages.Domain,
        SlangStage.Geometry => ReflectedStages.Geometry,
        SlangStage.Fragment => ReflectedStages.Pixel,
        SlangStage.Compute => ReflectedStages.Compute,
        SlangStage.Amplification => ReflectedStages.Amplification,
        SlangStage.Mesh => ReflectedStages.Mesh,
        SlangStage.RayGeneration or SlangStage.Intersection or SlangStage.AnyHit or
            SlangStage.ClosestHit or SlangStage.Miss or SlangStage.Callable =>
            ReflectedStages.RayTracing,
        _ => ReflectedStages.All,
    };

    private static ShaderVisibility ToShaderVisibility(ReflectedStages stages) => stages switch
    {
        ReflectedStages.Vertex => ShaderVisibility.Vertex,
        ReflectedStages.Hull => ShaderVisibility.Hull,
        ReflectedStages.Domain => ShaderVisibility.Domain,
        ReflectedStages.Geometry => ShaderVisibility.Geometry,
        ReflectedStages.Pixel => ShaderVisibility.Pixel,
        ReflectedStages.Amplification => ShaderVisibility.Amplification,
        ReflectedStages.Mesh => ShaderVisibility.Mesh,
        _ => ShaderVisibility.All,
    };

    private static string BlobText(ID3D10Blob* blob)
    {
        if (blob is null || blob->GetBufferPointer() is null || blob->GetBufferSize() == 0)
            return string.Empty;
        ReadOnlySpan<byte> bytes = new(
            blob->GetBufferPointer(),
            checked((int)blob->GetBufferSize()));
        int length = bytes.IndexOf((byte)0);
        if (length >= 0)
            bytes = bytes[..length];
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static void ReleaseBlob(ID3D10Blob* blob)
    {
        if (blob is not null)
            _ = blob->Release();
    }

    internal static StaticSamplerDesc[] GetCompiledStaticSamplers(Pipeline pipeline) =>
        NativeCast.Pipeline(pipeline).RootLayout.StaticSamplers.ToArray();

    internal static byte[] GetSerializedRootLayout(Pipeline pipeline) =>
        NativeCast.Pipeline(pipeline).RootLayout.Serialized.ToArray();

    private static PipelineSignature ToPipelineSignature(ReadOnlySpan<byte> hash)
    {
        if (hash.Length != 32)
            throw new ArgumentException("A pipeline signature requires a SHA-256 digest.", nameof(hash));
        return new PipelineSignature(
            BinaryPrimitives.ReadUInt64LittleEndian(hash),
            BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(hash[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(hash[24..]));
    }
}
