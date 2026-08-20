using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using NativeResource = Silk.NET.Direct3D12.ID3D12Resource;
using NativeResourceDesc = Silk.NET.Direct3D12.ResourceDesc;
using NativeResourceDimension = Silk.NET.Direct3D12.ResourceDimension;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    private const ulong SparseTileSize = 64 * 1024;

    public Buffer CreateReservedBuffer(Device device, in BufferDesc desc)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        _ = nativeDevice.RequireCapability<SparseResources>(nameof(CreateReservedBuffer));
        if (desc.Size == 0)
            throw new ArgumentOutOfRangeException(nameof(desc), "A reserved Buffer must have a nonzero size.");
        RequireDefaultReservedResourcePlacement(desc.NodePlacement, nameof(desc));

        NativeResourceDesc nativeDescription = CreateBufferDescription(desc);
        NativeResource* native = CreateReservedResource(
            nativeDevice,
            nativeDescription,
            ReadOnlySpan<DxgiFormat>.Empty);
        D3D12SparseState? sparse = null;
        D3D12Buffer? result = null;
        try
        {
            sparse = QuerySparseState(nativeDevice, native, 1);
            (PipelineSync sync, ResourceAccess access) = InitialBufferAccess(MemoryType.DeviceLocal);
            result = new D3D12Buffer(
                nativeDevice,
                heap: null,
                native,
                new BufferInfo(
                    desc.Size,
                    desc.Usages,
                    MemoryType.DeviceLocal,
                    0,
                    checked(sparse.Info.TotalTileCount * SparseTileSize),
                    0,
                    nativeDevice.EnabledNodeMask),
                sync,
                access,
                desc.Label)
            {
                SparseState = sparse,
            };
            sparse = null;
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            if (result is null)
            {
                sparse?.Dispose();
                _ = native->Release();
            }
            else
            {
                result.Dispose();
            }
            throw;
        }
    }

    public Texture CreateReservedTexture(Device device, in TextureDesc desc)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        SparseResources capability =
            nativeDevice.RequireCapability<SparseResources>(nameof(CreateReservedTexture));
        RequireDefaultReservedResourcePlacement(desc.NodePlacement, nameof(desc));
        ValidateReservedTextureSupport(nativeDevice, capability, desc);
        NativeResourceDesc nativeDescription = CreateTextureDescription(desc);
        nativeDescription.Layout =
            Silk.NET.Direct3D12.TextureLayout.Layout64KBUndefinedSwizzle;
        DxgiFormat[] castableFormats = CreateCastableFormats(desc);
        NativeResource* native = CreateReservedResource(
            nativeDevice,
            nativeDescription,
            castableFormats);
        D3D12SparseState? sparse = null;
        D3D12Texture? result = null;
        try
        {
            uint arrayLayers = desc.Dimension == TextureDimension.Texture3D
                ? 1u
                : desc.ArrayLayerCount;
            uint subresourceCount = checked(
                desc.MipLevelCount * arrayLayers * FormatMappings.PlaneCount(desc.Format));
            sparse = QuerySparseState(
                nativeDevice,
                native,
                subresourceCount);
            result = new D3D12Texture(
                nativeDevice,
                heap: null,
                native,
                CreateTextureInfo(
                    desc,
                    0,
                    checked(sparse.Info.TotalTileCount * SparseTileSize),
                    0,
                    nativeDevice.EnabledNodeMask),
                desc.Label)
            {
                SparseState = sparse,
            };
            sparse = null;
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            if (result is null)
            {
                sparse?.Dispose();
                _ = native->Release();
            }
            else
            {
                result.Dispose();
            }
            throw;
        }
    }

    private static void ValidateReservedTextureSupport(
        D3D12Device device,
        SparseResources capability,
        in TextureDesc description)
    {
        ReadOnlySpan<Format> formats = description.Dimension switch
        {
            TextureDimension.Texture2D => capability.SupportedTexture2DFormats,
            TextureDimension.Texture3D => capability.SupportedTexture3DFormats,
            TextureDimension.Texture1D => throw new NotSupportedException(
                "D3D12 reserved Texture1D resources are unavailable."),
            _ => throw new ArgumentOutOfRangeException(nameof(description)),
        };
        if (!formats.Contains(description.Format))
        {
            throw new NotSupportedException(
                $"D3D12 tiled resources do not support {description.Dimension} {description.Format}.");
        }

        FormatSupport support = device.Capabilities.GetFormatSupport(description.Format);
        if (!support.SupportsSparseSampleCount(description.SampleCount))
        {
            throw new NotSupportedException(
                $"D3D12 tiled resources do not support {description.SampleCount}x " +
                $"multisampling for {description.Format}.");
        }
    }

    public SparseResourceInfo GetSparseResourceInfo(Resource resource)
    {
        RequireBackendOwner(resource, nameof(resource));
        _ = RequireDevice(resource.Device, nameof(resource))
            .RequireCapability<SparseResources>(nameof(GetSparseResourceInfo));
        return GetSparseState(resource).Info;
    }

    public QueueCompletion UpdateSparseMappings(
        Queue queue,
        ReadOnlySpan<SparseMappingDesc> mappings)
    {
        D3D12Queue nativeQueue = RequireQueue(queue, nameof(queue));
        SparseResources capability = nativeQueue.NativeDevice.RequireCapability<SparseResources>(
            nameof(UpdateSparseMappings));
        if ((uint)mappings.Length > capability.MaximumMappingsPerCall)
            throw new ArgumentOutOfRangeException(nameof(mappings));

        CapabilityWorkspace workspace = nativeQueue.AcquireCapabilityWorkspace();
        try
        {
            PreparedSparseMapping[] prepared = PrepareSparseMappings(
                nativeQueue,
                mappings,
                workspace);
            return SubmitSparseMappings(nativeQueue, prepared, mappings.Length, ref workspace);
        }
        catch
        {
            workspace?.Dispose();
            throw;
        }
    }

    private PreparedSparseMapping[] PrepareSparseMappings(
        D3D12Queue nativeQueue,
        ReadOnlySpan<SparseMappingDesc> mappings,
        CapabilityWorkspace workspace)
    {
        PreparedSparseMapping[] prepared = workspace.PrepareMappings(mappings.Length);
        for (int index = 0; index < mappings.Length; index++)
        {
            ref readonly SparseMappingDesc mapping = ref mappings[index];
            D3D12SparseState state = GetSparseState(mapping.Resource);
            EnsureSameDevice(nativeQueue.NativeDevice, mapping.Resource.Device, nameof(mappings));
            SparseLogicalRegion logicalRegion = state.PrepareRegion(mapping.ResourceTiles);
            D3D12Heap? heap = ResolveSparseMappingHeap(nativeQueue, mapping, out TileRangeFlags flags, out uint heapOffset);
            prepared[index] = new PreparedSparseMapping(
                state,
                GetNativeResource(mapping.Resource),
                heap,
                logicalRegion,
                ToNativeCoordinate(mapping.ResourceTiles.Start),
                ToNativeRegion(mapping.ResourceTiles),
                flags,
                heapOffset,
                mapping.ResourceTiles.TileCount);
            workspace.RetainForSubmission(GetNativeLifetime(mapping.Resource), mapping.Resource);
            if (heap is not null)
                workspace.RetainForSubmission(heap.NativeLifetime, heap);
        }
        return prepared;
    }

    private D3D12Heap? ResolveSparseMappingHeap(
        D3D12Queue nativeQueue,
        in SparseMappingDesc mapping,
        out TileRangeFlags nativeFlags,
        out uint heapOffset)
    {
        heapOffset = 0;
        switch (mapping.Type)
        {
            case SparseMappingType.Mapped:
            case SparseMappingType.Reused:
                if (mapping.Heap is null)
                    throw new ArgumentException("A mapped sparse range requires a Heap.", "mappings");
                D3D12Heap heap = RequireHeap(mapping.Heap);
                EnsureSameDevice(nativeQueue.NativeDevice, heap.Device, "mappings");
                heap.ThrowIfDisposed();
                if (heap.Info.MemoryType != MemoryType.DeviceLocal)
                    throw new ArgumentException("Sparse mappings require a DeviceLocal Heap.", "mappings");
                ValidateSparseHeapCompatibility(mapping.Resource, heap, "mappings");
                if (mapping.HeapTileOffset > uint.MaxValue)
                    throw new ArgumentOutOfRangeException("mappings");
                heapOffset = (uint)mapping.HeapTileOffset;
                ulong consumedTiles = mapping.Type == SparseMappingType.Reused
                    ? 1UL
                    : mapping.ResourceTiles.TileCount;
                ulong heapTiles = heap.Info.Size / SparseTileSize;
                if (mapping.HeapTileOffset > heapTiles ||
                    consumedTiles > heapTiles - mapping.HeapTileOffset)
                {
                    throw new ArgumentOutOfRangeException(
                        "mappings",
                        "The sparse mapping exceeds its Heap tile range.");
                }
                nativeFlags = mapping.Type == SparseMappingType.Reused
                    ? TileRangeFlags.ReuseSingleTile
                    : TileRangeFlags.None;
                return heap;

            case SparseMappingType.Unmapped:
                if (mapping.Heap is not null || mapping.HeapTileOffset != 0)
                {
                    throw new ArgumentException(
                        "An unmapped sparse range cannot name a Heap or Heap offset.",
                        "mappings");
                }
                nativeFlags = TileRangeFlags.Null;
                return null;

            default:
                throw new ArgumentOutOfRangeException("mappings");
        }
    }

    private QueueCompletion SubmitSparseMappings(
        D3D12Queue nativeQueue,
        PreparedSparseMapping[] prepared,
        int mappingCount,
        ref CapabilityWorkspace workspace)
    {
        bool accepted = false;
        List<(D3D12SparseState State, SparseMappingGeneration Generation)>? generations = null;
        try
        {
            using (nativeQueue.Gate.EnterScope())
            {
                nativeQueue.Device.ThrowIfUnavailable();
                generations = workspace.CreateUpdateGenerations(prepared.AsSpan(0, mappingCount));
                for (int index = 0; index < mappingCount; index++)
                {
                    IssueSparseMapping(nativeQueue, prepared[index]);
                    accepted = true;
                }
                CommitSparseGenerations(generations);
                generations = null;
                QueueCompletion completion = nativeQueue.SignalCompletionUnderGate();
                nativeQueue.RegisterCapabilityPayloadUnderGate(completion.Value, workspace);
                workspace = null!;
                return completion;
            }
        }
        catch (Exception exception)
        {
            ReleaseUncommittedGenerations(generations);
            if (!accepted)
            {
                workspace.Dispose();
                throw;
            }
            nativeQueue.RegisterUntrustedCapabilityPayload(workspace);
            workspace = null!;
            GraphicsException loss = CreateAcceptedOperationLoss(
                "A sparse mapping update was accepted but its completion could not be established.",
                exception);
            loss = nativeQueue.NativeDevice.MarkLost(loss);
            throw loss;
        }
    }

    private static void IssueSparseMapping(
        D3D12Queue nativeQueue,
        in PreparedSparseMapping mapping)
    {
        ID3D12Heap* nativeHeap = mapping.Heap is null ? null : mapping.Heap.Native;
        TiledResourceCoordinate coordinate = mapping.Coordinate;
        TileRegionSize region = mapping.Region;
        TileRangeFlags rangeFlags = mapping.RangeFlags;
        uint heapOffset = mapping.HeapOffset;
        uint tileCount = mapping.TileCount;
        nativeQueue.Native->UpdateTileMappings(
            mapping.Resource,
            1,
            &coordinate,
            &region,
            nativeHeap,
            1,
            &rangeFlags,
            &heapOffset,
            &tileCount,
            TileMappingFlags.None);
    }

    public QueueCompletion CopySparseMappings(
        Queue queue,
        ReadOnlySpan<SparseMappingCopyDesc> copies)
    {
        D3D12Queue nativeQueue = RequireQueue(queue, nameof(queue));
        SparseResources capability = nativeQueue.NativeDevice.RequireCapability<SparseResources>(
            nameof(CopySparseMappings));
        if ((uint)copies.Length > capability.MaximumMappingsPerCall)
            throw new ArgumentOutOfRangeException(nameof(copies));

        CapabilityWorkspace workspace = nativeQueue.AcquireCapabilityWorkspace();
        try
        {
            PreparedSparseCopy[] prepared = workspace.PrepareCopies(copies.Length);
            for (int index = 0; index < copies.Length; index++)
            {
                ref readonly SparseMappingCopyDesc copy = ref copies[index];
                D3D12SparseState destinationState = GetSparseState(copy.Destination);
                D3D12SparseState sourceState = GetSparseState(copy.Source);
                EnsureSameDevice(nativeQueue.NativeDevice, copy.Destination.Device, nameof(copies));
                EnsureSameDevice(nativeQueue.NativeDevice, copy.Source.Device, nameof(copies));

                SparseTileRegion destinationRegion = copy.Region with
                {
                    Start = copy.DestinationStart,
                };
                SparseTileRegion sourceRegion = copy.Region with
                {
                    Start = copy.SourceStart,
                };
                SparseLogicalRegion logicalDestination =
                    destinationState.PrepareRegion(destinationRegion);
                SparseLogicalRegion logicalSource = sourceState.PrepareRegion(sourceRegion);

                prepared[index] = new PreparedSparseCopy(
                    destinationState,
                    sourceState,
                    GetNativeResource(copy.Destination),
                    GetNativeResource(copy.Source),
                    logicalDestination,
                    logicalSource,
                    ToNativeCoordinate(copy.DestinationStart),
                    ToNativeCoordinate(copy.SourceStart),
                    ToNativeRegion(copy.Region));
                workspace.RetainForSubmission(GetNativeLifetime(copy.Destination), copy.Destination);
                workspace.RetainForSubmission(GetNativeLifetime(copy.Source), copy.Source);
            }

            bool accepted = false;
            List<(D3D12SparseState State, SparseMappingGeneration Generation)>? generations = null;
            try
            {
                using (nativeQueue.Gate.EnterScope())
                {
                    nativeQueue.Device.ThrowIfUnavailable();
                    generations = workspace.CreateCopyGenerations(
                        prepared.AsSpan(0, copies.Length));
                    for (int index = 0; index < copies.Length; index++)
                    {
                        ref readonly PreparedSparseCopy copy = ref prepared[index];
                        TiledResourceCoordinate destination = copy.DestinationCoordinate;
                        TiledResourceCoordinate source = copy.SourceCoordinate;
                        TileRegionSize region = copy.Region;
                        nativeQueue.Native->CopyTileMappings(
                            copy.Destination,
                            &destination,
                            copy.Source,
                            &source,
                            &region,
                            TileMappingFlags.None);
                        accepted = true;
                    }

                    CommitSparseGenerations(generations);
                    generations = null;
                    QueueCompletion completion = nativeQueue.SignalCompletionUnderGate();
                    nativeQueue.RegisterCapabilityPayloadUnderGate(completion.Value, workspace);
                    workspace = null!;
                    return completion;
                }
            }
            catch (Exception exception)
            {
                ReleaseUncommittedGenerations(generations);
                if (!accepted)
                {
                    workspace.Dispose();
                    throw;
                }

                nativeQueue.RegisterUntrustedCapabilityPayload(workspace);
                workspace = null!;
                GraphicsException loss = CreateAcceptedOperationLoss(
                    "A sparse mapping copy was accepted but its completion could not be established.",
                    exception);
                loss = nativeQueue.NativeDevice.MarkLost(loss);
                throw loss;
            }
        }
        catch
        {
            workspace?.Dispose();
            throw;
        }
    }

    public ResidencyInfo GetResidencyInfo(Device device)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        _ = nativeDevice.RequireCapability<Residency>(nameof(GetResidencyInfo));
        IDXGIAdapter3* adapter = (IDXGIAdapter3*)nativeDevice.NativeAdapter;
        ulong localBudget = 0;
        ulong localUsage = 0;
        ulong nonLocalBudget = 0;
        ulong nonLocalUsage = 0;
        uint nodeMask = nativeDevice.EnabledNodeMask;
        while (nodeMask != 0)
        {
            uint node = (uint)BitOperations.TrailingZeroCount(nodeMask);
            nodeMask &= nodeMask - 1;
            QueryVideoMemoryInfo local = default;
            QueryVideoMemoryInfo nonLocal = default;
            ThrowIfFailed(
                nativeDevice,
                adapter->QueryVideoMemoryInfo(node, MemorySegmentGroup.Local, &local),
                NativeOperationType.Ordinary,
                "IDXGIAdapter3::QueryVideoMemoryInfo(Local)");
            ThrowIfFailed(
                nativeDevice,
                adapter->QueryVideoMemoryInfo(node, MemorySegmentGroup.NonLocal, &nonLocal),
                NativeOperationType.Ordinary,
                "IDXGIAdapter3::QueryVideoMemoryInfo(NonLocal)");
            localBudget = SaturatingAdd(localBudget, local.Budget);
            localUsage = SaturatingAdd(localUsage, local.CurrentUsage);
            nonLocalBudget = SaturatingAdd(nonLocalBudget, nonLocal.Budget);
            nonLocalUsage = SaturatingAdd(nonLocalUsage, nonLocal.CurrentUsage);
        }
        return new ResidencyInfo(localBudget, localUsage, nonLocalBudget, nonLocalUsage);
    }

    public ResidencyResource GetResidencyResource(Heap heap)
    {
        D3D12Heap native = RequireHeap(heap);
        _ = RequireDevice(heap.Device, nameof(heap))
            .RequireCapability<Residency>(nameof(GetResidencyResource));
        native.ThrowIfDisposed();
        return new ResidencyResource(
            native.Device,
            D3D12ResidencyHandle.ForLease(native.NativeDevice, native, native.NativeLifetime));
    }

    public ResidencyResource GetResidencyResource(Resource resource)
    {
        RequireBackendOwner(resource, nameof(resource));
        D3D12Device device = RequireDevice(resource.Device, nameof(resource));
        _ = device.RequireCapability<Residency>(nameof(GetResidencyResource));
        resource.ThrowIfDisposed();
        if (GetMemoryAllocationOrNull(resource) is D3D12MemoryAllocation allocation)
        {
            return new ResidencyResource(
                resource.Device,
                D3D12ResidencyHandle.ForLease(
                    device,
                    resource,
                    allocation.HeapLifetime));
        }
        if (resource.Heap is not null || GetSparseStateOrNull(resource) is not null)
        {
            throw new NotSupportedException(
                "Only committed D3D12 resources have an independent residency identity.");
        }
        return new ResidencyResource(
            resource.Device,
            D3D12ResidencyHandle.ForLease(
                device,
                resource,
                GetNativeLifetime(resource)));
    }

    public ResidencyResource GetResidencyResource(QueryPool pool)
    {
        D3D12QueryPool native = RequireQueryPool(pool);
        D3D12Device device = RequireDevice(pool.Device, nameof(pool));
        _ = device.RequireCapability<Residency>(nameof(GetResidencyResource));
        native.ThrowIfDisposed();
        return new ResidencyResource(
            pool.Device,
            D3D12ResidencyHandle.ForLease(
                device,
                pool,
                native.NativeLifetime));
    }

    public ResidencyResource GetResidencyResource(DescriptorTable table)
    {
        D3D12DescriptorTable native = RequireDescriptorTable(table);
        _ = RequireDevice(table.Device, nameof(table))
            .RequireCapability<Residency>(nameof(GetResidencyResource));
        native.ThrowIfDisposed();
        throw new NotSupportedException(
            "D3D12 DescriptorTables are CPU staging allocations; their shader-visible execution arenas are command-scoped and have no stable independent residency identity.");
    }

    public QueueCompletion EnqueueMakeResident(
        Queue queue,
        ReadOnlySpan<ResidencyResource> resources)
    {
        D3D12Queue nativeQueue = RequireQueue(queue, nameof(queue));
        _ = nativeQueue.NativeDevice.RequireCapability<Residency>(nameof(EnqueueMakeResident));
        if (resources.IsEmpty)
            throw new ArgumentException("At least one residency resource is required.", nameof(resources));
        CapabilityWorkspace workspace = nativeQueue.AcquireCapabilityWorkspace();
        nint[] pointers;
        try
        {
            pointers = workspace.PrepareResidency(
                nativeQueue.NativeDevice,
                resources,
                retainForSubmission: true);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
        bool accepted = false;
        try
        {
            using (nativeQueue.Gate.EnterScope())
            {
                nativeQueue.Device.ThrowIfUnavailable();
                fixed (nint* nativePointers = pointers)
                {
                    ResidencyFencePoint residency = nativeQueue.NativeDevice.EnqueueResidency(
                        (uint)resources.Length,
                        (ID3D12Pageable**)nativePointers);
                    accepted = true;
                    ThrowIfFailed(
                        nativeQueue.NativeDevice,
                        nativeQueue.Native->Wait(residency.Fence, residency.Value),
                        NativeOperationType.Ordinary,
                        "ID3D12CommandQueue::Wait(residency)");
                }
                QueueCompletion completion = nativeQueue.SignalCompletionUnderGate();
                nativeQueue.RegisterCapabilityPayloadUnderGate(
                    completion.Value,
                    workspace);
                workspace = null!;
                return completion;
            }
        }
        catch (Exception exception)
        {
            if (!accepted)
            {
                workspace.Dispose();
                throw;
            }

            nativeQueue.RegisterUntrustedCapabilityPayload(workspace);
            workspace = null!;
            GraphicsException loss = CreateAcceptedOperationLoss(
                "A residency request was accepted but Queue ordering could not be established.",
                exception);
            loss = nativeQueue.NativeDevice.MarkLost(loss);
            throw loss;
        }
    }

    public void Evict(Device device, ReadOnlySpan<ResidencyResource> resources)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        _ = nativeDevice.RequireCapability<Residency>(nameof(Evict));
        if (resources.IsEmpty)
            throw new ArgumentException("At least one residency resource is required.", nameof(resources));
        CapabilityWorkspace workspace = nativeDevice.AcquireResidencyWorkspace();
        nint[] pointers;
        try
        {
            pointers = workspace.PrepareResidency(
                nativeDevice,
                resources,
                retainForSubmission: false);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
        try
        {
            fixed (nint* nativePointers = pointers)
            {
                ThrowIfFailed(
                    nativeDevice,
                    nativeDevice.Native->Evict(
                        (uint)resources.Length,
                        (ID3D12Pageable**)nativePointers),
                    NativeOperationType.Ordinary,
                    "ID3D12Device::Evict");
            }
        }
        finally
        {
            workspace.Dispose();
        }
    }

    private static void RequireDefaultReservedResourcePlacement(
        in ResourceNodePlacement placement,
        string parameterName)
    {
        if (placement.CreationNodeMask != 0 || placement.VisibleNodeMask != 0)
        {
            throw new ArgumentException(
                "A reserved resource is node-neutral until its tiles are mapped; explicit creation placement is not supported.",
                parameterName);
        }
    }

    private static NativeResource* CreateReservedResource(
        D3D12Device device,
        in NativeResourceDesc description,
        ReadOnlySpan<DxgiFormat> castableFormats)
    {
        NativeResource* resource = null;
        NativeResourceDesc copy = description;
        Guid iid = NativeResource.Guid;
        if (device.EnhancedBarriers)
        {
            fixed (DxgiFormat* formats = castableFormats)
            {
                ThrowIfFailed(
                    device,
                    device.Native->CreateReservedResource2(
                        &copy,
                        InitialLayout(MemoryType.DeviceLocal, description.Dimension),
                        null,
                        null,
                        (uint)castableFormats.Length,
                        formats,
                        &iid,
                        (void**)&resource),
                    NativeOperationType.Ordinary,
                    "ID3D12Device10::CreateReservedResource2");
            }
        }
        else
        {
            ThrowIfFailed(
                device,
                device.Native->CreateReservedResource(
                    &copy,
                    ResourceStates.Common,
                    null,
                    &iid,
                    (void**)&resource),
                NativeOperationType.Ordinary,
                "ID3D12Device::CreateReservedResource");
        }
        return resource;
    }

    private static D3D12SparseState QuerySparseState(
        D3D12Device device,
        NativeResource* resource,
        uint requestedSubresourceCount)
    {
        if (requestedSubresourceCount == 0)
            throw new ArgumentOutOfRangeException(nameof(requestedSubresourceCount));
        SubresourceTiling[] tilings = new SubresourceTiling[requestedSubresourceCount];
        uint totalTiles = 0;
        PackedMipInfo packed = default;
        TileShape shape = default;
        uint tilingCount = requestedSubresourceCount;
        fixed (SubresourceTiling* nativeTilings = tilings)
        {
            device.Native->GetResourceTiling(
                resource,
                &totalTiles,
                &packed,
                &shape,
                &tilingCount,
                0,
                nativeTilings);
        }
        if (tilingCount > requestedSubresourceCount)
            throw new GraphicsException(GraphicsError.NativeFailure, "D3D12 returned an invalid sparse tiling count.");
        if (tilingCount != requestedSubresourceCount)
            Array.Resize(ref tilings, checked((int)tilingCount));

        SparseResourceInfo info = new(
            new SparseTileShape(shape.WidthInTexels, shape.HeightInTexels, shape.DepthInTexels),
            totalTiles,
            new SparsePackedMipInfo(
                packed.NumStandardMips,
                packed.NumPackedMips,
                packed.StartTileIndexInOverallResource,
                packed.NumTilesForPackedMips),
            SparseTileSize);
        return new D3D12SparseState(device, info, tilings, requestedSubresourceCount);
    }

    private static D3D12SparseState GetSparseState(Resource resource) =>
        GetSparseStateOrNull(resource)
        ?? throw new ArgumentException("The Resource is not a reserved sparse resource.", nameof(resource));

    private static D3D12SparseState? GetSparseStateOrNull(Resource resource) => resource switch
    {
        Buffer buffer => RequireD3D12.Buffer(buffer).SparseState,
        Texture texture => RequireD3D12.Texture(texture).SparseState,
        _ => throw new ArgumentOutOfRangeException(nameof(resource)),
    };

    private static NativeResource* GetNativeResource(Resource resource) => resource switch
    {
        Buffer buffer => RequireD3D12.Buffer(buffer).Native,
        Texture texture => RequireD3D12.Texture(texture).Native,
        _ => throw new ArgumentOutOfRangeException(nameof(resource)),
    };

    private static NativeLease GetNativeLifetime(Resource resource) => resource switch
    {
        Buffer buffer => RequireD3D12.Buffer(buffer).NativeLifetime,
        Texture texture => RequireD3D12.Texture(texture).NativeLifetime,
        _ => throw new ArgumentOutOfRangeException(nameof(resource)),
    };

    private static void EnsureSameDevice(
        D3D12Device expected,
        Device actual,
        string parameterName)
    {
        if (!ReferenceEquals(expected, actual))
            throw new ArgumentException("Every object must belong to the supplied Device.", parameterName);
    }

    private static TiledResourceCoordinate ToNativeCoordinate(in SparseTileCoordinate value) =>
        new(value.X, value.Y, value.Z, value.Subresource);

    private static TileRegionSize ToNativeRegion(in SparseTileRegion value) =>
        new(
            value.TileCount,
            value.Boxed,
            value.Boxed ? value.Width : 0,
            value.Boxed ? checked((ushort)value.Height) : (ushort)0,
            value.Boxed ? checked((ushort)value.Depth) : (ushort)0);

    private static void ValidateSparseHeapCompatibility(
        Resource resource,
        D3D12Heap heap,
        string parameterName)
    {
        SomeEngine.Graphics.HeapFlags requiredClass;
        bool shareable;
        switch (resource)
        {
            case Buffer buffer:
                requiredClass = SomeEngine.Graphics.HeapFlags.Buffers;
                shareable = (buffer.Info.Usages & BufferUsages.Shareable) != 0;
                break;

            case Texture texture:
                requiredClass =
                    (texture.Info.Usages & (TextureUsages.ColorAttachment |
                                            TextureUsages.DepthStencilAttachment)) != 0
                        ? SomeEngine.Graphics.HeapFlags.Attachments
                        : SomeEngine.Graphics.HeapFlags.Textures;
                shareable = (texture.Info.Usages & TextureUsages.Shareable) != 0;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(resource));
        }

        SomeEngine.Graphics.HeapFlags classes = heap.Info.Flags &
            (SomeEngine.Graphics.HeapFlags.Buffers |
             SomeEngine.Graphics.HeapFlags.Textures |
             SomeEngine.Graphics.HeapFlags.Attachments);
        bool unrestricted = classes == SomeEngine.Graphics.HeapFlags.None ||
            BitOperations.PopCount((uint)classes) > 1;
        if (!unrestricted && (classes & requiredClass) == 0)
        {
            throw new ArgumentException(
                "The Heap class is incompatible with the sparse resource.",
                parameterName);
        }
        if (shareable && (heap.Info.Flags & SomeEngine.Graphics.HeapFlags.Shareable) == 0)
        {
            throw new ArgumentException(
                "A shareable sparse resource requires a shareable Heap.",
                parameterName);
        }
    }

    private static void CommitSparseGenerations(
        List<(D3D12SparseState State, SparseMappingGeneration Generation)> generations)
    {
        foreach ((D3D12SparseState state, SparseMappingGeneration generation) in generations)
            state.Commit(generation);
        generations.Clear();
    }

    private static void ReleaseUncommittedGenerations(
        List<(D3D12SparseState State, SparseMappingGeneration Generation)>? generations)
    {
        if (generations is null)
            return;
        foreach ((_, SparseMappingGeneration generation) in generations)
            generation.Release();
        generations.Clear();
    }

    private static GraphicsException CreateAcceptedOperationLoss(
        string message,
        Exception inner) =>
        new(GraphicsError.DeviceLost, message, innerException: inner);

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;


    private readonly struct PreparedSparseMapping
    {
        private readonly nint _resource;

        internal PreparedSparseMapping(
            D3D12SparseState state,
            NativeResource* resource,
            D3D12Heap? heap,
            in SparseLogicalRegion logicalRegion,
            TiledResourceCoordinate coordinate,
            TileRegionSize region,
            TileRangeFlags rangeFlags,
            uint heapOffset,
            uint tileCount)
        {
            State = state;
            _resource = (nint)resource;
            Heap = heap;
            LogicalRegion = logicalRegion;
            Coordinate = coordinate;
            Region = region;
            RangeFlags = rangeFlags;
            HeapOffset = heapOffset;
            TileCount = tileCount;
        }

        internal D3D12SparseState State { get; }
        internal NativeResource* Resource => (NativeResource*)_resource;
        internal D3D12Heap? Heap { get; }
        internal SparseLogicalRegion LogicalRegion { get; }
        internal TiledResourceCoordinate Coordinate { get; }
        internal TileRegionSize Region { get; }
        internal TileRangeFlags RangeFlags { get; }
        internal uint HeapOffset { get; }
        internal uint TileCount { get; }
    }

    private readonly struct PreparedSparseCopy
    {
        private readonly nint _destination;
        private readonly nint _source;

        internal PreparedSparseCopy(
            D3D12SparseState destinationState,
            D3D12SparseState sourceState,
            NativeResource* destination,
            NativeResource* source,
            in SparseLogicalRegion destinationRegion,
            in SparseLogicalRegion sourceRegion,
            TiledResourceCoordinate destinationCoordinate,
            TiledResourceCoordinate sourceCoordinate,
            TileRegionSize region)
        {
            DestinationState = destinationState;
            SourceState = sourceState;
            _destination = (nint)destination;
            _source = (nint)source;
            DestinationRegion = destinationRegion;
            SourceRegion = sourceRegion;
            DestinationCoordinate = destinationCoordinate;
            SourceCoordinate = sourceCoordinate;
            Region = region;
        }

        internal D3D12SparseState DestinationState { get; }
        internal D3D12SparseState SourceState { get; }
        internal NativeResource* Destination => (NativeResource*)_destination;
        internal NativeResource* Source => (NativeResource*)_source;
        internal SparseLogicalRegion DestinationRegion { get; }
        internal SparseLogicalRegion SourceRegion { get; }
        internal TiledResourceCoordinate DestinationCoordinate { get; }
        internal TiledResourceCoordinate SourceCoordinate { get; }
        internal TileRegionSize Region { get; }
    }

    private readonly struct ResidencyFencePoint
    {
        private readonly nint _fence;

        internal ResidencyFencePoint(ID3D12Fence* fence, ulong value)
        {
            _fence = (nint)fence;
            Value = value;
        }

        internal ID3D12Fence* Fence => (ID3D12Fence*)_fence;
        internal ulong Value { get; }
    }


    private sealed class D3D12SparseState : IDisposable
    {
        private readonly object _gate = new();
        private readonly D3D12Device? _device;
        private readonly SubresourceTiling[] _tilings;
        private readonly uint _subresourceCount;
        private SparseMappingGeneration? _current;
        private SparseMappingGeneration? _generationPool;

        internal D3D12SparseState(
            D3D12Device device,
            in SparseResourceInfo info,
            SubresourceTiling[] tilings,
            uint subresourceCount)
            : this(info, tilings, subresourceCount)
        {
            _device = device;
        }

        internal D3D12SparseState(
            in SparseResourceInfo info,
            SubresourceTiling[] tilings,
            uint subresourceCount)
        {
            Info = info;
            _tilings = tilings;
            _subresourceCount = subresourceCount;
            _current = new SparseMappingGeneration(this);
            _current.Configure([], []);
        }

        internal SparseResourceInfo Info { get; }

        internal SparseLogicalRegion PrepareRegion(in SparseTileRegion region)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_current is null, this);
                ValidateRegion(region);
                return new SparseLogicalRegion(this, region);
            }
        }

        internal SparseMappingGeneration CaptureCurrent()
        {
            lock (_gate)
            {
                SparseMappingGeneration current = _current
                    ?? throw new ObjectDisposedException(nameof(D3D12SparseState));
                current.Retain();
                return current;
            }
        }

        internal void PrepareBuilder(SparseMappingBuilder builder)
        {
            lock (_gate)
            {
                SparseMappingGeneration current = _current
                    ?? throw new ObjectDisposedException(nameof(D3D12SparseState));
                current.Retain();
                builder.Reset(current);
            }
        }

        internal SparseMappingGeneration CreateGeneration(
            IReadOnlyList<SparseMappingRange> ranges,
            IReadOnlyList<NativeLease> heaps)
        {
            SparseMappingGeneration generation;
            lock (_gate)
            {
                generation = _generationPool ?? new SparseMappingGeneration(this);
                _generationPool = generation.PoolNext;
                generation.PoolNext = null;
            }
            try
            {
                generation.Configure(
                    ranges,
                    heaps);
                return generation;
            }
            catch
            {
                ReturnGeneration(generation);
                throw;
            }
        }

        internal void ReturnGeneration(SparseMappingGeneration generation)
        {
            lock (_gate)
            {
                generation.PoolNext = _generationPool;
                _generationPool = generation;
            }
        }

        private void ValidateRegion(in SparseTileRegion region)
        {
            if (region.TileCount == 0 || region.Start.Subresource >= _subresourceCount)
                throw new ArgumentOutOfRangeException(nameof(region));

            GetStartSegmentAndOffset(region.Start, out uint segment, out ulong offset);
            if (region.Boxed)
            {
                if (IsPacked(segment) ||
                    region.Width == 0 || region.Height == 0 || region.Depth == 0 ||
                    region.Height > ushort.MaxValue || region.Depth > ushort.MaxValue ||
                    checked((ulong)region.Width * region.Height * region.Depth) != region.TileCount)
                {
                    throw new ArgumentOutOfRangeException(nameof(region));
                }

                SubresourceTiling tiling = GetStandardTiling(segment);
                if (region.Width > tiling.WidthInTiles - region.Start.X ||
                    region.Height > tiling.HeightInTiles - region.Start.Y ||
                    region.Depth > tiling.DepthInTiles - region.Start.Z)
                {
                    throw new ArgumentOutOfRangeException(nameof(region));
                }
                return;
            }

            ulong remaining = region.TileCount;
            while (remaining != 0)
            {
                ulong tileCount = GetSegmentTileCount(segment);
                if (offset >= tileCount)
                    throw new ArgumentOutOfRangeException(nameof(region));
                ulong consumed = Math.Min(remaining, tileCount - offset);
                remaining -= consumed;
                if (remaining == 0)
                    return;
                if (!TryGetNextSegment(segment, out segment))
                    throw new ArgumentOutOfRangeException(nameof(region));
                offset = 0;
            }
        }

        internal void GetStartSegmentAndOffset(
            in SparseTileCoordinate coordinate,
            out uint segment,
            out ulong offset)
        {
            segment = GetCanonicalSegment(coordinate.Subresource);
            if (IsPacked(segment))
            {
                if (coordinate.Y != 0 || coordinate.Z != 0 ||
                    coordinate.X >= Info.PackedMips.PackedMipTileCount)
                {
                    throw new ArgumentOutOfRangeException(nameof(coordinate));
                }
                offset = coordinate.X;
                return;
            }

            SubresourceTiling tiling = GetStandardTiling(segment);
            if (coordinate.X >= tiling.WidthInTiles ||
                coordinate.Y >= tiling.HeightInTiles ||
                coordinate.Z >= tiling.DepthInTiles)
            {
                throw new ArgumentOutOfRangeException(nameof(coordinate));
            }
            offset = checked(
                ((ulong)coordinate.Z * tiling.HeightInTiles + coordinate.Y) *
                tiling.WidthInTiles + coordinate.X);
        }

        private uint GetCanonicalSegment(uint subresource)
        {
            if (!IsPacked(subresource))
                return subresource;
            uint mipCount = checked(
                Info.PackedMips.StandardMipLevelCount +
                Info.PackedMips.PackedMipLevelCount);
            if (mipCount == 0)
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "D3D12 reported a packed sparse subresource without packed-mip metadata.");
            uint mip = subresource % mipCount;
            if (mip < Info.PackedMips.StandardMipLevelCount)
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "D3D12 reported inconsistent sparse subresource tiling metadata.");
            return checked(
                subresource - mip + Info.PackedMips.StandardMipLevelCount);
        }

        private bool IsPacked(uint subresource)
        {
            if (subresource < _tilings.Length)
                return _tilings[subresource].StartTileIndexInOverallResource == uint.MaxValue;

            uint packedMipCount = Info.PackedMips.PackedMipLevelCount;
            uint mipCount = checked(
                Info.PackedMips.StandardMipLevelCount + packedMipCount);
            return packedMipCount != 0 && mipCount != 0 &&
                subresource % mipCount >= Info.PackedMips.StandardMipLevelCount;
        }

        internal SubresourceTiling GetStandardTiling(uint segment)
        {
            if (segment >= _tilings.Length || IsPacked(segment))
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "D3D12 did not report a standard tiling for the sparse subresource.");
            SubresourceTiling tiling = _tilings[segment];
            if (tiling.WidthInTiles == 0 || tiling.HeightInTiles == 0 ||
                tiling.DepthInTiles == 0)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "D3D12 reported an empty standard sparse-subresource tiling.");
            }
            return tiling;
        }

        internal ulong GetSegmentTileCount(uint segment)
        {
            if (IsPacked(segment))
                return Info.PackedMips.PackedMipTileCount;
            SubresourceTiling tiling = GetStandardTiling(segment);
            return checked(
                (ulong)tiling.WidthInTiles * tiling.HeightInTiles * tiling.DepthInTiles);
        }

        internal bool TryGetNextSegment(uint segment, out uint next)
        {
            if (IsPacked(segment))
            {
                uint mipCount = checked(
                    Info.PackedMips.StandardMipLevelCount +
                    Info.PackedMips.PackedMipLevelCount);
                uint groupStart = segment - Info.PackedMips.StandardMipLevelCount;
                next = checked(groupStart + mipCount);
            }
            else
            {
                next = checked(segment + 1);
                if (next < _subresourceCount)
                    next = GetCanonicalSegment(next);
            }
            return next < _subresourceCount;
        }

        internal void Commit(SparseMappingGeneration generation)
        {
            lock (_gate)
            {
                SparseMappingGeneration? previous = _current;
                if (previous is null)
                {
                    generation.Release();
                    return;
                }
                _current = generation;
                previous.Release();
            }
        }

        public void Dispose()
        {
            SparseMappingGeneration? current;
            lock (_gate)
            {
                current = _current;
                _current = null;
            }
            current?.Release();
        }
    }

    private sealed class SparseMappingGeneration
    {
        private readonly D3D12SparseState _owner;
        private SparseMappingRange[] _ranges = [];
        private NativeLease[] _heaps = [];
        private int _rangeCount;
        private int _heapCount;
        private int _references;

        internal SparseMappingGeneration(D3D12SparseState owner) => _owner = owner;

        internal SparseMappingGeneration? PoolNext { get; set; }
        internal ReadOnlySpan<SparseMappingRange> Ranges => _ranges.AsSpan(0, _rangeCount);

        internal void Configure(
            IReadOnlyList<SparseMappingRange> ranges,
            IReadOnlyList<NativeLease> heaps)
        {
            _ranges = EnsureCapacity(_ranges, ranges.Count);
            int heapCount = heaps.Count;
            _heaps = EnsureCapacity(_heaps, heapCount);
            for (int index = 0; index < ranges.Count; index++)
                _ranges[index] = ranges[index];
            int retained = 0;
            try
            {
                for (; retained < heapCount; retained++)
                {
                    _heaps[retained] = heaps[retained];
                    heaps[retained].Retain();
                }
            }
            catch
            {
                for (int index = 0; index < retained; index++)
                    heaps[index].Release();
                throw;
            }
            _rangeCount = ranges.Count;
            _heapCount = heapCount;
            Volatile.Write(ref _references, 1);
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
            throw new ObjectDisposedException(nameof(SparseMappingGeneration));
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0)
                return;
            for (int index = 0; index < _heapCount; index++)
            {
                _heaps[index].Release();
                _heaps[index] = null!;
            }
            _rangeCount = 0;
            _heapCount = 0;
            _owner.ReturnGeneration(this);
        }

        private static T[] EnsureCapacity<T>(T[] current, int count) =>
            current.Length >= count
                ? current
                : new T[checked(Math.Max(count, current.Length == 0 ? 4 : current.Length * 2))];
    }

    private readonly record struct SparseLogicalRegion(
        D3D12SparseState State,
        SparseTileRegion Region)
    {
        internal SparseIntervalEnumerator GetEnumerator() => new(State, Region);
    }

    private readonly record struct SparseTileInterval(
        uint Segment,
        ulong Start,
        ulong TileCount)
    {
        internal ulong End => checked(Start + TileCount);
    }

    private readonly record struct SparseMappingRange(
        uint Segment,
        ulong Start,
        ulong End,
        NativeLease Heap);

    private readonly record struct SparseMappingRun(
        ulong TileCount,
        NativeLease? Heap);

    private struct SparseIntervalEnumerator
    {
        private readonly D3D12SparseState _state;
        private readonly SparseTileRegion _region;
        private uint _segment;
        private ulong _offset;
        private ulong _remaining;
        private uint _boxRow;
        private uint _boxSlice;

        internal SparseIntervalEnumerator(
            D3D12SparseState state,
            in SparseTileRegion region)
        {
            _state = state;
            _region = region;
            state.GetStartSegmentAndOffset(region.Start, out _segment, out _offset);
            _remaining = region.TileCount;
            _boxRow = 0;
            _boxSlice = 0;
            Current = default;
        }

        internal SparseTileInterval Current { get; private set; }

        internal bool MoveNext()
        {
            if (_remaining == 0)
                return false;
            if (_region.Boxed)
            {
                SubresourceTiling tiling = _state.GetStandardTiling(_segment);
                ulong start = checked(
                    ((ulong)(_region.Start.Z + _boxSlice) * tiling.HeightInTiles +
                     _region.Start.Y + _boxRow) * tiling.WidthInTiles +
                    _region.Start.X);
                Current = new SparseTileInterval(_segment, start, _region.Width);
                _remaining -= _region.Width;
                _boxRow++;
                if (_boxRow == _region.Height)
                {
                    _boxRow = 0;
                    _boxSlice++;
                }
                return true;
            }

            ulong segmentTiles = _state.GetSegmentTileCount(_segment);
            ulong tileCount = Math.Min(_remaining, segmentTiles - _offset);
            Current = new SparseTileInterval(_segment, _offset, tileCount);
            _remaining -= tileCount;
            if (_remaining != 0)
            {
                if (!_state.TryGetNextSegment(_segment, out _segment))
                    throw new InvalidOperationException("The validated sparse region became invalid.");
                _offset = 0;
            }
            return true;
        }
    }

    private sealed class SparseMappingBuilder : IDisposable
    {
        private readonly List<SparseMappingRange> _ranges = [];
        private SparseMappingGeneration? _source;

        internal SparseMappingBuilder()
        {
        }

        internal void Reset(SparseMappingGeneration source)
        {
            System.Diagnostics.Debug.Assert(_source is null);
            _source = source;
            _ranges.EnsureCapacity(source.Ranges.Length);
            foreach (SparseMappingRange range in source.Ranges)
                _ranges.Add(range);
        }

        internal void Replace(in SparseLogicalRegion region, NativeLease? heap)
        {
            ThrowIfDisposed();
            SparseIntervalEnumerator intervals = region.GetEnumerator();
            while (intervals.MoveNext())
                Replace(intervals.Current, heap);
        }

        internal void Read(in SparseLogicalRegion region, List<SparseMappingRun> runs)
        {
            ThrowIfDisposed();
            runs.Clear();
            SparseIntervalEnumerator intervals = region.GetEnumerator();
            while (intervals.MoveNext())
                AppendRuns(intervals.Current, runs);
        }

        internal void Replace(
            in SparseLogicalRegion region,
            ReadOnlySpan<SparseMappingRun> runs)
        {
            ThrowIfDisposed();
            int runIndex = 0;
            ulong runOffset = 0;
            SparseIntervalEnumerator intervals = region.GetEnumerator();
            while (intervals.MoveNext())
            {
                SparseTileInterval interval = intervals.Current;
                ulong destinationOffset = 0;
                while (destinationOffset != interval.TileCount)
                {
                    if ((uint)runIndex >= (uint)runs.Length)
                        throw new InvalidOperationException("Captured sparse mapping data was truncated.");
                    ref readonly SparseMappingRun run = ref runs[runIndex];
                    ulong tileCount = Math.Min(
                        interval.TileCount - destinationOffset,
                        run.TileCount - runOffset);
                    Replace(
                        new SparseTileInterval(
                            interval.Segment,
                            checked(interval.Start + destinationOffset),
                            tileCount),
                        run.Heap);
                    destinationOffset += tileCount;
                    runOffset += tileCount;
                    if (runOffset == run.TileCount)
                    {
                        runIndex++;
                        runOffset = 0;
                    }
                }
            }
            if (runIndex != runs.Length || runOffset != 0)
                throw new InvalidOperationException("Captured sparse mapping data exceeded its destination.");
        }

        internal SparseMappingGeneration Build(
            D3D12SparseState state,
            HashSet<NativeLease> unique,
            List<NativeLease> heaps)
        {
            ThrowIfDisposed();
            unique.Clear();
            heaps.Clear();
            foreach (SparseMappingRange range in _ranges)
            {
                if (unique.Add(range.Heap))
                    heaps.Add(range.Heap);
            }

            SparseMappingGeneration result = state.CreateGeneration(_ranges, heaps);
            Dispose();
            return result;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _source, null)?.Release();
            _ranges.Clear();
        }

        private void AppendRuns(
            in SparseTileInterval interval,
            List<SparseMappingRun> runs)
        {
            ulong cursor = interval.Start;
            ulong end = interval.End;
            int index = LowerBound(interval.Segment, interval.Start);
            if (index != 0)
            {
                SparseMappingRange previous = _ranges[index - 1];
                if (previous.Segment == interval.Segment && previous.End > cursor)
                    index--;
            }

            while (cursor != end)
            {
                if (index >= _ranges.Count ||
                    _ranges[index].Segment != interval.Segment ||
                    _ranges[index].Start >= end)
                {
                    AppendRun(runs, end - cursor, null);
                    break;
                }

                SparseMappingRange range = _ranges[index];
                if (range.End <= cursor)
                {
                    index++;
                    continue;
                }
                if (range.Start > cursor)
                {
                    ulong holeEnd = Math.Min(end, range.Start);
                    AppendRun(runs, holeEnd - cursor, null);
                    cursor = holeEnd;
                    continue;
                }

                ulong mappedEnd = Math.Min(end, range.End);
                AppendRun(runs, mappedEnd - cursor, range.Heap);
                cursor = mappedEnd;
                if (cursor == range.End)
                    index++;
            }
        }

        private void Replace(in SparseTileInterval interval, NativeLease? heap)
        {
            ulong end = interval.End;
            int index = LowerBound(interval.Segment, interval.Start);
            if (index != 0)
            {
                SparseMappingRange previous = _ranges[index - 1];
                if (previous.Segment == interval.Segment && previous.End > interval.Start)
                    index--;
            }

            int removeStart = index;
            SparseMappingRange? left = null;
            SparseMappingRange? right = null;
            while (index < _ranges.Count)
            {
                SparseMappingRange range = _ranges[index];
                if (range.Segment != interval.Segment || range.Start >= end)
                    break;
                if (range.End <= interval.Start)
                {
                    index++;
                    removeStart = index;
                    continue;
                }
                if (range.Start < interval.Start)
                    left = range with { End = interval.Start };
                if (range.End > end)
                    right = range with { Start = end };
                index++;
            }

            if (index != removeStart)
                _ranges.RemoveRange(removeStart, index - removeStart);
            int insert = removeStart;
            if (left is SparseMappingRange leftRange)
                _ranges.Insert(insert++, leftRange);
            if (heap is not null)
            {
                _ranges.Insert(
                    insert++,
                    new SparseMappingRange(
                        interval.Segment,
                        interval.Start,
                        end,
                        heap));
            }
            if (right is SparseMappingRange rightRange)
                _ranges.Insert(insert, rightRange);
            MergeAdjacent(Math.Max(0, removeStart - 1));
        }

        private int LowerBound(uint segment, ulong start)
        {
            int low = 0;
            int high = _ranges.Count;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                SparseMappingRange range = _ranges[middle];
                if (range.Segment < segment ||
                    range.Segment == segment && range.Start < start)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return low;
        }

        private void MergeAdjacent(int index)
        {
            while (index + 1 < _ranges.Count)
            {
                SparseMappingRange left = _ranges[index];
                SparseMappingRange right = _ranges[index + 1];
                if (left.Segment == right.Segment && left.End == right.Start &&
                    ReferenceEquals(left.Heap, right.Heap))
                {
                    _ranges[index] = left with { End = right.End };
                    _ranges.RemoveAt(index + 1);
                    continue;
                }
                index++;
            }
        }

        private static void AppendRun(
            List<SparseMappingRun> runs,
            ulong tileCount,
            NativeLease? heap)
        {
            if (tileCount == 0)
                return;
            if (runs.Count != 0)
            {
                SparseMappingRun previous = runs[^1];
                if (ReferenceEquals(previous.Heap, heap))
                {
                    runs[^1] = previous with
                    {
                        TileCount = checked(previous.TileCount + tileCount),
                    };
                    return;
                }
            }
            runs.Add(new SparseMappingRun(tileCount, heap));
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(_source is null, this);
    }

    private sealed class D3D12ResidencyHandle
    {
        private D3D12ResidencyHandle(
            D3D12Device device,
            GraphicsObject owner,
            NativeLease lifetime)
        {
            Device = device;
            Owner = owner;
            Lifetime = lifetime;
        }

        internal D3D12Device Device { get; }
        internal GraphicsObject Owner { get; }
        internal NativeLease Lifetime { get; }

        internal static D3D12ResidencyHandle ForLease(
            D3D12Device device,
            GraphicsObject owner,
            NativeLease lifetime) =>
            new(device, owner, lifetime);
    }

    private sealed class CapabilityWorkspace :
        IntrusiveRetirementPayload<CapabilityWorkspace>
    {
        private readonly D3D12Device _device;
        private readonly D3D12Queue? _queue;
        private readonly HashSet<NativeLease> _lifetimes = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<GraphicsObject> _owners = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<D3D12SparseState, SparseMappingBuilder> _builders =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<D3D12SparseState> _modified =
            new(ReferenceEqualityComparer.Instance);
        private readonly List<SparseMappingBuilder> _builderPool = [];
        private readonly List<(D3D12SparseState State, SparseMappingGeneration Generation)>
            _generations = [];
        private readonly List<SparseMappingRun> _runs = [];
        private readonly HashSet<NativeLease> _generationHeapSet =
            new(ReferenceEqualityComparer.Instance);
        private readonly List<NativeLease> _generationHeaps = [];
        private PreparedSparseMapping[] _mappings = [];
        private PreparedSparseCopy[] _copies = [];
        private nint[] _residencyPointers = [];
        private int _mappingCount;
        private int _copyCount;
        private int _residencyCount;
        private int _buildersUsed;
        private int _disposed;

        internal CapabilityWorkspace(D3D12Device device, D3D12Queue? queue)
        {
            _device = device;
            _queue = queue;
        }

        internal CapabilityWorkspace? PoolNext { get; set; }
        internal bool IsEmpty => _lifetimes.Count == 0 && _owners.Count == 0;

        internal PreparedSparseMapping[] PrepareMappings(int count)
        {
            _mappings = EnsureCapacity(_mappings, count);
            _mappingCount = count;
            return _mappings;
        }

        internal PreparedSparseCopy[] PrepareCopies(int count)
        {
            _copies = EnsureCapacity(_copies, count);
            _copyCount = count;
            return _copies;
        }

        internal nint[] PrepareResidency(
            D3D12Device device,
            ReadOnlySpan<ResidencyResource> resources,
            bool retainForSubmission)
        {
            _residencyPointers = EnsureCapacity(_residencyPointers, resources.Length);
            _residencyCount = resources.Length;
            for (int index = 0; index < resources.Length; index++)
            {
                ref readonly ResidencyResource resource = ref resources[index];
                if (resource.IsDefault || resource.Value is not D3D12ResidencyHandle handle)
                    throw new ArgumentException("A default or foreign ResidencyResource is invalid.", nameof(resources));
                EnsureSameDevice(device, resource.Device, nameof(resources));
                if (!ReferenceEquals(device, handle.Device))
                    throw new ArgumentException("A foreign ResidencyResource is invalid.", nameof(resources));
                NativeLease lease = handle.Lifetime;
                nint pointer = lease.Pointer;
                if (pointer == 0)
                    throw new ObjectDisposedException(handle.Owner.GetType().FullName);
                _residencyPointers[index] = pointer;
                if (retainForSubmission)
                    RetainForSubmission(lease, handle.Owner);
            }
            return _residencyPointers;
        }

        internal List<(D3D12SparseState State, SparseMappingGeneration Generation)>
            CreateUpdateGenerations(ReadOnlySpan<PreparedSparseMapping> mappings)
        {
            _generations.Clear();
            if (mappings.Length == 0)
                return _generations;

            _builders.EnsureCapacity(mappings.Length);
            _generations.EnsureCapacity(mappings.Length);
            try
            {
                foreach (ref readonly PreparedSparseMapping mapping in mappings)
                {
                    SparseMappingBuilder builder = GetBuilder(mapping.State);
                    builder.Replace(mapping.LogicalRegion, mapping.Heap?.NativeLifetime);
                }

                foreach ((D3D12SparseState state, SparseMappingBuilder builder) in _builders)
                {
                    _generations.Add((
                        state,
                        builder.Build(state, _generationHeapSet, _generationHeaps)));
                }
                return _generations;
            }
            catch
            {
                ReleaseUncommittedGenerations(_generations);
                throw;
            }
            finally
            {
                DisposeBuilders();
            }
        }

        internal List<(D3D12SparseState State, SparseMappingGeneration Generation)>
            CreateCopyGenerations(ReadOnlySpan<PreparedSparseCopy> copies)
        {
            _generations.Clear();
            if (copies.Length == 0)
                return _generations;

            int stateCapacity = checked(copies.Length * 2);
            _builders.EnsureCapacity(stateCapacity);
            _modified.EnsureCapacity(copies.Length);
            _generations.EnsureCapacity(copies.Length);
            try
            {
                foreach (ref readonly PreparedSparseCopy copy in copies)
                {
                    SparseMappingBuilder source = GetBuilder(copy.SourceState);
                    SparseMappingBuilder destination = GetBuilder(copy.DestinationState);
                    source.Read(copy.SourceRegion, _runs);
                    destination.Replace(
                        copy.DestinationRegion,
                        CollectionsMarshal.AsSpan(_runs));
                    _modified.Add(copy.DestinationState);
                }

                foreach (D3D12SparseState state in _modified)
                {
                    _generations.Add((
                        state,
                        _builders[state].Build(
                            state,
                            _generationHeapSet,
                            _generationHeaps)));
                }
                return _generations;
            }
            catch
            {
                ReleaseUncommittedGenerations(_generations);
                throw;
            }
            finally
            {
                _modified.Clear();
                DisposeBuilders();
            }
        }

        internal void RetainForSubmission(NativeLease lifetime, GraphicsObject owner)
        {
            lifetime.Retain();
            bool added = false;
            try
            {
                added = _lifetimes.Add(lifetime);
                if (!added)
                    lifetime.Release();
                _owners.Add(owner);
            }
            catch
            {
                if (added)
                {
                    _lifetimes.Remove(lifetime);
                    lifetime.Release();
                }
                throw;
            }
        }

        internal void Activate() => Volatile.Write(ref _disposed, 0);

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            foreach (NativeLease lifetime in _lifetimes)
                lifetime.Release();
            _lifetimes.Clear();
            _owners.Clear();
            Array.Clear(_mappings, 0, _mappingCount);
            Array.Clear(_copies, 0, _copyCount);
            Array.Clear(_residencyPointers, 0, _residencyCount);
            _mappingCount = 0;
            _copyCount = 0;
            _residencyCount = 0;
            ReleaseUncommittedGenerations(_generations);
            _modified.Clear();
            DisposeBuilders();
            if (_queue is null)
                _device.ReturnResidencyWorkspace(this);
            else
                _queue.ReturnCapabilityWorkspace(this);
        }

        private SparseMappingBuilder GetBuilder(D3D12SparseState state)
        {
            if (_builders.TryGetValue(state, out SparseMappingBuilder? builder))
                return builder;

            if (_buildersUsed == _builderPool.Count)
                _builderPool.Add(new SparseMappingBuilder());
            builder = _builderPool[_buildersUsed];
            state.PrepareBuilder(builder);
            try
            {
                _builders.Add(state, builder);
                _buildersUsed++;
                return builder;
            }
            catch
            {
                builder.Dispose();
                throw;
            }
        }

        private void DisposeBuilders()
        {
            foreach (SparseMappingBuilder builder in _builders.Values)
                builder.Dispose();
            _builders.Clear();
            _buildersUsed = 0;
        }

        private static T[] EnsureCapacity<T>(T[] current, int count) =>
            current.Length >= count
                ? current
                : new T[checked(Math.Max(count, current.Length == 0 ? 4 : current.Length * 2))];
    }

    private sealed partial class D3D12Device
    {
        private readonly object _residencyGate = new();
        private CapabilityWorkspace? _residencyWorkspacePool;
        private ID3D12Fence* _residencyFence;
        private ulong _nextResidencyValue = 1;

        internal ResidencyFencePoint EnqueueResidency(
            uint count,
            ID3D12Pageable** objects)
        {
            lock (_residencyGate)
            {
                ThrowIfUnavailable();
                if (_nextResidencyValue == ulong.MaxValue)
                    throw new InvalidOperationException("The residency-fence value domain is exhausted.");
                if (_residencyFence is null)
                {
                    Guid iid = ID3D12Fence.Guid;
                    ID3D12Fence* fence = null;
                    ThrowIfFailed(
                        this,
                        Native->CreateFence(
                            0,
                            FenceFlags.None,
                            &iid,
                            (void**)&fence),
                        NativeOperationType.Ordinary,
                        "ID3D12Device::CreateFence(residency)");
                    _residencyFence = fence;
                }
                ulong value = _nextResidencyValue;
                ThrowIfFailed(
                    this,
                    ((ID3D12Device3*)Native)->EnqueueMakeResident(
                        ResidencyFlags.None,
                        count,
                        objects,
                        _residencyFence,
                        value),
                    NativeOperationType.Ordinary,
                    "ID3D12Device3::EnqueueMakeResident");
                _nextResidencyValue++;
                return new ResidencyFencePoint(_residencyFence, value);
            }
        }

        internal CapabilityWorkspace AcquireResidencyWorkspace()
        {
            lock (_residencyGate)
            {
                CapabilityWorkspace workspace = _residencyWorkspacePool ?? new(this, null);
                _residencyWorkspacePool = workspace.PoolNext;
                workspace.PoolNext = null;
                workspace.Activate();
                return workspace;
            }
        }

        internal void ReturnResidencyWorkspace(CapabilityWorkspace workspace)
        {
            lock (_residencyGate)
            {
                workspace.PoolNext = _residencyWorkspacePool;
                _residencyWorkspacePool = workspace;
            }
        }

        private void ReleaseResidencyInfrastructure()
        {
            lock (_residencyGate)
            {
                ID3D12Fence* fence = _residencyFence;
                _residencyFence = null;
                if (fence is not null)
                    _ = fence->Release();
            }
        }
    }

    private sealed partial class D3D12Queue
    {
        private IntrusiveRetirementChain<CapabilityWorkspace> _pendingCapabilityPayloads;
        private IntrusiveRetirementChain<CapabilityWorkspace> _untrustedCapabilityPayloads;
        private readonly object _capabilityWorkspaceGate = new();
        private CapabilityWorkspace? _capabilityWorkspacePool;

        internal D3D12Device NativeDevice => _device;

        internal CapabilityWorkspace AcquireCapabilityWorkspace()
        {
            lock (_capabilityWorkspaceGate)
            {
                CapabilityWorkspace workspace = _capabilityWorkspacePool ?? new(_device, this);
                _capabilityWorkspacePool = workspace.PoolNext;
                workspace.PoolNext = null;
                workspace.Activate();
                return workspace;
            }
        }

        internal void ReturnCapabilityWorkspace(CapabilityWorkspace workspace)
        {
            lock (_capabilityWorkspaceGate)
            {
                workspace.PoolNext = _capabilityWorkspacePool;
                _capabilityWorkspacePool = workspace;
            }
        }

        internal void RegisterCapabilityPayloadUnderGate(
            ulong completion,
            CapabilityWorkspace payload)
        {
            if (payload.IsEmpty)
            {
                payload.Dispose();
                return;
            }
            _pendingCapabilityPayloads.Append(payload, completion);
        }

        internal void RegisterUntrustedCapabilityPayload(CapabilityWorkspace payload)
        {
            if (payload.IsEmpty)
            {
                payload.Dispose();
                return;
            }
            using (Gate.EnterScope())
                RegisterUntrustedCapabilityPayloadUnderGate(payload);
        }

        private void RegisterUntrustedCapabilityPayloadUnderGate(CapabilityWorkspace payload) =>
            _untrustedCapabilityPayloads.Append(payload, 0);

        private ulong GetCapabilityRetirementTargetUnderGate() =>
            _pendingCapabilityPayloads.Target;

        private bool HasUntrustedCapabilityRetirementsUnderGate =>
            _untrustedCapabilityPayloads.HasAny;

        private void CollectCapabilityRetirementsUnderGate(ulong completed)
            => _pendingCapabilityPayloads.Collect(completed);

        private void AbandonCapabilityRetirementsUnderGate()
        {
            _pendingCapabilityPayloads.Abandon();
            _untrustedCapabilityPayloads.Abandon();
        }
    }

}
