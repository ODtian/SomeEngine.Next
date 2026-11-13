using System.Runtime.InteropServices;
using SlangShaderSharp;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;
internal sealed unsafe partial class D3D12Backend
{
    public DescriptorTable CreateDescriptorTable(
        Device device,
        ReadOnlySpan<DescriptorEntryDesc> entries,
        string? label = null,
        uint nodeIndex = uint.MaxValue,
        CancellationToken cancellationToken = default
    )
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        cancellationToken.ThrowIfCancellationRequested();
        uint resolvedNodeIndex = nativeDevice.ResolveNodeIndex(nodeIndex, nameof(nodeIndex));
        DescriptorTableType type = entries[0].Type == ResourceBindingType.Sampler ? DescriptorTableType.Sampler : DescriptorTableType.Resource;
        DescriptorPublisher publisher = nativeDevice.GetDescriptorPublisher(resolvedNodeIndex);
        uint count = checked((uint)entries.Length);
        DescriptorRange range = publisher.Reserve(type, count);
        D3D12DescriptorTable? result = null;
        try
        {
            result = new D3D12DescriptorTable(nativeDevice, publisher, type, resolvedNodeIndex, range, entries, label);
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

    public DescriptorIndex GetDescriptorIndex(DescriptorTable table, uint entry)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12DescriptorTable native = RequireDescriptorTable(table);
        native.CheckEntry(entry);
        return new DescriptorIndex(native, checked(native.FirstIndex + entry));
    }

