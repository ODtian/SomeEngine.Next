using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Resources.Geometry;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Core.Collections;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Render.Cluster;

internal readonly record struct PageRetirement(uint PageID, uint Offset, uint Size);

internal readonly record struct RegisteredMeshPages(
    Mesh Mesh,
    uint FirstPageId,
    int PageCount,
    MeshPayloadSource Source,
    bool OwnsSource);

internal sealed class PreparedMeshPages
{
    internal PreparedMeshPages(
        Mesh mesh,
        uint firstPageId,
        int registrationIndex,
        MeshPayloadSource source,
        bool ownsSource,
        LinkedListNode<uint>[] lruNodes,
        int pageCount,
        uint[] lowestDetailPageIds)
    {
        Mesh = mesh;
        FirstPageId = firstPageId;
        RegistrationIndex = registrationIndex;
        Source = source;
        OwnsSource = ownsSource;
        LruNodes = lruNodes;
        PageCount = pageCount;
        LowestDetailPageIds = lowestDetailPageIds;
    }

    internal Mesh Mesh { get; }
    internal uint FirstPageId { get; }
    internal int RegistrationIndex { get; }
    internal MeshPayloadSource Source { get; }
    internal bool OwnsSource { get; }
    internal LinkedListNode<uint>[] LruNodes { get; }
    internal int PageCount { get; }
    internal uint[] LowestDetailPageIds { get; }
}

internal readonly record struct PreparedMeshPagesDisposal(
    ResidencyReservation[] GpuReservations,
    MeshPayloadSource?[] OwnedSources);

