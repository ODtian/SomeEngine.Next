using SlangShaderSharp;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    public DescriptorTable CreateDescriptorTable(
        Device device,
        DescriptorTableType type,
        uint count,
        string? label = null)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        nativeDevice.ThrowIfUnavailable();
        if (count == 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        DescriptorRange range = nativeDevice.Descriptors.Reserve(type, count);
        D3D12DescriptorTable? result = null;
        try
        {
            result = new D3D12DescriptorTable(nativeDevice, range, label);
            nativeDevice.Descriptors.InitializeTable(result);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            if (result is null)
                nativeDevice.Descriptors.Cancel(range);
            else
                result.Dispose();
            throw;
        }
    }

    public uint GetDescriptorIndex(DescriptorTable table, uint slot)
    {
        D3D12DescriptorTable native = NativeCast.DescriptorTable(table);
        native.CheckSlot(slot);
        return checked(native.FirstIndex + slot);
    }

    public void WriteDescriptor(
        DescriptorTable table,
        uint slot,
        in ResourceBinding value)
    {
        D3D12DescriptorTable native = NativeCast.DescriptorTable(table);
        native.CheckSlot(slot);
        EnsureTableBindingType(native.Type, value);
        native.NativeDevice.Descriptors.StageBinding(
            native.Type,
            checked(native.FirstIndex + slot),
            value);
    }

    public PersistentParameterBindings CreatePersistentParameterBindings(
        Device device,
        in ParameterBlockBindings bindings,
        string? label = null)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        nativeDevice.ThrowIfUnavailable();
        D3D12PersistentParameterBindings result = new(
            nativeDevice,
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
            NativeCast.PersistentParameterBindings(destination);
        native.StageReplacement(bindings);
    }

    public void PublishDescriptors(Device device) =>
        NativeCast.Device(device).Descriptors.Publish();

    private static void EnsureTableBindingType(
        DescriptorTableType tableType,
        in ResourceBinding binding)
    {
        bool sampler = binding.Type == ResourceBindingType.Sampler;
        if (tableType == DescriptorTableType.Sampler)
        {
            if (binding.Type is not (ResourceBindingType.None or ResourceBindingType.Sampler))
                throw new ArgumentException("A Sampler table accepts only Sampler descriptors.", nameof(binding));
            return;
        }

        if (sampler)
            throw new ArgumentException("A Resource table cannot contain Sampler descriptors.", nameof(binding));
        if (!Enum.IsDefined(binding.Type))
            throw new ArgumentOutOfRangeException(nameof(binding));
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
        internal DescriptorRange(DescriptorTableType type, uint first, uint count)
        {
            Type = type;
            First = first;
            Count = count;
        }

        internal DescriptorTableType Type { get; }
        internal uint First { get; set; }
        internal uint Count { get; set; }
        internal DescriptorRangeState State { get; set; }
        internal bool HasPublishedContent { get; set; }
        internal bool ActivationQueued { get; set; }
        internal ulong ReusableAfterGeneration { get; set; }
        internal DescriptorRange? Next { get; set; }
        internal DescriptorRange? ActivationNext { get; set; }
    }

    private sealed class DescriptorPublisher : IDisposable
    {
        private readonly D3D12Device _device;
        private readonly object _gate = new();
        private readonly Dictionary<uint, DescriptorRecord> _pendingResources = [];
        private readonly Dictionary<uint, DescriptorRecord> _pendingSamplers = [];
        private readonly Dictionary<GraphicsObject, DescriptorRange> _owners =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<D3D12PersistentParameterBindings> _pendingBindings =
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
        private ulong _nextGeneration = 2;
        private bool _disposed;

        internal DescriptorPublisher(D3D12Device device)
        {
            _device = device;
            _current = DescriptorGeneration.Create(this, device, 1, 1, 1, [], []);
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
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _current.Retain();
                return _current;
            }
        }

        internal DescriptorRange Reserve(DescriptorTableType type, uint count)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!Enum.IsDefined(type) || count == 0)
                    throw new ArgumentOutOfRangeException(nameof(count));

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
                        reused = new DescriptorRange(type, candidate.First, count);
                        candidate.First = checked(candidate.First + count);
                        candidate.Count -= count;
                    }
                    ResetReserved(reused);
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
                DescriptorRange result = new(type, next, count);
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
            GraphicsObject owner)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (range.Count != 1 || range.State != DescriptorRangeState.Reserved)
                    throw new InvalidOperationException("The bindless descriptor reservation is not active.");
                DescriptorRecord record = DescriptorRecord.Create(
                    _device,
                    source,
                    owner,
                    ResourceBindingType.None);
                bool ownerAdded = false;
                bool recordTransferred = false;
                try
                {
                    _owners.EnsureCapacity(checked(_owners.Count + 1));
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
                        ReplacePending(
                            range.Type,
                            checked(range.First + slot),
                            DescriptorRecord.CreateNull(
                                range.Type == DescriptorTableType.Sampler
                                    ? ResourceBindingType.Sampler
                                    : ResourceBindingType.TextureSrv));
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
            in ResourceBinding binding)
        {
            lock (_gate)
            {
                DescriptorRecord record = CreateBindingRecord(binding);
                ReplacePending(type, index, record);
            }
        }

        internal void StagePersistentBinding(
            D3D12PersistentParameterBindings owner,
            ReadOnlySpan<ResourceBinding> bindings)
        {
            D3D12ParameterBlockShape shape = owner.Shape;
            shape.RequireMaterializationShape(bindings, owner.PendingOrdinaryData);
            List<(DescriptorTableType Type, uint Index, DescriptorRecord Record)> prepared = [];
            int ordinal = 0;
            try
            {
                foreach (ParameterLeaf leaf in shape.Leaves)
                {
                    if (leaf.Unbounded)
                        continue;
                    for (uint element = 0; element < leaf.DescriptorCount; element++)
                    {
                        DescriptorTableType tableType = leaf.Heap == ParameterHeap.Sampler
                            ? DescriptorTableType.Sampler
                            : DescriptorTableType.Resource;
                        uint baseIndex = tableType == DescriptorTableType.Sampler
                            ? owner.SamplerBaseIndex
                            : owner.ResourceBaseIndex;
                        prepared.Add((
                            tableType,
                            checked(baseIndex + leaf.HeapOffset + element),
                            CreateBindingRecord(bindings[ordinal++], leaf.Type)));
                    }
                }
            }
            catch
            {
                foreach (var value in prepared)
                    value.Record.Release();
                throw;
            }

            lock (_gate)
            {
                try
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    int resourceAdds = 0;
                    int samplerAdds = 0;
                    foreach (var value in prepared)
                    {
                        if (value.Type == DescriptorTableType.Resource)
                            resourceAdds++;
                        else
                            samplerAdds++;
                    }
                    _pendingResources.EnsureCapacity(checked(_pendingResources.Count + resourceAdds));
                    _pendingSamplers.EnsureCapacity(checked(_pendingSamplers.Count + samplerAdds));
                    _pendingBindings.EnsureCapacity(checked(_pendingBindings.Count + 1));
                }
                catch
                {
                    foreach (var value in prepared)
                        value.Record.Release();
                    throw;
                }
                foreach (var value in prepared)
                    ReplacePending(value.Type, value.Index, value.Record);
                _pendingBindings.Add(owner);
                Activate(owner.ResourceRange);
                Activate(owner.SamplerRange);
            }
        }

        internal void RemoveBindingObject(D3D12PersistentParameterBindings binding)
        {
            lock (_gate)
            {
                _pendingBindings.Remove(binding);
                Retire(binding.ResourceRange);
                Retire(binding.SamplerRange);
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

        internal void Publish()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _device.ThrowIfUnavailable();
                StageRetirementTombstones();
                if (_pendingResources.Count == 0 &&
                    _pendingSamplers.Count == 0 &&
                    _pendingBindings.Count == 0)
                    return;
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

                DescriptorGeneration candidate = DescriptorGeneration.Create(
                    this,
                    _device,
                    _nextGeneration,
                    Math.Max(1u, _nextResource),
                    Math.Max(1u, _nextSampler),
                    resources,
                    samplers);
                try
                {
                    _liveGenerations.Add(candidate.Identity);
                }
                catch
                {
                    candidate.Release();
                    throw;
                }

                DescriptorGeneration previous = _current;
                CommitArray(ref _resources, resources, _pendingResources);
                CommitArray(ref _samplers, samplers, _pendingSamplers);
                _current = candidate;
                _nextGeneration++;
                MarkActivationsPublished();
                CommitRetirements(candidate.Identity);
                foreach (D3D12PersistentParameterBindings binding in _pendingBindings)
                {
                    if (!binding.IsDisposed)
                        binding.CommitPublished(candidate.Identity);
                }
                _pendingBindings.Clear();
                previous.Release();
                ReclaimRetiredRanges();
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
                _pendingBindings.Clear();
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

        private static void ResetReserved(DescriptorRange range)
        {
            range.State = DescriptorRangeState.Reserved;
            range.HasPublishedContent = false;
            range.ActivationQueued = false;
            range.ReusableAfterGeneration = 0;
            range.Next = null;
            range.ActivationNext = null;
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
                ResourceBindingType nullType = range.Type == DescriptorTableType.Sampler
                    ? ResourceBindingType.Sampler
                    : ResourceBindingType.TextureSrv;
                for (uint slot = 0; slot < range.Count; slot++)
                {
                    ReplacePending(
                        range.Type,
                        checked(range.First + slot),
                        DescriptorRecord.CreateNull(nullType));
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

        private void CommitRetirements(ulong generation)
        {
            DescriptorRange? current = _pendingRetirementHead;
            _pendingRetirementHead = null;
            _pendingRetirementTail = null;
            while (current is not null)
            {
                DescriptorRange? next = current.Next;
                current.State = DescriptorRangeState.Retired;
                current.ReusableAfterGeneration = generation;
                current.Next = null;
                if (_retiredTail is null)
                    _retiredHead = current;
                else
                    _retiredTail.Next = current;
                _retiredTail = current;
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

        private DescriptorRecord CreateBindingRecord(
            in ResourceBinding binding,
            ResourceBindingType expectedType)
        {
            if (binding.Value is null)
                return DescriptorRecord.CreateNull(expectedType);
            if (binding.Value is GraphicsObject owner && owner is INativeDescriptor descriptor)
            {
                return DescriptorRecord.Create(
                    _device,
                    descriptor.NativeDescriptor,
                    owner,
                    expectedType);
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
        private static readonly DescriptorRecord[] NullRecords =
        [
            new(ResourceBindingType.None, immortal: true),
            new(ResourceBindingType.ConstantBuffer, immortal: true),
            new(ResourceBindingType.BufferSrv, immortal: true),
            new(ResourceBindingType.BufferUav, immortal: true),
            new(ResourceBindingType.TextureSrv, immortal: true),
            new(ResourceBindingType.TextureUav, immortal: true),
            new(ResourceBindingType.Sampler, immortal: true),
            new(ResourceBindingType.AccelerationStructure, immortal: true),
        ];

        private readonly bool _immortal;
        private DescriptorLease? _source;
        private NativeLease? _resource;
        private NativeLease? _secondaryResource;
        private GraphicsObject? _owner;
        private int _references = 1;

        private DescriptorRecord(ResourceBindingType type, bool immortal = false)
        {
            Type = type;
            _immortal = immortal;
        }

        internal ResourceBindingType Type { get; }
        internal DescriptorLease? Source => _source;
        internal GraphicsObject? Owner => _owner;
        internal NativeLease? Resource => _resource;

        internal static DescriptorRecord CreateNull(ResourceBindingType type)
        {
            if (!Enum.IsDefined(type))
                throw new ArgumentOutOfRangeException(nameof(type));
            return NullRecords[(int)type];
        }

        internal static DescriptorRecord Create(
            D3D12Device device,
            DescriptorLease source,
            GraphicsObject owner,
            ResourceBindingType type)
        {
            DescriptorRecord result = new(type);
            (NativeLease? resource, NativeLease? secondaryResource) =
                GetResourceLifetimes(owner);
            source.Retain();
            try
            {
                if (device.RetirementType == RetirementType.Automatic)
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
            }
            catch
            {
                source.Release();
                throw;
            }
            result._source = source;
            if (device.RetirementType == RetirementType.Automatic)
            {
                result._owner = owner;
                result._resource = resource;
                result._secondaryResource = secondaryResource;
            }
            return result;
        }

        internal void Retain()
        {
            if (_immortal)
                return;
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
            if (_immortal)
                return;
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
            BufferCbv view => (NativeCast.Buffer(view.Resource).NativeLifetime, null),
            BufferSrv view => (NativeCast.Buffer(view.Resource).NativeLifetime, null),
            BufferUav view => (
                NativeCast.Buffer(view.Resource).NativeLifetime,
                view.Description.CounterBuffer is Buffer counter
                    ? NativeCast.Buffer(counter).NativeLifetime
                    : null),
            D3D12SamplerFeedbackUav view => (
                view.FeedbackResource.NativeLifetime,
                view.SampledResource.NativeLifetime),
            TextureSrv view => (NativeCast.Texture(view.Resource).NativeLifetime, null),
            TextureUav view => (NativeCast.Texture(view.Resource).NativeLifetime, null),
            AccelerationStructureSrv view => (
                NativeCast.AccelerationStructure(view.Resource).NativeLifetime,
                null),
            _ => (null, null),
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
            ulong identity,
            uint resourceCount,
            uint samplerCount,
            DescriptorRecord?[] resources,
            DescriptorRecord?[] samplers)
        {
            ID3D12DescriptorHeap* resourceHeap = CreateHeap(
                device,
                DescriptorHeapType.CbvSrvUav,
                resourceCount);
            ID3D12DescriptorHeap* samplerHeap = null;
            List<DescriptorRecord> retained = [];
            try
            {
                samplerHeap = CreateHeap(device, DescriptorHeapType.Sampler, samplerCount);
                CopyRecords(device, resourceHeap, DescriptorHeapType.CbvSrvUav, resources, retained);
                CopyRecords(device, samplerHeap, DescriptorHeapType.Sampler, samplers, retained);
                return new DescriptorGeneration(
                    publisher,
                    identity,
                    resourceCount,
                    samplerCount,
                    resourceHeap,
                    samplerHeap,
                    [.. retained]);
            }
            catch
            {
                foreach (DescriptorRecord record in retained)
                    record.Release();
                if (samplerHeap is not null)
                    _ = samplerHeap->Release();
                _ = resourceHeap->Release();
                throw;
            }
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
            if (sampler is not null)
                _ = sampler->Release();
            ID3D12DescriptorHeap* resources = _resources;
            _resources = null;
            if (resources is not null)
                _ = resources->Release();
            _publisher.OnGenerationReleased(Identity);
        }

        internal void CopyTo(
            D3D12Device device,
            ID3D12DescriptorHeap* resourceDestination,
            ID3D12DescriptorHeap* samplerDestination)
        {
            if (ResourceCount != 0)
            {
                device.Native->CopyDescriptorsSimple(
                    ResourceCount,
                    resourceDestination->GetCPUDescriptorHandleForHeapStart(),
                    ResourceHeap->GetCPUDescriptorHandleForHeapStart(),
                    DescriptorHeapType.CbvSrvUav);
            }
            if (SamplerCount != 0)
            {
                device.Native->CopyDescriptorsSimple(
                    SamplerCount,
                    samplerDestination->GetCPUDescriptorHandleForHeapStart(),
                    SamplerHeap->GetCPUDescriptorHandleForHeapStart(),
                    DescriptorHeapType.Sampler);
            }
        }

        private static ID3D12DescriptorHeap* CreateHeap(
            D3D12Device device,
            DescriptorHeapType type,
            uint count)
        {
            DescriptorHeapDesc desc = new(
                type,
                count,
                DescriptorHeapFlags.ShaderVisible,
                device.EnabledNodeMask);
            ID3D12DescriptorHeap* heap = null;
            Guid iid = ID3D12DescriptorHeap.Guid;
            NativeCall.ThrowIfFailed(
                device.Native->CreateDescriptorHeap(&desc, &iid, (void**)&heap),
                "ID3D12Device::CreateDescriptorHeap");
            return heap;
        }

        private static void CopyRecords(
            D3D12Device device,
            ID3D12DescriptorHeap* heap,
            DescriptorHeapType type,
            DescriptorRecord?[] records,
            List<DescriptorRecord> retained)
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
                    device.Native->CopyDescriptorsSimple(1, target, source.Cpu, type);
                    record.Retain();
                    retained.Add(record);
                    continue;
                }
                if (type == DescriptorHeapType.CbvSrvUav)
                {
                    WriteNullResourceDescriptor(
                        device,
                        record?.Type ?? ResourceBindingType.TextureSrv,
                        target);
                }
                else
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
                    device.Native->CreateSampler(&sampler, target);
                }
            }
        }

        private static void WriteNullResourceDescriptor(
            D3D12Device device,
            ResourceBindingType type,
            CpuDescriptorHandle destination)
        {
            switch (type)
            {
                case ResourceBindingType.ConstantBuffer:
                    device.Native->CreateConstantBufferView(null, destination);
                    return;
                case ResourceBindingType.BufferSrv:
                {
                    ShaderResourceViewDesc native = new()
                    {
                        Format = Silk.NET.DXGI.Format.FormatR32Uint,
                        ViewDimension = SrvDimension.Buffer,
                        Shader4ComponentMapping = 5768,
                    };
                    native.Buffer = new Silk.NET.Direct3D12.BufferSrv { NumElements = 1 };
                    device.Native->CreateShaderResourceView(null, &native, destination);
                    return;
                }
                case ResourceBindingType.BufferUav:
                {
                    UnorderedAccessViewDesc native = new()
                    {
                        Format = Silk.NET.DXGI.Format.FormatR32Uint,
                        ViewDimension = UavDimension.Buffer,
                    };
                    native.Buffer = new Silk.NET.Direct3D12.BufferUav { NumElements = 1 };
                    device.Native->CreateUnorderedAccessView(null, null, &native, destination);
                    return;
                }
                case ResourceBindingType.TextureUav:
                {
                    UnorderedAccessViewDesc native = new()
                    {
                        Format = Silk.NET.DXGI.Format.FormatR8G8B8A8Unorm,
                        ViewDimension = UavDimension.Texture2D,
                    };
                    device.Native->CreateUnorderedAccessView(null, null, &native, destination);
                    return;
                }
                case ResourceBindingType.AccelerationStructure:
                {
                    ShaderResourceViewDesc native = new()
                    {
                        Format = Silk.NET.DXGI.Format.FormatUnknown,
                        ViewDimension = SrvDimension.RaytracingAccelerationStructure,
                        Shader4ComponentMapping = 5768,
                    };
                    native.RaytracingAccelerationStructure =
                        new RaytracingAccelerationStructureSrv(0);
                    device.Native->CreateShaderResourceView(null, &native, destination);
                    return;
                }
                case ResourceBindingType.None:
                case ResourceBindingType.TextureSrv:
                {
                    ShaderResourceViewDesc native = new()
                    {
                        Format = Silk.NET.DXGI.Format.FormatR8G8B8A8Unorm,
                        ViewDimension = SrvDimension.Texture2D,
                        Shader4ComponentMapping = 5768,
                    };
                    native.Texture2D = new Tex2DSrv { MipLevels = 1 };
                    device.Native->CreateShaderResourceView(null, &native, destination);
                    return;
                }
                case ResourceBindingType.Sampler:
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }
    }

    private sealed class D3D12DescriptorTable : DescriptorTable
    {
        private readonly D3D12Device _device;
        private int _released;

        internal D3D12DescriptorTable(
            D3D12Device device,
            DescriptorRange range,
            string? label)
            : base(device, range.Type, range.Count, label)
        {
            _device = device;
            Range = range;
        }

        internal D3D12Device NativeDevice => _device;
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
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            _device.Descriptors.DisposeTable(this);
            _device.UnregisterChild(this);
        }
    }

    private sealed class D3D12PersistentParameterBindings : PersistentParameterBindings
    {
        private readonly D3D12Device _device;
        private readonly object _gate = new();
        private D3D12ParameterMaterialization? _pending;
        private D3D12ParameterMaterialization? _published;
        private ulong _nextVersion = 1;
        private int _released;

        internal D3D12PersistentParameterBindings(
            D3D12Device device,
            VariableLayoutReflection layout,
            string? label)
            : base(device, layout, label)
        {
            _device = device;
            Shape = D3D12ParameterBlockShape.Compile(layout);
            DescriptorRange? resourceRange = null;
            try
            {
                resourceRange = Shape.ResourceDescriptorCount == 0
                    ? null
                    : device.Descriptors.Reserve(
                        DescriptorTableType.Resource,
                        Shape.ResourceDescriptorCount);
                SamplerRange = Shape.SamplerDescriptorCount == 0
                    ? null
                    : device.Descriptors.Reserve(
                        DescriptorTableType.Sampler,
                        Shape.SamplerDescriptorCount);
                ResourceRange = resourceRange;
            }
            catch
            {
                if (resourceRange is not null)
                    device.Descriptors.Cancel(resourceRange);
                throw;
            }
        }

        internal D3D12Device NativeDevice => _device;
        internal D3D12ParameterBlockShape Shape { get; }
        internal DescriptorRange? ResourceRange { get; }
        internal DescriptorRange? SamplerRange { get; }
        internal uint ResourceBaseIndex => ResourceRange?.First ?? 0;
        internal uint SamplerBaseIndex => SamplerRange?.First ?? 0;
        internal D3D12ParameterMaterialization? PublishedMaterialization
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => Volatile.Read(ref _published);
        }
        internal ReadOnlySpan<byte> PendingOrdinaryData =>
            _pending?.OrdinaryData ?? ReadOnlySpan<byte>.Empty;

        internal void StageReplacement(in ParameterBlockBindings bindings)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (Layout != bindings.Layout)
                    throw new ArgumentException("The parameter layout cannot change during an update.", nameof(bindings));
                Shape.RequireMaterializationShape(bindings.Resources, bindings.OrdinaryData);
                if (_nextVersion == ulong.MaxValue)
                {
                    throw new GraphicsException(
                        GraphicsError.OutOfDescriptors,
                        "The persistent-parameter version domain is exhausted.");
                }

                D3D12ParameterMaterialization candidate =
                    D3D12ParameterMaterialization.Create(
                        _device,
                        _nextVersion,
                        bindings.Resources,
                        bindings.OrdinaryData);
                D3D12ParameterMaterialization? previous = _pending;
                _pending = candidate;
                try
                {
                    _device.Descriptors.StagePersistentBinding(this, bindings.Resources);
                    _nextVersion++;
                }
                catch
                {
                    _pending = previous;
                    candidate.Release();
                    throw;
                }
                previous?.Release();
            }
        }

        internal void CommitPublished(ulong generationIdentity)
        {
            lock (_gate)
            {
                if (_pending is null || IsDisposed)
                    return;
                _pending.PublishedGeneration = generationIdentity;
                D3D12ParameterMaterialization? previous = _published;
                _published = _pending;
                _pending = null;
                MarkPublished();
                previous?.Release();
            }
        }

        internal D3D12ParameterMaterialization CapturePublished(
            ulong descriptorGeneration)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                D3D12ParameterMaterialization materialization = _published
                    ?? throw new InvalidOperationException(
                        "Persistent parameter bindings must be published before recording use.");
                if (materialization.PublishedGeneration > descriptorGeneration)
                {
                    throw new InvalidOperationException(
                        "The CommandContext captured an older descriptor generation than these bindings.");
                }
                materialization.Retain();
                return materialization;
            }
        }

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            _device.Descriptors.RemoveBindingObject(this);
            lock (_gate)
            {
                Interlocked.Exchange(ref _pending, null)?.Release();
                Interlocked.Exchange(ref _published, null)?.Release();
            }
            _device.UnregisterChild(this);
        }
    }

    private static partial class NativeCast
    {
        internal static D3D12DescriptorTable DescriptorTable(DescriptorTable value)
        {
#if DEBUG
            return (D3D12DescriptorTable)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<DescriptorTable, D3D12DescriptorTable>(ref value);
#endif
        }

        internal static D3D12PersistentParameterBindings PersistentParameterBindings(
            PersistentParameterBindings value)
        {
#if DEBUG
            return (D3D12PersistentParameterBindings)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<PersistentParameterBindings, D3D12PersistentParameterBindings>(ref value);
#endif
        }
    }
}
