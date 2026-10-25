using Silk.NET.Core.Native;
using System.Buffers;
using System.Numerics;
using Silk.NET.Direct3D12;
using SlangShaderSharp;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeRange = Silk.NET.Direct3D12.Range;
using NativeResource = Silk.NET.Direct3D12.ID3D12Resource;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void SetPipeline(CommandContext context, Pipeline pipeline)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiBindings);
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12Pipeline native = RequirePipeline(pipeline);
        RequireSameDevice(command.NativeDevice, native, nameof(pipeline));
        if (native.Type == PipelineType.WorkGraph)
            throw new ArgumentException(
                "A Work Graph Pipeline must be selected with BindWorkGraph.",
                nameof(pipeline));
        if (ReferenceEquals(command.CurrentPipeline, native))
            return;
        command.PrepareCaptures(1);
        command.PrepareDescriptorTables(native.RootSignature.DefaultTables);
        SetPipelineSlow(command, native);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void SetPipelineSlow(
        D3D12CommandContext command,
        D3D12Pipeline native)
    {
        command.CapturePipeline(native);
        command.ResetWorkGraphState();
        if (native is D3D12ClassicPipeline classicPipeline)
            D3D12CommandListFastCalls.SetPipelineState(command.List, classicPipeline.Native);
        else if (native is D3D12RayTracingPipeline rayTracing)
            CommandBindingTrace.SetPipelineState1(command.List, rayTracing.Native);
        else
            throw new ArgumentException("The Pipeline cannot be selected by SetPipeline.", nameof(native));
        bool rootSignatureChanged = !command.RootSignatureEqual(native);
        if (rootSignatureChanged)
        {
            D3D12CommandListFastCalls.SetRootSignature(
                command.List,
                native.Type is PipelineType.Compute or PipelineType.RayTracing,
                native.RootSignature.Native);
        }
        command.RememberPipeline(native);

        if (rootSignatureChanged)
            foreach (DefaultRootTable table in native.RootSignature.DefaultTables)
                command.SetRootTable(table.RootParameterIndex, table.Heap, 0);

        if (native.Type == PipelineType.Graphics)
        {
            D3D12ClassicPipeline classic = (D3D12ClassicPipeline)native;
            if ((classic.DynamicStates & DynamicStates.PrimitiveTopology) == 0 &&
                !command.PrimitiveTopologyEqual(classic.Topology))
            {
                D3D12CommandListFastCalls.SetPrimitiveTopology(
                    command.List,
                    ToNativeTopology(classic.Topology));
                command.RememberPrimitiveTopology(classic.Topology);
            }
            if ((classic.DynamicStates & DynamicStates.StripCut) == 0)
                command.RememberStripCut(classic.StripCut);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void SetPersistentParameterBindings(
        CommandContext context,
        PersistentParameterBindings bindings)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiBindings);
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12PersistentParameterBindings native =
            bindings as D3D12PersistentParameterBindings ??
            throw new ArgumentException(
                "The PersistentParameterBindings object was not created by the Direct3D 12 backend.",
                nameof(bindings));
        D3D12PersistentParameterData current =
            RequireCurrentPersistentParameterData(native);
        command.Recording.TrackRepeatedParameters(native, current);
        if (command.PersistentBindingIdentityEqual(native, current))
            return;
        D3D12Pipeline pipeline = command.Pipeline;
        if (!ReferenceEquals(pipeline, native.OwnerPipeline))
            throw new ArgumentException(
                "Persistent parameter bindings belong to a different Pipeline instance.",
                nameof(bindings));
        NativeParameterBinding layout = native.NativeLayout;
        if (native.OrdinaryConstantBufferRootParameter is uint rootParameter)
        {
            BindPersistentRootConstantBuffer(
                command,
                native,
                current,
                rootParameter);
            return;
        }
        BindPersistentDescriptors(command, native, layout, current);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static D3D12PersistentParameterData RequireCurrentPersistentParameterData(
        D3D12PersistentParameterBindings bindings)
    {
        D3D12PersistentParameterData? current = bindings.CurrentData;
        if (current is not null)
            return current;
        bindings.ThrowIfDisposed();
        throw new InvalidOperationException(
            "Persistent parameter bindings have no materialized value.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BindPersistentRootConstantBuffer(
        D3D12CommandContext command,
        D3D12PersistentParameterBindings bindings,
        D3D12PersistentParameterData current,
        uint rootParameter)
    {
        while (true)
        {
            if (!command.TryCapturePersistentParameterData(current))
            {
                current = RequireCurrentPersistentParameterData(bindings);
                if (command.PersistentBindingIdentityEqual(bindings, current))
                    return;
                continue;
            }
            command.SetPersistentRootConstantBufferPrepared(
                rootParameter,
                current.OrdinaryAddress);
            command.RememberPersistentBindings(bindings, current);
            return;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void BindPersistentDescriptors(
        D3D12CommandContext command,
        D3D12PersistentParameterBindings bindings,
        NativeParameterBinding layout,
        D3D12PersistentParameterData current)
    {
        while (true)
        {
            command.RequireResourceVisible(current.VisibleNodeMask, nameof(bindings));
            command.PrepareSwapchainUses(current.SwapchainImages.Length);
            if (!command.TryCapturePersistentParameterData(current))
            {
                current = RequireCurrentPersistentParameterData(bindings);
                if (command.PersistentBindingIdentityEqual(bindings, current))
                    return;
                continue;
            }
            if (layout.OrdinaryRoot is { UsesRootConstants: true } ordinary)
            {
                command.PrepareRootConstants(
                    ordinary.RootParameterIndex,
                    ordinary.ConstantCount);
            }
            command.PrepareDescriptors(
                layout.ResourceTable?.DescriptorCount ?? 0,
                layout.SamplerTable?.DescriptorCount ?? 0);
            ApplyPersistentDescriptors(command, bindings, layout, current);
            return;
        }
    }

    private void ApplyPersistentDescriptors(
        D3D12CommandContext command,
        D3D12PersistentParameterBindings bindings,
        NativeParameterBinding layout,
        D3D12PersistentParameterData data)
    {
        uint resourceCount = layout.ResourceTable?.DescriptorCount ?? 0;
        uint samplerCount = layout.SamplerTable?.DescriptorCount ?? 0;
        uint resourceBase = 0;
        uint samplerBase = 0;
        if (resourceCount != 0 || samplerCount != 0)
        {
            (resourceBase, samplerBase) =
                command.AllocateTransientDescriptorPair(resourceCount, samplerCount);
        }
        command.CopyPersistentDescriptors(resourceBase, samplerBase, data.Descriptors);
        command.ApplyPersistentBlock(
            layout,
            resourceBase,
            samplerBase,
            data);
        command.RememberPersistentBindings(bindings, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetTransientParameterBindings(
        CommandContext context,
        in ParameterBlockBindings bindings)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiBindings);
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12Pipeline pipeline = command.Pipeline;
        NativeParameterBinding layout = command.ResolveParameterBlock(pipeline, bindings.Layout);
        int ordinaryRootParameter = layout.ResourceTable is null && layout.SamplerTable is null &&
            layout.OrdinaryRoot is { UsesRootConstants: false } ordinary
                ? checked((int)ordinary.RootParameterIndex)
                : -1;
        if (ordinaryRootParameter >= 0 &&
            layout.OrdinaryRoot!.Value.DataSize == 16 &&
            bindings.Resources.IsEmpty &&
            bindings.OrdinaryData.Length == 16)
        {
            ref byte data = ref MemoryMarshal.GetReference(bindings.OrdinaryData);
            command.SetTransientOrdinaryConstantBuffer16(
                bindings.Layout,
                checked((uint)ordinaryRootParameter),
                ref data);
            return;
        }
        SetTransientParameterBindingsGeneral(command, layout, bindings);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SetTransientParameterBindingsGeneral(
        D3D12CommandContext command,
        NativeParameterBinding layout,
        in ParameterBlockBindings bindings)
    {
        RequireNativeParameterBindings(
            layout,
            bindings.Resources,
            bindings.OrdinaryData);
        if (command.ParameterBindingsEqual(
                bindings.Layout,
                bindings.Resources,
                bindings.OrdinaryData,
                out bool sameTransientShape))
        {
            return;
        }
        if ((layout.ResourceTable is null && layout.SamplerTable is null) ||
            command.TransientResourceBindingsEqual(bindings.Layout, bindings.Resources))
        {
            command.PrepareOrdinaryData(layout.OrdinaryRoot is { UsesRootConstants: false }
                    ? checked((ulong)bindings.OrdinaryData.Length)
                    : 0);
            command.PrepareBindingStorage(bindings.Resources.Length, bindings.OrdinaryData.Length);
            command.ApplyTransientOrdinaryData(layout, bindings.OrdinaryData);
            command.RememberTransientBindings(bindings, sameTransientShape);
            return;
        }
        SetTransientParameterBindingsSlow(command, layout, sameTransientShape, bindings);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void SetTransientParameterBindingsSlow(
        D3D12CommandContext command,
        NativeParameterBinding layout,
        bool sameTransientShape,
        in ParameterBlockBindings bindings)
    {
        bool usesOrdinaryConstantBuffer = layout.OrdinaryRoot is { UsesRootConstants: false };
        int bindingCount = bindings.Resources.Length;
        command.PrepareCaptures(checked(bindingCount * 2), bindingCount, bindingCount);
        command.PrepareSwapchainUses(bindingCount);
        command.PrepareOrdinaryData(usesOrdinaryConstantBuffer
                ? checked((ulong)bindings.OrdinaryData.Length)
                : 0);
        command.PrepareBindingStorage(bindingCount, bindings.OrdinaryData.Length);
        if (layout.OrdinaryRoot is { UsesRootConstants: true } ordinaryRoot)
        {
            command.PrepareRootConstants(
                ordinaryRoot.RootParameterIndex,
                ordinaryRoot.ConstantCount);
        }
        D3D12OrdinaryDataReservation ordinary = usesOrdinaryConstantBuffer
            ? command.ReserveTransientOrdinaryData(checked((ulong)bindings.OrdinaryData.Length))
            : default;
        for (int ordinal = 0; ordinal < bindings.Resources.Length; ordinal++)
            command.Capture(bindings.Resources[ordinal]);
        command.PrepareDescriptors(
            layout.ResourceTable?.DescriptorCount ?? 0,
            layout.SamplerTable?.DescriptorCount ?? 0);

        uint resourceCount = layout.ResourceTable?.DescriptorCount ?? 0;
        uint samplerCount = layout.SamplerTable?.DescriptorCount ?? 0;
        (uint resourceBase, uint samplerBase) = command.AllocateTransientDescriptorPair(resourceCount, samplerCount);

        command.CopyTransientDescriptors(resourceBase, samplerBase, bindings.Resources, layout.Slots);

        if (usesOrdinaryConstantBuffer)
            ordinary.Commit(bindings.OrdinaryData);
        command.ApplyTransientBlock(
            layout,
            resourceBase,
            samplerBase,
            bindings.OrdinaryData,
            ordinary.Address);
        command.RememberTransientBindings(bindings, sameTransientShape);
    }

    private sealed class D3D12PersistentParameterData
    {
        private D3D12ParameterBytes? _ordinaryBytes;
        private int _references = 1;

        private D3D12PersistentParameterData(
            ulong version,
            ResourceBinding[] resources,
            DescriptorRecord[] descriptors,
            D3D12SwapchainImageLease[] swapchainImages,
            byte[] ordinaryData,
            D3D12ParameterBytes? ordinary,
            ulong ordinaryAddress,
            uint visibleNodeMask)
        {
            Version = version;
            Resources = resources;
            Descriptors = descriptors;
            SwapchainImages = swapchainImages;
            OrdinaryData = ordinaryData;
            _ordinaryBytes = ordinary;
            OrdinaryAddress = ordinaryAddress;
            VisibleNodeMask = visibleNodeMask;
        }

        internal ulong Version { get; }
        internal ResourceBinding[] Resources { get; }
        internal DescriptorRecord[] Descriptors { get; }
        internal D3D12SwapchainImageLease[] SwapchainImages { get; }
        internal byte[] OrdinaryData { get; }
        internal ulong OrdinaryAddress { get; }
        internal uint VisibleNodeMask { get; }

        internal bool ContentEquals(D3D12PersistentParameterData other)
            => ContentEquals(other.Resources, other.OrdinaryData);

        internal bool ContentEquals(
            ReadOnlySpan<ResourceBinding> resources,
            ReadOnlySpan<byte> ordinaryData)
        {
            if (!OrdinaryData.AsSpan().SequenceEqual(ordinaryData) ||
                Resources.Length != resources.Length)
            {
                return false;
            }
            for (int index = 0; index < Resources.Length; index++)
            {
                if (Resources[index] != resources[index])
                    return false;
            }
            return true;
        }

        internal static D3D12PersistentParameterData Create(
            D3D12Device device,
            ulong version,
            ReadOnlySpan<ResourceBinding> resources,
            ReadOnlySpan<DescriptorSlotDesc> slots,
            ReadOnlySpan<byte> ordinaryData,
            bool createOrdinaryBuffer)
        {
            if (resources.Length != slots.Length)
                throw new ArgumentException("The reflected descriptor-slot count does not match the binding count.", nameof(slots));
            ResourceBinding[] resourcesCopy = resources.ToArray();
            DescriptorRecord[] descriptorRecords = CreateDescriptorRecords(
                resourcesCopy,
                slots);
            D3D12ParameterBytes? lifetime = null;
            try
            {
                uint visibleNodeMask = uint.MaxValue;
                foreach (DescriptorRecord descriptor in descriptorRecords)
                    visibleNodeMask &= descriptor.VisibleNodeMask;
                byte[] ordinaryDataCopy = ordinaryData.ToArray();
                D3D12SwapchainImageLease[] swapchainImages =
                    CaptureSwapchainBindings(resourcesCopy);
                ulong address = 0;
                if (createOrdinaryBuffer && ordinaryDataCopy.Length != 0)
                {
                    lifetime = device.ResourceAllocator.AllocateParameterBytes(ordinaryDataCopy);
                    address = lifetime.Address;
                }
                D3D12PersistentParameterData result =
                    new(
                        version,
                        resourcesCopy,
                        descriptorRecords,
                        swapchainImages,
                        ordinaryDataCopy,
                        lifetime,
                        address,
                        visibleNodeMask);
                lifetime = null;
                descriptorRecords = [];
                return result;
            }
            catch
            {
                lifetime?.Release();
                foreach (DescriptorRecord record in descriptorRecords)
                    record.Release();
                throw;
            }
        }

        private static DescriptorRecord[] CreateDescriptorRecords(
            ReadOnlySpan<ResourceBinding> resources,
            ReadOnlySpan<DescriptorSlotDesc> slots)
        {
            DescriptorRecord[] records = new DescriptorRecord[resources.Length];
            int created = 0;
            try
            {
                for (; created < resources.Length; created++)
                {
                    ref readonly ResourceBinding binding = ref resources[created];
                    ref readonly DescriptorSlotDesc slot = ref slots[created];
                    if (binding.Value is null)
                    {
                        records[created] = DescriptorRecord.CreateNull(slot);
                        continue;
                    }
                    records[created] = DescriptorRecord.Create(
                        GetBindingDescriptor(binding),
                        binding.Value!,
                        binding.Type,
                        slot);
                }
                return records;
            }
            catch
            {
                for (int index = 0; index < created; index++)
                    records[index].Release();
                throw;
            }
        }

        private static D3D12SwapchainImageLease[] CaptureSwapchainBindings(
            ReadOnlySpan<ResourceBinding> resources)
        {
            HashSet<D3D12SwapchainImageLease>? images = null;
            foreach (ref readonly ResourceBinding binding in resources)
            {
                D3D12SwapchainImageLease? lease = binding.Value switch
                {
                    TextureSrv view => RequireD3D12.Texture(view.Resource).SwapchainLease,
                    TextureUav view => RequireD3D12.Texture(view.Resource).SwapchainLease,
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

        internal bool TryRetain()
        {
            int current = Volatile.Read(ref _references);
            while (current > 0)
            {
                int exchanged = Interlocked.CompareExchange(
                    ref _references,
                    checked(current + 1),
                    current);
                if (exchanged == current)
                    return true;
                current = exchanged;
            }
            return false;
        }

        internal void Retain()
        {
            if (!TryRetain())
                throw new ObjectDisposedException(nameof(D3D12PersistentParameterData));
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0)
                return;
            foreach (DescriptorRecord descriptor in Descriptors)
                descriptor.Release();
            Interlocked.Exchange(ref _ordinaryBytes, null)?.Release();
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
            GpuAddress = ResourceTrace.GetGPUVirtualAddress(resource);
        }

        internal ulong Capacity { get; }
        internal ulong GpuAddress { get; }
        internal NativeResource* Resource => _resource;
        internal ulong Used => _used;

        internal static D3D12OrdinaryDataChunk Create(
            D3D12Device device,
            uint nodeMask,
            ulong capacity,
            uint? visibleNodeMask = null)
        {
            NativeResource* resource = CreateCommittedResource(
                device,
                MemoryType.Upload,
                shareable: false,
                nodeMask,
                visibleNodeMask ?? nodeMask,
                CreateBufferDescription(new BufferDesc(
                    capacity,
                    BufferUsages.Constant)),
                ReadOnlySpan<Silk.NET.DXGI.Format>.Empty);
            try
            {
                void* mapped = null;
                NativeRange readRange = default;
                ThrowIfFailed(
                    device,
                        ResourceTrace.Map(resource, 0, &readRange, &mapped),
                    NativeOperationType.Ordinary,
                    "ID3D12Resource::Map(command ordinary-data arena)");
                return new D3D12OrdinaryDataChunk(resource, (byte*)mapped, capacity);
            }
            catch
            {
                _ = ResourceTrace.Release(resource);
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryWrite16(ref byte data, out ulong address)
        {
            const ulong reservedSize = 256;
            ulong offset = _used;
            if (reservedSize > Capacity - offset)
            {
                address = 0;
                return false;
            }
            byte* destination = _mapped + checked((nint)offset);
            Unsafe.WriteUnaligned(
                destination,
                Unsafe.ReadUnaligned<ulong>(ref data));
            Unsafe.WriteUnaligned(
                destination + sizeof(ulong),
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref data, sizeof(ulong))));
            _used = offset + reservedSize;
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
            ulong remaining = size;
            while (remaining >= sizeof(uint))
            {
                int count = checked((int)Math.Min(remaining / sizeof(uint), (ulong)int.MaxValue));
                new Span<uint>(destination, count).Fill(value);
                ulong written = checked((ulong)count * sizeof(uint));
                destination += checked((nint)written);
                remaining -= written;
            }
            byte* pattern = (byte*)&value;
            for (uint index = 0; index < remaining; index++)
                destination[index] = pattern[index];
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

        internal void WriteBytes(ulong offset, ReadOnlySpan<byte> data) =>
            data.CopyTo(new Span<byte>(_mapped + checked((nint)offset), data.Length));

        internal void Reset() => _used = 0;

        internal void Release()
        {
            NativeResource* resource = _resource;
            _resource = null;
            _mapped = null;
            _used = 0;
            if (resource is null)
                return;
            ResourceTrace.Unmap(resource, 0, null);
            _ = ResourceTrace.Release(resource);
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
        internal void GetDescriptorArenaCapacities(out uint resources, out uint samplers)
        {
            resources = 0;
            samplers = 0;
            foreach (D3D12CommandSlot storage in _slots)
            {
                resources = Math.Max(resources, storage.ResourceDescriptorCapacity);
                samplers = Math.Max(samplers, storage.SamplerDescriptorCapacity);
            }
        }

        private readonly RootTableState[] _rootTables = new RootTableState[64];
        private readonly bool[] _rootTableSet = new bool[64];
        private readonly ulong[] _rootConstantBuffers = new ulong[64];
        private readonly bool[] _rootConstantBufferSet = new bool[64];
        private readonly byte[]?[] _rootConstants = new byte[]?[64];
        private readonly bool[] _rootConstantsSet = new bool[64];
        private int _rootStateLength;
        private D3D12Pipeline? _pipeline;
        private D3D12PersistentParameterBindings? _persistentBindings;
        private D3D12PersistentParameterData? _persistentData;
        private VariableLayoutReflection _resolvedParameterLayout = VariableLayoutReflection.Null;
        private NativeParameterBinding? _resolvedParameterBinding;
        private VariableLayoutReflection _transientBindingLayout = VariableLayoutReflection.Null;
        private ResourceBinding[] _transientBindingResources = [];
        private byte[] _transientBindingOrdinaryData = [];
        private int _transientBindingResourceCount;
        private int _transientBindingOrdinaryDataCount;
        private bool _hasTransientBindings;
        private bool _computeRootBindings;
        internal D3D12Pipeline? CurrentPipeline => _pipeline;

        internal D3D12Pipeline Pipeline
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => _pipeline!;
        }

        internal bool RootSignatureEqual(D3D12Pipeline pipeline) =>
            _pipeline is not null &&
            _pipeline.RootSignature.Native == pipeline.RootSignature.Native &&
            _computeRootBindings == (pipeline.Type is PipelineType.Compute or
                PipelineType.RayTracing or PipelineType.WorkGraph);

        internal void RememberPipeline(D3D12Pipeline pipeline)
        {
            if (!RootSignatureEqual(pipeline))
                ClearRootBindingState();
            _persistentBindings = null;
            _persistentData = null;
            _pipeline = pipeline;
            _resolvedParameterLayout = VariableLayoutReflection.Null;
            _resolvedParameterBinding = null;
            _computeRootBindings = pipeline.Type is PipelineType.Compute or
                PipelineType.RayTracing or PipelineType.WorkGraph;
            _rootStateLength = pipeline.RootSignature.RootStateLength;
        }

        internal void ResetPipelineBindingState()
        {
            _pipeline = null;
            _resolvedParameterLayout = VariableLayoutReflection.Null;
            _resolvedParameterBinding = null;
            ClearRootBindingState();
        }

        internal void ClearRootBindingState()
        {
            Array.Clear(_rootTableSet, 0, _rootStateLength);
            Array.Clear(_rootConstantBufferSet, 0, _rootStateLength);
            Array.Clear(_rootConstantsSet, 0, _rootStateLength);
            _rootStateLength = 0;
            _persistentBindings = null;
            _persistentData = null;
            if (_transientBindingResourceCount != 0)
            {
                Array.Clear(
                    _transientBindingResources,
                    0,
                    _transientBindingResourceCount);
            }
            _transientBindingResourceCount = 0;
            _transientBindingOrdinaryDataCount = 0;
            _hasTransientBindings = false;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal NativeParameterBinding ResolveParameterBlock(
            D3D12Pipeline pipeline,
            VariableLayoutReflection layout)
        {
            NativeParameterBinding? cached = _resolvedParameterBinding;
            if (cached is not null && _resolvedParameterLayout == layout)
                return cached;
            NativeParameterBinding resolved = pipeline.RootSignature.GetBlock(layout);
            _resolvedParameterLayout = layout;
            _resolvedParameterBinding = resolved;
            return resolved;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool PersistentBindingIdentityEqual(
            D3D12PersistentParameterBindings bindings,
            D3D12PersistentParameterData data) =>
            ReferenceEquals(_persistentBindings, bindings) &&
            ReferenceEquals(_persistentData, data);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool ParameterBindingsEqual(
            VariableLayoutReflection layout,
            ReadOnlySpan<ResourceBinding> resources,
            ReadOnlySpan<byte> ordinaryData,
            out bool sameTransientShape)
        {
            if (_hasTransientBindings)
            {
                if (_transientBindingLayout != layout ||
                    _transientBindingResourceCount != resources.Length ||
                    _transientBindingOrdinaryDataCount != ordinaryData.Length)
                {
                    sameTransientShape = false;
                    return false;
                }
                sameTransientShape = true;
                if (!resources.IsEmpty && !TransientResourcesEqualSlow(resources))
                    return false;
                return TransientOrdinaryDataEqual(ordinaryData);
            }
            sameTransientShape = false;
            return false;
        }

        internal bool TransientResourceBindingsEqual(
            VariableLayoutReflection layout,
            ReadOnlySpan<ResourceBinding> resources) =>
            _hasTransientBindings && _transientBindingLayout == layout &&
            _transientBindingResourceCount == resources.Length &&
            (resources.IsEmpty || TransientResourcesEqualSlow(resources));

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool TransientResourcesEqualSlow(ReadOnlySpan<ResourceBinding> resources)
        {
            for (int index = 0; index < resources.Length; index++)
            {
                if (_transientBindingResources[index] != resources[index])
                    return false;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TransientOrdinaryDataEqual(ReadOnlySpan<byte> ordinaryData)
        {
            if (ordinaryData.Length == 16)
            {
                ref byte candidate = ref MemoryMarshal.GetReference(ordinaryData);
                ref byte current = ref MemoryMarshal.GetArrayDataReference(
                    _transientBindingOrdinaryData);
                return Unsafe.ReadUnaligned<ulong>(ref candidate) ==
                       Unsafe.ReadUnaligned<ulong>(ref current) &&
                       Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref candidate, 8)) ==
                       Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref current, 8));
            }
            return TransientOrdinaryDataEqualSlow(ordinaryData);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool TransientOrdinaryDataEqualSlow(ReadOnlySpan<byte> ordinaryData) =>
            ordinaryData.SequenceEqual(
                _transientBindingOrdinaryData.AsSpan(0, _transientBindingOrdinaryDataCount));

        internal void RememberPersistentBindings(
            D3D12PersistentParameterBindings bindings,
            D3D12PersistentParameterData data)
        {
            _persistentBindings = bindings;
            _persistentData = data;
            _hasTransientBindings = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RememberTransientBindings(
            in ParameterBlockBindings bindings,
            bool sameTransientShape)
        {
            if (!sameTransientShape &&
                (_transientBindingResources.Length < bindings.Resources.Length ||
                 _transientBindingOrdinaryData.Length < bindings.OrdinaryData.Length))
            {
                EnsureTransientBindingCapacity(
                    bindings.Resources.Length,
                    bindings.OrdinaryData.Length);
            }
            if (!bindings.Resources.IsEmpty)
                CopyTransientResourcesSlow(bindings.Resources);
            if (bindings.OrdinaryData.Length == 16)
            {
                ref byte source = ref MemoryMarshal.GetReference(bindings.OrdinaryData);
                ref byte destination = ref MemoryMarshal.GetArrayDataReference(
                    _transientBindingOrdinaryData);
                Unsafe.WriteUnaligned(
                    ref destination,
                    Unsafe.ReadUnaligned<ulong>(ref source));
                Unsafe.WriteUnaligned(
                    ref Unsafe.Add(ref destination, 8),
                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, 8)));
            }
            else if (!bindings.OrdinaryData.IsEmpty)
            {
                CopyTransientOrdinaryDataSlow(bindings.OrdinaryData);
            }
            if (sameTransientShape)
                return;
            _transientBindingLayout = bindings.Layout;
            _transientBindingResourceCount = bindings.Resources.Length;
            _transientBindingOrdinaryDataCount = bindings.OrdinaryData.Length;
            _hasTransientBindings = true;
            _persistentBindings = null;
            _persistentData = null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CopyTransientResourcesSlow(ReadOnlySpan<ResourceBinding> resources) =>
            resources.CopyTo(_transientBindingResources);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CopyTransientOrdinaryDataSlow(ReadOnlySpan<byte> ordinaryData) =>
            ordinaryData.CopyTo(_transientBindingOrdinaryData);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EnsureTransientBindingCapacity(int resourceCount, int ordinaryDataCount)
        {
            if (_transientBindingResources.Length < resourceCount)
                Array.Resize(ref _transientBindingResources, resourceCount);
            if (_transientBindingOrdinaryData.Length < ordinaryDataCount)
                Array.Resize(ref _transientBindingOrdinaryData, ordinaryDataCount);
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
            SetRootTablePrepared(rootParameter, heap, index, slot);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetRootTablePrepared(
            uint rootParameter,
            ParameterHeap heap,
            uint index,
            int slot)
        {
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
            SetRootConstantBufferPrepared(rootParameter, address, slot);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void SetRootConstantBufferPrepared(uint rootParameter, ulong address)
        {
            if (address == 0)
                throw new ArgumentOutOfRangeException(nameof(address));
            int slot = checked((int)rootParameter);
            SetRootConstantBufferPrepared(rootParameter, address, slot);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void SetRootConstantBufferPrepared(
            uint rootParameter,
            ulong address,
            int slot)
        {
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
        internal void SetPersistentRootConstantBufferPrepared(
            uint rootParameter,
            ulong address)
        {
            int slot = checked((int)rootParameter);
            if (_rootConstantBufferSet[slot] &&
                _rootConstantBuffers[slot] == address)
            {
                return;
            }
            D3D12CommandListFastCalls.SetRootConstantBufferView(
                List,
                _computeRootBindings,
                rootParameter,
                address);
            _rootConstantBuffers[slot] = address;
            _rootConstantBufferSet[slot] = true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetRootConstants(
            uint rootParameter,
            uint constantCount,
            ReadOnlySpan<byte> data)
        {
            if (constantCount == 0 || data.IsEmpty ||
                data.Length > checked((int)(constantCount * sizeof(uint))))
            {
                throw new ArgumentException(
                    "Root-constant data must fit the non-empty reflected DWORD range.",
                    nameof(data));
            }

            int slot = EnsureRootStateCapacity(rootParameter);
            int nativeSize = checked((int)(constantCount * sizeof(uint)));
            byte[] values = _rootConstants[slot] is byte[] existing &&
                existing.Length == nativeSize
                    ? existing
                    : new byte[nativeSize];
            bool equal = _rootConstantsSet[slot] &&
                data.SequenceEqual(values.AsSpan(0, data.Length)) &&
                values.AsSpan(data.Length).IndexOfAnyExcept((byte)0) < 0;
            if (equal)
                return;

            data.CopyTo(values);
            values.AsSpan(data.Length).Clear();
            fixed (byte* pointer = values)
            {
                D3D12CommandListFastCalls.SetRoot32BitConstants(
                    List,
                    _computeRootBindings,
                    rootParameter,
                    constantCount,
                    pointer);
            }
            _rootConstants[slot] = values;
            _rootConstantsSet[slot] = true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private int EnsureRootStateCapacity(uint rootParameter)
        {
            int required = checked((int)rootParameter + 1);
            if (required > 64)
                throw new ArgumentOutOfRangeException(nameof(rootParameter));
            if (_rootStateLength < required)
                _rootStateLength = required;
            return checked((int)rootParameter);
        }

        internal void ApplyPersistentBlock(
            NativeParameterBinding layout,
            uint resourceBase,
            uint samplerBase,
            D3D12PersistentParameterData data)
        {
            if (layout.ResourceTable is D3D12BoundedTable resource)
            {
                SetRootTablePrepared(
                    resource.RootParameterIndex,
                    ParameterHeap.Resource,
                    resourceBase,
                    checked((int)resource.RootParameterIndex));
            }
            if (layout.SamplerTable is D3D12BoundedTable sampler)
            {
                SetRootTablePrepared(
                    sampler.RootParameterIndex,
                    ParameterHeap.Sampler,
                    samplerBase,
                    checked((int)sampler.RootParameterIndex));
            }
            if (layout.OrdinaryRoot is OrdinaryRootBinding ordinary)
            {
                if (ordinary.UsesRootConstants)
                {
                    SetRootConstants(
                        ordinary.RootParameterIndex,
                        ordinary.ConstantCount,
                        data.OrdinaryData);
                }
                else
                {
                    SetRootConstantBufferPrepared(
                        ordinary.RootParameterIndex,
                        data.OrdinaryAddress);
                }
            }
        }

        internal void ApplyTransientBlock(
            NativeParameterBinding layout,
            uint resourceBase,
            uint samplerBase,
            ReadOnlySpan<byte> ordinaryData,
            ulong ordinaryAddress)
        {
            if (layout.ResourceTable is D3D12BoundedTable resource)
            {
                SetRootTablePrepared(
                    resource.RootParameterIndex,
                    ParameterHeap.Resource,
                    resourceBase,
                    checked((int)resource.RootParameterIndex));
            }
            if (layout.SamplerTable is D3D12BoundedTable sampler)
            {
                SetRootTablePrepared(
                    sampler.RootParameterIndex,
                    ParameterHeap.Sampler,
                    samplerBase,
                    checked((int)sampler.RootParameterIndex));
            }
            if (layout.OrdinaryRoot is OrdinaryRootBinding ordinary)
            {
                if (ordinary.UsesRootConstants)
                {
                    SetRootConstants(
                        ordinary.RootParameterIndex,
                        ordinary.ConstantCount,
                        ordinaryData);
                }
                else
                {
                    SetRootConstantBuffer(ordinary.RootParameterIndex, ordinaryAddress);
                }
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void ApplyTransientOrdinaryData(
            NativeParameterBinding layout,
            ReadOnlySpan<byte> ordinaryData)
        {
            if (layout.OrdinaryRoot is not OrdinaryRootBinding ordinary)
                return;
            if (ordinary.UsesRootConstants)
            {
                SetRootConstants(
                    ordinary.RootParameterIndex,
                    ordinary.ConstantCount,
                    ordinaryData);
                return;
            }

            ulong ordinaryAddress;
            if (ordinaryData.Length == 16)
            {
                ref byte source = ref MemoryMarshal.GetReference(ordinaryData);
                ordinaryAddress = Recording.WriteOrdinaryData16(ref source);
            }
            else
            {
                ordinaryAddress = WriteTransientOrdinaryDataSlow(ordinaryData);
            }
            SetTransientRootConstantBuffer(ordinary.RootParameterIndex, ordinaryAddress);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetTransientOrdinaryConstantBuffer16(
            VariableLayoutReflection layout,
            uint rootParameter,
            ref byte data)
        {
            bool sameTransientShape =
                _hasTransientBindings &&
                _transientBindingLayout == layout &&
                _transientBindingResourceCount == 0 &&
                _transientBindingOrdinaryDataCount == 16;
            if (sameTransientShape)
            {
                ref byte current = ref MemoryMarshal.GetArrayDataReference(
                    _transientBindingOrdinaryData);
                if (Unsafe.ReadUnaligned<ulong>(ref data) ==
                        Unsafe.ReadUnaligned<ulong>(ref current) &&
                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref data, 8)) ==
                        Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref current, 8)))
                {
                    return;
                }
            }
            else if (PrepareTransientOrdinaryConstantBuffer16Slow(layout, ref data))
            {
                return;
            }

            PrepareOrdinaryData(16);
            PrepareBindingStorage(0, 16);
            ulong address = Recording.WriteOrdinaryData16(ref data);
            SetTransientRootConstantBuffer(rootParameter, address);

            ref byte destination = ref MemoryMarshal.GetArrayDataReference(
                _transientBindingOrdinaryData);
            Unsafe.WriteUnaligned(
                ref destination,
                Unsafe.ReadUnaligned<ulong>(ref data));
            Unsafe.WriteUnaligned(
                ref Unsafe.Add(ref destination, 8),
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref data, 8)));
            if (!sameTransientShape)
            {
                _transientBindingLayout = layout;
                _transientBindingResourceCount = 0;
                _transientBindingOrdinaryDataCount = 16;
                _hasTransientBindings = true;
                _persistentBindings = null;
                _persistentData = null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool PrepareTransientOrdinaryConstantBuffer16Slow(
            VariableLayoutReflection layout,
            ref byte data)
        {
            if (!_hasTransientBindings &&
                _persistentBindings is D3D12PersistentParameterBindings persistent &&
                _persistentData is D3D12PersistentParameterData persistentData &&
                persistent.Layout == layout &&
                persistentData.ContentEquals(
                    ReadOnlySpan<ResourceBinding>.Empty,
                    MemoryMarshal.CreateReadOnlySpan(ref data, 16)))
            {
                return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ulong WriteTransientOrdinaryDataSlow(ReadOnlySpan<byte> ordinaryData) =>
            Recording.WriteOrdinaryData(ordinaryData);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void SetTransientRootConstantBuffer(uint rootParameter, ulong address)
        {
            int slot = checked((int)rootParameter);
            if ((uint)slot >= (uint)_rootConstantBuffers.Length)
            {
                SetTransientRootConstantBufferSlow(rootParameter, address);
                return;
            }
            D3D12CommandListFastCalls.SetRootConstantBufferView(
                List,
                _computeRootBindings,
                rootParameter,
                address);
            _rootConstantBuffers[slot] = address;
            _rootConstantBufferSet[slot] = true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SetTransientRootConstantBufferSlow(uint rootParameter, ulong address) =>
            SetRootConstantBuffer(rootParameter, address);

        internal (uint ResourceBase, uint SamplerBase) AllocateTransientDescriptorPair(
            uint resourceCount,
            uint samplerCount) =>
            Recording.AllocateDescriptorPair(resourceCount, samplerCount);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal D3D12OrdinaryDataReservation ReserveTransientOrdinaryData(ulong size) =>
            Recording.ReserveOrdinaryData(size);

        internal void CopyTransientDescriptors(
            uint resourceBase, uint samplerBase,
            ReadOnlySpan<ResourceBinding> resources, ReadOnlySpan<DescriptorSlotDesc> slots) =>
            Recording.CopyTransientDescriptors(resourceBase, samplerBase, resources, slots);

        internal void CopyPersistentDescriptors(
            uint resourceBase, uint samplerBase, ReadOnlySpan<DescriptorRecord> records) =>
            Recording.CopyPersistentDescriptors(resourceBase, samplerBase, records);


        internal void Capture(in ResourceBinding binding)
        {
            switch (binding.Value)
            {
                case Buffer value:
                    Capture(RequireD3D12.Buffer(value));
                    break;
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
            D3D12CommandListFastCalls.SetRootDescriptorTable(
                List,
                Pipeline.Type is PipelineType.Compute or
                    PipelineType.RayTracing or PipelineType.WorkGraph,
                rootParameter,
                handle);
        }
    }

    // The contents of a descriptor position survive command allocator resets. The
    // recording object cannot be claimed again until its submitted GPU work retires.
    private readonly record struct DescriptorContent(DescriptorLease? Source, DescriptorSlotDesc Slot);

    private sealed partial class D3D12CommandSlot
    {
        private DescriptorContent[] _descriptorWrites = [];
        private DescriptorContent[] _resourceContents = [];
        private DescriptorContent[] _samplerContents = [];
        private readonly HashSet<D3D12PersistentParameterData> _capturedParameterData =
            new(ReferenceEqualityComparer.Instance);
        private readonly List<D3D12OrdinaryDataChunk> _ordinaryDataChunks = [];
        private int _ordinaryDataCursor;
        private D3D12OrdinaryDataChunk? _ordinaryDataCurrent;
        private CpuDescriptorHandle _resourceCpuStart;
        private CpuDescriptorHandle _samplerCpuStart;
        private GpuDescriptorHandle _resourceGpuStart;
        private GpuDescriptorHandle _samplerGpuStart;
        private ID3D12DescriptorHeap* _resourceArena;
        private ID3D12DescriptorHeap* _samplerArena;
        private bool _usesPrivateDescriptorArena;
        private uint _initialResourceDescriptorCapacity;
        private uint _initialSamplerDescriptorCapacity;
        private uint _resourceCapacity;
        private uint _samplerCapacity;
        private uint _resourceUsed;
        private uint _samplerUsed;
        private ulong _descriptorArenaVersion;
        private bool _descriptorArenaReady;
        private bool _descriptorHeapsBound;
        private bool _resourceArenaContainsGeneration;
        private bool _samplerArenaContainsGeneration;

        internal ulong DescriptorArenaVersion => _descriptorArenaVersion;
        internal uint ResourceDescriptorCapacity => _resourceCapacity;
        internal uint SamplerDescriptorCapacity => _samplerCapacity;

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ulong WriteOrdinaryData16(ref byte data)
        {
            D3D12OrdinaryDataChunk? current = _ordinaryDataCurrent;
            if (current is not null && current.TryWrite16(ref data, out ulong address))
                return address;
            return WriteOrdinaryData16Slow(ref data);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ulong WriteOrdinaryData16Slow(ref byte data)
        {
            ReadOnlySpan<byte> source = MemoryMarshal.CreateReadOnlySpan(ref data, 16);
            return WriteOrdinaryDataSlow(source, 256);
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
                _context.NativeNodeMask,
                capacity);
            _ordinaryDataChunks.Add(created);
            if (!created.TryReserve(alignedSize, out ulong createdOffset))
                throw new InvalidOperationException("A new ordinary-data chunk is too small.");
            _ordinaryDataCurrent = created;
            return new D3D12OrdinaryDataReservation(created, createdOffset, alignedSize);
        }

        internal void PrepareOrdinaryDataCapacity(ulong size)
        {
            if (size == 0)
                return;
            ulong alignedSize = checked(((size + 255UL) / 256UL) * 256UL);
            D3D12OrdinaryDataChunk? current = _ordinaryDataCurrent;
            if (current is not null && current.TryReserve(alignedSize, out _))
                return;

            int cursor = _ordinaryDataCursor;
            while (cursor < _ordinaryDataChunks.Count)
            {
                D3D12OrdinaryDataChunk candidate = _ordinaryDataChunks[cursor];
                if (candidate.TryReserve(alignedSize, out _))
                {
                    _ordinaryDataCursor = cursor;
                    _ordinaryDataCurrent = candidate;
                    return;
                }
                cursor++;
            }

            _ordinaryDataChunks.EnsureCapacity(checked(_ordinaryDataChunks.Count + 1));
            D3D12OrdinaryDataChunk created = D3D12OrdinaryDataChunk.Create(
                _context.NativeDevice,
                _context.NativeNodeMask,
                Math.Max(64UL * 1024UL, alignedSize));
            _ordinaryDataChunks.Add(created);
            _ordinaryDataCursor = _ordinaryDataChunks.Count - 1;
            _ordinaryDataCurrent = created;
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
                    _context.NativeNodeMask,
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

        internal void ResetDescriptorArenaState(in CommandRecordingDesc description)
        {
            ValidateCapacity(
                ParameterHeap.Resource,
                Math.Max(1u, description.InitialResourceDescriptorCapacity));
            ValidateCapacity(
                ParameterHeap.Sampler,
                Math.Max(1u, description.InitialSamplerDescriptorCapacity));
            _initialResourceDescriptorCapacity =
                description.InitialResourceDescriptorCapacity;
            _initialSamplerDescriptorCapacity =
                description.InitialSamplerDescriptorCapacity;
            _resourceUsed = 0;
            _samplerUsed = 0;
            _descriptorArenaVersion = checked(_descriptorArenaVersion + 1);
            _descriptorArenaReady = false;
            _descriptorHeapsBound = false;
            // Retain the published-table prefixes observed by these heaps. The first
            // local binding reserves them again from the current descriptor generation.
        }

        internal void PrepareDescriptorTables(ReadOnlySpan<DefaultRootTable> tables)
        {
            GetDescriptorTableHeaps(
                tables,
                out bool needsResources,
                out bool needsSamplers);
            if (!needsResources && !needsSamplers)
                return;

            DescriptorGeneration descriptors = Descriptors;
            if (!_descriptorArenaReady)
            {
                _resourceArenaContainsGeneration |= needsResources;
                _samplerArenaContainsGeneration |= needsSamplers;
                return;
            }

            bool replaceResources =
                needsResources && !_resourceArenaContainsGeneration;
            bool replaceSamplers =
                needsSamplers && !_samplerArenaContainsGeneration;
            if (!replaceResources && !replaceSamplers)
                return;

            ReplaceDescriptorArenaHeaps(
                descriptors,
                replaceResources,
                replaceSamplers);
        }

        private static void GetDescriptorTableHeaps(
            ReadOnlySpan<DefaultRootTable> tables,
            out bool resources,
            out bool samplers)
        {
            resources = false;
            samplers = false;
            foreach (ref readonly DefaultRootTable table in tables)
            {
                if (table.Heap == ParameterHeap.Resource)
                    resources = true;
                else
                    samplers = true;
            }
        }

        private void ReplaceDescriptorArenaHeaps(
            DescriptorGeneration descriptors,
            bool replaceResources,
            bool replaceSamplers)
        {
            uint resourcePrefix = replaceResources
                ? descriptors.ResourceCount
                : _resourceUsed;
            uint samplerPrefix = replaceSamplers
                ? descriptors.SamplerCount
                : _samplerUsed;
            uint resourceRequired = replaceResources
                ? Math.Max(
                    1u,
                    checked(
                        resourcePrefix +
                        _initialResourceDescriptorCapacity))
                : _resourceCapacity;
            uint samplerRequired = replaceSamplers
                ? Math.Max(
                    1u,
                    checked(
                        samplerPrefix +
                        _initialSamplerDescriptorCapacity))
                : _samplerCapacity;
            uint resourceCapacity = replaceResources
                ? Math.Max(_resourceCapacity, resourceRequired)
                : _resourceCapacity;
            uint samplerCapacity = replaceSamplers
                ? Math.Max(_samplerCapacity, samplerRequired)
                : _samplerCapacity;
            ValidateCapacity(ParameterHeap.Resource, resourceCapacity);
            ValidateCapacity(ParameterHeap.Sampler, samplerCapacity);
            int replacedHeapCount =
                (replaceResources && _resourceArena is not null ? 1 : 0) +
                (replaceSamplers && _samplerArena is not null ? 1 : 0);
            PrepareTransientObjects(replacedHeapCount);
            ulong nextVersion = checked(_descriptorArenaVersion + 1);

            ID3D12DescriptorHeap* resourceReplacement = null;
            ID3D12DescriptorHeap* samplerReplacement = null;
            try
            {
                if (replaceResources)
                {
                    resourceReplacement = CreateShaderVisibleHeap(
                        ParameterHeap.Resource,
                        resourceCapacity);
                }
                if (replaceSamplers)
                {
                    samplerReplacement = CreateShaderVisibleHeap(
                        ParameterHeap.Sampler,
                        samplerCapacity);
                }
                if (replaceResources)
                {
                    descriptors.CopyResourceTo(
                        _context.NativeDevice,
                        resourceReplacement);
                }
                if (replaceSamplers)
                {
                    descriptors.CopySamplerTo(
                        _context.NativeDevice,
                        samplerReplacement);
                }

                if (replaceResources)
                {
                    ID3D12DescriptorHeap* previous = _resourceArena;
                    _resourceArena = resourceReplacement;
                    Array.Clear(_resourceContents);
                    _resourceCpuStart = ResourceTrace.GetCPUDescriptorHandleForHeapStart(_resourceArena);
                    _resourceGpuStart = ResourceTrace.GetGPUDescriptorHandleForHeapStart(_resourceArena);
                    _resourceCapacity = resourceCapacity;
                    _resourceUsed = resourcePrefix;
                    _resourceArenaContainsGeneration = true;
                    resourceReplacement = null;
                    if (previous is not null)
                        _transientObjects.Add((nint)previous);
                }
                if (replaceSamplers)
                {
                    ID3D12DescriptorHeap* previous = _samplerArena;
                    _samplerArena = samplerReplacement;
                    Array.Clear(_samplerContents);
                    _samplerCpuStart = ResourceTrace.GetCPUDescriptorHandleForHeapStart(_samplerArena);
                    _samplerGpuStart = ResourceTrace.GetGPUDescriptorHandleForHeapStart(_samplerArena);
                    _samplerCapacity = samplerCapacity;
                    _samplerUsed = samplerPrefix;
                    _samplerArenaContainsGeneration = true;
                    samplerReplacement = null;
                    if (previous is not null)
                        _transientObjects.Add((nint)previous);
                }
                _descriptorArenaVersion = nextVersion;
                _descriptorHeapsBound = false;
                _context.InvalidateParameterBindingState();
            }
            finally
            {
                if (samplerReplacement is not null)
                    _ = ResourceTrace.Release(samplerReplacement);
                if (resourceReplacement is not null)
                    _ = ResourceTrace.Release(resourceReplacement);
            }
        }

        private void EnsureDescriptorArenaReady(uint resourceCount, uint samplerCount)
        {
            if (_descriptorArenaReady)
                return;
            bool rebind = _descriptorHeapsBound;
            DescriptorGeneration? descriptors =
                _resourceArenaContainsGeneration ||
                _samplerArenaContainsGeneration
                    ? Descriptors
                    : null;
            uint resourcePrefix = _resourceArenaContainsGeneration
                ? descriptors!.ResourceCount
                : 0;
            uint samplerPrefix = _samplerArenaContainsGeneration
                ? descriptors!.SamplerCount
                : 0;
            DeviceLimits limits = _context.NativeDevice.Capabilities.Limits;
            uint resourceEstimate = Math.Min(_initialResourceDescriptorCapacity,
                limits.ResourceDescriptorCapacity - resourcePrefix);
            uint samplerEstimate = Math.Min(_initialSamplerDescriptorCapacity,
                limits.SamplerDescriptorCapacity - samplerPrefix);
            uint resourceRequired = checked(resourcePrefix + Math.Max(resourceEstimate, resourceCount));
            uint samplerRequired = checked(samplerPrefix + Math.Max(samplerEstimate, samplerCount));
            // Other recordings of this context already established the required
            // capacity. New in-flight storage starts at that size in one allocation.
            _context.GetDescriptorArenaCapacities(out uint resourceCapacity, out uint samplerCapacity);
            EnsureResetHeaps(
                Math.Max(resourceCapacity, GrowCapacity(ParameterHeap.Resource, 0, Math.Max(1u, resourceRequired))),
                Math.Max(samplerCapacity, GrowCapacity(ParameterHeap.Sampler, 0, Math.Max(1u, samplerRequired))));
            if (_resourceArenaContainsGeneration)
            {
                descriptors!.CopyResourceTo(_context.NativeDevice, _resourceArena);
                Array.Clear(_resourceContents, 0, Math.Min(_resourceContents.Length, checked((int)resourcePrefix)));
            }
            if (_samplerArenaContainsGeneration)
            {
                descriptors!.CopySamplerTo(_context.NativeDevice, _samplerArena);
                Array.Clear(_samplerContents, 0, Math.Min(_samplerContents.Length, checked((int)samplerPrefix)));
            }
            _resourceUsed = resourcePrefix;
            _samplerUsed = samplerPrefix;
            _descriptorArenaReady = true;
            if (rebind)
            {
                BindDescriptorHeaps();
                _context.ReapplyRootTables();
            }
        }

        internal (uint ResourceBase, uint SamplerBase) AllocateDescriptorPair(
            uint resourceCount,
            uint samplerCount)
        {
            if (resourceCount == 0 && samplerCount == 0)
                return (0, 0);
            uint resourceRequired = checked(_resourceUsed + resourceCount);
            uint samplerRequired = checked(_samplerUsed + samplerCount);
            if (!_descriptorArenaReady ||
                resourceRequired > _resourceCapacity ||
                samplerRequired > _samplerCapacity)
            {
                throw new InvalidOperationException(
                    "Descriptor capacity must be prepared before allocation.");
            }

            uint resourceBase = _resourceUsed;
            uint samplerBase = _samplerUsed;
            _resourceUsed = resourceRequired;
            _samplerUsed = samplerRequired;
            return (resourceBase, samplerBase);
        }

        internal void CopyTransientDescriptors(
            uint resourceBase,
            uint samplerBase,
            ReadOnlySpan<ResourceBinding> resources,
            ReadOnlySpan<DescriptorSlotDesc> slots)
        {
            if (_descriptorWrites.Length < resources.Length)
                Array.Resize(ref _descriptorWrites, resources.Length);
            try
            {
                for (int index = 0; index < resources.Length; index++)
                {
                    DescriptorLease? source = resources[index].IsNull ? null : GetBindingDescriptor(resources[index]);
                    _descriptorWrites[index] = new DescriptorContent(source, slots[index]);
                }
                WriteDescriptors(resourceBase, samplerBase, _descriptorWrites.AsSpan(0, resources.Length));
            }
            finally
            {
                Array.Clear(_descriptorWrites, 0, resources.Length);
            }
        }

        internal void CopyPersistentDescriptors(
            uint resourceBase, uint samplerBase, ReadOnlySpan<DescriptorRecord> records)
        {
            if (_descriptorWrites.Length < records.Length)
                Array.Resize(ref _descriptorWrites, records.Length);
            try
            {
                for (int index = 0; index < records.Length; index++)
                    _descriptorWrites[index] = new DescriptorContent(records[index].Source, records[index].Slot);
                WriteDescriptors(resourceBase, samplerBase, _descriptorWrites.AsSpan(0, records.Length));
            }
            finally
            {
                Array.Clear(_descriptorWrites, 0, records.Length);
            }
        }

        private void WriteDescriptors(uint resourceBase, uint samplerBase, ReadOnlySpan<DescriptorContent> writes)
        {
            if (writes.IsEmpty) return;
            if (_resourceContents.Length < _resourceCapacity)
                Array.Resize(ref _resourceContents, checked((int)_resourceCapacity));
            if (_samplerContents.Length < _samplerCapacity)
                Array.Resize(ref _samplerContents, checked((int)_samplerCapacity));
            CpuDescriptorHandle[]? rented = null;
            try
            {
                int length = writes.Length;
                Span<CpuDescriptorHandle> handles = length <= 128
                    ? stackalloc CpuDescriptorHandle[length * 4]
                    : (rented = ArrayPool<CpuDescriptorHandle>.Shared.Rent(checked(length * 4)));
                Span<CpuDescriptorHandle> resourceSources = handles.Slice(0, length);
                Span<CpuDescriptorHandle> resourceDestinations = handles.Slice(length, length);
                Span<CpuDescriptorHandle> samplerSources = handles.Slice(length * 2, length);
                Span<CpuDescriptorHandle> samplerDestinations = handles.Slice(length * 3, length);
                int resourceCopies = 0, samplerCopies = 0;
                uint resourceIndex = resourceBase, samplerIndex = samplerBase;
                foreach (ref readonly DescriptorContent next in writes)
                {
                    bool sampler = next.Slot.Type == ResourceBindingType.Sampler;
                    uint index = sampler ? samplerIndex++ : resourceIndex++;
                    DescriptorContent[] contents = sampler ? _samplerContents : _resourceContents;
                    if (contents[index] == next) continue;
                    DescriptorHeapType type = sampler ? DescriptorHeapType.Sampler : DescriptorHeapType.CbvSrvUav;
                    CpuDescriptorHandle start = sampler ? _samplerCpuStart : _resourceCpuStart;
                    CpuDescriptorHandle destination = new(start.Ptr + checked((nuint)(
                        index * _context.NativeDevice.DescriptorIncrement(type))));
                    CpuDescriptorHandle source = next.Source?.Cpu ?? GetNullDescriptor(next.Slot);
                    if (sampler)
                    {
                        samplerSources[samplerCopies] = source;
                        samplerDestinations[samplerCopies++] = destination;
                    }
                    else
                    {
                        resourceSources[resourceCopies] = source;
                        resourceDestinations[resourceCopies++] = destination;
                    }
                }
                CopyDescriptorRanges(resourceSources[..resourceCopies], resourceDestinations[..resourceCopies], DescriptorHeapType.CbvSrvUav);
                CopyDescriptorRanges(samplerSources[..samplerCopies], samplerDestinations[..samplerCopies], DescriptorHeapType.Sampler);
                resourceIndex = resourceBase;
                samplerIndex = samplerBase;
                foreach (ref readonly DescriptorContent next in writes)
                {
                    if (next.Slot.Type == ResourceBindingType.Sampler)
                        _samplerContents[samplerIndex++] = next;
                    else
                        _resourceContents[resourceIndex++] = next;
                }
            }
            finally
            {
                if (rented is not null) ArrayPool<CpuDescriptorHandle>.Shared.Return(rented);
            }
        }

        private readonly Dictionary<DescriptorSlotDesc, DescriptorLease> _nullDescriptors = [];

        private CpuDescriptorHandle GetNullDescriptor(in DescriptorSlotDesc slot)
        {
            if (_nullDescriptors.TryGetValue(slot, out DescriptorLease? descriptor)) return descriptor.Cpu;
            uint node = checked((uint)System.Numerics.BitOperations.TrailingZeroCount(_context.NativeNodeMask));
            descriptor = (slot.Type == ResourceBindingType.Sampler
                ? _context.NativeDevice.GetSamplerDescriptors(node)
                : _context.NativeDevice.GetResourceDescriptors(node)).Allocate();
            try
            {
                WriteTypedNullDescriptor(_context.NativeDevice, slot, descriptor.Cpu, allowDummySampler: true);
                _nullDescriptors.Add(slot, descriptor);
                return descriptor.Cpu;
            }
            catch
            {
                descriptor.Release();
                throw;
            }
        }

        private void CopyDescriptorRanges(
            ReadOnlySpan<CpuDescriptorHandle> sources,
            ReadOnlySpan<CpuDescriptorHandle> destinations,
            DescriptorHeapType type)
        {
            if (sources.IsEmpty) return;
            if (sources.Length == 1)
            {
                DeviceQueryTrace.CopyDescriptorsSimple(_context.NativeDevice.Native, 1, destinations[0], sources[0], type);
                return;
            }
            fixed (CpuDescriptorHandle* source = sources)
            fixed (CpuDescriptorHandle* destination = destinations)
                DeviceQueryTrace.CopyDescriptors(_context.NativeDevice.Native, checked((uint)sources.Length),
                    destination, source, type);
        }

        internal GpuDescriptorHandle GetGpuHandle(ParameterHeap heap, uint index)
        {
            EnsureDescriptorHeapsBound();
            DescriptorHeapType nativeType = heap == ParameterHeap.Resource
                ? DescriptorHeapType.CbvSrvUav
                : DescriptorHeapType.Sampler;
            GpuDescriptorHandle start;
            if (_descriptorArenaReady)
            {
                start = heap == ParameterHeap.Resource ? _resourceGpuStart : _samplerGpuStart;
            }
            else
            {
                DescriptorGeneration descriptors = Descriptors;
                start = heap == ParameterHeap.Resource
                    ? descriptors.ResourceGpuStart
                    : descriptors.SamplerGpuStart;
            }
            uint increment = _context.NativeDevice.DescriptorIncrement(nativeType);
            return new GpuDescriptorHandle(
                start.Ptr + checked((ulong)index * increment));
        }

        internal void ReleaseBindingTransients()
        {
            foreach (D3D12PersistentParameterData value in _capturedParameterData)
                value.Release();
            _capturedParameterData.Clear();
        }

        internal void ReleaseDescriptorArena()
        {
            foreach (DescriptorLease descriptor in _nullDescriptors.Values) descriptor.Release();
            _nullDescriptors.Clear();
            foreach (D3D12OrdinaryDataChunk chunk in _ordinaryDataChunks)
                chunk.Release();
            _ordinaryDataChunks.Clear();
            _ordinaryDataCursor = 0;
            _ordinaryDataCurrent = null;
            ID3D12DescriptorHeap* sampler = _samplerArena;
            _samplerArena = null;
            _samplerContents = [];
            _samplerCpuStart = default;
            _samplerGpuStart = default;
            if (sampler is not null)
                _ = ResourceTrace.Release(sampler);
            ID3D12DescriptorHeap* resource = _resourceArena;
            _resourceArena = null;
            _resourceContents = [];
            _resourceCpuStart = default;
            _resourceGpuStart = default;
            if (resource is not null)
                _ = ResourceTrace.Release(resource);
            _resourceCapacity = 0;
            _samplerCapacity = 0;
            _resourceUsed = 0;
            _samplerUsed = 0;
            _initialResourceDescriptorCapacity = 0;
            _initialSamplerDescriptorCapacity = 0;
            _descriptorArenaReady = false;
            _descriptorHeapsBound = false;
            _resourceArenaContainsGeneration = false;
            _samplerArenaContainsGeneration = false;
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
                    Array.Clear(_resourceContents);
                    _resourceCpuStart = ResourceTrace.GetCPUDescriptorHandleForHeapStart(_resourceArena);
                    _resourceGpuStart = ResourceTrace.GetGPUDescriptorHandleForHeapStart(_resourceArena);
                    _resourceCapacity = resourceRequired;
                    resourceReplacement = null;
                    if (previous is not null)
                        _ = ResourceTrace.Release(previous);
                }
                if (replaceSampler)
                {
                    ID3D12DescriptorHeap* previous = _samplerArena;
                    _samplerArena = samplerReplacement;
                    Array.Clear(_samplerContents);
                    _samplerCpuStart = ResourceTrace.GetCPUDescriptorHandleForHeapStart(_samplerArena);
                    _samplerGpuStart = ResourceTrace.GetGPUDescriptorHandleForHeapStart(_samplerArena);
                    _samplerCapacity = samplerRequired;
                    samplerReplacement = null;
                    if (previous is not null)
                        _ = ResourceTrace.Release(previous);
                }
            }
            finally
            {
                if (samplerReplacement is not null)
                    _ = ResourceTrace.Release(samplerReplacement);
                if (resourceReplacement is not null)
                    _ = ResourceTrace.Release(resourceReplacement);
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
            int retainedHeapCount =
                (replaceResource && _resourceArena is not null ? 1 : 0) +
                (replaceSampler && _samplerArena is not null ? 1 : 0);
            PrepareTransientObjects(retainedHeapCount);

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
                    resourceReplacement,
                    _resourceUsed);
                CopyRecordingDescriptors(
                    ParameterHeap.Sampler,
                    samplerReplacement,
                    _samplerUsed);

                if (replaceResource)
                {
                    ID3D12DescriptorHeap* previous = _resourceArena;
                    _resourceArena = resourceReplacement;
                    int resourceCopied = Math.Min(_resourceContents.Length, checked((int)_resourceUsed));
                    Array.Clear(_resourceContents, resourceCopied, _resourceContents.Length - resourceCopied);
                    _resourceCpuStart = ResourceTrace.GetCPUDescriptorHandleForHeapStart(_resourceArena);
                    _resourceGpuStart = ResourceTrace.GetGPUDescriptorHandleForHeapStart(_resourceArena);
                    _resourceCapacity = resourceCapacity;
                    resourceReplacement = null;
                    if (previous is not null)
                        _transientObjects.Add((nint)previous);
                }
                if (replaceSampler)
                {
                    ID3D12DescriptorHeap* previous = _samplerArena;
                    _samplerArena = samplerReplacement;
                    int samplerCopied = Math.Min(_samplerContents.Length, checked((int)_samplerUsed));
                    Array.Clear(_samplerContents, samplerCopied, _samplerContents.Length - samplerCopied);
                    _samplerCpuStart = ResourceTrace.GetCPUDescriptorHandleForHeapStart(_samplerArena);
                    _samplerGpuStart = ResourceTrace.GetGPUDescriptorHandleForHeapStart(_samplerArena);
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
                    _ = ResourceTrace.Release(samplerReplacement);
                if (resourceReplacement is not null)
                    _ = ResourceTrace.Release(resourceReplacement);
            }
        }

        private uint GrowCapacity(ParameterHeap heap, uint current, uint required)
        {
            uint maximum = MaximumCapacity(heap);
            uint capacity = Math.Max(required, checked(current * 2));
            return Math.Min(maximum, BitOperations.RoundUpToPowerOf2(capacity));
        }

        private void CopyRecordingDescriptors(
            ParameterHeap heap,
            ID3D12DescriptorHeap* destination,
            uint count)
        {
            if (destination is null || count == 0) return;
            bool sampler = heap == ParameterHeap.Sampler;
            uint prefix = 0;
            if (sampler ? _samplerArenaContainsGeneration : _resourceArenaContainsGeneration)
            {
                DescriptorGeneration descriptors = Descriptors;
                if (sampler) descriptors.CopySamplerTo(_context.NativeDevice, destination);
                else descriptors.CopyResourceTo(_context.NativeDevice, destination);
                prefix = sampler ? descriptors.SamplerCount : descriptors.ResourceCount;
            }
            DescriptorContent[] contents = sampler ? _samplerContents : _resourceContents;
            DescriptorHeapType type = sampler ? DescriptorHeapType.Sampler : DescriptorHeapType.CbvSrvUav;
            CpuDescriptorHandle start = ResourceTrace.GetCPUDescriptorHandleForHeapStart(destination);
            uint increment = _context.NativeDevice.DescriptorIncrement(type);
            for (uint index = prefix; index < count; index++)
            {
                if (index >= contents.Length || contents[index].Slot.Type == ResourceBindingType.None)
                    continue;
                DescriptorContent content = contents[index];
                CpuDescriptorHandle target = new(start.Ptr + checked((nuint)(index * increment)));
                if (content.Source is { } source)
                    DeviceQueryTrace.CopyDescriptorsSimple(_context.NativeDevice.Native, 1, target, source.Cpu, type);
                else
                    WriteTypedNullDescriptor(_context.NativeDevice, content.Slot, target, allowDummySampler: true);
            }
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
                _context.NativeNodeMask);
            ID3D12DescriptorHeap* result = null;
            Guid iid = ID3D12DescriptorHeap.Guid;
            ThrowIfFailed(
                _context.NativeDevice,
                    DeviceResourceTrace.CreateDescriptorHeap(
                _context.NativeDevice.Native, &description,
                    &iid,
                    (void**)&result),
                NativeOperationType.Ordinary,
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
            DescriptorGeneration? descriptors = _descriptorArenaReady
                ? null
                : Descriptors;
            ID3D12DescriptorHeap** heaps = stackalloc ID3D12DescriptorHeap*[2]
            {
                _descriptorArenaReady ? _resourceArena : descriptors!.ResourceHeap,
                _descriptorArenaReady ? _samplerArena : descriptors!.SamplerHeap,
            };
            D3D12CommandListFastCalls.SetDescriptorHeaps(List, 2, heaps);
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

    private static DescriptorLease GetBindingDescriptor(in ResourceBinding binding) =>
        binding.Value is D3D12Buffer buffer
            ? buffer.GetConstantBufferDescriptor(binding.BufferRange)
            : ((INativeDescriptor)binding.Value!).NativeDescriptor;

}
