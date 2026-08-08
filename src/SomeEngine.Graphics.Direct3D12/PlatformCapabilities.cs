using System.Diagnostics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using NativeHeapDesc = Silk.NET.Direct3D12.HeapDesc;
using NativeResourceDesc = Silk.NET.Direct3D12.ResourceDesc;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    private const uint GenericAll = 0x1000_0000;

    public CalibratedTimestampInfo CalibrateTimestamps(Queue queue)
    {
        D3D12Queue native = NativeCast.Queue(queue);
        _ = native.NativeDevice.RequireCapability<CalibratedTimestamps>(nameof(CalibrateTimestamps));
        lock (native.Gate)
        {
            ulong queueFrequency = 0;
            ulong queueCounter = 0;
            ulong cpuCounter = 0;
            NativeCall.ThrowIfFailed(
                native.Native->GetTimestampFrequency(&queueFrequency),
                "ID3D12CommandQueue::GetTimestampFrequency");
            NativeCall.ThrowIfFailed(
                native.Native->GetClockCalibration(&queueCounter, &cpuCounter),
                "ID3D12CommandQueue::GetClockCalibration");
            return new CalibratedTimestampInfo(
                checked((long)cpuCounter),
                Stopwatch.Frequency,
                queueCounter,
                queueFrequency);
        }
    }

    public Buffer ImportBuffer(
        Device device,
        ExternalHandle handle,
        in BufferDesc desc,
        in ImportedResourceState state)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        ExternalResources capability =
            nativeDevice.RequireCapability<ExternalResources>(nameof(ImportBuffer));
        RequireExternalHandleType(capability.BufferImportHandleTypes, handle.Type, nameof(ImportBuffer));
        ID3D12Resource* resource = OpenShared<ID3D12Resource>(nativeDevice, handle);
        return ImportBufferCore(nativeDevice, resource, ownsReference: true, desc, state);
    }

    /// <summary>Imports an existing D3D12 Buffer resource without opening an OS handle.</summary>
    /// <remarks>
    /// Borrowed leaves the COM reference caller-owned for the complete wrapper and GPU-use lifetime.
    /// Transferred hands the supplied reference to the RHI after Device/capability/pointer/ownership
    /// preconditions pass; the RHI releases it on every later failure or during terminal retirement.
    /// </remarks>
    public Buffer ImportBuffer(
        Device device,
        ID3D12Resource* resource,
        NativeObjectOwnership ownership,
        in BufferDesc desc,
        in ImportedResourceState state)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        _ = nativeDevice.RequireCapability<D3D12NativeAccess>(nameof(ImportBuffer));
        _ = nativeDevice.RequireCapability<ExternalResources>(nameof(ImportBuffer));
        bool ownsReference = BeginNativeImport(resource, ownership, nameof(resource));
        return ImportBufferCore(nativeDevice, resource, ownsReference, desc, state);
    }

    public Texture ImportTexture(
        Device device,
        ExternalHandle handle,
        in TextureDesc desc,
        in ImportedResourceState state)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        ExternalResources capability =
            nativeDevice.RequireCapability<ExternalResources>(nameof(ImportTexture));
        RequireExternalHandleType(capability.TextureImportHandleTypes, handle.Type, nameof(ImportTexture));
        ID3D12Resource* resource = OpenShared<ID3D12Resource>(nativeDevice, handle);
        return ImportTextureCore(nativeDevice, resource, ownsReference: true, desc, state);
    }

    /// <summary>Imports an existing D3D12 Texture resource without opening an OS handle.</summary>
    /// <remarks>
    /// Borrowed leaves the COM reference caller-owned for the complete wrapper and GPU-use lifetime.
    /// Transferred hands the supplied reference to the RHI after Device/capability/pointer/ownership
    /// preconditions pass; the RHI releases it on every later failure or during terminal retirement.
    /// </remarks>
    public Texture ImportTexture(
        Device device,
        ID3D12Resource* resource,
        NativeObjectOwnership ownership,
        in TextureDesc desc,
        in ImportedResourceState state)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        _ = nativeDevice.RequireCapability<D3D12NativeAccess>(nameof(ImportTexture));
        _ = nativeDevice.RequireCapability<ExternalResources>(nameof(ImportTexture));
        bool ownsReference = BeginNativeImport(resource, ownership, nameof(resource));
        return ImportTextureCore(nativeDevice, resource, ownsReference, desc, state);
    }

    public Heap ImportHeap(
        Device device,
        ExternalHandle handle,
        in HeapDesc desc)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        ExternalResources capability =
            nativeDevice.RequireCapability<ExternalResources>(nameof(ImportHeap));
        RequireExternalHandleType(capability.HeapImportHandleTypes, handle.Type, nameof(ImportHeap));
        ID3D12Heap* heap = OpenShared<ID3D12Heap>(nativeDevice, handle);
        return ImportHeapCore(nativeDevice, heap, ownsReference: true, desc);
    }

    /// <summary>Imports an existing D3D12 Heap without opening an OS handle.</summary>
    /// <remarks>
    /// Borrowed leaves the COM reference caller-owned for the complete wrapper and GPU-use lifetime.
    /// Transferred hands the supplied reference to the RHI after Device/capability/pointer/ownership
    /// preconditions pass; the RHI releases it on every later failure or during terminal retirement.
    /// </remarks>
    public Heap ImportHeap(
        Device device,
        ID3D12Heap* heap,
        NativeObjectOwnership ownership,
        in HeapDesc desc)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        _ = nativeDevice.RequireCapability<D3D12NativeAccess>(nameof(ImportHeap));
        _ = nativeDevice.RequireCapability<ExternalResources>(nameof(ImportHeap));
        bool ownsReference = BeginNativeImport(heap, ownership, nameof(heap));
        return ImportHeapCore(nativeDevice, heap, ownsReference, desc);
    }

    public ExternalHandle ExportBuffer(Buffer buffer, ExternalHandleType type)
    {
        D3D12Buffer native = NativeCast.Buffer(buffer);
        D3D12Device device = NativeCast.Device(buffer.Device);
        ExternalResources capability =
            device.RequireCapability<ExternalResources>(nameof(ExportBuffer));
        RequireExternalHandleType(capability.BufferExportHandleTypes, type, nameof(ExportBuffer));
        if ((native.Info.Usages & BufferUsages.Shareable) == 0)
            throw new ArgumentException("The Buffer was not created as shareable.", nameof(buffer));
        return CreateSharedHandle(
            device,
            (ID3D12DeviceChild*)native.Native,
            type);
    }

    public ExternalHandle ExportTexture(Texture texture, ExternalHandleType type)
    {
        D3D12TextureResource native = NativeCast.Texture(texture);
        D3D12Device device = NativeCast.Device(texture.Device);
        ExternalResources capability =
            device.RequireCapability<ExternalResources>(nameof(ExportTexture));
        RequireExternalHandleType(capability.TextureExportHandleTypes, type, nameof(ExportTexture));
        if ((native.Info.Usages & TextureUsages.Shareable) == 0)
            throw new ArgumentException("The Texture was not created as shareable.", nameof(texture));
        return CreateSharedHandle(
            device,
            (ID3D12DeviceChild*)native.Native,
            type);
    }

    public ExternalHandle ExportHeap(Heap heap, ExternalHandleType type)
    {
        D3D12Heap native = NativeCast.Heap(heap);
        D3D12Device device = NativeCast.Device(heap.Device);
        ExternalResources capability =
            device.RequireCapability<ExternalResources>(nameof(ExportHeap));
        RequireExternalHandleType(capability.HeapExportHandleTypes, type, nameof(ExportHeap));
        if ((native.Info.Flags & HeapFlags.Shareable) == 0)
            throw new ArgumentException("The Heap was not created as shareable.", nameof(heap));
        return CreateSharedHandle(
            device,
            (ID3D12DeviceChild*)native.Native,
            type);
    }

    public ExternalTimeline CreateExternalTimeline(
        Device device,
        ulong initialValue,
        string? label = null)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        _ = nativeDevice.RequireCapability<ExternalTimelines>(nameof(CreateExternalTimeline));
        ID3D12Fence* fence = null;
        Guid iid = ID3D12Fence.Guid;
        NativeCall.ThrowIfFailed(
            nativeDevice.Native->CreateFence(
                initialValue,
                FenceFlags.Shared,
                &iid,
                (void**)&fence),
            "ID3D12Device::CreateFence");
        try
        {
            D3D12ExternalTimeline result = new(
                nativeDevice,
                fence,
                label,
                ownsReference: true);
            fence = null;
            nativeDevice.RegisterChild(result);
            return result;
        }
        finally
        {
            if (fence is not null)
                _ = fence->Release();
        }
    }

    public ExternalTimeline ImportTimeline(
        Device device,
        ExternalHandle handle,
        string? label = null)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        ExternalTimelines capability =
            nativeDevice.RequireCapability<ExternalTimelines>(nameof(ImportTimeline));
        RequireExternalHandleType(capability.ImportHandleTypes, handle.Type, nameof(ImportTimeline));
        ID3D12Fence* fence = OpenShared<ID3D12Fence>(nativeDevice, handle);
        try
        {
            D3D12ExternalTimeline result = new(
                nativeDevice,
                fence,
                label,
                ownsReference: true);
            fence = null;
            nativeDevice.RegisterChild(result);
            return result;
        }
        finally
        {
            if (fence is not null)
                _ = fence->Release();
        }
    }

    private Buffer ImportBufferCore(
        D3D12Device device,
        ID3D12Resource* resource,
        bool ownsReference,
        in BufferDesc desc,
        in ImportedResourceState state)
    {
        D3D12Buffer? result = null;
        try
        {
            RequireNativeObjectDevice(device, (ID3D12DeviceChild*)resource, nameof(resource));
            ValidateImportedResourceState(state, texture: false);
            NativeResourceDesc expectedDescription = CreateBufferDescription(desc);
            ValidateImportedResourceDescription(resource->GetDesc(), expectedDescription, "Buffer");
            MemoryRequirements requirements = GetBufferMemoryRequirements(device, desc);
            result = new D3D12Buffer(
                device,
                heap: null,
                resource,
                new BufferInfo(
                    desc.Size,
                    desc.Usages,
                    MemoryType.DeviceLocal,
                    0,
                    requirements.Size),
                state.Sync,
                state.Access,
                desc.Label,
                state.QueueType,
                ownsReference);
            device.RegisterChild(result);
            return result;
        }
        catch
        {
            if (result is not null)
                result.Dispose();
            else if (ownsReference)
                _ = resource->Release();
            throw;
        }
    }

    private Texture ImportTextureCore(
        D3D12Device device,
        ID3D12Resource* resource,
        bool ownsReference,
        in TextureDesc desc,
        in ImportedResourceState state)
    {
        D3D12Texture? result = null;
        try
        {
            RequireNativeObjectDevice(device, (ID3D12DeviceChild*)resource, nameof(resource));
            ValidateImportedResourceState(state, texture: true);
            TextureLayout layout = state.Layout!.Value;
            NativeResourceDesc expectedDescription = CreateTextureDescription(desc);
            ValidateImportedResourceDescription(resource->GetDesc(), expectedDescription, "Texture");
            MemoryRequirements requirements = GetTextureMemoryRequirements(device, desc);
            result = new D3D12Texture(
                device,
                heap: null,
                resource,
                CreateTextureInfo(desc, 0, requirements.Size),
                desc.Label,
                state.Sync,
                state.Access,
                layout,
                state.QueueType,
                ownsReference);
            device.RegisterChild(result);
            return result;
        }
        catch
        {
            if (result is not null)
                result.Dispose();
            else if (ownsReference)
                _ = resource->Release();
            throw;
        }
    }

    private static Heap ImportHeapCore(
        D3D12Device device,
        ID3D12Heap* heap,
        bool ownsReference,
        in HeapDesc desc)
    {
        D3D12Heap? result = null;
        try
        {
            RequireNativeObjectDevice(device, (ID3D12DeviceChild*)heap, nameof(heap));
            ValidateHeapDescription(device, desc);
            ValidateImportedHeapDescription(heap->GetDesc(), desc);
            result = new D3D12Heap(
                device,
                heap,
                new HeapInfo(
                    desc.Size,
                    desc.Alignment,
                    desc.MemoryType,
                    desc.Flags,
                    desc.CreationNodeMask,
                    desc.VisibleNodeMask),
                desc.Label,
                ownsReference);
            device.RegisterChild(result);
            return result;
        }
        catch
        {
            if (result is not null)
                result.Dispose();
            else if (ownsReference)
                _ = heap->Release();
            throw;
        }
    }

    private static bool BeginNativeImport(
        void* value,
        NativeObjectOwnership ownership,
        string parameterName)
    {
        if (value is null)
            throw new ArgumentNullException(parameterName);
        return ownership switch
        {
            NativeObjectOwnership.Borrowed => false,
            NativeObjectOwnership.Transferred => true,
            _ => throw new ArgumentOutOfRangeException(nameof(ownership)),
        };
    }

    private static void RequireNativeObjectDevice(
        D3D12Device expected,
        ID3D12DeviceChild* value,
        string parameterName)
    {
        ID3D12Device10* actual = null;
        try
        {
            Guid iid = ID3D12Device10.Guid;
            NativeCall.ThrowIfFailed(
                value->GetDevice(&iid, (void**)&actual),
                "ID3D12DeviceChild::GetDevice");
            if (actual != expected.Native)
            {
                throw new ArgumentException(
                    "The native object belongs to another D3D12 Device.",
                    parameterName);
            }
        }
        finally
        {
            if (actual is not null)
                _ = actual->Release();
        }
    }

    private static void ValidateImportedResourceState(
        in ImportedResourceState state,
        bool texture)
    {
        _ = ToBarrierSync(state.Sync);
        _ = ToBarrierAccess(state.Access);
        if (!Enum.IsDefined(state.QueueType))
            throw new ArgumentOutOfRangeException(nameof(state), "The imported Queue type is unknown.");
        if (texture)
        {
            if (state.Layout is not TextureLayout layout)
                throw new ArgumentException("An imported Texture requires its current layout.", nameof(state));
            _ = ToBarrierLayout(layout);
        }
        else if (state.Layout is not null)
        {
            throw new ArgumentException("An imported Buffer cannot have a Texture layout.", nameof(state));
        }
    }

    private static void ValidateImportedResourceDescription(
        in NativeResourceDesc actual,
        in NativeResourceDesc expected,
        string resourceType)
    {
        if (actual.Dimension != expected.Dimension ||
            !MatchesResolvedAlignment(actual.Alignment, expected.Alignment) ||
            actual.Width != expected.Width ||
            actual.Height != expected.Height ||
            actual.DepthOrArraySize != expected.DepthOrArraySize ||
            actual.MipLevels != expected.MipLevels ||
            actual.Format != expected.Format ||
            actual.SampleDesc.Count != expected.SampleDesc.Count ||
            actual.SampleDesc.Quality != expected.SampleDesc.Quality ||
            actual.Layout != expected.Layout ||
            actual.Flags != expected.Flags)
        {
            throw new ArgumentException(
                $"The imported native {resourceType} description does not match the public description. " +
                $"Actual=({actual.Dimension},{actual.Alignment},{actual.Width},{actual.Height}," +
                $"{actual.DepthOrArraySize},{actual.MipLevels},{actual.Format}," +
                $"{actual.SampleDesc.Count}:{actual.SampleDesc.Quality},{actual.Layout},{actual.Flags}); " +
                $"Expected=({expected.Dimension},{expected.Alignment},{expected.Width},{expected.Height}," +
                $"{expected.DepthOrArraySize},{expected.MipLevels},{expected.Format}," +
                $"{expected.SampleDesc.Count}:{expected.SampleDesc.Quality},{expected.Layout},{expected.Flags}).",
                "desc");
        }
    }

    private static void ValidateImportedHeapDescription(
        in NativeHeapDesc actual,
        in SomeEngine.Graphics.HeapDesc expected)
    {
        HeapProperties properties = CreateHeapProperties(
            expected.MemoryType,
            expected.CreationNodeMask,
            expected.VisibleNodeMask);
        if (actual.SizeInBytes != expected.Size ||
            !MatchesResolvedAlignment(actual.Alignment, expected.Alignment) ||
            actual.Flags != ToNativeHeapFlags(expected.Flags) ||
            actual.Properties.Type != properties.Type ||
            actual.Properties.CPUPageProperty != properties.CPUPageProperty ||
            actual.Properties.MemoryPoolPreference != properties.MemoryPoolPreference ||
            actual.Properties.CreationNodeMask != properties.CreationNodeMask ||
            actual.Properties.VisibleNodeMask != properties.VisibleNodeMask)
        {
            throw new ArgumentException(
                "The imported native Heap description does not match the public description. " +
                $"Actual=({actual.SizeInBytes},{actual.Alignment},{actual.Flags}," +
                $"{actual.Properties.Type},{actual.Properties.CPUPageProperty}," +
                $"{actual.Properties.MemoryPoolPreference},{actual.Properties.CreationNodeMask}," +
                $"{actual.Properties.VisibleNodeMask}); " +
                $"Expected=({expected.Size},{expected.Alignment},{ToNativeHeapFlags(expected.Flags)}," +
                $"{properties.Type},{properties.CPUPageProperty},{properties.MemoryPoolPreference}," +
                $"{properties.CreationNodeMask},{properties.VisibleNodeMask}).",
                "desc");
        }
    }

    private static bool MatchesResolvedAlignment(ulong actual, ulong requested) =>
        requested == 0
            ? actual is 0 or 65_536 or 4_194_304
            : actual == requested;

    public ExternalHandle ExportTimeline(ExternalTimeline timeline, ExternalHandleType type)
    {
        D3D12ExternalTimeline native = NativeCast.Timeline(timeline);
        D3D12Device device = NativeCast.Device(timeline.Device);
        ExternalTimelines capability =
            device.RequireCapability<ExternalTimelines>(nameof(ExportTimeline));
        RequireExternalHandleType(capability.ExportHandleTypes, type, nameof(ExportTimeline));
        return CreateSharedHandle(
            device,
            (ID3D12DeviceChild*)native.Native,
            type);
    }

    private static T* OpenShared<T>(D3D12Device device, ExternalHandle handle)
        where T : unmanaged, IComVtbl<T>
    {
        if (handle.Type != ExternalHandleType.OpaqueWin32)
            throw new NotSupportedException("D3D12 supports NT shared handles through this RHI path.");
        T* result = null;
        Guid iid = typeof(T) == typeof(ID3D12Resource)
            ? ID3D12Resource.Guid
            : typeof(T) == typeof(ID3D12Heap)
                ? ID3D12Heap.Guid
                : ID3D12Fence.Guid;
        NativeCall.ThrowIfFailed(
            device.Native->OpenSharedHandle((void*)handle.Value, &iid, (void**)&result),
            "ID3D12Device::OpenSharedHandle");
        return result;
    }

    private static void RequireExternalHandleType(
        ExternalHandleTypes supported,
        ExternalHandleType type,
        string operation)
    {
        ExternalHandleTypes required = type switch
        {
            ExternalHandleType.OpaqueWin32 => ExternalHandleTypes.OpaqueWin32,
            ExternalHandleType.OpaqueWin32Kmt => ExternalHandleTypes.OpaqueWin32Kmt,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
        if ((supported & required) == 0)
        {
            throw new NotSupportedException(
                $"{operation} does not support external handle type {type}.");
        }
    }

    private static ExternalHandle CreateSharedHandle(
        D3D12Device device,
        ID3D12DeviceChild* value,
        ExternalHandleType type)
    {
        if (type != ExternalHandleType.OpaqueWin32)
            throw new NotSupportedException("D3D12 supports NT shared handles through this RHI path.");
        void* handle = null;
        NativeCall.ThrowIfFailed(
            device.Native->CreateSharedHandle(
                value,
                null,
                GenericAll,
                (char*)null,
                &handle),
            "ID3D12Device::CreateSharedHandle");
        return new ExternalHandle(
            type,
            (nint)handle,
            static value => _ = SilkMarshal.CloseWindowsHandle(value));
    }

    private sealed class D3D12ExternalTimeline : ExternalTimeline
    {
        private readonly D3D12Device _device;
        private readonly NativeLease _native;
        private int _released;

        internal D3D12ExternalTimeline(
            D3D12Device device,
            ID3D12Fence* native,
            string? label,
            bool ownsReference)
            : base(device, label)
        {
            _device = device;
            _native = new NativeLease((IUnknown*)native, ownsReference);
        }

        internal ID3D12Fence* Native => (ID3D12Fence*)_native.Pointer;

        internal void RetainSubmission()
        {
            if (_device.RetirementType == RetirementType.Automatic)
                _native.Retain();
        }

        internal void ReleaseSubmission()
        {
            if (_device.RetirementType == RetirementType.Automatic)
                _native.Release();
        }

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            _native.Release();
            _device.UnregisterChild(this);
        }
    }

    private static partial class NativeCast
    {
        internal static D3D12ExternalTimeline Timeline(ExternalTimeline value)
        {
#if DEBUG
            return (D3D12ExternalTimeline)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<ExternalTimeline, D3D12ExternalTimeline>(ref value);
#endif
        }
    }
}