internal readonly struct MeshPageReadSource
{
    private readonly MeshPayloadSource _source;
    private readonly int _pageIndex;

    internal MeshPageReadSource(MeshPayloadSource source, int pageIndex)
    {
        _source = source;
        _pageIndex = pageIndex;
    }

    internal MeshPayloadPage Descriptor => _source.Pages[_pageIndex];

    internal bool MatchesStreamedDescriptor(in MeshPayloadPage page)
    {
        MeshPayloadPage expected = Descriptor;
        return page.Size == expected.Size &&
               page.ClusterCount == expected.ClusterCount &&
               page.VertexEncodingHash == expected.VertexEncodingHash;
    }

    internal async ValueTask ReadIntoAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        await _source
            .ReadPageIntoAsync(_pageIndex, destination, cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed class MeshPages
{
    private readonly FlatDictionary<Mesh, int> _registrationByMesh = new();
    private readonly List<RegisteredMeshPages> _registrations = [];
    private readonly List<int> _pageRegistrationIndices = [];
    private readonly FlatDictionary<uint, ResidencyReservation> _gpuReservations = new();
    private readonly HashSet<uint> _residentPages = new();
    private readonly HashSet<uint> _pendingLoads = new();
    private readonly HashSet<uint> _pendingEvictions = new();
    private readonly HashSet<uint> _lowestDetailPageIds = new();
    private readonly List<uint> _offsets = [];
    private readonly List<LinkedListNode<uint>?> _lruNodes = [];
    private readonly LinkedList<uint> _residentLru = new();
    private uint _registeredPages;

    public uint Count => (uint)_offsets.Count;
    public uint RegisteredCount => _registeredPages;
    public uint ResidentCount => (uint)_residentPages.Count;
    public uint MissingCount => RegisteredCount - ResidentCount;
    public int PendingLoadCount => _pendingLoads.Count;
    public int PendingEvictionCount => _pendingEvictions.Count;

    public PreparedMeshPages PrepareStreamedRegistration(
        Mesh mesh,
        MeshPayloadSource streamedSource,
        bool ownsSource = true)
    {
        ArgumentNullException.ThrowIfNull(streamedSource);
        IReadOnlyList<MeshPayloadPage> payloadPages = streamedSource.Pages;
        EnsureCanRegister(mesh, payloadPages.Count);
        uint firstPageId = Count;
        var lruNodes = new LinkedListNode<uint>[payloadPages.Count];
        var lowestDetailPageIds = new uint[streamedSource.LowestDetailPageIndices.Length];
        long pageRegionLength = checked(streamedSource.Length - streamedSource.BvhLength);
        long expectedOffset = 0;
        for (int index = 0; index < payloadPages.Count; index++)
        {
            MeshPayloadPage payloadPage = payloadPages[index];
            if (payloadPage.Size <= 0 ||
                payloadPage.Offset != expectedOffset ||
                checked(payloadPage.Offset + payloadPage.Size) > pageRegionLength ||
                payloadPage.ClusterCount == 0 ||
                payloadPage.VertexEncodingHash == 0)
            {
                throw new InvalidDataException($"Streamed mesh page {index} has invalid registration metadata.");
            }

            uint pageId = checked(firstPageId + (uint)index);
            lruNodes[index] = new LinkedListNode<uint>(pageId);
            expectedOffset = checked(payloadPage.Offset + payloadPage.Size);
        }
        for (int index = 0; index < lowestDetailPageIds.Length; index++)
        {
            uint localPage = streamedSource.LowestDetailPageIndices.Span[index];
            if (localPage >= payloadPages.Count)
                throw new InvalidDataException("A lowest-detail mesh page index is outside the payload.");
            lowestDetailPageIds[index] = checked(firstPageId + localPage);
        }

        if (expectedOffset != pageRegionLength)
            throw new InvalidDataException("Streamed mesh page descriptors do not exactly cover the payload page region.");

        return new PreparedMeshPages(
            mesh,
            firstPageId,
            _registrations.Count,
            streamedSource,
            ownsSource,
            lruNodes,
            payloadPages.Count,
            lowestDetailPageIds);
    }

    public void ReserveRegistration(PreparedMeshPages registration)
    {
        ValidatePreparedRegistration(registration);
        int pageCount = registration.PageCount;
        int requiredPageCount = checked(_offsets.Count + pageCount);
        _registrationByMesh.EnsureCapacity(checked(_registrationByMesh.Count + 1));
        _registrations.EnsureCapacity(checked(_registrations.Count + 1));
        _pageRegistrationIndices.EnsureCapacity(requiredPageCount);
        _offsets.EnsureCapacity(requiredPageCount);
        _lruNodes.EnsureCapacity(requiredPageCount);
        _residentPages.EnsureCapacity(requiredPageCount);
        _lowestDetailPageIds.EnsureCapacity(checked(
            _lowestDetailPageIds.Count + registration.LowestDetailPageIds.Length));
    }

    public void CommitRegistration(PreparedMeshPages registration)
    {
        _registrationByMesh.Add(registration.Mesh, registration.RegistrationIndex);
        _registrations.Add(new RegisteredMeshPages(
            registration.Mesh,
            registration.FirstPageId,
            registration.PageCount,
            registration.Source,
            registration.OwnsSource));

        for (int index = 0; index < registration.PageCount; index++)
        {
            _offsets.Add(0);
            _lruNodes.Add(registration.LruNodes[index]);
            _pageRegistrationIndices.Add(registration.RegistrationIndex);
        }
        foreach (uint pageId in registration.LowestDetailPageIds)
            _lowestDetailPageIds.Add(pageId);
        _registeredPages += checked((uint)registration.PageCount);
    }

    public bool TryRegistration(
        Mesh mesh,
        out uint firstPageId,
        out uint pageCount)
    {
        if (_registrationByMesh.TryGetValue(mesh, out int index))
        {
            RegisteredMeshPages registration = _registrations[index];
            firstPageId = registration.FirstPageId;
            pageCount = checked((uint)registration.PageCount);
            return true;
        }
        firstPageId = 0;
        pageCount = 0;
        return false;
    }

    private void EnsureCanRegister(Mesh mesh, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (_registrationByMesh.ContainsKey(mesh))
            throw new InvalidOperationException($"Mesh '{mesh}' is already registered in this cluster epoch.");
        if (pageCount <= 0)
            throw new InvalidDataException("A cluster mesh must contain at least one page.");
        _ = checked(_offsets.Count + pageCount);
    }

    private void ValidatePreparedRegistration(PreparedMeshPages registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.FirstPageId != Count ||
            registration.RegistrationIndex != _registrations.Count ||
            registration.PageCount == 0 ||
            registration.PageCount != registration.Source.Pages.Count ||
            registration.PageCount != registration.LruNodes.Length ||
            registration.LowestDetailPageIds.Length == 0 ||
            _registrationByMesh.ContainsKey(registration.Mesh))
        {
            throw new InvalidOperationException("The prepared mesh-page registration is stale or malformed.");
        }

        for (int index = 0; index < registration.PageCount; index++)
        {
            uint pageId = checked(registration.FirstPageId + (uint)index);
            if (registration.LruNodes[index].Value != pageId ||
                registration.LruNodes[index].List is not null)
            {
                throw new InvalidOperationException("The prepared mesh-page registration is stale or malformed.");
            }
        }
    }

    public bool IsResident(uint pageId)
        => _residentPages.Contains(pageId);

    public bool IsLoadPending(uint pageId)
        => _pendingLoads.Contains(pageId);

    public bool IsRetiring(uint pageId)
        => _pendingEvictions.Contains(pageId);

    public bool IsEvictionPending(uint pageId)
        => _pendingEvictions.Contains(pageId);

    public bool TryOffset(uint pageId, out uint offset)
    {
        if (pageId >= _offsets.Count || !_residentPages.Contains(pageId))
        {
            offset = 0;
            return false;
        }

        offset = _offsets[(int)pageId];
        return true;
    }

    public bool TrySource(uint pageId, out uint size, out MeshPageReadSource source)
    {
        if (pageId >= _pageRegistrationIndices.Count || _pageRegistrationIndices[(int)pageId] < 0)
        {
            size = 0;
            source = default;
            return false;
        }

        RegisteredMeshPages registration = _registrations[_pageRegistrationIndices[(int)pageId]];
        int localPageIndex = checked((int)(pageId - registration.FirstPageId));
        MeshPayloadPage descriptor = registration.Source.Pages[localPageIndex];
        size = checked((uint)descriptor.Size);
        source = new MeshPageReadSource(registration.Source, localPageIndex);
        return true;
    }

    internal bool IsRemoved(uint pageId) =>
        pageId < _pageRegistrationIndices.Count && _pageRegistrationIndices[(int)pageId] < 0;

    internal RegisteredMeshPages RemoveMesh(Mesh mesh)
    {
        int index = _registrationByMesh[mesh];
        RegisteredMeshPages registration = _registrations[index];
        _registrationByMesh.Remove(mesh);
        _registrations[index] = default;
        _registeredPages -= checked((uint)registration.PageCount);
        for (uint page = registration.FirstPageId; page < registration.FirstPageId + registration.PageCount; page++)
        {
            _pageRegistrationIndices[(int)page] = -1;
            _lowestDetailPageIds.Remove(page);
        }
        return registration;
    }

    internal ResidencyReservation? RemovePage(uint pageId, out bool uploadReady)
    {
        uploadReady = _pendingLoads.Remove(pageId);
        _residentPages.Remove(pageId);
        _pendingEvictions.Remove(pageId);
        Remove(pageId);
        _lruNodes[(int)pageId] = null;
        _offsets[(int)pageId] = 0;
        _gpuReservations.TryGetValue(pageId, out ResidencyReservation? reservation);
        _gpuReservations.Remove(pageId);
        return reservation;
    }

    public void StageResident(
        uint pageId,
        uint offset,
        ResidencyReservation gpuReservation)
    {
        Ensure(pageId);
        ArgumentNullException.ThrowIfNull(gpuReservation);
        if (gpuReservation.ResidencyClass != ResidencyClass.Gpu)
            throw new ArgumentException("A resident mesh page requires a GPU reservation.", nameof(gpuReservation));
        if (_residentPages.Contains(pageId) || IsLoadPending(pageId))
            throw new InvalidOperationException($"Mesh page {pageId} is already resident or awaiting publication.");
        if (_gpuReservations.ContainsKey(pageId))
            throw new InvalidOperationException($"Mesh page {pageId} already owns a GPU reservation.");

        _pendingLoads.EnsureCapacity(checked(_pendingLoads.Count + 1));
        _gpuReservations.EnsureCapacity(checked(_gpuReservations.Count + 1));
        if (!_pendingLoads.Add(pageId))
            throw new InvalidOperationException($"Mesh page {pageId} could not enter the pending-load state.");
        try
        {
            _gpuReservations[pageId] = gpuReservation;
            _offsets[(int)pageId] = offset;
        }
        catch
        {
            _pendingLoads.Remove(pageId);
            _gpuReservations.Remove(pageId);
            throw;
        }
    }

    public ResidencyReservation CancelStagedResident(uint pageId, ResidencyReservation expectedReservation)
    {
        ArgumentNullException.ThrowIfNull(expectedReservation);
        if (!_pendingLoads.Contains(pageId) ||
            !_gpuReservations.TryGetValue(pageId, out ResidencyReservation? reservation) ||
            !ReferenceEquals(reservation, expectedReservation))
        {
            throw new InvalidOperationException($"Mesh page {pageId} does not own the expected staged GPU reservation.");
        }

        _pendingLoads.Remove(pageId);
        _gpuReservations.Remove(pageId);
        _offsets[(int)pageId] = 0;
        return reservation;
    }

    public bool StageEviction(uint pageId, out PageRetirement retirement)
    {
        if (_lowestDetailPageIds.Contains(pageId) ||
            !_residentPages.Contains(pageId) ||
            _pendingEvictions.Contains(pageId))
        {
            retirement = default;
            return false;
        }

        uint offset = _offsets[(int)pageId];

        _pendingEvictions.EnsureCapacity(checked(_pendingEvictions.Count + 1));
        if (!TrySource(pageId, out uint size, out _))
            throw new InvalidOperationException($"Mesh page {pageId} has no canonical source descriptor.");
        retirement = new PageRetirement(pageId, offset, size);
        _pendingEvictions.Add(pageId);
        Remove(pageId);
        return true;
    }

    public bool TryCancelPendingEviction(uint pageId, out uint offset)
    {
        if (!_pendingEvictions.Contains(pageId))
        {
            offset = 0;
            return false;
        }

        offset = _offsets[(int)pageId];
        _pendingEvictions.Remove(pageId);
        Touch(pageId);
        return true;
    }

    public uint[] PrepareLoads()
    {
        if (_pendingLoads.Count == 0)
            return [];

        var pages = new uint[_pendingLoads.Count];
        _pendingLoads.CopyTo(pages);
        Array.Sort(pages);
        _residentPages.EnsureCapacity(checked(_residentPages.Count + pages.Length));
        return pages;
    }

    public PageRetirement[] PrepareEvictions()
    {
        if (_pendingEvictions.Count == 0)
            return [];

        var retirements = new PageRetirement[_pendingEvictions.Count];
        int index = 0;
        foreach (uint pageId in _pendingEvictions)
        {
            if (!TrySource(pageId, out uint size, out _))
                throw new InvalidOperationException($"Mesh page {pageId} has no canonical source descriptor.");
            retirements[index++] = new PageRetirement(
                pageId,
                _offsets[checked((int)pageId)],
                size);
        }
        Array.Sort(
            retirements,
            static (left, right) => left.PageID.CompareTo(right.PageID));
        return retirements;
    }

    public void PublishLoad(uint pageId)
    {
        if (!_pendingLoads.Contains(pageId) ||
            _residentPages.Contains(pageId) ||
            !_gpuReservations.ContainsKey(pageId))
        {
            throw new InvalidOperationException($"Mesh page {pageId} is not ready for residency publication.");
        }

        _pendingLoads.Remove(pageId);
        _residentPages.Add(pageId);
        Touch(pageId);
    }

    public ResidencyReservation PublishEviction(uint pageId)
    {
        if (!_pendingEvictions.Contains(pageId) ||
            !_residentPages.Contains(pageId) ||
            !_gpuReservations.TryGetValue(pageId, out ResidencyReservation? gpuReservation))
        {
            throw new InvalidOperationException($"Mesh page {pageId} has no GPU reservation to release.");
        }

        _pendingEvictions.Remove(pageId);
        _residentPages.Remove(pageId);
        _gpuReservations.Remove(pageId);

        _offsets[(int)pageId] = 0;
        Remove(pageId);
        return gpuReservation;
    }

    public void ValidatePublication(
        ReadOnlySpan<uint> residentPages,
        ReadOnlySpan<PageRetirement> retirements)
    {
        foreach (uint pageId in residentPages)
        {
            if (!_pendingLoads.Contains(pageId) ||
                _residentPages.Contains(pageId) ||
                !_gpuReservations.ContainsKey(pageId))
            {
                throw new InvalidOperationException(
                    $"Mesh page {pageId} is not ready to complete residency publication.");
            }
        }

        foreach (PageRetirement retirement in retirements)
        {
            if (!_pendingEvictions.Contains(retirement.PageID) ||
                !_residentPages.Contains(retirement.PageID) ||
                !_gpuReservations.ContainsKey(retirement.PageID) ||
                _offsets[checked((int)retirement.PageID)] != retirement.Offset ||
                !TrySource(retirement.PageID, out uint size, out _) ||
                size != retirement.Size)
            {
                throw new InvalidOperationException(
                    $"Mesh page {retirement.PageID} is not ready to complete eviction publication.");
            }
        }
    }

    public void Touch(uint pageId)
    {
        if (!_residentPages.Contains(pageId) ||
            _pendingEvictions.Contains(pageId))
            return;

        LinkedListNode<uint> node = _lruNodes[checked((int)pageId)]!;
        if (node.List is not null)
        {
            _residentLru.Remove(node);
            _residentLru.AddLast(node);
            return;
        }

        _residentLru.AddLast(node);
    }

    public bool TryVictim(uint protectedPageId, out uint pageId)
    {
        LinkedListNode<uint>? node = _residentLru.First;
        while (node != null)
        {
            pageId = node.Value;
            if (pageId != protectedPageId && !_lowestDetailPageIds.Contains(pageId))
                return true;

            node = node.Next;
        }

        pageId = 0;
        return false;
    }

    public PreparedMeshPagesDisposal PrepareDisposal()
    {
        var gpuReservations = new ResidencyReservation[_gpuReservations.Count];
        int gpuIndex = 0;
        foreach (KeyValuePair<uint, ResidencyReservation> entry in _gpuReservations)
            gpuReservations[gpuIndex++] = entry.Value;

        var ownedSources = new MeshPayloadSource?[_registrations.Count];
        for (int index = 0; index < _registrations.Count; index++)
        {
            if (_registrations[index].OwnsSource)
                ownedSources[index] = _registrations[index].Source;
        }

        return new PreparedMeshPagesDisposal(
            gpuReservations,
            ownedSources);
    }

    public void CommitDisposal(in PreparedMeshPagesDisposal prepared)
    {
        if (prepared.GpuReservations is null ||
            prepared.OwnedSources is null ||
            prepared.GpuReservations.Length != _gpuReservations.Count ||
            prepared.OwnedSources.Length != _registrations.Count)
        {
            throw new InvalidOperationException("The prepared mesh-page disposal is stale.");
        }

        _gpuReservations.Clear();
        _registrationByMesh.Clear();
        _registrations.Clear();
        _pageRegistrationIndices.Clear();
        _residentPages.Clear();
        _lowestDetailPageIds.Clear();
        _pendingLoads.Clear();
        _pendingEvictions.Clear();
        _residentLru.Clear();
        _offsets.Clear();
        _lruNodes.Clear();
        _registeredPages = 0;
    }

    private void Ensure(uint pageId)
    {
        if (pageId >= _offsets.Count)
            throw new ArgumentOutOfRangeException(nameof(pageId), pageId, "Mesh page id is outside the registered page range.");
    }

    private void Remove(uint pageId)
    {
        LinkedListNode<uint>? node = _lruNodes[checked((int)pageId)];
        if (node?.List is null)
            return;

        _residentLru.Remove(node);
    }
}


