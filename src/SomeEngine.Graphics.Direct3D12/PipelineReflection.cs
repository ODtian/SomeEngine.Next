using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    private const uint RootLayoutSchemaVersion = 1;

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
        OrdinaryData,
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

    private sealed class D3D12ParameterBlockShape
    {
        private D3D12ParameterBlockShape(
            ParameterBindingContract contract,
            ParameterLeaf[] leaves,
            uint resourceDescriptorCount,
            uint samplerDescriptorCount)
        {
            Contract = contract;
            Leaves = leaves;
            ResourceDescriptorCount = resourceDescriptorCount;
            SamplerDescriptorCount = samplerDescriptorCount;
        }

        internal ParameterBindingContract Contract { get; }
        internal ParameterLeaf[] Leaves { get; }
        internal uint ResourceDescriptorCount { get; }
        internal uint SamplerDescriptorCount { get; }
        internal uint OrdinaryDataSize => Contract.OrdinaryDataSize;
        internal uint OrdinaryRegister => Contract.OrdinaryRegister;
        internal uint OrdinarySpace => Contract.OrdinarySpace;

        internal static D3D12ParameterBlockShape Compile(
            VariableLayoutReflection layout) =>
            Compile(ParameterBindingContract.Compile(layout));

        internal static D3D12ParameterBlockShape Compile(
            ParameterBindingContract contract)
        {
            ParameterLeaf[] leaves = new ParameterLeaf[contract.Leaves.Length];
            uint resources = 0;
            uint samplers = 0;
            for (int index = 0; index < leaves.Length; index++)
            {
                ParameterBindingLeaf source = contract.Leaves[index];
                (ParameterHeap heap, DescriptorRangeType rangeType) =
                    ToNativeBinding(source.Type);
                uint offset = heap == ParameterHeap.Resource ? resources : samplers;
                leaves[index] = new ParameterLeaf(
                    source.Type,
                    heap,
                    rangeType,
                    source.Binding,
                    source.Space,
                    source.DescriptorCount,
                    source.Unbounded,
                    offset);
                if (!source.Unbounded)
                {
                    if (heap == ParameterHeap.Resource)
                        resources = checked(resources + source.DescriptorCount);
                    else
                        samplers = checked(samplers + source.DescriptorCount);
                }
            }

            return new D3D12ParameterBlockShape(
                contract,
                leaves,
                resources,
                samplers);
        }

        internal void RequireMaterializationShape(
            ReadOnlySpan<ResourceBinding> bindings,
            ReadOnlySpan<byte> ordinaryData) =>
            Contract.RequireMaterializationShape(bindings, ordinaryData);

        private static (ParameterHeap Heap, DescriptorRangeType RangeType) ToNativeBinding(
            ResourceBindingType type) => type switch
        {
            ResourceBindingType.Sampler =>
                (ParameterHeap.Sampler, DescriptorRangeType.Sampler),
            ResourceBindingType.BufferUav or ResourceBindingType.TextureUav =>
                (ParameterHeap.Resource, DescriptorRangeType.Uav),
            ResourceBindingType.ConstantBuffer =>
                (ParameterHeap.Resource, DescriptorRangeType.Cbv),
            ResourceBindingType.BufferSrv or ResourceBindingType.TextureSrv or
                ResourceBindingType.AccelerationStructure =>
                (ParameterHeap.Resource, DescriptorRangeType.Srv),
            _ => throw new GraphicsException(
                GraphicsError.NativeFailure,
                $"Slang produced unsupported resource binding type {type}."),
        };
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
    }

    private readonly record struct BlockLeafBinding(
        uint RootParameterIndex,
        ParameterHeap Heap,
        ResourceBindingType Type,
        uint HeapOffset,
        uint DescriptorCount,
        bool Unbounded);

    private sealed class D3D12ParameterBlockLayout
    {
        internal D3D12ParameterBlockLayout(
            D3D12ParameterBlockShape shape,
            BlockLeafBinding[] leaves,
            uint? ordinaryRootParameter)
        {
            Shape = shape;
            Leaves = leaves;
            OrdinaryRootParameter = ordinaryRootParameter;
        }

        internal D3D12ParameterBlockShape Shape { get; }
        internal BlockLeafBinding[] Leaves { get; }
        internal uint? OrdinaryRootParameter { get; }
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
            byte[] serialized,
            uint rootArgumentSize)
        {
            _native = new NativeLease((IUnknown*)native, ownsReference: true);
            _blocks = blocks;
            BindingContracts = new ParameterBindingContractSet(
                blocks.Values.Select(static block => block.Shape.Contract));
            DefaultTables = defaults;
            Serialized = serialized;
            RootArgumentSize = rootArgumentSize;
        }

        internal ID3D12RootSignature* Native =>
            (ID3D12RootSignature*)_native.Pointer;
        internal NativeLease NativeLifetime => _native;
        internal ParameterBindingContractSet BindingContracts { get; }
        internal DefaultRootTable[] DefaultTables { get; }
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
            if (includeGlobal)
            {
                for (uint index = 0; index < reflection.ParameterCount; index++)
                    builder.AddBlock(reflection.GetParameterByIndex(index), ReflectedStages.All);
            }
            if (includeEntries)
            {
                foreach (EntryPointReflection entryPoint in entryPoints)
                {
                    ReflectedStages stage = ToReflectedStage(entryPoint.Stage);
                    VariableLayoutReflection container = entryPoint.VarLayout;
                    if (container != VariableLayoutReflection.Null)
                        builder.AddBlock(container, stage);
                    for (uint index = 0; index < entryPoint.ParameterCount; index++)
                        builder.AddBlock(entryPoint.GetParameterByIndex(index), stage);
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
                ordinary = new RootBindingKey(
                    RootBindingRole.OrdinaryData,
                    shape.OrdinarySpace,
                    DescriptorRangeType.Cbv,
                    shape.OrdinaryRegister,
                    1,
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

            if (_blocks.TryGetValue(layout, out BlockCandidate? existing))
            {
                existing.Stages |= stages;
                return;
            }
            _blocks.Add(layout, new BlockCandidate(shape, leaves, ordinary, stages));
        }

        private void AddDeclaration(RootBindingKey key, ReflectedStages stages)
        {
            if (_declarations.TryGetValue(key, out RootDeclaration? existing))
                existing.Stages |= stages;
            else
                _declarations.Add(key, new RootDeclaration(key, stages));
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
                declarations[index].RootParameterIndex = checked((uint)index);

            RootParameter1[] parameters = new RootParameter1[declarations.Length];
            DescriptorRange1[] ranges = new DescriptorRange1[declarations.Length];
            fixed (DescriptorRange1* rangePointer = ranges)
            {
                for (int index = 0; index < declarations.Length; index++)
                {
                    RootDeclaration declaration = declarations[index];
                    ShaderVisibility visibility = ToShaderVisibility(declaration.Stages);
                    if (declaration.Key.Role == RootBindingRole.OrdinaryData)
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
                {
                    RootSignatureDesc1 description = new(
                        checked((uint)parameters.Length),
                        parameterPointer,
                        0,
                        null,
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
                                leaf.Heap,
                                leaf.Type,
                                leaf.HeapOffset,
                                leaf.DescriptorCount,
                                leaf.Unbounded);
                        }
                        uint? ordinaryRoot = candidate.OrdinaryKey is RootBindingKey ordinary
                            ? _declarations[ordinary].RootParameterIndex
                            : null;
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
                    uint rootArgumentSize = _localRootSignature
                        ? checked((uint)parameters.Length * sizeof(ulong))
                        : 0;
                    return new D3D12RootLayout(
                        rootSignature,
                        blocks,
                        defaults,
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
