using System.Runtime.InteropServices;
using SlangShaderSharp;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    public DescriptorTable CreateDescriptorTable(
        Device device,
        ReadOnlySpan<DescriptorSlotDesc> slots,
        string? label = null,
        uint nodeIndex = uint.MaxValue,
        CancellationToken cancellationToken = default)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        cancellationToken.ThrowIfCancellationRequested();
        if (slots.IsEmpty)
            throw new ArgumentException("A DescriptorTable requires at least one typed slot.", nameof(slots));

        uint resolvedNodeIndex = nativeDevice.ResolveNodeIndex(nodeIndex, nameof(nodeIndex));
        DescriptorTableType type = slots[0].Type == ResourceBindingType.Sampler
            ? DescriptorTableType.Sampler
            : DescriptorTableType.Resource;
        DescriptorPublisher publisher = nativeDevice.GetDescriptorPublisher(resolvedNodeIndex);
        uint count = checked((uint)slots.Length);
        DescriptorRange range = publisher.Reserve(type, count);
        D3D12DescriptorTable? result = null;
        try
        {
            result = new D3D12DescriptorTable(
                nativeDevice,
                publisher,
                type,
                resolvedNodeIndex,
                range,
                slots,
                label);
            publisher.InitializeTable(result);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            if (result is null)
                publisher.Cancel(range);
            else
                result.Dispose();
            throw;
        }
    }

    public DescriptorIndex GetDescriptorIndex(DescriptorTable table, uint slot)
    {
        D3D12DescriptorTable native = RequireDescriptorTable(table);
        native.CheckSlot(slot);
        return new DescriptorIndex(
            native,
            checked(native.FirstIndex + slot));
    }

    public void WriteDescriptor(
        DescriptorTable table,
        uint slot,
        in ResourceBinding value)
    {
        D3D12DescriptorTable native = RequireDescriptorTable(table);
        native.CheckSlot(slot);
        DescriptorSlotDesc slotDesc = native.GetSlotDesc(slot);
        EnsureTableBindingType(slotDesc.Type, value);
        native.Publisher.StageBinding(
            native.Type,
            checked(native.FirstIndex + slot),
            slotDesc,
            value);
    }

    public PersistentParameterBindings CreatePersistentParameterBindings(
        Device device,
        Pipeline pipeline,
        in ParameterBlockBindings bindings,
        string? label = null)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        ObjectDisposedException.ThrowIf(pipeline.IsDisposed, pipeline);
        if (!ReferenceEquals(device, pipeline.Device))
            throw new ArgumentException("Pipeline belongs to a different Device.", nameof(pipeline));
        D3D12Pipeline nativePipeline = RequirePipeline(pipeline);
        NativeParameterBinding nativeLayout = nativePipeline.RootSignature.GetBlock(bindings.Layout);
        D3D12PersistentParameterBindings result = new(
            nativeDevice,
            nativePipeline,
            nativeLayout,
            bindings.Layout,
            label);
        try
        {
            result.StageReplacement(bindings);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            result.Release(fromParent: true);
            throw;
        }
    }

    public void UpdatePersistentParameterBindings(
        PersistentParameterBindings destination,
        in ParameterBlockBindings bindings)
    {
        D3D12PersistentParameterBindings native =
            RequirePersistentParameterBindings(destination);
        native.StageReplacement(bindings);
    }

    public void PublishDescriptors(
        Device device,
        uint nodeIndex = uint.MaxValue,
        CancellationToken cancellationToken = default)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        uint resolvedNodeIndex = nativeDevice.ResolveNodeIndex(nodeIndex, nameof(nodeIndex));
        DescriptorGeneration generation = nativeDevice
            .GetDescriptorPublisher(resolvedNodeIndex)
            .Publish(cancellationToken);
        try
        {
            // Publish returns one retained caller reference in addition to the publisher's current
            // generation ownership. Public callers only need the new current generation installed.
        }
        finally
        {
            generation.Release();
        }
    }

    private static void EnsureTableBindingType(
        ResourceBindingType slotType,
        in ResourceBinding binding)
    {
        if (!Enum.IsDefined(binding.Type))
            throw new ArgumentOutOfRangeException(nameof(binding));
        if (binding.Type != slotType)
        {
            throw new ArgumentException(
                $"Descriptor slot type {slotType} cannot be written as {binding.Type}.",
                nameof(binding));
        }
    }

    private enum DescriptorRangeState : byte
    {
        Reserved,
        Active,
        PendingRetirement,
        Retired,
        Free,
    }

    private sealed class DescriptorRange
    {
        internal DescriptorRange(
            DescriptorTableType type,
            uint first,
            uint count,
            ulong identity)
        {
            Type = type;
            First = first;
            Count = count;
            Identity = identity;
        }

        internal DescriptorTableType Type { get; }
        internal uint First { get; set; }
        internal uint Count { get; set; }
        internal ulong Identity { get; set; }
        internal DescriptorRangeState State { get; set; }
        internal bool HasPublishedContent { get; set; }
        internal bool ActivationQueued { get; set; }
        internal ulong ReusableAfterGeneration { get; set; }
        internal DescriptorRange? Next { get; set; }
        internal DescriptorRange? ActivationNext { get; set; }
        internal DescriptorSlotDesc[]? Slots { get; set; }

        internal void SetSlot(uint slot, in DescriptorSlotDesc value)
        {
            if (slot >= Count || value.Type == ResourceBindingType.None || !Enum.IsDefined(value.Type))
                throw new ArgumentOutOfRangeException(nameof(slot));
            Slots ??= new DescriptorSlotDesc[checked((int)Count)];
            DescriptorSlotDesc current = Slots[checked((int)slot)];
            if (current.Type != ResourceBindingType.None && current != value)
                throw new InvalidOperationException("A descriptor range slot changed its declared shape.");
            Slots[checked((int)slot)] = value;
        }

        internal DescriptorSlotDesc GetSlot(uint slot)
        {
            if (slot >= Count || Slots is null)
                throw new InvalidOperationException("The descriptor range has no declared slot shape.");
            DescriptorSlotDesc value = Slots[checked((int)slot)];
            if (value.Type == ResourceBindingType.None)
                throw new InvalidOperationException("The descriptor range contains an untyped slot.");
            return value;
        }
    }

    private sealed class DescriptorPublisher : IDisposable
    {
        private readonly D3D12Device _device;
        private readonly DescriptorTableType? _restrictedType;
        private readonly uint _nodeMask;
        private readonly object _gate = new();
        private readonly Dictionary<uint, DescriptorRecord> _pendingResources = [];
        private readonly Dictionary<uint, DescriptorRecord> _pendingSamplers = [];
        private readonly Dictionary<GraphicsObject, DescriptorRange> _owners =
            new(ReferenceEqualityComparer.Instance);
        private readonly SortedSet<ulong> _liveGenerations = [];
        private DescriptorRecord?[] _resources = [];
        private DescriptorRecord?[] _samplers = [];
        private DescriptorGeneration _current;
        private DescriptorRange? _freeResources;
        private DescriptorRange? _freeSamplers;
        private DescriptorRange? _pendingActivationHead;
        private DescriptorRange? _pendingActivationTail;
        private DescriptorRange? _pendingRetirementHead;
        private DescriptorRange? _pendingRetirementTail;
        private DescriptorRange? _retiredHead;
        private DescriptorRange? _retiredTail;
        private uint _nextResource;
        private uint _nextSampler;
        private ulong _nextAllocationIdentity = 1;
        private ulong _nextGeneration = 2;
        private bool _disposed;

        internal DescriptorPublisher(
            D3D12Device device,
            DescriptorTableType? restrictedType = null,
            uint initialCapacity = 0,
            uint nodeMask = 0)
        {
            _device = device;
            _restrictedType = restrictedType;
            _nodeMask = nodeMask == 0 ? device.PrimaryNodeMask : nodeMask;
            if (restrictedType is DescriptorTableType.Resource)
                _pendingResources.EnsureCapacity(checked((int)Math.Min(initialCapacity, int.MaxValue)));
            else if (restrictedType is DescriptorTableType.Sampler)
                _pendingSamplers.EnsureCapacity(checked((int)Math.Min(initialCapacity, int.MaxValue)));
            _current = DescriptorGeneration.Create(
                this,
                device,
                _nodeMask,
                1,
                1,
                1,
                [],
                []);
            try
            {
                _liveGenerations.Add(1);
            }
            catch
            {
                _current.Release();
                throw;
            }
        }

        internal DescriptorGeneration CaptureCurrent()
        {
            while (true)
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposed),
                    this);
                DescriptorGeneration? current = Volatile.Read(ref _current);
                if (current is null)
                    continue;
                if (current.TryRetain())
                    return current;
            }
        }

        internal DescriptorRange Reserve(DescriptorTableType type, uint count)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!Enum.IsDefined(type) || count == 0)
                    throw new ArgumentOutOfRangeException(nameof(count));
                if (_restrictedType is DescriptorTableType restricted && restricted != type)
                {
                    throw new ArgumentException(
                        "The descriptor allocation does not match the descriptor heap type.",
                        nameof(type));
                }

                ref DescriptorRange? free = ref type == DescriptorTableType.Resource
                    ? ref _freeResources
                    : ref _freeSamplers;
                DescriptorRange? previous = null;
                DescriptorRange? candidate = free;
                while (candidate is not null && candidate.Count < count)
                {
                    previous = candidate;
                    candidate = candidate.Next;
                }
                if (candidate is not null)
                {
                    DescriptorRange reused;
                    if (candidate.Count == count)
                    {
                        if (previous is null)
                            free = candidate.Next;
                        else
                            previous.Next = candidate.Next;
                        reused = candidate;
                    }
                    else
                    {
                        reused = new DescriptorRange(
                            type,
                            candidate.First,
                            count,
                            identity: 0);
                        candidate.First = checked(candidate.First + count);
                        candidate.Count -= count;
                    }
                    ResetReserved(reused, AllocateIdentity());
                    return reused;
                }

                ref uint next = ref type == DescriptorTableType.Resource
                    ? ref _nextResource
                    : ref _nextSampler;
                uint capacity = type == DescriptorTableType.Resource
                    ? _device.Capabilities.Limits.ResourceDescriptorCapacity
                    : _device.Capabilities.Limits.SamplerDescriptorCapacity;
                if (next > capacity || count > capacity - next)
                {
                    throw new GraphicsException(
                        GraphicsError.OutOfDescriptors,
                        $"The D3D12 {type} logical descriptor index space is exhausted.");
                }
                DescriptorRange result = new(
                    type,
                    next,
                    count,
                    AllocateIdentity());
                next = checked(next + count);
                return result;
            }
        }

        internal void Cancel(DescriptorRange range)
        {
            lock (_gate)
            {
                if (_disposed || range.State == DescriptorRangeState.Free)
                    return;
                if (range.State is not (DescriptorRangeState.Reserved or DescriptorRangeState.Active))
                    throw new InvalidOperationException("Only an unpublished descriptor reservation can be canceled.");
                RemovePendingActivation(range);
                RemovePendingRange(GetPending(range.Type), range.First, range.Count);
                Recycle(range);
            }
        }

        internal void StageDescriptor(
            DescriptorRange range,
            DescriptorLease source,
            GraphicsObject owner,
            ResourceBindingType type)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (range.Count != 1 || range.State != DescriptorRangeState.Reserved)
                    throw new InvalidOperationException("The bindless descriptor reservation is not active.");
                DescriptorRecord record = DescriptorRecord.Create(
                    source,
                    owner,
                    type,
                    GetDescriptorSlotDesc(owner, type));
                if ((record.VisibleNodeMask & _nodeMask) == 0)
                {
                    record.Release();
                    throw new ArgumentException(
                        "The descriptor resource is not visible from the table node.",
                        nameof(owner));
                }
                bool ownerAdded = false;
                bool recordTransferred = false;
                try
                {
                    _owners.EnsureCapacity(checked(_owners.Count + 1));
                    range.SetSlot(0, GetDescriptorSlotDesc(owner, type));
                    _owners.Add(owner, range);
                    ownerAdded = true;
                    ReplacePending(range.Type, range.First, record);
                    recordTransferred = true;
                    range.State = DescriptorRangeState.Active;
                    QueueActivation(range);
                }
                catch
                {
                    if (ownerAdded)
                        _owners.Remove(owner);
                    if (!ownerAdded && !recordTransferred)
                        record.Release();
                    throw;
                }
            }
        }

        internal void InitializeTable(D3D12DescriptorTable table)
        {
            lock (_gate)
            {
                DescriptorRange range = table.Range;
                if (range.State != DescriptorRangeState.Reserved)
                    throw new InvalidOperationException("The DescriptorTable reservation is not active.");
                Dictionary<uint, DescriptorRecord> pending = GetPending(range.Type);
                pending.EnsureCapacity(checked(pending.Count + checked((int)range.Count)));
                try
                {
                    for (uint slot = 0; slot < range.Count; slot++)
                    {
                        range.SetSlot(slot, table.GetSlotDesc(slot));
                        ReplacePending(
                            range.Type,
                            checked(range.First + slot),
                            DescriptorRecord.CreateNull(table.GetSlotDesc(slot)));
                    }
                    range.State = DescriptorRangeState.Active;
                    QueueActivation(range);
                }
                catch
                {
                    RemovePendingRange(pending, range.First, range.Count);
                    throw;
                }
            }
        }

        internal void StageBinding(
            DescriptorTableType type,
            uint index,
            in DescriptorSlotDesc slot,
            in ResourceBinding binding)
        {
            lock (_gate)
            {
                DescriptorRecord record = CreateBindingRecord(binding, slot);
                if ((record.VisibleNodeMask & _nodeMask) == 0)
                {
                    record.Release();
                    throw new ArgumentException(
                        "The descriptor resource is not visible from the table node.",
                        nameof(binding));
                }
                ReplacePending(type, index, record);
            }
        }

        internal void NotifyDisposed(GraphicsObject owner)
        {
            lock (_gate)
            {
                if (!_owners.Remove(owner, out DescriptorRange? range))
                    return;
                Retire(range);
            }
        }

        internal void DisposeTable(D3D12DescriptorTable table)
        {
            lock (_gate)
                Retire(table.Range);
        }

        internal DescriptorGeneration Publish(
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _device.ThrowIfUnavailable();
                cancellationToken.ThrowIfCancellationRequested();
                StageRetirementTombstones();
                if (_pendingResources.Count == 0 &&
                    _pendingSamplers.Count == 0)
                {
                    _current.Retain();
                    return _current;
                }
                if (_nextGeneration == ulong.MaxValue)
                {
                    throw new GraphicsException(
                        GraphicsError.OutOfDescriptors,
                        "The descriptor-generation identity domain is exhausted.");
                }

                DescriptorRecord?[] resources = GrowCopy(_resources, _nextResource);
                DescriptorRecord?[] samplers = GrowCopy(_samplers, _nextSampler);
                ApplyPending(resources, _pendingResources);
                ApplyPending(samplers, _pendingSamplers);
                cancellationToken.ThrowIfCancellationRequested();

                DescriptorGeneration candidate = DescriptorGeneration.Create(
                    this,
                    _device,
                    _nodeMask,
                    _nextGeneration,
                    Math.Max(1u, _nextResource),
                    Math.Max(1u, _nextSampler),
                    resources,
                    samplers);
                try
                {
                    _liveGenerations.Add(candidate.Identity);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch
                {
                    candidate.Release();
                    throw;
                }

                DescriptorGeneration previous = _current;
                CommitArray(ref _resources, resources, _pendingResources);
                CommitArray(ref _samplers, samplers, _pendingSamplers);
                Volatile.Write(ref _current, candidate);
                _nextGeneration++;
                MarkActivationsPublished();
                CommitRetirements();
                candidate.Retain();
                previous.Release();
                ReclaimRetiredRanges();
                return candidate;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                foreach (DescriptorRecord record in _pendingResources.Values)
                    record.Release();
                foreach (DescriptorRecord record in _pendingSamplers.Values)
                    record.Release();
                foreach (DescriptorRecord? record in _resources)
                    record?.Release();
                foreach (DescriptorRecord? record in _samplers)
                    record?.Release();
                _pendingResources.Clear();
                _pendingSamplers.Clear();
                _owners.Clear();
                _resources = [];
                _samplers = [];
                _current.Release();
                _liveGenerations.Clear();
                _freeResources = null;
                _freeSamplers = null;
                _pendingActivationHead = null;
                _pendingActivationTail = null;
                _pendingRetirementHead = null;
                _pendingRetirementTail = null;
                _retiredHead = null;
                _retiredTail = null;
            }
        }

        internal void OnGenerationReleased(ulong identity)
        {
            lock (_gate)
            {
                _liveGenerations.Remove(identity);
                if (!_disposed)
                    ReclaimRetiredRanges();
            }
        }

        private Dictionary<uint, DescriptorRecord> GetPending(DescriptorTableType type) =>
            type == DescriptorTableType.Resource ? _pendingResources : _pendingSamplers;

        private static void ResetReserved(DescriptorRange range, ulong identity)
        {
            range.Identity = identity;
            range.State = DescriptorRangeState.Reserved;
            range.HasPublishedContent = false;
            range.ActivationQueued = false;
            range.ReusableAfterGeneration = 0;
            range.Next = null;
            range.ActivationNext = null;
            range.Slots = null;
        }

        private ulong AllocateIdentity()
        {
            if (_nextAllocationIdentity == ulong.MaxValue)
                throw new InvalidOperationException(
                    "The descriptor allocation identity space is exhausted.");
            return _nextAllocationIdentity++;
        }

        private void Activate(DescriptorRange? range)
        {
            if (range is null)
                return;
            if (range.State == DescriptorRangeState.Reserved)
                range.State = DescriptorRangeState.Active;
            else if (range.State != DescriptorRangeState.Active)
                throw new InvalidOperationException("The descriptor range is not active.");
            QueueActivation(range);
        }

        private void QueueActivation(DescriptorRange range)
        {
            if (range.HasPublishedContent || range.ActivationQueued)
                return;
            range.ActivationQueued = true;
            range.ActivationNext = null;
            if (_pendingActivationTail is null)
                _pendingActivationHead = range;
            else
                _pendingActivationTail.ActivationNext = range;
            _pendingActivationTail = range;
        }

        private void RemovePendingActivation(DescriptorRange range)
        {
            if (!range.ActivationQueued)
                return;
            DescriptorRange? previous = null;
            DescriptorRange? current = _pendingActivationHead;
            while (current is not null && !ReferenceEquals(current, range))
            {
                previous = current;
                current = current.ActivationNext;
            }
            if (current is null)
                throw new InvalidOperationException("The descriptor activation queue is inconsistent.");
            if (previous is null)
                _pendingActivationHead = current.ActivationNext;
            else
                previous.ActivationNext = current.ActivationNext;
            if (ReferenceEquals(_pendingActivationTail, current))
                _pendingActivationTail = previous;
            current.ActivationQueued = false;
            current.ActivationNext = null;
        }

        private void Retire(DescriptorRange? range)
        {
            if (range is null || range.State == DescriptorRangeState.Free)
                return;
            if (range.State is not (DescriptorRangeState.Reserved or DescriptorRangeState.Active))
                return;

            RemovePendingActivation(range);
            RemovePendingRange(GetPending(range.Type), range.First, range.Count);
            if (!range.HasPublishedContent)
            {
                Recycle(range);
                return;
            }

            range.State = DescriptorRangeState.PendingRetirement;
            range.Next = null;
            if (_pendingRetirementTail is null)
                _pendingRetirementHead = range;
            else
                _pendingRetirementTail.Next = range;
            _pendingRetirementTail = range;
        }

        private void StageRetirementTombstones()
        {
            int resourceCount = 0;
            int samplerCount = 0;
            for (DescriptorRange? range = _pendingRetirementHead;
                 range is not null;
                 range = range.Next)
            {
                if (range.Type == DescriptorTableType.Resource)
                    resourceCount = checked(resourceCount + checked((int)range.Count));
                else
                    samplerCount = checked(samplerCount + checked((int)range.Count));
            }
            _pendingResources.EnsureCapacity(checked(_pendingResources.Count + resourceCount));
            _pendingSamplers.EnsureCapacity(checked(_pendingSamplers.Count + samplerCount));
            for (DescriptorRange? range = _pendingRetirementHead;
                 range is not null;
                 range = range.Next)
            {
                for (uint slot = 0; slot < range.Count; slot++)
                {
                    ReplacePending(
                        range.Type,
                        checked(range.First + slot),
                        DescriptorRecord.CreateTombstone(range.GetSlot(slot)));
                }
            }
        }

        private void MarkActivationsPublished()
        {
            DescriptorRange? current = _pendingActivationHead;
            while (current is not null)
            {
                DescriptorRange? next = current.ActivationNext;
                current.HasPublishedContent = true;
                current.ActivationQueued = false;
                current.ActivationNext = null;
                current = next;
            }
            _pendingActivationHead = null;
            _pendingActivationTail = null;
        }

        private void CommitRetirements()
        {
            DescriptorRange? current = _pendingRetirementHead;
            _pendingRetirementHead = null;
            _pendingRetirementTail = null;
            while (current is not null)
            {
                DescriptorRange? next = current.Next;
                current.Next = null;
                Recycle(current);
                current = next;
            }
        }

        private void ReclaimRetiredRanges()
        {
            ulong oldestLive = _liveGenerations.Count == 0
                ? ulong.MaxValue
                : _liveGenerations.Min;
            while (_retiredHead is DescriptorRange range &&
                   oldestLive >= range.ReusableAfterGeneration)
            {
                _retiredHead = range.Next;
                if (_retiredHead is null)
                    _retiredTail = null;
                range.Next = null;
                Recycle(range);
            }
        }

        private void Recycle(DescriptorRange range)
        {
            range.State = DescriptorRangeState.Free;
            range.HasPublishedContent = false;
            range.ActivationQueued = false;
            range.ReusableAfterGeneration = 0;
            range.ActivationNext = null;
            range.Slots = null;
            ref DescriptorRange? head = ref range.Type == DescriptorTableType.Resource
                ? ref _freeResources
                : ref _freeSamplers;
            DescriptorRange? previous = null;
            DescriptorRange? current = head;
            while (current is not null && current.First < range.First)
            {
                previous = current;
                current = current.Next;
            }

            if (previous is not null && checked(previous.First + previous.Count) == range.First)
            {
                previous.Count = checked(previous.Count + range.Count);
                range = previous;
            }
            else
            {
                range.Next = current;
                if (previous is null)
                    head = range;
                else
                    previous.Next = range;
            }

            current = range.Next;
            if (current is not null && checked(range.First + range.Count) == current.First)
            {
                range.Count = checked(range.Count + current.Count);
                range.Next = current.Next;
            }
        }

        private void ReplacePending(
            DescriptorTableType type,
            uint index,
            DescriptorRecord record)
        {
            Dictionary<uint, DescriptorRecord> pending = GetPending(type);
            try
            {
                if (!pending.ContainsKey(index))
                    pending.EnsureCapacity(checked(pending.Count + 1));
                pending.TryGetValue(index, out DescriptorRecord? previous);
                pending[index] = record;
                previous?.Release();
            }
            catch
            {
                record.Release();
                throw;
            }
        }

        private DescriptorRecord CreateBindingRecord(in ResourceBinding binding)
            => CreateBindingRecord(binding, binding.Type);

        internal static DescriptorSlotDesc GetDescriptorSlotDesc(
            GraphicsObject owner,
            ResourceBindingType type) => owner switch
        {
            BufferCbv => new DescriptorSlotDesc(ResourceBindingType.ConstantBuffer),
            BufferSrv value => new DescriptorSlotDesc(
                ResourceBindingType.BufferSrv,
                value.Description.Format,
                value.Description.StructureStride),
            BufferUav value => new DescriptorSlotDesc(
                ResourceBindingType.BufferUav,
                value.Description.Format,
                value.Description.StructureStride,
                HasCounter: value.Description.CounterBuffer is not null),
            TextureSrv value => new DescriptorSlotDesc(
                ResourceBindingType.TextureSrv,
                value.Description.Format,
                TextureDimension: value.Description.Dimension,
                Aspects: value.Description.Range.Aspects),
            TextureUav value => new DescriptorSlotDesc(
                ResourceBindingType.TextureUav,
                value.Description.Format,
                TextureDimension: value.Description.Dimension,
                Aspects: value.Description.Range.Aspects),
            Sampler => new DescriptorSlotDesc(ResourceBindingType.Sampler),
            AccelerationStructureSrv =>
                new DescriptorSlotDesc(ResourceBindingType.AccelerationStructure),
            _ => new DescriptorSlotDesc(type),
        };

        private DescriptorRecord CreateBindingRecord(
            in ResourceBinding binding,
            in DescriptorSlotDesc slot)
        {
            if (binding.Value is null)
                return DescriptorRecord.CreateNull(slot);
            if (binding.Value is GraphicsObject owner && owner is INativeDescriptor descriptor)
            {
                return DescriptorRecord.Create(
                    descriptor.NativeDescriptor,
                    owner,
                    slot.Type,
                    slot);
            }
            throw new ArgumentException("The binding is not a D3D12 descriptor.", nameof(binding));
        }

        private DescriptorRecord CreateBindingRecord(
            in ResourceBinding binding,
            ResourceBindingType expectedType)
        {
            if (binding.Value is null)
                return DescriptorRecord.CreateNull(expectedType);
            if (binding.Value is GraphicsObject owner && owner is INativeDescriptor descriptor)
            {
                return DescriptorRecord.Create(
                    descriptor.NativeDescriptor,
                    owner,
                    expectedType,
                    new DescriptorSlotDesc(expectedType));
            }
            throw new ArgumentException("The binding is not a D3D12 descriptor.", nameof(binding));
        }

        private static void RemovePendingRange(
            Dictionary<uint, DescriptorRecord> pending,
            uint first,
            uint count)
        {
            for (uint offset = 0; offset < count; offset++)
            {
                if (pending.Remove(checked(first + offset), out DescriptorRecord? record))
                    record.Release();
            }
        }

        private static DescriptorRecord?[] GrowCopy(DescriptorRecord?[] source, uint count)
        {
            int length = checked((int)count);
            DescriptorRecord?[] result = new DescriptorRecord?[length];
            source.AsSpan().CopyTo(result);
            return result;
        }

        private static void ApplyPending(
            DescriptorRecord?[] destination,
            Dictionary<uint, DescriptorRecord> pending)
        {
            foreach ((uint index, DescriptorRecord record) in pending)
                destination[checked((int)index)] = record;
        }

        private static void CommitArray(
            ref DescriptorRecord?[] committed,
            DescriptorRecord?[] candidate,
            Dictionary<uint, DescriptorRecord> pending)
        {
            foreach (uint index in pending.Keys)
            {
                if (index < committed.Length)
                    committed[checked((int)index)]?.Release();
            }
            committed = candidate;
            pending.Clear();
        }
    }

    private sealed class DescriptorRecord
    {
        private DescriptorLease? _source;
        private NativeLease? _resource;
        private NativeLease? _secondaryResource;
        private GraphicsObject? _owner;
        private int _references = 1;

        private DescriptorRecord(
            in DescriptorSlotDesc slot,
            uint visibleNodeMask = uint.MaxValue,
            bool allowDummySampler = false)
        {
            Slot = slot;
            VisibleNodeMask = visibleNodeMask;
            AllowDummySampler = allowDummySampler;
        }

        internal DescriptorSlotDesc Slot { get; }
        internal ResourceBindingType Type => Slot.Type;
        internal bool AllowDummySampler { get; }
        internal uint VisibleNodeMask { get; }
        internal DescriptorLease? Source => _source;
        internal GraphicsObject? Owner => _owner;
        internal NativeLease? Resource => _resource;

        internal static DescriptorRecord CreateNull(ResourceBindingType type)
            => CreateNull(new DescriptorSlotDesc(type));

        internal static DescriptorRecord CreateNull(in DescriptorSlotDesc slot)
        {
            if (!Enum.IsDefined(slot.Type) || slot.Type == ResourceBindingType.None)
                throw new ArgumentOutOfRangeException(nameof(slot));
            return new DescriptorRecord(slot);
        }

        internal static DescriptorRecord CreateTombstone(in DescriptorSlotDesc slot)
        {
            if (!Enum.IsDefined(slot.Type) || slot.Type == ResourceBindingType.None)
                throw new ArgumentOutOfRangeException(nameof(slot));
            return new DescriptorRecord(slot, allowDummySampler: true);
        }

        internal static DescriptorRecord Create(
            DescriptorLease source,
            GraphicsObject owner,
            ResourceBindingType type,
            in DescriptorSlotDesc slot)
        {
            if (slot.Type != type)
                throw new ArgumentException("Descriptor slot type does not match the descriptor value.", nameof(slot));
            DescriptorRecord result = new(
                slot,
                visibleNodeMask: GetVisibleNodeMask(owner));
            (NativeLease? resource, NativeLease? secondaryResource) =
                GetResourceLifetimes(owner);
            source.Retain();
            try
            {
                resource?.Retain();
                try
                {
                    secondaryResource?.Retain();
                }
                catch
                {
                    resource?.Release();
                    throw;
                }
            }
            catch
            {
                source.Release();
                throw;
            }
            result._source = source;
            result._owner = owner;
            result._resource = resource;
            result._secondaryResource = secondaryResource;
            return result;
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
            throw new ObjectDisposedException(nameof(DescriptorRecord));
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0)
                return;
            Interlocked.Exchange(ref _secondaryResource, null)?.Release();
            Interlocked.Exchange(ref _resource, null)?.Release();
            Interlocked.Exchange(ref _source, null)?.Release();
            _owner = null;
        }

        private static (NativeLease? Primary, NativeLease? Secondary)
            GetResourceLifetimes(GraphicsObject owner) => owner switch
        {
            BufferCbv view => (RequireD3D12.Buffer(view.Resource).NativeLifetime, null),
            BufferSrv view => (RequireD3D12.Buffer(view.Resource).NativeLifetime, null),
            BufferUav view => (
                RequireD3D12.Buffer(view.Resource).NativeLifetime,
                view.Description.CounterBuffer is Buffer counter
                    ? RequireD3D12.Buffer(counter).NativeLifetime
                    : null),
            D3D12SamplerFeedbackUav view => (
                view.FeedbackResource.NativeLifetime,
                view.SampledResource.NativeLifetime),
            TextureSrv view => (RequireD3D12.Texture(view.Resource).NativeLifetime, null),
            TextureUav view => (RequireD3D12.Texture(view.Resource).NativeLifetime, null),
            AccelerationStructureSrv view => (
                RequireD3D12.AccelerationStructure(view.Resource).NativeLifetime,
                null),
            _ => (null, null),
        };

        private static uint GetVisibleNodeMask(GraphicsObject owner) => owner switch
        {
            BufferCbv view => RequireD3D12.Buffer(view.Resource).Info.VisibleNodeMask,
            BufferSrv view => RequireD3D12.Buffer(view.Resource).Info.VisibleNodeMask,
            BufferUav view => view.Description.CounterBuffer is Buffer counter
                ? RequireD3D12.Buffer(view.Resource).Info.VisibleNodeMask &
                  RequireD3D12.Buffer(counter).Info.VisibleNodeMask
                : RequireD3D12.Buffer(view.Resource).Info.VisibleNodeMask,
            D3D12SamplerFeedbackUav view =>
                view.FeedbackResource.Info.VisibleNodeMask &
                view.SampledResource.Info.VisibleNodeMask,
            TextureSrv view => RequireD3D12.Texture(view.Resource).Info.VisibleNodeMask,
            TextureUav view => RequireD3D12.Texture(view.Resource).Info.VisibleNodeMask,
            AccelerationStructureSrv view =>
                RequireD3D12.AccelerationStructure(view.Resource).Storage.Info.VisibleNodeMask,
            _ => uint.MaxValue,
        };
    }

    private sealed class DescriptorGeneration
    {
        private readonly DescriptorPublisher _publisher;
        private readonly DescriptorRecord[] _records;
        private ID3D12DescriptorHeap* _resources;
        private ID3D12DescriptorHeap* _samplers;
        private int _references = 1;

        private DescriptorGeneration(
            DescriptorPublisher publisher,
            ulong identity,
            uint resourceCount,
            uint samplerCount,
            ID3D12DescriptorHeap* resources,
            ID3D12DescriptorHeap* samplers,
            DescriptorRecord[] records)
        {
            _publisher = publisher;
            Identity = identity;
            ResourceCount = resourceCount;
            SamplerCount = samplerCount;
            _resources = resources;
            _samplers = samplers;
            _records = records;
        }

        internal ulong Identity { get; }
        internal uint ResourceCount { get; }
        internal uint SamplerCount { get; }
        internal ID3D12DescriptorHeap* ResourceHeap => _resources;
        internal ID3D12DescriptorHeap* SamplerHeap => _samplers;

        internal static DescriptorGeneration Create(
            DescriptorPublisher publisher,
            D3D12Device device,
            uint nodeMask,
            ulong identity,
            uint resourceCount,
            uint samplerCount,
            DescriptorRecord?[] resources,
            DescriptorRecord?[] samplers)
        {
            int retainedCapacity = checked(
                CountRetainedRecords(resources) + CountRetainedRecords(samplers));
            DescriptorRecord[] retained = new DescriptorRecord[retainedCapacity];
            int retainedCount = 0;
            ID3D12DescriptorHeap* resourceHeap = null;
            ID3D12DescriptorHeap* samplerHeap = null;
            try
            {
                resourceHeap = CreateHeap(
                    device,
                    DescriptorHeapType.CbvSrvUav,
                    resourceCount,
                    nodeMask);
                samplerHeap = CreateHeap(
                    device,
                    DescriptorHeapType.Sampler,
                    samplerCount,
                    nodeMask);
                CopyRecords(
                    device,
                    resourceHeap,
                    DescriptorHeapType.CbvSrvUav,
                    resources,
                    retained,
                    ref retainedCount);
                CopyRecords(
                    device,
                    samplerHeap,
                    DescriptorHeapType.Sampler,
                    samplers,
                    retained,
                    ref retainedCount);
                return new DescriptorGeneration(
                    publisher,
                    identity,
                    resourceCount,
                    samplerCount,
                    resourceHeap,
                    samplerHeap,
                    retained);
            }
            catch
            {
                for (int index = 0; index < retainedCount; index++)
                    retained[index].Release();
                ReleaseHeap(samplerHeap);
                ReleaseHeap(resourceHeap);
                throw;
            }
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
            if (TryRetain())
                return;
            throw new ObjectDisposedException(nameof(DescriptorGeneration));
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0)
                return;
            foreach (DescriptorRecord record in _records)
                record.Release();
            ID3D12DescriptorHeap* sampler = _samplers;
            _samplers = null;
            ReleaseHeap(sampler);
            ID3D12DescriptorHeap* resources = _resources;
            _resources = null;
            ReleaseHeap(resources);
            _publisher.OnGenerationReleased(Identity);
        }

        internal void CopyTo(
            D3D12Device device,
            ID3D12DescriptorHeap* resourceDestination,
            ID3D12DescriptorHeap* samplerDestination)
        {
            CopyResourceTo(device, resourceDestination);
            CopySamplerTo(device, samplerDestination);
        }

        internal void CopyResourceTo(
            D3D12Device device,
            ID3D12DescriptorHeap* destination)
        {
            if (ResourceCount == 0)
                return;
            device.Native->CopyDescriptorsSimple(
                ResourceCount,
                destination->GetCPUDescriptorHandleForHeapStart(),
                ResourceHeap->GetCPUDescriptorHandleForHeapStart(),
                DescriptorHeapType.CbvSrvUav);
        }

        internal void CopySamplerTo(
            D3D12Device device,
            ID3D12DescriptorHeap* destination)
        {
            if (SamplerCount == 0)
                return;
            device.Native->CopyDescriptorsSimple(
                SamplerCount,
                destination->GetCPUDescriptorHandleForHeapStart(),
                SamplerHeap->GetCPUDescriptorHandleForHeapStart(),
                DescriptorHeapType.Sampler);
        }

        private static ID3D12DescriptorHeap* CreateHeap(
            D3D12Device device,
            DescriptorHeapType type,
            uint count,
            uint nodeMask)
        {
            DescriptorHeapDesc desc = new(
                type,
                count,
                DescriptorHeapFlags.ShaderVisible,
                nodeMask);
            ID3D12DescriptorHeap* heap = null;
            Guid iid = ID3D12DescriptorHeap.Guid;
            ThrowIfFailed(
                device,
                device.Native->CreateDescriptorHeap(&desc, &iid, (void**)&heap),
                NativeOperationType.Ordinary,
                "ID3D12Device::CreateDescriptorHeap");
            SetNativeName(
                heap,
                $"Published {type} Descriptor Heap (count={count}, nodeMask=0x{nodeMask:X})");
            return heap;
        }

        private static void ReleaseHeap(ID3D12DescriptorHeap* heap)
        {
            if (heap is null)
                return;
            _ = heap->Release();
        }

        private static int CountRetainedRecords(DescriptorRecord?[] records)
        {
            int result = 0;
            foreach (DescriptorRecord? record in records)
            {
                if (record?.Source is not null)
                    result = checked(result + 1);
            }
            return result;
        }

        private static void CopyRecords(
            D3D12Device device,
            ID3D12DescriptorHeap* heap,
            DescriptorHeapType type,
            DescriptorRecord?[] records,
            DescriptorRecord[] retained,
            ref int retainedCount)
        {
            CpuDescriptorHandle destination = heap->GetCPUDescriptorHandleForHeapStart();
            uint increment = device.Native->GetDescriptorHandleIncrementSize(type);
            for (int index = 0; index < records.Length; index++)
            {
                DescriptorRecord? record = records[index];
                CpuDescriptorHandle target = new(
                    destination.Ptr + checked((nuint)((uint)index * increment)));
                if (record?.Source is DescriptorLease source)
                {
                    record.Retain();
                    try
                    {
                        retained[retainedCount] = record;
                        retainedCount++;
                    }
                    catch
                    {
                        record.Release();
                        throw;
                    }
                    device.Native->CopyDescriptorsSimple(1, target, source.Cpu, type);
                    continue;
                }
                if (record is not null &&
                    record.Type == ResourceBindingType.Sampler &&
                    !record.AllowDummySampler)
                {
                    throw new InvalidOperationException(
                        "A Sampler DescriptorTable contains an unwritten slot. D3D12 has no null sampler descriptor.");
                }
                WriteTypedNullDescriptor(
                    device,
                    record?.Slot ?? (type == DescriptorHeapType.Sampler
                        ? new DescriptorSlotDesc(ResourceBindingType.Sampler)
                        : new DescriptorSlotDesc(
                            ResourceBindingType.TextureSrv,
                            Format.R8G8B8A8UNorm,
                            TextureDimension: TextureViewDimension.Texture2D)),
                    target,
                    allowDummySampler: record is null || record.AllowDummySampler);
            }
        }
    }

    private static void WriteTypedNullDescriptor(
        D3D12Device device,
        ResourceBindingType type,
        CpuDescriptorHandle destination) =>
        WriteTypedNullDescriptor(
            device,
            new DescriptorSlotDesc(type),
            destination,
            allowDummySampler: true);

    private static void WriteTypedNullDescriptor(
        D3D12Device device,
        in DescriptorSlotDesc slot,
        CpuDescriptorHandle destination,
        bool allowDummySampler = false)
    {
        switch (slot.Type)
        {
            case ResourceBindingType.ConstantBuffer:
                device.Native->CreateConstantBufferView(null, destination);
                return;
            case ResourceBindingType.BufferSrv:
                WriteNullBufferSrv(device, slot, destination);
                return;
            case ResourceBindingType.BufferUav:
                WriteNullBufferUav(device, slot, destination);
                return;
            case ResourceBindingType.TextureUav:
                WriteNullTextureUav(device, slot, destination);
                return;
            case ResourceBindingType.AccelerationStructure:
                WriteNullAccelerationStructure(device, destination);
                return;
            case ResourceBindingType.Sampler:
                if (!allowDummySampler)
                {
                    throw new InvalidOperationException(
                        "D3D12 has no null sampler descriptor; write a concrete Sampler before publication.");
                }
                WriteDummySampler(device, destination);
                return;
            case ResourceBindingType.None:
            case ResourceBindingType.TextureSrv:
                WriteNullTextureSrv(device, slot, destination);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(slot));
        }
    }

    private static void WriteNullBufferSrv(
        D3D12Device device,
        in DescriptorSlotDesc slot,
        CpuDescriptorHandle destination)
    {
        GetNullBufferShape(
            slot,
            out Silk.NET.DXGI.Format format,
            out uint stride,
            out BufferSrvFlags flags);
        ShaderResourceViewDesc native = new()
        {
            Format = format,
            ViewDimension = SrvDimension.Buffer,
            Shader4ComponentMapping = 5768,
        };
        native.Buffer = new Silk.NET.Direct3D12.BufferSrv
        {
            NumElements = 1,
            StructureByteStride = stride,
            Flags = flags,
        };
        device.Native->CreateShaderResourceView(null, &native, destination);
    }

    private static void WriteNullBufferUav(
        D3D12Device device,
        in DescriptorSlotDesc slot,
        CpuDescriptorHandle destination)
    {
        GetNullBufferShape(
            slot,
            out Silk.NET.DXGI.Format format,
            out uint stride,
            out BufferSrvFlags srvFlags);
        UnorderedAccessViewDesc native = new()
        {
            Format = format,
            ViewDimension = UavDimension.Buffer,
        };
        native.Buffer = new Silk.NET.Direct3D12.BufferUav
        {
            NumElements = 1,
            StructureByteStride = stride,
            Flags = srvFlags == BufferSrvFlags.Raw
                ? BufferUavFlags.Raw
                : BufferUavFlags.None,
        };
        device.Native->CreateUnorderedAccessView(null, null, &native, destination);
    }

    private static void WriteNullTextureUav(
        D3D12Device device,
        in DescriptorSlotDesc slot,
        CpuDescriptorHandle destination)
    {
        TextureViewDimension dimension = slot.TextureDimension
            ?? throw new ArgumentException(
                "A Texture UAV descriptor slot requires TextureDimension.",
                nameof(slot));
        Format format = slot.Format
            ?? throw new ArgumentException(
                "A Texture UAV descriptor slot requires Format.",
                nameof(slot));
        UnorderedAccessViewDesc native = new()
        {
            Format = FormatMappings.ToDxgi(format),
            ViewDimension = ToUavDimension(dimension),
        };
        InitializeNullUav(ref native, dimension);
        device.Native->CreateUnorderedAccessView(null, null, &native, destination);
    }

    private static void WriteNullAccelerationStructure(
        D3D12Device device,
        CpuDescriptorHandle destination)
    {
        ShaderResourceViewDesc native = new()
        {
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            ViewDimension = SrvDimension.RaytracingAccelerationStructure,
            Shader4ComponentMapping = 5768,
        };
        native.RaytracingAccelerationStructure = new RaytracingAccelerationStructureSrv(0);
        device.Native->CreateShaderResourceView(null, &native, destination);
    }

    private static void WriteDummySampler(
        D3D12Device device,
        CpuDescriptorHandle destination)
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
    }

    private static void WriteNullTextureSrv(
        D3D12Device device,
        in DescriptorSlotDesc slot,
        CpuDescriptorHandle destination)
    {
        TextureViewDimension dimension = slot.TextureDimension
            ?? TextureViewDimension.Texture2D;
        Format format = slot.Format ?? Format.R8G8B8A8UNorm;
        ShaderResourceViewDesc native = new()
        {
            Format = FormatMappings.ToShaderViewFormat(format, slot.Aspects),
            ViewDimension = ToSrvDimension(dimension),
            Shader4ComponentMapping = 5768,
        };
        InitializeNullSrv(ref native, dimension);
        device.Native->CreateShaderResourceView(null, &native, destination);
    }

    private static void GetNullBufferShape(
        in DescriptorSlotDesc slot,
        out Silk.NET.DXGI.Format format,
        out uint stride,
        out BufferSrvFlags flags)
    {
        if (slot.Format is Format typed)
        {
            format = FormatMappings.ToDxgi(typed);
            stride = 0;
            flags = BufferSrvFlags.None;
            return;
        }
        if (slot.StructureStride != 0)
        {
            format = Silk.NET.DXGI.Format.FormatUnknown;
            stride = slot.StructureStride;
            flags = BufferSrvFlags.None;
            return;
        }
        format = Silk.NET.DXGI.Format.FormatR32Typeless;
        stride = 0;
        flags = BufferSrvFlags.Raw;
    }

    private static void InitializeNullSrv(
        ref ShaderResourceViewDesc native,
        TextureViewDimension dimension)
    {
        switch (dimension)
        {
            case TextureViewDimension.Texture1D:
                native.Texture1D = new Tex1DSrv { MipLevels = 1 };
                break;
            case TextureViewDimension.Texture1DArray:
                native.Texture1DArray = new Tex1DArraySrv { MipLevels = 1, ArraySize = 1 };
                break;
            case TextureViewDimension.Texture2D:
                native.Texture2D = new Tex2DSrv { MipLevels = 1 };
                break;
            case TextureViewDimension.Texture2DArray:
                native.Texture2DArray = new Tex2DArraySrv { MipLevels = 1, ArraySize = 1 };
                break;
            case TextureViewDimension.Texture2DMultisampled:
                break;
            case TextureViewDimension.Texture2DMultisampledArray:
                native.Texture2DMSArray = new Tex2DmsArraySrv { ArraySize = 1 };
                break;
            case TextureViewDimension.Cube:
                native.TextureCube = new TexcubeSrv { MipLevels = 1 };
                break;
            case TextureViewDimension.CubeArray:
                native.TextureCubeArray = new TexcubeArraySrv { MipLevels = 1, NumCubes = 1 };
                break;
            case TextureViewDimension.Texture3D:
                native.Texture3D = new Tex3DSrv { MipLevels = 1 };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(dimension));
        }
    }

    private static void InitializeNullUav(
        ref UnorderedAccessViewDesc native,
        TextureViewDimension dimension)
    {
        switch (dimension)
        {
            case TextureViewDimension.Texture1D:
                native.Texture1D = new Tex1DUav();
                break;
            case TextureViewDimension.Texture1DArray:
                native.Texture1DArray = new Tex1DArrayUav { ArraySize = 1 };
                break;
            case TextureViewDimension.Texture2D:
                native.Texture2D = new Tex2DUav();
                break;
            case TextureViewDimension.Texture2DArray:
            case TextureViewDimension.Cube:
            case TextureViewDimension.CubeArray:
                native.ViewDimension = UavDimension.Texture2Darray;
                native.Texture2DArray = new Tex2DArrayUav { ArraySize = 1 };
                break;
            case TextureViewDimension.Texture3D:
                native.Texture3D = new Tex3DUav { WSize = 1 };
                break;
            case TextureViewDimension.Texture2DMultisampled:
            case TextureViewDimension.Texture2DMultisampledArray:
                throw new NotSupportedException("D3D12 does not support multisampled UAV descriptors.");
            default:
                throw new ArgumentOutOfRangeException(nameof(dimension));
        }
    }

    private sealed class D3D12DescriptorTable : DescriptorTable
    {
        private readonly D3D12Device _device;

        internal D3D12DescriptorTable(
            D3D12Device device,
            DescriptorPublisher publisher,
            DescriptorTableType type,
            uint nodeIndex,
            DescriptorRange range,
            ReadOnlySpan<DescriptorSlotDesc> slots,
            string? label)
            : base(device, type, nodeIndex, slots, label)
        {
            _device = device;
            Publisher = publisher;
            Range = range;
        }

        internal D3D12Device NativeDevice => _device;
        internal DescriptorPublisher Publisher { get; }
        internal DescriptorRange Range { get; }
        internal uint FirstIndex => Range.First;
        internal void CheckSlot(uint slot)
        {
            ThrowIfDisposed();
            if (slot >= Count)
                throw new ArgumentOutOfRangeException(nameof(slot));
        }

        internal override void Release(bool fromParent)
        {
            Publisher.DisposeTable(this);
            _device.UnregisterChild(this);
        }
    }

    private sealed class D3D12PersistentParameterBindings : PersistentParameterBindings
    {
        private readonly D3D12Device _device;
        private RetainedSlangProgram? _program;
        private readonly NativeLease _pipelineState;
        private readonly object _gate = new();
        private D3D12PersistentParameterData? _current;
        private ulong _nextVersion = 1;

        internal D3D12PersistentParameterBindings(
            D3D12Device device,
            D3D12Pipeline ownerPipeline,
            NativeParameterBinding nativeLayout,
            VariableLayoutReflection layout,
            string? label)
            : base(device, layout, label)
        {
            _device = device;
            OwnerPipeline = ownerPipeline;
            NativeLayout = nativeLayout;
            OrdinaryConstantBufferRootParameter =
                nativeLayout.ResourceTable is null &&
                nativeLayout.SamplerTable is null &&
                nativeLayout.OrdinaryRoot is { UsesRootConstants: false } ordinary
                    ? ordinary.RootParameterIndex
                    : null;
            RetainedSlangProgram? program = null;
            NativeLease? pipelineState = null;
            try
            {
                program = ownerPipeline.RetainProgramReference();
                pipelineState = ownerPipeline.RetainNativeState();
                _program = program;
                program = null;
                _pipelineState = pipelineState;
            }
            catch
            {
                pipelineState?.Release();
                program?.Dispose();
                throw;
            }
        }

        internal D3D12Device NativeDevice => _device;
        internal D3D12Pipeline OwnerPipeline { get; }
        internal NativeParameterBinding NativeLayout { get; }
        internal uint? OrdinaryConstantBufferRootParameter { get; }
        internal D3D12PersistentParameterData? CurrentData
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => Volatile.Read(ref _current);
        }

        internal void StageReplacement(in ParameterBlockBindings bindings)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (Layout != bindings.Layout)
                    throw new ArgumentException("The parameter layout cannot change during an update.", nameof(bindings));
                RequireNativeParameterBindings(
                    Layout,
                    NativeLayout,
                    bindings.Resources,
                    bindings.OrdinaryData);
                if (_nextVersion == ulong.MaxValue)
                {
                    throw new GraphicsException(
                        GraphicsError.OutOfDescriptors,
                        "The persistent-parameter version domain is exhausted.");
                }

                D3D12PersistentParameterData candidate =
                    D3D12PersistentParameterData.Create(
                        _device,
                        _nextVersion,
                        bindings.Resources,
                        NativeLayout.Slots,
                        bindings.OrdinaryData,
                        NativeLayout.OrdinaryRoot is { UsesRootConstants: false });
                D3D12PersistentParameterData? previous = _current;
                Volatile.Write(ref _current, candidate);
                _nextVersion++;
                previous?.Release();
            }
        }

        internal override void Release(bool fromParent)
        {
            lock (_gate)
                Interlocked.Exchange(ref _current, null)?.Release();
            _pipelineState.Release();
            Interlocked.Exchange(ref _program, null)?.Dispose();
            _device.UnregisterChild(this);
        }
    }

    private static partial class RequireD3D12
    {
        internal static D3D12DescriptorTable DescriptorTable(DescriptorTable value) =>
            value as D3D12DescriptorTable ??
            throw new ArgumentException(
                "The DescriptorTable was not created by the Direct3D 12 backend.",
                nameof(value));

        internal static D3D12PersistentParameterBindings PersistentParameterBindings(
            PersistentParameterBindings value) =>
            value as D3D12PersistentParameterBindings ??
            throw new ArgumentException(
                "The PersistentParameterBindings were not created by the Direct3D 12 backend.",
                nameof(value));
    }
}
