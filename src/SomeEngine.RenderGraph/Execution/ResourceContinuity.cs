namespace SomeEngine.RenderGraph;

internal readonly record struct PreparedResource(
    ImportedBuffer Buffer,
    ImportedTexture Texture,
    ulong Generation,
    long ExportTicket);

internal sealed class ResourceContinuity : IDisposable
{
    private readonly IDevice _device;
    private readonly Dictionary<ContinuityKey, ContinuityEntry> _entries = new();
    private readonly Dictionary<ContinuityKey, ulong> _generations = new();
    private readonly Dictionary<long, PendingExport> _pendingExports = new();
    private readonly Dictionary<long, ContinuityAttempt> _attempts = new();
    private long _nextExportTicket;
    private bool _disposed;

    public ResourceContinuity(IDevice device) => _device = device;

    public PreparedResource Prepare(in MutableResource resource, long frameIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_attempts.TryGetValue(frameIndex, out ContinuityAttempt? attempt))
        {
            attempt = new ContinuityAttempt();
            _attempts.Add(frameIndex, attempt);
        }
        if (resource.Exported) return PrepareExport(resource, attempt);

        ContinuityKey key = ContinuityKey.Create(resource);
        if (!attempt.Entries.TryGetValue(key, out ContinuityEntry? entry))
        {
            if (!_entries.TryGetValue(key, out entry) || !entry.Matches(resource))
            {
                entry = CreateEntry(key, resource);
                attempt.Candidates.Add(key, entry);
            }
            attempt.Entries.Add(key, entry);
        }