    public void WriteDescriptor(DescriptorTable table, uint entry, in ResourceBinding value)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12DescriptorTable native = RequireDescriptorTable(table);
        native.CheckEntry(entry);
        DescriptorEntryDesc slotDesc = native.GetEntryDesc(entry);
        EnsureTableBindingType(slotDesc.Type, value);
        native.Publisher.StageBinding(native.Type, checked(native.FirstIndex + entry), slotDesc, value);
    }

    public PersistentParameterBindings CreatePersistentParameterBindings(Device device, Pipeline pipeline, in ParameterBlockBindings bindings, string? label = null)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiBindings);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        D3D12Pipeline nativePipeline = RequirePipeline(pipeline);
        NativeParameterBinding nativeLayout = nativePipeline.RootSignature.GetBlock(bindings.Layout);
        D3D12PersistentParameterBindings result = new(nativeDevice, nativePipeline, nativeLayout, bindings.Layout, label);
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

    public void UpdatePersistentParameterBindings(PersistentParameterBindings destination, in ParameterBlockBindings bindings)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiBindings);
        D3D12PersistentParameterBindings native = RequirePersistentParameterBindings(destination);
        native.StageReplacement(bindings);
    }

    public void PublishDescriptors(Device device, uint nodeIndex = uint.MaxValue, CancellationToken cancellationToken = default)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        uint resolvedNodeIndex = nativeDevice.ResolveNodeIndex(nodeIndex, nameof(nodeIndex));
        DescriptorGeneration generation = nativeDevice.GetDescriptorPublisher(resolvedNodeIndex).Publish(cancellationToken);
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

    private static void EnsureTableBindingType(ResourceBindingType entryType, in ResourceBinding binding)
    {
    }

    private enum DescriptorRangeState : byte
    {
        Reserved,
        Active,
        QueueRetirement,
        Free,
    }

    private sealed class DescriptorRange
    {
        internal DescriptorRange(DescriptorTableType type, uint first, uint count, ulong identity)
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
        internal DescriptorRange? Next { get; set; }
        internal DescriptorRange? ActivationNext { get; set; }
        internal DescriptorEntryDesc[]? Entries { get; set; }

        internal void SetEntry(uint entry, in DescriptorEntryDesc value)
        {
            Entries ??= new DescriptorEntryDesc[checked((int)Count)];
            DescriptorEntryDesc current = Entries[checked((int)entry)];
            Entries[checked((int)entry)] = value;
        }

        internal DescriptorEntryDesc GetEntry(uint entry)
        {
            DescriptorEntryDesc value = Entries![checked((int)entry)];
            return value;
        }
    }

    private sealed class DescriptorPublisher : IDisposable
    {
        private readonly D3D12Device _device;
        private readonly DescriptorTableType? _restrictedType;
        private readonly uint _nodeMask;
        private readonly object _gate = new();
        private readonly Dictionary<uint, DescriptorRecord> _resourceWrites = [];
        private readonly Dictionary<uint, DescriptorRecord> _samplerWrites = [];
        private readonly Dictionary<GraphicsObject, DescriptorRange> _owners = new(ReferenceEqualityComparer.Instance);
        private DescriptorRecord? [] _resources = [];
        private DescriptorRecord? [] _samplers = [];
        private DescriptorGeneration _current;
        private DescriptorRange? _freeResources;
        private DescriptorRange? _freeSamplers;
        private DescriptorRange? _activationHead;
        private DescriptorRange? _activationTail;
        private DescriptorRange? _retirementHead;
        private DescriptorRange? _retirementTail;
        private uint _nextResource;
        private uint _nextSampler;
        private ulong _nextAllocationIdentity = 1;
        private ulong _nextGeneration = 2;
        private bool _disposed;
        internal DescriptorPublisher(
            D3D12Device device,
            DescriptorTableType? restrictedType = null,
            uint initialCapacity = 0,
            uint nodeMask = 0
        )
        {
            _device = device;
            _restrictedType = restrictedType;
            _nodeMask = nodeMask == 0 ? device.PrimaryNodeMask : nodeMask;
            if (restrictedType is DescriptorTableType.Resource)
                _resourceWrites.EnsureCapacity(checked((int)Math.Min(initialCapacity, int.MaxValue)));
            else if (restrictedType is DescriptorTableType.Sampler)
                _samplerWrites.EnsureCapacity(checked((int)Math.Min(initialCapacity, int.MaxValue)));
            _current = DescriptorGeneration.Create(device, _nodeMask, 1, 1, 1, [], []);
        }

        internal DescriptorGeneration CaptureCurrent()
        {
            while (true)
            {
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
                ref DescriptorRange? free = ref type == DescriptorTableType.Resource ? ref _freeResources : ref _freeSamplers;
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
                        reused = new DescriptorRange(type, candidate.First, count, identity: 0);
                        candidate.First = checked(candidate.First + count);
                        candidate.Count -= count;
                    }

                    ResetReserved(reused, AllocateIdentity());
                    return reused;
                }

                ref uint next = ref type == DescriptorTableType.Resource ? ref _nextResource : ref _nextSampler;
                uint capacity = type == DescriptorTableType.Resource ? _device.Capabilities.Limits.ResourceDescriptorCapacity : _device.Capabilities.Limits.SamplerDescriptorCapacity;
                if (next > capacity || count > capacity - next)
                {
                    throw new GraphicsException(GraphicsError.OutOfDescriptors, $"The D3D12 {type} logical descriptor index space is exhausted.");
                }

                DescriptorRange result = new(type, next, count, AllocateIdentity());
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
                RemoveActivation(range);
                RemoveRange(GetSubmission(range.Type), range.First, range.Count);
                Recycle(range);
            }
        }

        internal void StageDescriptor(
            DescriptorRange range,
            DescriptorAllocation source,
            GraphicsObject sourceObject,
            ResourceBindingType type
        )
        {
            lock (_gate)
            {
                DescriptorRecord record = DescriptorRecord.Create(source, sourceObject, type, GetDescriptorEntryDesc(sourceObject, type));
                bool ownerAdded = false;
                bool recordTransferred = false;
                try
                {
                    _owners.EnsureCapacity(checked(_owners.Count + 1));
                    range.SetEntry(0, GetDescriptorEntryDesc(sourceObject, type));
                    _owners.Add(sourceObject, range);
                    ownerAdded = true;
                    ReplaceSubmission(range.Type, range.First, record);
                    recordTransferred = true;
                    range.State = DescriptorRangeState.Active;
                    QueueActivation(range);
                }
                catch
                {
                    if (ownerAdded)
                        _owners.Remove(sourceObject);
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
                Dictionary<uint, DescriptorRecord> descriptorChanges = GetSubmission(range.Type);
                descriptorChanges.EnsureCapacity(checked(descriptorChanges.Count + checked((int)range.Count)));
                try
                {
                    for (uint entry = 0; entry < range.Count; entry++)
                    {
                        range.SetEntry(entry, table.GetEntryDesc(entry));
                        ReplaceSubmission(range.Type, checked(range.First + entry), DescriptorRecord.CreateNull(table.GetEntryDesc(entry)));
                    }

                    range.State = DescriptorRangeState.Active;
                    QueueActivation(range);
                }
                catch
                {
                    RemoveRange(descriptorChanges, range.First, range.Count);
                    throw;
                }
            }
        }

        internal void StageBinding(DescriptorTableType type, uint index, in DescriptorEntryDesc entry, in ResourceBinding binding)
        {
            lock (_gate)
            {
                DescriptorRecord record = CreateBindingRecord(binding, entry);
                ReplaceSubmission(type, index, record);
            }
        }

        internal void NotifyDisposed(GraphicsObject sourceObject)
        {
            lock (_gate)
            {
                if (!_owners.Remove(sourceObject, out DescriptorRange? range))
                    return;
                Retire(range);
            }
        }

        internal void DisposeTable(D3D12DescriptorTable table)
        {
            lock (_gate)
                Retire(table.Range);
        }

        internal DescriptorGeneration Publish(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _device.ThrowIfUnavailable();
                cancellationToken.ThrowIfCancellationRequested();
                StageRetirementTombstones();
                if (_resourceWrites.Count == 0 && _samplerWrites.Count == 0)
                {
                    _current.Retain();
                    return _current;
                }

                if (_nextGeneration == ulong.MaxValue)
                {
                    throw new GraphicsException(GraphicsError.OutOfDescriptors, "The descriptor-generation identity domain is exhausted.");
                }

                DescriptorRecord? [] resources = GrowCopy(_resources, _nextResource);
                DescriptorRecord? [] samplers = GrowCopy(_samplers, _nextSampler);
                ApplyChanges(resources, _resourceWrites);
                ApplyChanges(samplers, _samplerWrites);
                cancellationToken.ThrowIfCancellationRequested();
                DescriptorGeneration candidate = DescriptorGeneration.Create(_device, _nodeMask, _nextGeneration, Math.Max(1u, _nextResource), Math.Max(1u, _nextSampler), resources, samplers);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch
                {
                    candidate.Release();
                    throw;
                }

                DescriptorGeneration previous = _current;
                CommitArray(ref _resources, resources, _resourceWrites);
                CommitArray(ref _samplers, samplers, _samplerWrites);
                Volatile.Write(ref _current, candidate);
                _nextGeneration++;
                MarkActivationsPublished();
                CommitRetirements();
                candidate.Retain();
                previous.Release();
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
                foreach (DescriptorRecord record in _resourceWrites.Values)
                    record.Release();
                foreach (DescriptorRecord record in _samplerWrites.Values)
                    record.Release();
                foreach (DescriptorRecord? record in _resources)
                    record?.Release();
                foreach (DescriptorRecord? record in _samplers)
                    record?.Release();
                _resourceWrites.Clear();
                _samplerWrites.Clear();
                _owners.Clear();
                _resources = [];
                _samplers = [];
                _current.Release();
                _freeResources = null;
                _freeSamplers = null;
                _activationHead = null;
                _activationTail = null;
                _retirementHead = null;
                _retirementTail = null;
            }
        }

        private Dictionary<uint, DescriptorRecord> GetSubmission(DescriptorTableType type) => type == DescriptorTableType.Resource ? _resourceWrites : _samplerWrites;
        private static void ResetReserved(DescriptorRange range, ulong identity)
        {
            range.Identity = identity;
            range.State = DescriptorRangeState.Reserved;
            range.HasPublishedContent = false;
            range.ActivationQueued = false;
            range.Next = null;
            range.ActivationNext = null;
            range.Entries = null;
        }

        private ulong AllocateIdentity()
        {
            return _nextAllocationIdentity++;
        }

        private void Activate(DescriptorRange? range)
        {
            if (range is null)
                return;
            if (range.State == DescriptorRangeState.Reserved)
                range.State = DescriptorRangeState.Active;
            QueueActivation(range);
        }

        private void QueueActivation(DescriptorRange range)
        {
            if (range.HasPublishedContent || range.ActivationQueued)
                return;
            range.ActivationQueued = true;
            range.ActivationNext = null;
            if (_activationTail is null)
                _activationHead = range;
            else
                _activationTail.ActivationNext = range;
            _activationTail = range;
        }

        private void RemoveActivation(DescriptorRange range)
        {
            if (!range.ActivationQueued)
                return;
            DescriptorRange? previous = null;
            DescriptorRange? current = _activationHead;
            while (current is not null && !ReferenceEquals(current, range))
            {
                previous = current;
                current = current.ActivationNext;
            }

            if (previous is null)
                _activationHead = range.ActivationNext;
            else
                previous.ActivationNext = range.ActivationNext;
            if (ReferenceEquals(_activationTail, current))
                _activationTail = previous;
            range.ActivationQueued = false;
            range.ActivationNext = null;
        }

        private void Retire(DescriptorRange? range)
        {
            if (range is null || range.State == DescriptorRangeState.Free)
                return;
            if (range.State is not (DescriptorRangeState.Reserved or DescriptorRangeState.Active))
                return;
            RemoveActivation(range);
            RemoveRange(GetSubmission(range.Type), range.First, range.Count);
            if (!range.HasPublishedContent)
            {
                Recycle(range);
                return;
            }

            range.State = DescriptorRangeState.QueueRetirement;
            range.Next = null;
            if (_retirementTail is null)
                _retirementHead = range;
            else
                _retirementTail.Next = range;
            _retirementTail = range;
        }

        private void StageRetirementTombstones()
        {
            int resourceCount = 0;
            int samplerCount = 0;
            for (DescriptorRange? range = _retirementHead; range is not null; range = range.Next)
            {
                if (range.Type == DescriptorTableType.Resource)
                    resourceCount = checked(resourceCount + checked((int)range.Count));
                else
                    samplerCount = checked(samplerCount + checked((int)range.Count));
            }

            _resourceWrites.EnsureCapacity(checked(_resourceWrites.Count + resourceCount));
            _samplerWrites.EnsureCapacity(checked(_samplerWrites.Count + samplerCount));
            for (DescriptorRange? range = _retirementHead; range is not null; range = range.Next)
            {
                for (uint entry = 0; entry < range.Count; entry++)
                {
                    ReplaceSubmission(range.Type, checked(range.First + entry), DescriptorRecord.CreateTombstone(range.GetEntry(entry)));
                }
            }
        }

        private void MarkActivationsPublished()
        {
            DescriptorRange? current = _activationHead;
            while (current is not null)
            {
                DescriptorRange? next = current.ActivationNext;
                current.HasPublishedContent = true;
                current.ActivationQueued = false;
                current.ActivationNext = null;
                current = next;
            }

            _activationHead = null;
            _activationTail = null;
        }

        private void CommitRetirements()
        {
            DescriptorRange? current = _retirementHead;
            _retirementHead = null;
            _retirementTail = null;
            while (current is not null)
            {
                DescriptorRange? next = current.Next;
                current.Next = null;
                Recycle(current);
                current = next;
            }
        }

        private void Recycle(DescriptorRange range)
        {
            range.State = DescriptorRangeState.Free;
            range.HasPublishedContent = false;
            range.ActivationQueued = false;
            range.ActivationNext = null;
            range.Entries = null;
            ref DescriptorRange? head = ref range.Type == DescriptorTableType.Resource ? ref _freeResources : ref _freeSamplers;
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

        private void ReplaceSubmission(DescriptorTableType type, uint index, DescriptorRecord record)
        {
            Dictionary<uint, DescriptorRecord> descriptorChanges = GetSubmission(type);
            try
            {
                if (!descriptorChanges.ContainsKey(index))
                    descriptorChanges.EnsureCapacity(checked(descriptorChanges.Count + 1));
                descriptorChanges.TryGetValue(index, out DescriptorRecord? previous);
                descriptorChanges[index] = record;
                previous?.Release();
            }
            catch
            {
                record.Release();
                throw;
            }
        }

        private DescriptorRecord CreateBindingRecord(in ResourceBinding binding) => CreateBindingRecord(binding, binding.Type);
        internal static DescriptorEntryDesc GetDescriptorEntryDesc(GraphicsObject sourceObject, ResourceBindingType type) => sourceObject switch
        {
            BufferCbv or Buffer => new DescriptorEntryDesc(ResourceBindingType.ConstantBuffer),
            BufferSrv value => new DescriptorEntryDesc(ResourceBindingType.BufferSrv, value.Description.Format, value.Description.StructureStride),
            BufferUav value => new DescriptorEntryDesc(
                ResourceBindingType.BufferUav,
                value.Description.Format,
                value.Description.StructureStride,
                HasCounter: value.Description.CounterBuffer is not null
            ),
            TextureSrv value => new DescriptorEntryDesc(
                ResourceBindingType.TextureSrv,
                value.Description.Format,
                TextureDimension: value.Description.Dimension,
                Aspects: value.Description.Range.Aspects
            ),
            TextureUav value => new DescriptorEntryDesc(
                ResourceBindingType.TextureUav,
                value.Description.Format,
                TextureDimension: value.Description.Dimension,
                Aspects: value.Description.Range.Aspects
            ),
            Sampler => new DescriptorEntryDesc(ResourceBindingType.Sampler),
            AccelerationStructureSrv => new DescriptorEntryDesc(ResourceBindingType.AccelerationStructure),
            _ => new DescriptorEntryDesc(type),
        };
        private DescriptorRecord CreateBindingRecord(in ResourceBinding binding, in DescriptorEntryDesc entry)
        {
            if (binding.Value is null)
                return DescriptorRecord.CreateNull(entry);
            return DescriptorRecord.Create(GetBindingDescriptor(binding), binding.Value, entry.Type, entry);
        }

        private DescriptorRecord CreateBindingRecord(in ResourceBinding binding, ResourceBindingType expectedType) => CreateBindingRecord(binding, new DescriptorEntryDesc(expectedType));
        private static void RemoveRange(Dictionary<uint, DescriptorRecord> descriptorChanges, uint first, uint count)
        {
            for (uint offset = 0; offset < count; offset++)
            {
                if (descriptorChanges.Remove(checked(first + offset), out DescriptorRecord? record))
                    record.Release();
            }
        }

        private static DescriptorRecord? [] GrowCopy(DescriptorRecord? [] source, uint count)
        {
            int length = checked((int)count);
            DescriptorRecord? [] result = new DescriptorRecord? [length];
            source.AsSpan().CopyTo(result);
            return result;
        }

        private static void ApplyChanges(DescriptorRecord? [] destination, Dictionary<uint, DescriptorRecord> descriptorChanges)
        {
            foreach ((uint index, DescriptorRecord record)in descriptorChanges)
                destination[checked((int)index)] = record;
        }

        private static void CommitArray(
            ref DescriptorRecord? [] committed,
            DescriptorRecord? [] candidate,
            Dictionary<uint, DescriptorRecord> descriptorChanges
        )
        {
            foreach (uint index in descriptorChanges.Keys)
            {
                if (index < committed.Length)
                    committed[checked((int)index)]?.Release();
            }

            committed = candidate;
            descriptorChanges.Clear();
        }
    }

    private sealed class DescriptorRecord
    {
        private DescriptorAllocation? _source;
        private NativeResourceData? _resource;
        private NativeResourceData? _secondaryResource;
        private ResourceReferences _primaryReferences;
        private ResourceReferences _secondaryReferences;
        private GraphicsObject? _sourceObject;
        private int _references = 1;
        private DescriptorRecord(
            in DescriptorEntryDesc entry,
            uint visibleNodeMask = uint.MaxValue,
            bool allowDummySampler = false
        )
        {
            Entry = entry;
            VisibleNodeMask = visibleNodeMask;
            AllowDummySampler = allowDummySampler;
        }

        internal DescriptorEntryDesc Entry { get; }
        internal ResourceBindingType Type => Entry.Type;
        internal bool AllowDummySampler { get; }
        internal uint VisibleNodeMask { get; }
        internal DescriptorAllocation? Source => _source;
        internal GraphicsObject? SourceObject => _sourceObject;
        internal NativeResourceData? Resource => _resource;

        internal static DescriptorRecord CreateNull(ResourceBindingType type) => CreateNull(new DescriptorEntryDesc(type));
        internal static DescriptorRecord CreateNull(in DescriptorEntryDesc entry)
        {
            return new DescriptorRecord(entry);
        }

        internal static DescriptorRecord CreateTombstone(in DescriptorEntryDesc entry)
        {
            return new DescriptorRecord(entry, allowDummySampler: true);
        }

        internal static DescriptorRecord Create(
            DescriptorAllocation source,
            GraphicsObject sourceObject,
            ResourceBindingType type,
            in DescriptorEntryDesc entry
        )
        {
            DescriptorRecord result = new(entry, visibleNodeMask: GetVisibility(sourceObject));
            var(primary, secondary) = GetResourceReferences(sourceObject);
            NativeResourceData? resource = primary.Lifetime;
            NativeResourceData? secondaryResource = secondary.Lifetime;
            source = source.Copy();
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

            result._primaryReferences = primary;
            result._secondaryReferences = secondary;
            result._source = source;
            result._sourceObject = sourceObject;
            result._resource = resource;
            result._secondaryResource = secondaryResource;
            return result;
        }

        internal void Retain()
        {
            int current = Volatile.Read(ref _references);
            while (current > 0)
            {
                int exchanged = Interlocked.CompareExchange(ref _references, checked(current + 1), current);
                if (exchanged == current)
                    return;
                current = exchanged;
            }

            return;
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0)
                return;
            _secondaryResource?.Release();
            _secondaryResource = null;
            _resource?.Release();
            _resource = null;
            Interlocked.Exchange(ref _source, null)?.Release();
            _primaryReferences = default;
            _secondaryReferences = default;
            _sourceObject = null;
        }

        private static uint GetVisibility(GraphicsObject value)
        {
            var(primary, secondary) = GetResourceReferences(value);
            return (primary.Lifetime is null ? uint.MaxValue : primary.VisibleNodeMask) & (secondary.Lifetime is null ? uint.MaxValue : secondary.VisibleNodeMask);
        }
    }

    private sealed class DescriptorGeneration
    {
        private readonly DescriptorRecord[] _records;
        private readonly DescriptorRecord? [] _resourceRecords;
        private readonly DescriptorRecord? [] _samplerRecords;
        private ID3D12DescriptorHeap* _resources;
        private ID3D12DescriptorHeap* _samplers;
        private int _references = 1;
        private DescriptorGeneration(
            ulong identity,
            uint resourceCount,
            uint samplerCount,
            ID3D12DescriptorHeap* resources,
            ID3D12DescriptorHeap* samplers,
            DescriptorRecord[] records,
            DescriptorRecord? [] resourceRecords,
            DescriptorRecord? [] samplerRecords
        )
        {
            Identity = identity;
            ResourceCount = resourceCount;
            SamplerCount = samplerCount;
            _resources = resources;
            _samplers = samplers;
            ResourceGpuStart = resources is null ? default : ResourceTrace.GetGPUDescriptorHandleForHeapStart(resources);
            SamplerGpuStart = samplers is null ? default : ResourceTrace.GetGPUDescriptorHandleForHeapStart(samplers);
            _records = records;
            // Publishing replaces these arrays; it never mutates an installed array.
            // Source records remain retained by this generation until GPU retirement.
            _resourceRecords = resourceRecords;
            _samplerRecords = samplerRecords;
        }

        internal ulong Identity { get; }
        internal uint ResourceCount { get; }
        internal uint SamplerCount { get; }
        internal GpuDescriptorHandle ResourceGpuStart { get; }
        internal GpuDescriptorHandle SamplerGpuStart { get; }
        internal ID3D12DescriptorHeap* ResourceHeap => _resources;
        internal ID3D12DescriptorHeap* SamplerHeap => _samplers;

        internal static DescriptorGeneration Create(
            D3D12Device device,
            uint nodeMask,
            ulong identity,
            uint resourceCount,
            uint samplerCount,
            DescriptorRecord? [] resources,
            DescriptorRecord? [] samplers
        )
        {
            int retainedCapacity = checked(CountRetainedRecords(resources) + CountRetainedRecords(samplers));
            DescriptorRecord[] retained = new DescriptorRecord[retainedCapacity];
            int retainedCount = 0;
            ID3D12DescriptorHeap* resourceHeap = null;
            ID3D12DescriptorHeap* samplerHeap = null;
            try
            {
                resourceHeap = CreateHeap(device, DescriptorHeapType.CbvSrvUav, resourceCount, nodeMask);
                samplerHeap = CreateHeap(device, DescriptorHeapType.Sampler, samplerCount, nodeMask);
                CopyRecords(device, resourceHeap, DescriptorHeapType.CbvSrvUav, resources, retained, ref retainedCount);
                CopyRecords(device, samplerHeap, DescriptorHeapType.Sampler, samplers, retained, ref retainedCount);
                return new DescriptorGeneration(identity, resourceCount, samplerCount, resourceHeap, samplerHeap, retained, resources, samplers);
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
                int exchanged = Interlocked.CompareExchange(ref _references, checked(current + 1), current);
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
            return;
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
        }

        internal void CopyTo(
            D3D12Device device,
            ID3D12DescriptorHeap* resourceDestination,
            ID3D12DescriptorHeap* samplerDestination
        )
        {
            CopyResourceTo(device, resourceDestination);
            CopySamplerTo(device, samplerDestination);
        }

        internal void CopyResourceTo(D3D12Device device, ID3D12DescriptorHeap* destination)
        {
            int unused = 0;
            CopyRecords(device, destination, DescriptorHeapType.CbvSrvUav, _resourceRecords, null, ref unused);
        }

        internal void CopySamplerTo(D3D12Device device, ID3D12DescriptorHeap* destination)
        {
            int unused = 0;
            CopyRecords(device, destination, DescriptorHeapType.Sampler, _samplerRecords, null, ref unused);
        }

        private static ID3D12DescriptorHeap* CreateHeap(D3D12Device device, DescriptorHeapType type, uint count, uint nodeMask)
        {
            DescriptorHeapDesc desc = new(type, count, DescriptorHeapFlags.ShaderVisible, nodeMask);
            ID3D12DescriptorHeap* heap = null;
            Guid iid = ID3D12DescriptorHeap.Guid;
            ThrowIfFailed(
                device,
                DeviceResourceTrace.CreateDescriptorHeap(device.Native, &desc, &iid, (void**)&heap),
                NativeOperationType.Ordinary,
                "ID3D12Device::CreateDescriptorHeap"
            );
            SetNativeName(heap, $"Published {type} Descriptor Heap (count={count}, nodeMask=0x{nodeMask:X})");
            return heap;
        }

        private static void ReleaseHeap(ID3D12DescriptorHeap* heap)
        {
            if (heap is null)
                return;
            _ = ResourceTrace.Release(heap);
        }

        private static int CountRetainedRecords(DescriptorRecord? [] records)
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
            DescriptorRecord? [] records,
            DescriptorRecord[]? retained,
            ref int retainedCount
        )
        {
            CpuDescriptorHandle destination = ResourceTrace.GetCPUDescriptorHandleForHeapStart(heap);
            uint increment = device.DescriptorIncrement(type);
            for (int index = 0; index < records.Length; index++)
            {
                DescriptorRecord? record = records[index];
                CpuDescriptorHandle target = new(destination.Ptr + checked((nuint)((uint)index * increment)));
                if (record?.Source is DescriptorAllocation source)
                {
                    if (retained is not null)
                    {
                        record.Retain();
                        retained[retainedCount++] = record;
                    }

                    DeviceQueryTrace.CopyDescriptorsSimple(device.Native, 1, target, source.Cpu, type);
                    continue;
                }

                WriteTypedNullDescriptor(
                    device,
                    record?.Entry ?? (type == DescriptorHeapType.Sampler ? new DescriptorEntryDesc(ResourceBindingType.Sampler) : new DescriptorEntryDesc(ResourceBindingType.TextureSrv, Format.R8G8B8A8UNorm, TextureDimension: TextureViewDimension.Texture2D)),
                    target,
                    allowDummySampler: record is null || record.AllowDummySampler
                );
            }
        }
    }

    private static void WriteTypedNullDescriptor(D3D12Device device, ResourceBindingType type, CpuDescriptorHandle destination) => WriteTypedNullDescriptor(device, new DescriptorEntryDesc(type), destination, allowDummySampler: true);
    private static void WriteTypedNullDescriptor(
        D3D12Device device,
        in DescriptorEntryDesc entry,
        CpuDescriptorHandle destination,
        bool allowDummySampler = false
    )
    {
        switch (entry.Type)
        {
            case ResourceBindingType.ConstantBuffer:
                DeviceResourceTrace.CreateConstantBufferView(device.Native, null, destination);
                return;
            case ResourceBindingType.BufferSrv:
                WriteNullBufferSrv(device, entry, destination);
                return;
            case ResourceBindingType.BufferUav:
                WriteNullBufferUav(device, entry, destination);
                return;
            case ResourceBindingType.TextureUav:
                WriteNullTextureUav(device, entry, destination);
                return;
            case ResourceBindingType.AccelerationStructure:
                WriteNullAccelerationStructure(device, destination);
                return;
            case ResourceBindingType.Sampler:

                WriteDummySampler(device, destination);
                return;
            case ResourceBindingType.None:
            case ResourceBindingType.TextureSrv:
                WriteNullTextureSrv(device, entry, destination);
                return;
            default:
                return;
        }
    }

    private static void WriteNullBufferSrv(D3D12Device device, in DescriptorEntryDesc entry, CpuDescriptorHandle destination)
    {
        GetNullBufferShape(entry, out Silk.NET.DXGI.Format format, out uint stride, out BufferSrvFlags flags);
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
        DeviceResourceTrace.CreateShaderResourceView(device.Native, null, &native, destination);
    }

    private static void WriteNullBufferUav(D3D12Device device, in DescriptorEntryDesc entry, CpuDescriptorHandle destination)
    {
        GetNullBufferShape(entry, out Silk.NET.DXGI.Format format, out uint stride, out BufferSrvFlags srvFlags);
        UnorderedAccessViewDesc native = new()
        {
            Format = format,
            ViewDimension = UavDimension.Buffer,
        };
        native.Buffer = new Silk.NET.Direct3D12.BufferUav
        {
            NumElements = 1,
            StructureByteStride = stride,
            Flags = srvFlags == BufferSrvFlags.Raw ? BufferUavFlags.Raw : BufferUavFlags.None,
        };
        DeviceResourceTrace.CreateUnorderedAccessView(device.Native, null, null, &native, destination);
    }

    private static void WriteNullTextureUav(D3D12Device device, in DescriptorEntryDesc entry, CpuDescriptorHandle destination)
    {
        TextureViewDimension dimension = entry.TextureDimension!.Value;
        Format format = entry.Format!.Value;
        UnorderedAccessViewDesc native = new()
        {
            Format = FormatMappings.ToDxgi(format),
            ViewDimension = ToUavDimension(dimension),
        };
        InitializeNullUav(ref native, dimension);
        DeviceResourceTrace.CreateUnorderedAccessView(device.Native, null, null, &native, destination);
    }

    private static void WriteNullAccelerationStructure(D3D12Device device, CpuDescriptorHandle destination)
    {
        ShaderResourceViewDesc native = new()
        {
            Format = Silk.NET.DXGI.Format.FormatUnknown,
            ViewDimension = SrvDimension.RaytracingAccelerationStructure,
            Shader4ComponentMapping = 5768,
        };
        native.RaytracingAccelerationStructure = new RaytracingAccelerationStructureSrv(0);
        DeviceResourceTrace.CreateShaderResourceView(device.Native, null, &native, destination);
    }

    private static void WriteDummySampler(D3D12Device device, CpuDescriptorHandle destination)
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
        DeviceResourceTrace.CreateSampler(device.Native, &sampler, destination);
    }

    private static void WriteNullTextureSrv(D3D12Device device, in DescriptorEntryDesc entry, CpuDescriptorHandle destination)
    {
        TextureViewDimension dimension = entry.TextureDimension ?? TextureViewDimension.Texture2D;
        Format format = entry.Format ?? Format.R8G8B8A8UNorm;
        ShaderResourceViewDesc native = new()
        {
            Format = FormatMappings.ToShaderViewFormat(format, entry.Aspects),
            ViewDimension = ToSrvDimension(dimension),
            Shader4ComponentMapping = 5768,
        };
        InitializeNullSrv(ref native, dimension);
        DeviceResourceTrace.CreateShaderResourceView(device.Native, null, &native, destination);
    }

    private static void GetNullBufferShape(
        in DescriptorEntryDesc entry,
        out Silk.NET.DXGI.Format format,
        out uint stride,
        out BufferSrvFlags flags
    )
    {
        if (entry.Format is Format typed)
        {
            format = FormatMappings.ToDxgi(typed);
            stride = 0;
            flags = BufferSrvFlags.None;
            return;
        }

        if (entry.StructureStride != 0)
        {
            format = Silk.NET.DXGI.Format.FormatUnknown;
            stride = entry.StructureStride;
            flags = BufferSrvFlags.None;
            return;
        }

        format = Silk.NET.DXGI.Format.FormatR32Typeless;
        stride = 0;
        flags = BufferSrvFlags.Raw;
    }

    private static void InitializeNullSrv(ref ShaderResourceViewDesc native, TextureViewDimension dimension)
    {
        switch (dimension)
        {
            case TextureViewDimension.Texture1D:
                native.Texture1D = new Tex1DSrv
                {
                    MipLevels = 1
                };
                break;
            case TextureViewDimension.Texture1DArray:
                native.Texture1DArray = new Tex1DArraySrv
                {
                    MipLevels = 1,
                    ArraySize = 1
                };
                break;
            case TextureViewDimension.Texture2D:
                native.Texture2D = new Tex2DSrv
                {
                    MipLevels = 1
                };
                break;
            case TextureViewDimension.Texture2DArray:
                native.Texture2DArray = new Tex2DArraySrv
                {
                    MipLevels = 1,
                    ArraySize = 1
                };
                break;
            case TextureViewDimension.Texture2DMultisampled:
                break;
            case TextureViewDimension.Texture2DMultisampledArray:
                native.Texture2DMSArray = new Tex2DmsArraySrv
                {
                    ArraySize = 1
                };
                break;
            case TextureViewDimension.Cube:
                native.TextureCube = new TexcubeSrv
                {
                    MipLevels = 1
                };
                break;
            case TextureViewDimension.CubeArray:
                native.TextureCubeArray = new TexcubeArraySrv
                {
                    MipLevels = 1,
                    NumCubes = 1
                };
                break;
            case TextureViewDimension.Texture3D:
                native.Texture3D = new Tex3DSrv
                {
                    MipLevels = 1
                };
                break;
            default:
                return;
        }
    }

    private static void InitializeNullUav(ref UnorderedAccessViewDesc native, TextureViewDimension dimension)
    {
        switch (dimension)
        {
            case TextureViewDimension.Texture1D:
                native.Texture1D = new Tex1DUav();
                break;
            case TextureViewDimension.Texture1DArray:
                native.Texture1DArray = new Tex1DArrayUav
                {
                    ArraySize = 1
                };
                break;
            case TextureViewDimension.Texture2D:
                native.Texture2D = new Tex2DUav();
                break;
            case TextureViewDimension.Texture2DArray:
            case TextureViewDimension.Cube:
            case TextureViewDimension.CubeArray:
                native.ViewDimension = UavDimension.Texture2Darray;
                native.Texture2DArray = new Tex2DArrayUav
                {
                    ArraySize = 1
                };
                break;
            case TextureViewDimension.Texture3D:
                native.Texture3D = new Tex3DUav
                {
                    WSize = 1
                };
                break;
            case TextureViewDimension.Texture2DMultisampled:
            case TextureViewDimension.Texture2DMultisampledArray:
                return;
            default:
                return;
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
            ReadOnlySpan<DescriptorEntryDesc> entries,
            string? label
        ) : base(device, type, nodeIndex, entries, label)
        {
            _device = device;
            Publisher = publisher;
            Range = range;
        }

        internal D3D12Device NativeDevice => _device;
        internal DescriptorPublisher Publisher { get; }
        internal DescriptorRange Range { get; }
        internal uint FirstIndex => Range.First;

        internal void CheckEntry(uint entry)
        {
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
        private SlangShaderData? _slangCode;
        private readonly NativeResourceData _pipelineState;
        private readonly object _gate = new();
        private D3D12PersistentParameterData? _current;
        private ulong _nextVersion = 1;
        internal D3D12PersistentParameterBindings(
            D3D12Device device,
            D3D12Pipeline pipeline,
            NativeParameterBinding nativeLayout,
            VariableLayoutReflection layout,
            string? label
        ) : base(device, layout, label)
        {
            _device = device;
            Pipeline = pipeline;
            NativeLayout = nativeLayout;
            OrdinaryConstantBufferRootParameter = nativeLayout.ResourceTable is null && nativeLayout.SamplerTable is null && nativeLayout.OrdinaryRoot is { UsesRootConstants: false } ordinary ? ordinary.RootParameterIndex : null;
            SlangShaderData? slangCode = null;
            NativeResourceData? pipelineState = null;
            try
            {
                slangCode = pipeline.GetSlangCodeReference();
                pipelineState = pipeline.RetainNativeState();
                _slangCode = slangCode;
                slangCode = null;
                _pipelineState = pipelineState.Value;
            }
            catch
            {
                pipelineState?.Release();
                throw;
            }
        }

        internal D3D12Device NativeDevice => _device;
        internal D3D12Pipeline Pipeline { get; }
        internal NativeParameterBinding NativeLayout { get; }
        internal uint? OrdinaryConstantBufferRootParameter { get; }
        internal D3D12PersistentParameterData? CurrentData {[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => Volatile.Read(ref _current); }

        internal void StageReplacement(in ParameterBlockBindings bindings)
        {
            lock (_gate)
            {
                if (_nextVersion == ulong.MaxValue)
                {
                    throw new GraphicsException(GraphicsError.OutOfDescriptors, "The persistent-parameter version domain is exhausted.");
                }

                D3D12PersistentParameterData candidate = D3D12PersistentParameterData.Create(
                    _device,
                    _nextVersion,
                    bindings.Resources,
                    NativeLayout.Entries,
                    bindings.OrdinaryData,
                    NativeLayout.OrdinaryRoot is { UsesRootConstants: false }
                );
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
            _ = Interlocked.Exchange(ref _slangCode, null);
            _device.UnregisterChild(this);
        }
    }

    private static partial class RequireD3D12
    {
        internal static D3D12DescriptorTable DescriptorTable(DescriptorTable value) => (D3D12DescriptorTable)value;
        internal static D3D12PersistentParameterBindings PersistentParameterBindings(PersistentParameterBindings value) => (D3D12PersistentParameterBindings)value;
    }
}
