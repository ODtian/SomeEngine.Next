namespace SomeEngine.Graphics.Direct3D12;
internal static unsafe class DeviceQueryTrace
{
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CopyDescriptors = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CopyDescriptors), detail: true);
    internal static void CopyDescriptors(global::Silk.NET.Direct3D12.ID3D12Device10* target, uint count,
        global::Silk.NET.Direct3D12.CpuDescriptorHandle* destinations,
        global::Silk.NET.Direct3D12.CpuDescriptorHandle* sources,
        global::Silk.NET.Direct3D12.DescriptorHeapType type)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(NativeID3D12Device10CopyDescriptors);
        target->CopyDescriptors(count, destinations, null, count, sources, null, type);
    }

    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10GetDescriptorHandleIncrementSize = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.GetDescriptorHandleIncrementSize), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CopyDescriptorsSimple = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CopyDescriptorsSimple), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12DeviceFactoryRelease = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12DeviceFactory) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12DeviceFactory.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12DeviceFactoryGetConfigurationInterface = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12DeviceFactory) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12DeviceFactory.GetConfigurationInterface), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10QueryInterface = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.QueryInterface), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10GetDeviceRemovedReason = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.GetDeviceRemovedReason), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10Release = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CheckFeatureSupport = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CheckFeatureSupport), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10GetNodeCount = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.GetNodeCount), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10GetResourceAllocationInfo = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.GetResourceAllocationInfo), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12DeviceChildGetDevice = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12DeviceChild) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12DeviceChild.GetDevice), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10OpenSharedHandle = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.OpenSharedHandle), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10GetRaytracingAccelerationStructurePrebuildInfo = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.GetRaytracingAccelerationStructurePrebuildInfo), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10GetCopyableFootprints = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.GetCopyableFootprints), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10GetResourceAllocationInfo2 = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.GetResourceAllocationInfo2), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10Evict = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.Evict), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10GetResourceTiling = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.GetResourceTiling), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device3EnqueueMakeResident = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device3) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device3.EnqueueMakeResident), detail: true);
    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint GetDescriptorHandleIncrementSize(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.DescriptorHeapType @DescriptorHeapType)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12Device10GetDescriptorHandleIncrementSize);
        return target->GetDescriptorHandleIncrementSize(@DescriptorHeapType);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void CopyDescriptorsSimple(global::Silk.NET.Direct3D12.ID3D12Device10* target, uint @NumDescriptors, global::Silk.NET.Direct3D12.CpuDescriptorHandle @DestDescriptorRangeStart, global::Silk.NET.Direct3D12.CpuDescriptorHandle @SrcDescriptorRangeStart, global::Silk.NET.Direct3D12.DescriptorHeapType @DescriptorHeapsType)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12Device10CopyDescriptorsSimple);
        target->CopyDescriptorsSimple(@NumDescriptors, @DestDescriptorRangeStart, @SrcDescriptorRangeStart, @DescriptorHeapsType);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12DeviceFactory* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12DeviceFactoryRelease);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int GetConfigurationInterface(global::Silk.NET.Direct3D12.ID3D12DeviceFactory* target, global::System.Guid* @clsid, global::System.Guid* @iid, void** @ppv)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12DeviceFactoryGetConfigurationInterface);
        return target->GetConfigurationInterface(@clsid, @iid, @ppv);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int QueryInterface(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::System.Guid* @riid, void** @ppvObject)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12Device10QueryInterface);
        return target->QueryInterface(@riid, @ppvObject);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int GetDeviceRemovedReason(global::Silk.NET.Direct3D12.ID3D12Device10* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12Device10GetDeviceRemovedReason);
        return target->GetDeviceRemovedReason();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12Device10* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12Device10Release);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CheckFeatureSupport(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.Feature @Feature, void* @pFeatureSupportData, uint @FeatureSupportDataSize)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12Device10CheckFeatureSupport);
        return target->CheckFeatureSupport(@Feature, @pFeatureSupportData, @FeatureSupportDataSize);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint GetNodeCount(global::Silk.NET.Direct3D12.ID3D12Device10* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12Device10GetNodeCount);
        return target->GetNodeCount();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static global::Silk.NET.Direct3D12.ResourceAllocationInfo GetResourceAllocationInfo(global::Silk.NET.Direct3D12.ID3D12Device10* target, uint @visibleMask, uint @numResourceDescs, global::Silk.NET.Direct3D12.ResourceDesc* @pResourceDescs)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12Device10GetResourceAllocationInfo);
        return target->GetResourceAllocationInfo(@visibleMask, @numResourceDescs, @pResourceDescs);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int GetDevice(global::Silk.NET.Direct3D12.ID3D12DeviceChild* target, global::System.Guid* @riid, void** @ppvDevice)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12DeviceChildGetDevice);
        return target->GetDevice(@riid, @ppvDevice);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int OpenSharedHandle(global::Silk.NET.Direct3D12.ID3D12Device10* target, void* @NTHandle, global::System.Guid* @riid, void** @ppvObj)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12Device10OpenSharedHandle);
        return target->OpenSharedHandle(@NTHandle, @riid, @ppvObj);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void GetRaytracingAccelerationStructurePrebuildInfo(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.BuildRaytracingAccelerationStructureInputs* @pDesc, global::Silk.NET.Direct3D12.RaytracingAccelerationStructurePrebuildInfo* @pInfo)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12Device10GetRaytracingAccelerationStructurePrebuildInfo);
        target->GetRaytracingAccelerationStructurePrebuildInfo(@pDesc, @pInfo);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void GetCopyableFootprints(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.ResourceDesc* @pResourceDesc, uint @FirstSubresource, uint @NumSubresources, ulong @BaseOffset, global::Silk.NET.Direct3D12.PlacedSubresourceFootprint* @pLayouts, uint* @pNumRows, ulong* @pRowSizeInBytes, ulong* @pTotalBytes)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12Device10GetCopyableFootprints);
        target->GetCopyableFootprints(@pResourceDesc, @FirstSubresource, @NumSubresources, @BaseOffset, @pLayouts, @pNumRows, @pRowSizeInBytes, @pTotalBytes);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static global::Silk.NET.Direct3D12.ResourceAllocationInfo GetResourceAllocationInfo2(global::Silk.NET.Direct3D12.ID3D12Device10* target, uint @visibleMask, uint @numResourceDescs, global::Silk.NET.Direct3D12.ResourceDesc1* @pResourceDescs, global::Silk.NET.Direct3D12.ResourceAllocationInfo1* @pResourceAllocationInfo1)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12Device10GetResourceAllocationInfo2);
        return target->GetResourceAllocationInfo2(@visibleMask, @numResourceDescs, @pResourceDescs, @pResourceAllocationInfo1);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int Evict(global::Silk.NET.Direct3D12.ID3D12Device10* target, uint @NumObjects, global::Silk.NET.Direct3D12.ID3D12Pageable** @ppObjects)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12Device10Evict);
        return target->Evict(@NumObjects, @ppObjects);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void GetResourceTiling(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.ID3D12Resource* @pTiledResource, uint* @pNumTilesForEntireResource, global::Silk.NET.Direct3D12.PackedMipInfo* @pPackedMipDesc, global::Silk.NET.Direct3D12.TileShape* @pStandardTileShapeForNonPackedMips, uint* @pNumSubresourceTilings, uint @FirstSubresourceTilingToGet, global::Silk.NET.Direct3D12.SubresourceTiling* @pSubresourceTilingsForNonPackedMips)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12Device10GetResourceTiling);
        target->GetResourceTiling(@pTiledResource, @pNumTilesForEntireResource, @pPackedMipDesc, @pStandardTileShapeForNonPackedMips, @pNumSubresourceTilings, @FirstSubresourceTilingToGet, @pSubresourceTilingsForNonPackedMips);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int EnqueueMakeResident(global::Silk.NET.Direct3D12.ID3D12Device3* target, global::Silk.NET.Direct3D12.ResidencyFlags @Flags, uint @NumObjects, global::Silk.NET.Direct3D12.ID3D12Pageable** @ppObjects, global::Silk.NET.Direct3D12.ID3D12Fence* @pFenceToSignal, ulong @FenceValueToSignal)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceQueryTrace.NativeID3D12Device3EnqueueMakeResident);
        return target->EnqueueMakeResident(@Flags, @NumObjects, @ppObjects, @pFenceToSignal, @FenceValueToSignal);
    }
}