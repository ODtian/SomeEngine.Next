using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using SlangShaderSharp;
using NativeRange = Silk.NET.Direct3D12.Range;
using NativeResource = Silk.NET.Direct3D12.ID3D12Resource;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void SetPipeline(CommandContext context, Pipeline pipeline)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12Pipeline native = NativeCast.Pipeline(pipeline);
        if (ReferenceEquals(command.CurrentPipeline, native))
            return;
        SetPipelineSlow(context, command, native);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void SetPipelineSlow(
        CommandContext context,
        D3D12CommandContext command,
        D3D12Pipeline native)
    {
        if (native is D3D12ClassicPipeline classicPipeline)
            command.List->SetPipelineState(classicPipeline.Native);
        else if (native is D3D12RayTracingPipeline rayTracing)
            command.List->SetPipelineState1(rayTracing.Native);
        else
            throw new ArgumentException("The Pipeline cannot be selected by SetPipeline.", nameof(native));
        command.Recording.RecordPipelineSetter();
        if (native.Type is PipelineType.Compute or PipelineType.RayTracing)
            command.List->SetComputeRootSignature(native.RootLayout.Native);
        else
            command.List->SetGraphicsRootSignature(native.RootLayout.Native);
        command.RememberPipeline(native);
        command.CapturePipelineArtifact(native);

        foreach (DefaultRootTable table in native.RootLayout.DefaultTables)
            command.SetRootTable(table.RootParameterIndex, table.Heap, 0);

        if (native.Type == PipelineType.Graphics)
        {
            D3D12ClassicPipeline classic = (D3D12ClassicPipeline)native;
            if ((classic.DynamicStates & DynamicStates.PrimitiveTopology) == 0)
                SetPrimitiveTopology(context, classic.Topology);
            if ((classic.DynamicStates & DynamicStates.StripCut) == 0)
                SetStripCut(context, classic.StripCut);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void SetPersistentParameterBindings(
        CommandContext context,
        PersistentParameterBindings bindings)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12PersistentParameterBindings native =
            NativeCast.PersistentParameterBindings(bindings);
        D3D12ParameterMaterialization? published = native.PublishedMaterialization;
        if (command.PersistentBindingsEqual(native, published))
            return;
        D3D12Pipeline pipeline = command.Pipeline;
        D3D12ParameterBlockLayout layout = command.ResolveParameterBlock(pipeline, native.Layout);
        SetPersistentParameterBindingsSlow(command, native, layout);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void SetPersistentParameterBindingsSlow(
        D3D12CommandContext command,
        D3D12PersistentParameterBindings native,
        D3D12ParameterBlockLayout layout)
    {
        D3D12ParameterMaterialization materialization =
            native.CapturePublished(command.DescriptorGeneration.Identity);
        command.Capture(materialization);
        command.CaptureObject(native);
        command.ApplyPersistentBlock(layout, native, materialization);
        command.RememberPersistentBindings(native, materialization);
        command.Recording.RecordPersistentBindingSetter();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void SetTransientParameterBindings(
        CommandContext context,
        in ParameterBlockBindings bindings)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12Pipeline pipeline = command.Pipeline;
        D3D12ParameterBlockLayout layout = command.ResolveParameterBlock(pipeline, bindings.Layout);
        layout.Shape.RequireMaterializationShape(bindings.Resources, bindings.OrdinaryData);
        if (layout.Shape.Leaves.Length == 0)
        {
            command.ApplyTransientOrdinaryData(layout, bindings.OrdinaryData);
            return;
        }
        SetTransientParameterBindingsSlow(command, layout, bindings);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void SetTransientParameterBindingsSlow(
        D3D12CommandContext command,
        D3D12ParameterBlockLayout layout,
        in ParameterBlockBindings bindings)
    {
        command.PrepareTransientBindingCaptures(bindings.Resources);
        D3D12OrdinaryDataReservation ordinary =
            command.ReserveTransientOrdinaryData(checked((ulong)bindings.OrdinaryData.Length));

        (uint resourceBase, uint samplerBase) = command.AllocateTransientDescriptorPair(
            layout.Shape.ResourceDescriptorCount,
            layout.Shape.SamplerDescriptorCount);

        int ordinal = 0;
        foreach (ParameterLeaf leaf in layout.Shape.Leaves)
        {
            if (leaf.Unbounded)
                continue;
            uint destinationBase = leaf.Heap == ParameterHeap.Resource
                ? resourceBase
                : samplerBase;
            for (uint element = 0; element < leaf.DescriptorCount; element++)
            {
                ref readonly ResourceBinding binding = ref bindings.Resources[ordinal++];
                command.CopyTransientDescriptor(
                    leaf.Heap,
                    checked(destinationBase + leaf.HeapOffset + element),
                    binding,
                    leaf.Type);
                command.Capture(binding);
            }
        }

        ordinary.Commit(bindings.OrdinaryData);
        command.ApplyTransientBlock(
            layout,
            resourceBase,
            samplerBase,
            ordinary.Address);
    }

    private sealed class D3D12ParameterMaterialization
    {
        private NativeLease? _ordinary;
        private int _references = 1;

        private D3D12ParameterMaterialization(
            ulong version,
            ResourceBinding[] resources,
            D3D12SwapchainImageLease[] swapchainImages,
            byte[] ordinaryData,
            NativeLease? ordinary,
            ulong ordinaryAddress)
        {
            Version = version;
            Resources = resources;
            SwapchainImages = swapchainImages;
            OrdinaryData = ordinaryData;
            _ordinary = ordinary;
            OrdinaryAddress = ordinaryAddress;
        }

        internal ulong Version { get; }
        internal ResourceBinding[] Resources { get; }
        internal D3D12SwapchainImageLease[] SwapchainImages { get; }
        internal byte[] OrdinaryData { get; }
        internal ulong OrdinaryAddress { get; }
        internal ulong PublishedGeneration { get; set; }

        internal static D3D12ParameterMaterialization Create(
            D3D12Device device,
            ulong version,
            ReadOnlySpan<ResourceBinding> resources,
            ReadOnlySpan<byte> ordinaryData)
        {
            NativeLease? lifetime = null;
            ulong address = 0;
            if (!ordinaryData.IsEmpty)
            {
                NativeResource* native = CreateOrdinaryDataResource(
                    device,
                    ordinaryData,
                    out address);
                lifetime = new NativeLease((IUnknown*)native, ownsReference: true);
            }
            D3D12SwapchainImageLease[] swapchainImages =
                CaptureSwapchainBindings(resources);
            return new D3D12ParameterMaterialization(
                version,
                device.RetirementType == RetirementType.Automatic
                    ? resources.ToArray()
                    : [],
                swapchainImages,
                ordinaryData.ToArray(),
                lifetime,
                address);
        }

        private static D3D12SwapchainImageLease[] CaptureSwapchainBindings(
            ReadOnlySpan<ResourceBinding> resources)
        {
            HashSet<D3D12SwapchainImageLease>? images = null;
            foreach (ref readonly ResourceBinding binding in resources)
            {
                D3D12SwapchainImageLease? lease = binding.Value switch
                {
                    TextureSrv view => NativeCast.Texture(view.Resource).SwapchainLease,
                    TextureUav view => NativeCast.Texture(view.Resource).SwapchainLease,
                    _ => null,
                };
                if (lease is null)
                    continue;
                images ??= new HashSet<D3D12SwapchainImageLease>(
                    ReferenceEqualityComparer.Instance);
                images.Add(lease);
            }
            return images?.ToArray() ?? [];
        }

        internal void Retain()
        {
            int current = Volatile.Read(ref _references);
            while (current > 0)
            {
                int exchanged = Interlocked.CompareExchange(
                    ref _references,
                    checked(current + 1),
                    current);
                if (exchanged == current)
                    return;
                current = exchanged;
            }
            throw new ObjectDisposedException(nameof(D3D12ParameterMaterialization));
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0)
                return;
            Interlocked.Exchange(ref _ordinary, null)?.Release();
        }
    }

    private static NativeResource* CreateOrdinaryDataResource(
        D3D12Device device,
        ReadOnlySpan<byte> data,
        out ulong gpuAddress)
    {
        if (data.IsEmpty)
            throw new ArgumentException("Ordinary-data storage cannot be empty.", nameof(data));
        ulong size = checked(((ulong)data.Length + 255UL) & ~255UL);
        Silk.NET.Direct3D12.ResourceDesc description = CreateBufferDescription(
            new BufferDesc(size, BufferUsages.Constant));
        NativeResource* resource = CreateCommittedResource(
            device,
            MemoryType.Upload,
            shareable: false,
            description,
            ReadOnlySpan<Silk.NET.DXGI.Format>.Empty);
        try
        {
            void* mapped = null;
            NativeRange readRange = default;
            NativeCall.ThrowIfFailed(
                resource->Map(0, &readRange, &mapped),
                "ID3D12Resource::Map(parameter data)");
            data.CopyTo(new Span<byte>(mapped, data.Length));
            NativeRange written = new()
            {
                Begin = 0,
                End = checked((nuint)data.Length),
            };
            resource->Unmap(0, &written);
            gpuAddress = resource->GetGPUVirtualAddress();
            return resource;
        }
        catch
        {
            _ = resource->Release();
            throw;
        }
    }

    private sealed class D3D12OrdinaryDataChunk
    {
        private NativeResource* _resource;
        private byte* _mapped;
        private ulong _used;

        private D3D12OrdinaryDataChunk(
            NativeResource* resource,
            byte* mapped,
            ulong capacity)
        {
            _resource = resource;
            _mapped = mapped;
            Capacity = capacity;
            GpuAddress = resource->GetGPUVirtualAddress();
        }

        internal ulong Capacity { get; }
        internal ulong GpuAddress { get; }
        internal NativeResource* Resource => _resource;
        internal ulong Used => _used;

        internal static D3D12OrdinaryDataChunk Create(D3D12Device device, ulong capacity)
        {
            NativeResource* resource = CreateCommittedResource(
                device,
                MemoryType.Upload,
                shareable: false,
                CreateBufferDescription(new BufferDesc(
                    capacity,
                    BufferUsages.Constant)),
                ReadOnlySpan<Silk.NET.DXGI.Format>.Empty);
            try
            {
                void* mapped = null;
                NativeRange readRange = default;
                NativeCall.ThrowIfFailed(
                    resource->Map(0, &readRange, &mapped),
                    "ID3D12Resource::Map(command ordinary-data arena)");
                return new D3D12OrdinaryDataChunk(resource, (byte*)mapped, capacity);
            }
            catch
            {
                _ = resource->Release();
                throw;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool TryReserve(ulong size, out ulong offset)
        {
            if (size > Capacity - _used)
            {
                offset = 0;
                return false;
            }
            offset = _used;
            return true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool TryWrite(
            ReadOnlySpan<byte> data,
            ulong reservedSize,
            out ulong address)
        {
            ulong offset = _used;
            if (reservedSize > Capacity - offset)
            {
                address = 0;
                return false;
            }
            byte* destination = _mapped + checked((nint)offset);
            if (data.Length == 16)
            {
                ref byte source = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(data);
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(
                    destination,
                    System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(ref source));
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(
                    destination + sizeof(ulong),
                    System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(
                        ref System.Runtime.CompilerServices.Unsafe.Add(ref source, sizeof(ulong))));
            }
            else
            {
                data.CopyTo(new Span<byte>(destination, data.Length));
            }
            _used = checked(offset + reservedSize);
            address = checked(GpuAddress + offset);
            return true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void Commit(ulong offset, ulong reservedSize, ReadOnlySpan<byte> data)
        {
            if (offset != _used || reservedSize > Capacity - offset ||
                (ulong)data.Length > reservedSize)
                throw new InvalidOperationException("The ordinary-data reservation is no longer current.");
            data.CopyTo(new Span<byte>(_mapped + checked((nint)offset), data.Length));
            _used = checked(offset + reservedSize);
        }

        internal void CommitPattern(
            ulong offset,
            ulong reservedSize,
            ulong size,
            uint value)
        {
            if (offset != _used || reservedSize > Capacity - offset || size > reservedSize)
                throw new InvalidOperationException("The transient upload reservation is no longer current.");
            byte* destination = _mapped + checked((nint)offset);
            byte* pattern = (byte*)&value;
            for (ulong index = 0; index < size; index++)
                destination[index] = pattern[index & 3];
            _used = checked(offset + reservedSize);
        }

        internal Span<byte> CommitSpan(
            ulong offset,
            ulong reservedSize,
            int length,
            bool clear)
        {
            if (offset != _used || reservedSize > Capacity - offset ||
                (ulong)length > reservedSize)
            {
                throw new InvalidOperationException(
                    "The transient upload reservation is no longer current.");
            }
            Span<byte> result = new(_mapped + checked((nint)offset), length);
            _used = checked(offset + reservedSize);
            if (clear)
                result.Clear();
            return result;
        }

        internal void Reset() => _used = 0;

        internal void Release()
        {
            NativeResource* resource = _resource;
            _resource = null;
            _mapped = null;
            _used = 0;
            if (resource is null)
                return;
            resource->Unmap(0, null);
            _ = resource->Release();
        }
    }

    private readonly struct D3D12OrdinaryDataReservation
    {
        private readonly D3D12OrdinaryDataChunk? _chunk;
        private readonly ulong _offset;
        private readonly ulong _reservedSize;

        internal D3D12OrdinaryDataReservation(
            D3D12OrdinaryDataChunk chunk,
            ulong offset,
            ulong reservedSize)
        {
            _chunk = chunk;
            _offset = offset;
            _reservedSize = reservedSize;
        }

        internal ulong Address => _chunk is null
            ? 0
            : checked(_chunk.GpuAddress + _offset);
        internal NativeResource* Resource => _chunk is null ? null : _chunk.Resource;
        internal ulong Offset => _offset;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void Commit(ReadOnlySpan<byte> data)
        {
            if (_chunk is null)
            {
                if (!data.IsEmpty)
                    throw new InvalidOperationException("Ordinary data has no storage reservation.");
                return;
            }
            _chunk.Commit(_offset, _reservedSize, data);
        }

        internal void CommitPattern(uint value, ulong size)
        {
            if (_chunk is null)
                throw new InvalidOperationException("Transient upload data has no storage reservation.");
            _chunk.CommitPattern(_offset, _reservedSize, size, value);
        }

        internal Span<byte> CommitSpan(int length, bool clear = false)
        {
            if (_chunk is null)
                throw new InvalidOperationException("Transient upload data has no storage reservation.");
            return _chunk.CommitSpan(_offset, _reservedSize, length, clear);
        }
    }

    private readonly record struct RootTableState(ParameterHeap Heap, uint Index);

    private sealed partial class D3D12CommandContext
    {
        private RootTableState[] _rootTables = [];
        private bool[] _rootTableSet = [];
        private ulong[] _rootConstantBuffers = [];
        private bool[] _rootConstantBufferSet = [];
        private int _rootStateLength;
        private D3D12Pipeline? _pipeline;
        private VariableLayoutReflection _resolvedParameterLayout;
        private D3D12ParameterBlockLayout? _resolvedParameterBlock;
        private D3D12PersistentParameterBindings? _persistentBindings;
        private D3D12ParameterMaterialization? _persistentMaterialization;
        private bool _computeRootBindings;

        internal D3D12Pipeline? CurrentPipeline => _pipeline;

        internal D3D12Pipeline Pipeline
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => _pipeline!;
        }

        internal void RememberPipeline(D3D12Pipeline pipeline)
        {
            _pipeline = pipeline;
            _computeRootBindings = pipeline.Type is PipelineType.Compute or
                PipelineType.RayTracing or PipelineType.WorkGraph;
            ClearRootBindingState();
        }

        internal void ResetPipelineBindingState()
        {
            _pipeline = null;
            ClearRootBindingState();
        }

        internal void ClearRootBindingState()
        {
            Array.Clear(_rootTableSet, 0, _rootStateLength);
            Array.Clear(_rootConstantBufferSet, 0, _rootStateLength);
            _rootStateLength = 0;
            _resolvedParameterBlock = null;
            _persistentBindings = null;
            _persistentMaterialization = null;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal D3D12ParameterBlockLayout ResolveParameterBlock(
            D3D12Pipeline pipeline,
            VariableLayoutReflection layout)
        {
            if (_resolvedParameterBlock is not null && _resolvedParameterLayout == layout)
                return _resolvedParameterBlock;
            D3D12ParameterBlockLayout block = pipeline.RootLayout.GetBlock(layout);
            _resolvedParameterLayout = layout;
            _resolvedParameterBlock = block;
            return block;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool PersistentBindingsEqual(
            D3D12PersistentParameterBindings bindings,
            D3D12ParameterMaterialization? materialization) =>
            materialization is not null &&
            ReferenceEquals(_persistentBindings, bindings) &&
            ReferenceEquals(_persistentMaterialization, materialization);

        internal void RememberPersistentBindings(
            D3D12PersistentParameterBindings bindings,
            D3D12ParameterMaterialization materialization)
        {
            _persistentBindings = bindings;
            _persistentMaterialization = materialization;
        }

        internal void ReapplyRootTables()
        {
            if (_pipeline is null)
                return;
            for (int rootParameter = 0; rootParameter < _rootStateLength; rootParameter++)
            {
                if (_rootTableSet[rootParameter])
                {
                    RootTableState state = _rootTables[rootParameter];
                    SetRootTableNative(checked((uint)rootParameter), state.Heap, state.Index);
                }
            }
        }

        internal void SetRootTable(
            uint rootParameter,
            ParameterHeap heap,
            uint index)
        {
            int slot = EnsureRootStateCapacity(rootParameter);
            RootTableState next = new(heap, index);
            if (_rootTableSet[slot] && _rootTables[slot] == next)
                return;
            SetRootTableNative(rootParameter, heap, index);
            _rootTables[slot] = next;
            _rootTableSet[slot] = true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void SetRootConstantBuffer(uint rootParameter, ulong address)
        {
            if (address == 0)
                throw new ArgumentOutOfRangeException(nameof(address));
            int slot = EnsureRootStateCapacity(rootParameter);
            if (_rootConstantBufferSet[slot] && _rootConstantBuffers[slot] == address)
                return;
            D3D12CommandListFastCalls.SetRootConstantBufferView(
                List,
                _computeRootBindings,
                rootParameter,
                address);
            _rootConstantBuffers[slot] = address;
            _rootConstantBufferSet[slot] = true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private int EnsureRootStateCapacity(uint rootParameter)
        {
            int required = checked((int)rootParameter + 1);
            if (_rootTables.Length < required)
            {
                int capacity = Math.Max(8, _rootTables.Length);
                while (capacity < required)
                    capacity = checked(capacity * 2);
                Array.Resize(ref _rootTables, capacity);
                Array.Resize(ref _rootTableSet, capacity);
                Array.Resize(ref _rootConstantBuffers, capacity);
                Array.Resize(ref _rootConstantBufferSet, capacity);
            }
            if (_rootStateLength < required)
                _rootStateLength = required;
            return checked((int)rootParameter);
        }

        internal void ApplyPersistentBlock(
            D3D12ParameterBlockLayout layout,
            D3D12PersistentParameterBindings bindings,
            D3D12ParameterMaterialization materialization)
        {
            foreach (BlockLeafBinding leaf in layout.Leaves)
            {
                if (leaf.Unbounded)
                    continue;
                uint first = leaf.Heap == ParameterHeap.Resource
                    ? bindings.ResourceBaseIndex
                    : bindings.SamplerBaseIndex;
                SetRootTable(
                    leaf.RootParameterIndex,
                    leaf.Heap,
                    checked(first + leaf.HeapOffset));
            }
            if (layout.OrdinaryRootParameter is uint ordinary)
                SetRootConstantBuffer(ordinary, materialization.OrdinaryAddress);
        }

        internal void ApplyTransientBlock(
            D3D12ParameterBlockLayout layout,
            uint resourceBase,
            uint samplerBase,
            ulong ordinaryAddress)
        {
            _persistentBindings = null;
            _persistentMaterialization = null;
            foreach (BlockLeafBinding leaf in layout.Leaves)
            {
                if (leaf.Unbounded)
                    continue;
                uint first = leaf.Heap == ParameterHeap.Resource
                    ? resourceBase
                    : samplerBase;
                SetRootTable(
                    leaf.RootParameterIndex,
                    leaf.Heap,
                    checked(first + leaf.HeapOffset));
            }
            if (layout.OrdinaryRootParameter is uint ordinary)
                SetRootConstantBuffer(ordinary, ordinaryAddress);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void ApplyTransientOrdinaryData(
            D3D12ParameterBlockLayout layout,
            ReadOnlySpan<byte> ordinaryData)
        {
            ulong ordinaryAddress = Recording.WriteOrdinaryData(ordinaryData);
            if (_persistentBindings is not null)
            {
                _persistentBindings = null;
                _persistentMaterialization = null;
            }
            if (layout.OrdinaryRootParameter is uint ordinary)
                SetTransientRootConstantBuffer(ordinary, ordinaryAddress);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void SetTransientRootConstantBuffer(uint rootParameter, ulong address)
        {
            int slot = checked((int)rootParameter);
            if ((uint)slot >= (uint)_rootConstantBuffers.Length)
            {
                SetRootConstantBuffer(rootParameter, address);
                return;
            }
            D3D12CommandListFastCalls.SetRootConstantBufferView(
                List,
                _computeRootBindings,
                rootParameter,
                address);
            _rootConstantBuffers[slot] = address;
            _rootConstantBufferSet[slot] = true;
            if (_rootStateLength <= slot)
                _rootStateLength = checked(slot + 1);
        }

        internal uint AllocateTransientDescriptors(ParameterHeap heap, uint count) =>
            Recording.AllocateDescriptors(heap, count);

        internal (uint ResourceBase, uint SamplerBase) AllocateTransientDescriptorPair(
            uint resourceCount,
            uint samplerCount) =>
            Recording.AllocateDescriptorPair(resourceCount, samplerCount);

        internal void PrepareTransientBindingCaptures(ReadOnlySpan<ResourceBinding> bindings) =>
            Recording.PrepareBindingCaptures(bindings);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal D3D12OrdinaryDataReservation ReserveTransientOrdinaryData(ulong size) =>
            Recording.ReserveOrdinaryData(size);

        internal void CopyTransientDescriptor(
            ParameterHeap heap,
            uint index,
            in ResourceBinding binding,
            ResourceBindingType expectedType) =>
            Recording.CopyDescriptor(heap, index, binding, expectedType);

        internal void Capture(D3D12ParameterMaterialization materialization) =>
            Recording.Capture(materialization);

        internal void CaptureObject(GraphicsObject value) =>
            Recording.CaptureObject(value);

        internal void Capture(in ResourceBinding binding)
        {
            switch (binding.Value)
            {
                case BufferCbv value:
                    Capture(value);
                    break;
                case BufferSrv value:
                    Capture(value);
                    break;
                case BufferUav value:
                    Capture(value);
                    break;
                case TextureSrv value:
                    Capture(value);
                    break;
                case TextureUav value:
                    Capture(value);
                    break;
                case Sampler value:
                    INativeDescriptor descriptor = (INativeDescriptor)value;
                    Recording.Capture(
                        value,
                        descriptor.NativeDescriptor,
                        resource: null);
                    break;
                case AccelerationStructureSrv value:
                    Capture(value);
                    break;
                case null:
                    break;
                default:
                    throw new ArgumentException(
                        "The resource binding is not a D3D12 shader-visible descriptor.",
                        nameof(binding));
            }
        }

        private void SetRootTableNative(
            uint rootParameter,
            ParameterHeap heap,
            uint index)
        {
            GpuDescriptorHandle handle = Recording.GetGpuHandle(heap, index);
            if (Pipeline.Type is PipelineType.Compute or
                PipelineType.RayTracing or PipelineType.WorkGraph)
                List->SetComputeRootDescriptorTable(rootParameter, handle);
            else
                List->SetGraphicsRootDescriptorTable(rootParameter, handle);
        }
    }

    private sealed partial class D3D12CommandSlot
    {
        private readonly HashSet<D3D12ParameterMaterialization> _capturedParameterData =
            new(ReferenceEqualityComparer.Instance);
        private readonly List<D3D12OrdinaryDataChunk> _ordinaryDataChunks = [];
        private int _ordinaryDataCursor;
        private D3D12OrdinaryDataChunk? _ordinaryDataCurrent;
        private ID3D12DescriptorHeap* _resourceArena;
        private ID3D12DescriptorHeap* _samplerArena;
        private uint _resourceCapacity;
        private uint _samplerCapacity;
        private uint _resourceUsed;
        private uint _samplerUsed;
        private ulong _descriptorArenaVersion;
        private bool _descriptorHeapsBound;

        internal ulong DescriptorArenaVersion => _descriptorArenaVersion;

        internal void PrepareBindingCaptures(ReadOnlySpan<ResourceBinding> bindings)
        {
            _automaticCaptures?.PrepareCapacity(bindings.Length);
            int swapchainCapacity = _swapchainUses.Count;

            foreach (ref readonly ResourceBinding binding in bindings)
            {
                object? value = binding.Value;
                if (value is null)
                    continue;
                if (value is TextureSrv or TextureUav)
                    swapchainCapacity = checked(swapchainCapacity + 1);
            }

            _swapchainUses.EnsureCapacity(swapchainCapacity);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal D3D12OrdinaryDataReservation ReserveOrdinaryData(ulong size)
        {
            if (size == 0)
                return default;
            ulong alignedSize = checked(((size + 255UL) / 256UL) * 256UL);
            D3D12OrdinaryDataChunk? current = _ordinaryDataCurrent;
            if (current is not null && current.TryReserve(alignedSize, out ulong currentOffset))
                return new D3D12OrdinaryDataReservation(current, currentOffset, alignedSize);
            return ReserveOrdinaryDataSlow(alignedSize);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal ulong WriteOrdinaryData(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
                return 0;
            ulong alignedSize = checked(((ulong)data.Length + 255UL) & ~255UL);
            D3D12OrdinaryDataChunk? current = _ordinaryDataCurrent;
            if (current is not null && current.TryWrite(data, alignedSize, out ulong address))
                return address;
            return WriteOrdinaryDataSlow(data, alignedSize);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private ulong WriteOrdinaryDataSlow(ReadOnlySpan<byte> data, ulong alignedSize)
        {
            D3D12OrdinaryDataReservation reservation = ReserveOrdinaryDataSlow(alignedSize);
            reservation.Commit(data);
            return reservation.Address;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private D3D12OrdinaryDataReservation ReserveOrdinaryDataSlow(ulong alignedSize)
        {
            while (_ordinaryDataCursor < _ordinaryDataChunks.Count)
            {
                D3D12OrdinaryDataChunk chunk = _ordinaryDataChunks[_ordinaryDataCursor];
                if (chunk.TryReserve(alignedSize, out ulong offset))
                {
                    _ordinaryDataCurrent = chunk;
                    return new D3D12OrdinaryDataReservation(chunk, offset, alignedSize);
                }
                _ordinaryDataCursor++;
            }

            _ordinaryDataChunks.EnsureCapacity(checked(_ordinaryDataChunks.Count + 1));
            ulong capacity = Math.Max(64UL * 1024UL, alignedSize);
            D3D12OrdinaryDataChunk created = D3D12OrdinaryDataChunk.Create(
                _context.NativeDevice,
                capacity);
            _ordinaryDataChunks.Add(created);
            if (!created.TryReserve(alignedSize, out ulong createdOffset))
                throw new InvalidOperationException("A new ordinary-data chunk is too small.");
            _ordinaryDataCurrent = created;
            return new D3D12OrdinaryDataReservation(created, createdOffset, alignedSize);
        }

        internal void ResetOrdinaryDataArena()
        {
            if (_ordinaryDataChunks.Count > 1)
            {
                ulong required = 0;
                foreach (D3D12OrdinaryDataChunk chunk in _ordinaryDataChunks)
                    required = checked(required + chunk.Used);
                ulong capacity = 64UL * 1024UL;
                while (capacity < required)
                    capacity = checked(capacity * 2);
                D3D12OrdinaryDataChunk consolidated = D3D12OrdinaryDataChunk.Create(
                    _context.NativeDevice,
                    capacity);
                foreach (D3D12OrdinaryDataChunk chunk in _ordinaryDataChunks)
                    chunk.Release();
                _ordinaryDataChunks.Clear();
                _ordinaryDataChunks.Add(consolidated);
            }
            else if (_ordinaryDataChunks.Count == 1)
            {
                _ordinaryDataChunks[0].Reset();
            }
            _ordinaryDataCursor = 0;
            _ordinaryDataCurrent = _ordinaryDataChunks.Count == 0
                ? null
                : _ordinaryDataChunks[0];
        }

        internal void ResetDescriptorArena(in CommandRecordingDesc description)
        {
            DescriptorGeneration generation = DescriptorGeneration;
            uint resourceRequired = checked(
                generation.ResourceCount + description.InitialResourceDescriptorCapacity);
            uint samplerRequired = checked(
                generation.SamplerCount + description.InitialSamplerDescriptorCapacity);
            EnsureResetHeaps(
                Math.Max(1u, resourceRequired),
                Math.Max(1u, samplerRequired));
            generation.CopyTo(
                _context.NativeDevice,
                _resourceArena,
                _samplerArena);
            _resourceUsed = generation.ResourceCount;
            _samplerUsed = generation.SamplerCount;
            _descriptorArenaVersion = checked(_descriptorArenaVersion + 1);
            _descriptorHeapsBound = false;
        }

        internal void ValidateDescriptorArenaCapacity(in CommandRecordingDesc description)
        {
            DescriptorGeneration generation = DescriptorGeneration;
            uint resourceRequired = checked(
                generation.ResourceCount + description.InitialResourceDescriptorCapacity);
            uint samplerRequired = checked(
                generation.SamplerCount + description.InitialSamplerDescriptorCapacity);
            ValidateCapacity(ParameterHeap.Resource, Math.Max(1u, resourceRequired));
            ValidateCapacity(ParameterHeap.Sampler, Math.Max(1u, samplerRequired));
        }

        internal uint AllocateDescriptors(ParameterHeap heap, uint count)
        {
            if (count == 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            (uint resourceBase, uint samplerBase) = heap == ParameterHeap.Resource
                ? AllocateDescriptorPair(count, 0)
                : AllocateDescriptorPair(0, count);
            return heap == ParameterHeap.Resource ? resourceBase : samplerBase;
        }

        internal (uint ResourceBase, uint SamplerBase) AllocateDescriptorPair(
            uint resourceCount,
            uint samplerCount)
        {
            uint resourceRequired = checked(_resourceUsed + resourceCount);
            uint samplerRequired = checked(_samplerUsed + samplerCount);
            EnsureRecordingCapacity(resourceRequired, samplerRequired);

            uint resourceBase = _resourceUsed;
            uint samplerBase = _samplerUsed;
            _resourceUsed = resourceRequired;
            _samplerUsed = samplerRequired;
            return (resourceBase, samplerBase);
        }

        internal void CopyDescriptor(
            ParameterHeap heap,
            uint index,
            in ResourceBinding binding,
            ResourceBindingType expectedType)
        {
            DescriptorHeapType nativeType = heap == ParameterHeap.Resource
                ? DescriptorHeapType.CbvSrvUav
                : DescriptorHeapType.Sampler;
            ID3D12DescriptorHeap* destinationHeap = heap == ParameterHeap.Resource
                ? _resourceArena
                : _samplerArena;
            CpuDescriptorHandle start =
                destinationHeap->GetCPUDescriptorHandleForHeapStart();
            uint increment = _context.NativeDevice.Native
                ->GetDescriptorHandleIncrementSize(nativeType);
            CpuDescriptorHandle destination = new(
                start.Ptr + checked((nuint)(index * increment)));
            if (binding.Value is GraphicsObject owner && owner is INativeDescriptor descriptor)
            {
                _context.NativeDevice.Native->CopyDescriptorsSimple(
                    1,
                    destination,
                    descriptor.NativeDescriptor.Cpu,
                    nativeType);
            }
            else if (binding.Value is null)
            {
                WriteNullDescriptor(_context.NativeDevice, nativeType, expectedType, destination);
            }
            else
            {
                throw new ArgumentException("The binding is not a D3D12 descriptor.", nameof(binding));
            }
        }

        internal GpuDescriptorHandle GetGpuHandle(ParameterHeap heap, uint index)
        {
            EnsureDescriptorHeapsBound();
            DescriptorHeapType nativeType = heap == ParameterHeap.Resource
                ? DescriptorHeapType.CbvSrvUav
                : DescriptorHeapType.Sampler;
            ID3D12DescriptorHeap* descriptorHeap = heap == ParameterHeap.Resource
                ? _resourceArena
                : _samplerArena;
            GpuDescriptorHandle start =
                descriptorHeap->GetGPUDescriptorHandleForHeapStart();
            uint increment = _context.NativeDevice.Native
                ->GetDescriptorHandleIncrementSize(nativeType);
            return new GpuDescriptorHandle(
                start.Ptr + checked((ulong)index * increment));
        }

        internal void Capture(D3D12ParameterMaterialization materialization)
        {
            foreach (D3D12SwapchainImageLease image in materialization.SwapchainImages)
                CaptureSwapchainUse(image);
            if (!_capturedParameterData.Add(materialization))
                materialization.Release();
        }

        internal void CaptureObject(GraphicsObject value)
            => _automaticCaptures?.CaptureObject(value);

        internal void ReleaseBindingTransients()
        {
            foreach (D3D12ParameterMaterialization value in _capturedParameterData)
                value.Release();
            _capturedParameterData.Clear();
        }

        internal void ReleaseDescriptorArena()
        {
            foreach (D3D12OrdinaryDataChunk chunk in _ordinaryDataChunks)
                chunk.Release();
            _ordinaryDataChunks.Clear();
            _ordinaryDataCursor = 0;
            _ordinaryDataCurrent = null;
            ID3D12DescriptorHeap* sampler = _samplerArena;
            _samplerArena = null;
            if (sampler is not null)
                _ = sampler->Release();
            ID3D12DescriptorHeap* resource = _resourceArena;
            _resourceArena = null;
            if (resource is not null)
                _ = resource->Release();
            _resourceCapacity = 0;
            _samplerCapacity = 0;
            _descriptorHeapsBound = false;
        }

        private void EnsureResetHeaps(uint resourceRequired, uint samplerRequired)
        {
            ValidateCapacity(ParameterHeap.Resource, resourceRequired);
            ValidateCapacity(ParameterHeap.Sampler, samplerRequired);
            bool replaceResource = _resourceArena is null || _resourceCapacity < resourceRequired;
            bool replaceSampler = _samplerArena is null || _samplerCapacity < samplerRequired;
            if (!replaceResource && !replaceSampler)
                return;

            ID3D12DescriptorHeap* resourceReplacement = null;
            ID3D12DescriptorHeap* samplerReplacement = null;
            try
            {
                if (replaceResource)
                {
                    resourceReplacement = CreateShaderVisibleHeap(
                        ParameterHeap.Resource,
                        resourceRequired);
                }
                if (replaceSampler)
                {
                    samplerReplacement = CreateShaderVisibleHeap(
                        ParameterHeap.Sampler,
                        samplerRequired);
                }

                if (replaceResource)
                {
                    ID3D12DescriptorHeap* previous = _resourceArena;
                    _resourceArena = resourceReplacement;
                    _resourceCapacity = resourceRequired;
                    resourceReplacement = null;
                    if (previous is not null)
                        _ = previous->Release();
                }
                if (replaceSampler)
                {
                    ID3D12DescriptorHeap* previous = _samplerArena;
                    _samplerArena = samplerReplacement;
                    _samplerCapacity = samplerRequired;
                    samplerReplacement = null;
                    if (previous is not null)
                        _ = previous->Release();
                }
            }
            finally
            {
                if (samplerReplacement is not null)
                    _ = samplerReplacement->Release();
                if (resourceReplacement is not null)
                    _ = resourceReplacement->Release();
            }
        }

        private void EnsureRecordingCapacity(uint resourceRequired, uint samplerRequired)
        {
            ValidateCapacity(ParameterHeap.Resource, resourceRequired);
            ValidateCapacity(ParameterHeap.Sampler, samplerRequired);
            bool replaceResource = resourceRequired > _resourceCapacity;
            bool replaceSampler = samplerRequired > _samplerCapacity;
            if (!replaceResource && !replaceSampler)
                return;
            bool rebind = _descriptorHeapsBound;

            uint resourceCapacity = replaceResource
                ? GrowCapacity(ParameterHeap.Resource, _resourceCapacity, resourceRequired)
                : _resourceCapacity;
            uint samplerCapacity = replaceSampler
                ? GrowCapacity(ParameterHeap.Sampler, _samplerCapacity, samplerRequired)
                : _samplerCapacity;
            ID3D12DescriptorHeap* resourceReplacement = null;
            ID3D12DescriptorHeap* samplerReplacement = null;
            try
            {
                if (replaceResource)
                {
                    resourceReplacement = CreateShaderVisibleHeap(
                        ParameterHeap.Resource,
                        resourceCapacity);
                }
                if (replaceSampler)
                {
                    samplerReplacement = CreateShaderVisibleHeap(
                        ParameterHeap.Sampler,
                        samplerCapacity);
                }

                CopyRecordingDescriptors(
                    ParameterHeap.Resource,
                    _resourceArena,
                    resourceReplacement,
                    _resourceUsed);
                CopyRecordingDescriptors(
                    ParameterHeap.Sampler,
                    _samplerArena,
                    samplerReplacement,
                    _samplerUsed);

                int retainedHeapCount =
                    (replaceResource && _resourceArena is not null ? 1 : 0) +
                    (replaceSampler && _samplerArena is not null ? 1 : 0);
                _transientObjects.EnsureCapacity(
                    checked(_transientObjects.Count + retainedHeapCount));

                if (replaceResource)
                {
                    ID3D12DescriptorHeap* previous = _resourceArena;
                    _resourceArena = resourceReplacement;
                    _resourceCapacity = resourceCapacity;
                    resourceReplacement = null;
                    if (previous is not null)
                        _transientObjects.Add((nint)previous);
                }
                if (replaceSampler)
                {
                    ID3D12DescriptorHeap* previous = _samplerArena;
                    _samplerArena = samplerReplacement;
                    _samplerCapacity = samplerCapacity;
                    samplerReplacement = null;
                    if (previous is not null)
                        _transientObjects.Add((nint)previous);
                }
                _descriptorArenaVersion = checked(_descriptorArenaVersion + 1);
                if (rebind)
                {
                    BindDescriptorHeaps();
                    _context.ReapplyRootTables();
                }
            }
            finally
            {
                if (samplerReplacement is not null)
                    _ = samplerReplacement->Release();
                if (resourceReplacement is not null)
                    _ = resourceReplacement->Release();
            }
        }

        private uint GrowCapacity(ParameterHeap heap, uint current, uint required)
        {
            uint maximum = MaximumCapacity(heap);
            return Math.Min(maximum, Math.Max(required, checked(current * 2)));
        }

        private void CopyRecordingDescriptors(
            ParameterHeap heap,
            ID3D12DescriptorHeap* source,
            ID3D12DescriptorHeap* destination,
            uint count)
        {
            if (destination is null || count == 0)
                return;
            if (source is null)
                throw new InvalidOperationException("A populated descriptor arena has no source Heap.");
            _context.NativeDevice.Native->CopyDescriptorsSimple(
                count,
                destination->GetCPUDescriptorHandleForHeapStart(),
                source->GetCPUDescriptorHandleForHeapStart(),
                heap == ParameterHeap.Resource
                    ? DescriptorHeapType.CbvSrvUav
                    : DescriptorHeapType.Sampler);
        }

        private ID3D12DescriptorHeap* CreateShaderVisibleHeap(
            ParameterHeap heap,
            uint count)
        {
            DescriptorHeapDesc description = new(
                heap == ParameterHeap.Resource
                    ? DescriptorHeapType.CbvSrvUav
                    : DescriptorHeapType.Sampler,
                count,
                DescriptorHeapFlags.ShaderVisible,
                _context.NativeDevice.EnabledNodeMask);
            ID3D12DescriptorHeap* result = null;
            Guid iid = ID3D12DescriptorHeap.Guid;
            NativeCall.ThrowIfFailed(
                _context.NativeDevice.Native->CreateDescriptorHeap(
                    &description,
                    &iid,
                    (void**)&result),
                "ID3D12Device::CreateDescriptorHeap(command arena)");
            return result;
        }

        private void EnsureDescriptorHeapsBound()
        {
            if (!_descriptorHeapsBound)
                BindDescriptorHeaps();
        }

        private void BindDescriptorHeaps()
        {
            ID3D12DescriptorHeap** heaps = stackalloc ID3D12DescriptorHeap*[2]
            {
                _resourceArena,
                _samplerArena,
            };
            List->SetDescriptorHeaps(2, heaps);
            _descriptorHeapsBound = true;
        }

        private void ValidateCapacity(ParameterHeap heap, uint required)
        {
            if (required > MaximumCapacity(heap))
            {
                throw new GraphicsException(
                    GraphicsError.OutOfDescriptors,
                    $"The command {heap} descriptor arena is exhausted.");
            }
        }

        private uint MaximumCapacity(ParameterHeap heap) => heap == ParameterHeap.Resource
            ? _context.NativeDevice.Capabilities.Limits.ResourceDescriptorCapacity
            : _context.NativeDevice.Capabilities.Limits.SamplerDescriptorCapacity;
    }

    private static void WriteNullDescriptor(
        D3D12Device device,
        DescriptorHeapType heap,
        ResourceBindingType type,
        CpuDescriptorHandle destination)
    {
        if (heap == DescriptorHeapType.Sampler)
        {
            Silk.NET.Direct3D12.SamplerDesc sampler = new()
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunc.Always,
                MaxAnisotropy = 1,
                MaxLOD = float.MaxValue,
            };
            device.Native->CreateSampler(&sampler, destination);
            return;
        }

        switch (type)
        {
            case ResourceBindingType.BufferUav:
            case ResourceBindingType.TextureUav:
                device.Native->CreateUnorderedAccessView(null, null, null, destination);
                break;
            case ResourceBindingType.ConstantBuffer:
                device.Native->CreateConstantBufferView(null, destination);
                break;
            case ResourceBindingType.None:
            case ResourceBindingType.BufferSrv:
            case ResourceBindingType.TextureSrv:
            case ResourceBindingType.AccelerationStructure:
                device.Native->CreateShaderResourceView(null, null, destination);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }
}