        int slot = PositiveModulo(frameIndex - resource.HistoryOffset, entry.Slots.Length);
        ContinuitySlot value = entry.Slots[slot];
        GpuCompletion[] readiness = Normalize(value.Completions);
        return resource.Kind == ResourceNodeKind.Buffer
            ? PrepareBuffer(value, readiness, entry.Generation)
            : PrepareTexture(value, readiness, entry.Generation);
    }

    private PreparedResource PrepareBuffer(
        ContinuitySlot slot,
        GpuCompletion[] readiness,
        ulong generation)
    {
        BufferHandle handle = slot.Buffer;
        ImportedBuffer buffer = new(
            handle,
            _device.GetBufferMetadata(handle),
            default,
            default,
            slot.ContentsAvailable,
            readiness,
            ResourceState.Common,
            ResourceState.Common);
        return new PreparedResource(buffer, default, generation, 0);
    }

    private PreparedResource PrepareTexture(
        ContinuitySlot slot,
        GpuCompletion[] readiness,
        ulong generation)
    {
        TextureHandle handle = slot.Texture;
        ImportedTexture texture = new(
            handle,
            _device.GetTextureMetadata(handle),
            default,
            default,
            slot.ContentsAvailable,
            readiness,
            ResourceState.Common,
            ResourceState.Common);
        return new PreparedResource(default, texture, generation, 0);
    }

    public void Complete(
        FrozenGraph graph,
        CompiledGraph compiled,
        long frameIndex,
        GpuCompletion[] completions)
    {
        if (_attempts.Remove(frameIndex, out ContinuityAttempt? attempt))
        {
            foreach ((ContinuityKey key, ContinuityEntry candidate) in attempt.Candidates)
            {
                if (_entries.Remove(key, out ContinuityEntry? previous)) Destroy(previous);
                _entries.Add(key, candidate);
                _generations[key] = candidate.Generation;
            }
        }

        GpuCompletion[] normalized = Normalize(completions);
        for (int resource = 0; resource < graph.Resources.Length; resource++)
        {
            FrozenResource value = graph.Resources[resource];
            if (value.Exported)
            {
                if (_pendingExports.TryGetValue(value.ExportTicket, out PendingExport? export))
                    export.Completion = normalized;
                continue;
            }
            if (value.Lifetime == ResourceLifetime.Transient || !compiled.LiveResources[resource]) continue;
            ContinuityKey key = ContinuityKey.Create(value);
            ContinuityEntry entry = _entries[key];
            int slot = PositiveModulo(frameIndex - value.HistoryOffset, entry.Slots.Length);
            ContinuitySlot physical = entry.Slots[slot];
            physical.Completions = Merge(physical.Completions, normalized);
            if (value.HistoryOffset == 0 && Writes(compiled, graph, resource)) physical.ContentsAvailable = true;
        }
    }

    public void Abort(long frameIndex)
    {
        if (!_attempts.Remove(frameIndex, out ContinuityAttempt? attempt)) return;
        foreach (ContinuityEntry candidate in attempt.Candidates.Values) Destroy(candidate);
        foreach (long ticket in attempt.ExportTickets) DestroyPendingExport(ticket);
    }

    public ResourceExport[] TransferExports(ReadOnlySpan<long> tickets)
    {
        // Publication is one ownership transaction. Validate every ticket and completion before
        // removing anything, so a caller can safely poll a multi-resource export without ending
        // up owning only an arbitrary prefix.
        PendingExport[] pending = new PendingExport[tickets.Length];
        for (int index = 0; index < tickets.Length; index++)
        {
            long ticket = tickets[index];
            if (!_pendingExports.TryGetValue(ticket, out PendingExport? export))
                throw new InvalidOperationException("The resource export is no longer owned by this graph execution.");
            foreach (GpuCompletion completion in export.Completion)
            {
                if (_device.GetCompletedValue(completion.Queue) < completion.Value)
                    throw new InvalidOperationException("Resource exports are published only after every producing GPU completion has finished.");
            }
            pending[index] = export;
        }

        ResourceExport[] result = new ResourceExport[tickets.Length];
        for (int index = 0; index < tickets.Length; index++)
        {
            long ticket = tickets[index];
            PendingExport export = pending[index];
            _pendingExports.Remove(ticket);
            result[index] = new ResourceExport(
                export.Buffer,
                export.Texture,
                ResourceState.Common,
                export.Completion.Length == 0 ? GpuCompletionSet.Empty : new GpuCompletionSet(export.Completion));
        }
        return result;
    }

    public void ResetHistory()
    {
        foreach (ContinuityKey key in _entries.Where(static pair => pair.Value.Lifetime == ResourceLifetime.Temporal).Select(static pair => pair.Key).ToArray())
            RemoveEntry(key);
    }

    public void ResetHistory(Guid stableId)
    {
        if (stableId == Guid.Empty) throw new ArgumentException("A stable temporal identity is required.", nameof(stableId));
        foreach (ContinuityKey key in _entries.Keys.Where(key => key.StableId == stableId).ToArray()) RemoveEntry(key);
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (long frameIndex in _attempts.Keys.ToArray()) Abort(frameIndex);
        foreach (ContinuityEntry entry in _entries.Values) Destroy(entry);
        _entries.Clear();
        foreach (long ticket in _pendingExports.Keys.ToArray()) DestroyPendingExport(ticket);
        _disposed = true;
    }

    private PreparedResource PrepareExport(in MutableResource resource, ContinuityAttempt attempt)
    {
        long ticket = checked(++_nextExportTicket);
        attempt.ExportTickets.Add(ticket);
        if (resource.Kind == ResourceNodeKind.Buffer)
        {
            BufferHandle buffer = _device.CreateBuffer(resource.BufferDesc);
            _pendingExports.Add(ticket, new PendingExport(buffer, default));
            return new PreparedResource(
                new ImportedBuffer(
                    buffer,
                    _device.GetBufferMetadata(buffer),
                    default,
                    default,
                    false,
                    [],
                    ResourceState.Common,
                    ResourceState.Common),
                default,
                0,
                ticket);
        }

        TextureHandle texture = _device.CreateTexture(resource.TextureDesc);
        _pendingExports.Add(ticket, new PendingExport(default, texture));
        return new PreparedResource(
            default,
            new ImportedTexture(
                texture,
                _device.GetTextureMetadata(texture),
                default,
                default,
                false,
                [],
                ResourceState.Common,
                ResourceState.Common),
            0,
            ticket);
    }

    private ContinuityEntry CreateEntry(ContinuityKey key, in MutableResource resource)
    {
        ulong generation = _generations.TryGetValue(key, out ulong current) ? checked(current + 1) : 1;
        _generations[key] = generation;
        int count = resource.Lifetime == ResourceLifetime.Temporal ? checked(resource.HistoryCount + 1) : 1;
        ContinuitySlot[] slots = new ContinuitySlot[count];
        for (int index = 0; index < count; index++)
        {
            slots[index] = resource.Kind == ResourceNodeKind.Buffer
                ? new ContinuitySlot(_device.CreateBuffer(resource.BufferDesc), default)
                : new ContinuitySlot(default, _device.CreateTexture(resource.TextureDesc));
        }
        return new ContinuityEntry(resource, generation, slots);
    }

    private void RemoveEntry(ContinuityKey key)
    {
        if (!_entries.Remove(key, out ContinuityEntry? entry)) return;
        Destroy(entry);
    }

    private void Destroy(ContinuityEntry entry)
    {
        foreach (ContinuitySlot slot in entry.Slots)
        {
            if (slot.Buffer.IsValid) _device.DestroyBuffer(slot.Buffer);
            if (slot.Texture.IsValid) _device.DestroyTexture(slot.Texture);
        }
    }

    private void DestroyPendingExport(long ticket)
    {
        if (!_pendingExports.Remove(ticket, out PendingExport? export)) return;
        if (export.Buffer.IsValid) _device.DestroyBuffer(export.Buffer);
        if (export.Texture.IsValid) _device.DestroyTexture(export.Texture);
    }

    private static bool Writes(CompiledGraph compiled, FrozenGraph graph, int resource) =>
        compiled.ActivePassOrdinals.Any(pass => graph.Passes[pass].Accesses.Any(
            access => access.Resource == resource && access.Effect != ResourceEffect.Read));

    private static GpuCompletion[] Normalize(IEnumerable<GpuCompletion> completions) => completions
        .Where(static completion => completion.IsValid)
        .GroupBy(static completion => completion.Queue)
        .Select(static values => values.MaxBy(static completion => completion.Value))
        .OrderBy(static completion => completion.Queue)
        .ToArray();

    private static GpuCompletion[] Merge(GpuCompletion[] current, GpuCompletion[] next) =>
        Normalize(current.Concat(next));

    private static int PositiveModulo(long value, int modulus)
    {
        long result = value % modulus;
        return checked((int)(result < 0 ? result + modulus : result));
    }

    private readonly record struct ContinuityKey(ResourceNodeKind Kind, Guid StableId, int ImplicitOrdinal)
    {
        public static ContinuityKey Create(in MutableResource resource) =>
            new(resource.Kind, resource.StableId, resource.StableId == Guid.Empty ? resource.BaseOrdinal : -1);

        public static ContinuityKey Create(in FrozenResource resource) =>
            new(resource.Kind, resource.StableId, resource.StableId == Guid.Empty ? resource.BaseOrdinal : -1);
    }

    private sealed class ContinuityEntry
    {
        public ContinuityEntry(in MutableResource resource, ulong generation, ContinuitySlot[] slots)
        {
            Kind = resource.Kind;
            BufferDesc = resource.BufferDesc;
            TextureDesc = resource.TextureDesc;
            Lifetime = resource.Lifetime;
            HistoryCount = resource.HistoryCount;
            Generation = generation;
            Slots = slots;
        }

        public ResourceNodeKind Kind { get; }
        public BufferDesc BufferDesc { get; }
        public TextureDesc TextureDesc { get; }
        public ResourceLifetime Lifetime { get; }
        public int HistoryCount { get; }
        public ulong Generation { get; }
        public ContinuitySlot[] Slots { get; }

        public bool Matches(in MutableResource resource) =>
            Kind == resource.Kind && Lifetime == resource.Lifetime && HistoryCount == resource.HistoryCount &&
            (Kind == ResourceNodeKind.Buffer ? BufferDesc == resource.BufferDesc : TextureDesc == resource.TextureDesc);
    }

    private sealed class ContinuitySlot
    {
        public ContinuitySlot(BufferHandle buffer, TextureHandle texture)
        {
            Buffer = buffer;
            Texture = texture;
        }

        public BufferHandle Buffer { get; }
        public TextureHandle Texture { get; }
        public bool ContentsAvailable { get; set; }
        public GpuCompletion[] Completions { get; set; } = [];
    }

    private sealed class PendingExport
    {
        public PendingExport(BufferHandle buffer, TextureHandle texture)
        {
            Buffer = buffer;
            Texture = texture;
        }

        public BufferHandle Buffer { get; }
        public TextureHandle Texture { get; }
        public GpuCompletion[] Completion { get; set; } = [];
    }

    private sealed class ContinuityAttempt
    {
        public Dictionary<ContinuityKey, ContinuityEntry> Entries { get; } = new();
        public Dictionary<ContinuityKey, ContinuityEntry> Candidates { get; } = new();
        public List<long> ExportTickets { get; } = [];
    }
}
