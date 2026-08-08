using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using NativeRayTracingTier = Silk.NET.Direct3D12.RaytracingTier;
using NativeSamplerFeedbackTier = Silk.NET.Direct3D12.SamplerFeedbackTier;
using NativeFeature = Silk.NET.Direct3D12.Feature;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceHung = unchecked((int)0x887A0006);
    private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);
    private const int DxgiErrorDriverInternalError = unchecked((int)0x887A0020);

    public Queue GetQueue(Device device, QueueType type, uint index = 0) =>
        NativeCast.Device(device).GetQueue(type, index);

    public bool TryGetCapability<TCapability>(
        Device device,
        out TCapability? capability)
        where TCapability : DeviceCapability =>
        NativeCast.Device(device).TryGetCapability(out capability);

    public void CollectCompleted(Device device) =>
        NativeCast.Device(device).CollectCompleted();

    internal GraphicsException RemoveDeviceForTesting(Device device)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        nativeDevice.ThrowIfUnavailable();
        nativeDevice.Native->RemoveDevice();
        int reason = nativeDevice.Native->GetDeviceRemovedReason();
        try
        {
            ThrowIfDeviceFailed(
                nativeDevice,
                reason,
                "ID3D12Device::RemoveDevice(test injection)");
        }
        catch (GraphicsException exception) when (exception.Error == GraphicsError.DeviceLost)
        {
            return exception;
        }

        throw new InvalidOperationException(
            "ID3D12Device::RemoveDevice did not report a device-removal HRESULT.");
    }

    private static bool IsDeviceRemovalCode(long result) =>
        result is DxgiErrorDeviceRemoved or
            DxgiErrorDeviceHung or
            DxgiErrorDeviceReset or
            DxgiErrorDriverInternalError;

    private static bool IsDeviceRemoval(Exception exception) =>
        exception is GraphicsException { NativeCode: long code } && IsDeviceRemovalCode(code);

    private static GraphicsException CreateDeviceLoss(
        D3D12Device device,
        long? reportedCode,
        string message,
        Exception? innerException = null)
    {
        long reason = reportedCode ?? DxgiErrorDeviceRemoved;
        if (device.Native is not null)
        {
            long queriedReason = device.Native->GetDeviceRemovedReason();
            if (queriedReason < 0)
                reason = queriedReason;
        }

        GraphicsException loss = new(
            GraphicsError.DeviceLost,
            message,
            reason,
            diagnostic: FormatDeviceRemovalDiagnostic(reason),
            innerException: innerException);
        return device.MarkLost(loss);
    }

    private static string FormatDeviceRemovalDiagnostic(long reason)
    {
        string name = reason switch
        {
            DxgiErrorDeviceRemoved => "DXGI_ERROR_DEVICE_REMOVED",
            DxgiErrorDeviceHung => "DXGI_ERROR_DEVICE_HUNG",
            DxgiErrorDeviceReset => "DXGI_ERROR_DEVICE_RESET",
            DxgiErrorDriverInternalError => "DXGI_ERROR_DRIVER_INTERNAL_ERROR",
            _ => "unknown device-removal reason",
        };
        return $"ID3D12Device::GetDeviceRemovedReason returned {name} " +
            $"(0x{unchecked((uint)reason):X8}).";
    }

    private static void ThrowIfDeviceFailed(
        D3D12Device device,
        int result,
        string operation)
    {
        if (result >= 0)
            return;
        if (IsDeviceRemovalCode(result))
        {
            throw CreateDeviceLoss(
                device,
                result,
                $"{operation} detected D3D12 device removal.");
        }
        NativeCall.ThrowIfFailed(result, operation);
    }

    private sealed partial class D3D12Device : Device
    {
        private readonly D3D12Backend _backend;
        private readonly object _childrenGate = new();
        private readonly HashSet<GraphicsObject> _children =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<D3D12RecordedCommandsLease> _commandPayloads =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Type, DeviceCapability> _capabilities = [];
        private readonly Dictionary<(QueueType Type, uint Index), D3D12Queue> _queues = [];

        private IDXGIAdapter4* _adapter;
        private ID3D12Device10* _native;
        private int _released;

        private D3D12Device(
            D3D12Backend backend,
            IDXGIAdapter4* adapter,
            ID3D12Device10* native,
            in AdapterInfo adapterInfo,
            in DeviceDesc description,
            in FeatureSnapshot features)
            : base(
                adapterInfo,
                CreateDeviceCapabilities(features),
                description.RetirementType,
                description.EnabledNodeMask,
                description.Label)
        {
            _backend = backend;
            _adapter = adapter;
            _native = native;
            RuntimeIdentity = backend;
            try
            {
                ResourceDescriptors = new DescriptorAllocator(
                    this,
                    DescriptorHeapType.CbvSrvUav,
                    4_096,
                    shaderVisible: false,
                    maximumHeapCount: int.MaxValue);
                SamplerDescriptors = new DescriptorAllocator(
                    this,
                    DescriptorHeapType.Sampler,
                    1_024,
                    shaderVisible: false,
                    maximumHeapCount: int.MaxValue);
                RenderTargetDescriptors = new DescriptorAllocator(
                    this,
                    DescriptorHeapType.Rtv,
                    512,
                    shaderVisible: false,
                    maximumHeapCount: int.MaxValue);
                DepthStencilDescriptors = new DescriptorAllocator(
                    this,
                    DescriptorHeapType.Dsv,
                    512,
                    shaderVisible: false,
                    maximumHeapCount: int.MaxValue);
                Descriptors = new DescriptorPublisher(this);

                CreateQueues(description.Queues);
                MaterializeCapabilities(features);
            }
            catch
            {
                ReleaseQueues();
                ReleaseAdvancedCommandSignatures();
                ReleaseResidencyInfrastructure();
                Descriptors?.Dispose();
                DepthStencilDescriptors?.Dispose();
                RenderTargetDescriptors?.Dispose();
                SamplerDescriptors?.Dispose();
                ResourceDescriptors?.Dispose();
                _capabilities.Clear();
                throw;
            }
        }

        internal ID3D12Device10* Native => _native;
        internal D3D12Backend Backend => _backend;
        internal IDXGIAdapter4* NativeAdapter => _adapter;
        internal bool EnhancedBarriers => CapabilitiesSnapshot.EnhancedBarriers;
        internal FeatureSnapshot CapabilitiesSnapshot { get; private init; }
        internal DescriptorAllocator ResourceDescriptors { get; }
        internal DescriptorAllocator SamplerDescriptors { get; }
        internal DescriptorAllocator RenderTargetDescriptors { get; }
        internal DescriptorAllocator DepthStencilDescriptors { get; }
        internal DescriptorPublisher Descriptors { get; }
        internal SparseResources? SparseCapability { get; private set; }

        internal static D3D12Device Create(
            D3D12Backend backend,
            IDXGIAdapter4* adapter,
            in AdapterInfo adapterInfo,
            in DeviceDesc description)
        {
            ID3D12Device10* native = null;
            Guid iid = ID3D12Device10.Guid;
            NativeCall.ThrowIfFailed(
                backend._d3d12.CreateDevice(
                    (IUnknown*)adapter,
                    D3DFeatureLevel.Level120,
                    &iid,
                    (void**)&native),
                "D3D12CreateDevice");

            try
            {
                FeatureSnapshot features = FeatureSnapshot.Query(native);
                DeviceFeatures missing = description.RequiredFeatures & ~features.AvailableFeatures;
                if (missing != DeviceFeatures.None)
                {
                    throw new GraphicsException(
                        GraphicsError.NativeFailure,
                        $"The selected adapter does not provide required Device features: {missing}.");
                }

                uint validNodeMask = features.NodeCount == 32
                    ? uint.MaxValue
                    : (1u << checked((int)features.NodeCount)) - 1u;
                if (description.EnabledNodeMask == 0 ||
                    (description.EnabledNodeMask & ~validNodeMask) != 0)
                {
                    throw new GraphicsException(
                        GraphicsError.NativeFailure,
                        "DeviceDesc.EnabledNodeMask selects a node that is not available.");
                }

                D3D12Device device = new(
                    backend,
                    adapter,
                    native,
                    adapterInfo,
                    description,
                    features)
                {
                    CapabilitiesSnapshot = features,
                };
                return device;
            }
            catch
            {
                if (native is not null)
                    _ = native->Release();
                throw;
            }
        }

        internal D3D12Queue GetQueue(QueueType type, uint index)
        {
            ThrowIfUnavailable();
            if (!_queues.TryGetValue((type, index), out D3D12Queue? queue))
                throw new ArgumentOutOfRangeException(nameof(index));
            return queue;
        }

        internal bool TryGetCapability<TCapability>(out TCapability? capability)
            where TCapability : DeviceCapability
        {
            ThrowIfUnavailable();
            if (_capabilities.TryGetValue(typeof(TCapability), out DeviceCapability? value))
            {
                capability = (TCapability)value;
                return true;
            }

            capability = null;
            return false;
        }

        internal GraphicsException MarkLost(GraphicsException exception)
        {
            if (!TryMarkLost(exception))
                return Loss ?? exception;

            GraphicsObject[] children;
            D3D12RecordedCommandsLease[] commandPayloads;
            lock (_childrenGate)
            {
                children = [.. _children];
                commandPayloads = [.. _commandPayloads];
            }
            foreach (GraphicsObject child in children)
            {
                if (child is D3D12Swapchain swapchain)
                    swapchain.MarkDeviceLost();
            }
            foreach (GraphicsObject child in children)
            {
                if (child is D3D12CommandContext context)
                    context.MarkDeviceLost();
            }
            foreach (D3D12RecordedCommandsLease payload in commandPayloads)
                payload.MarkDeviceLostFromDevice();
            foreach (D3D12Queue queue in _queues.Values)
                queue.MarkDeviceLost();
            return exception;
        }

        internal void ActivateCommandPayload(
            D3D12RecordedCommandsLease payload,
            ulong sequence)
        {
            lock (_childrenGate)
            {
                ThrowIfUnavailable();
                if (!_commandPayloads.Add(payload))
                    throw new InvalidOperationException("The command payload is already active.");
                try
                {
                    payload.ActivateCommands(sequence);
                }
                catch
                {
                    _commandPayloads.Remove(payload);
                    throw;
                }
            }
        }

        internal void UnregisterCommandPayload(D3D12RecordedCommandsLease payload)
        {
            lock (_childrenGate)
                _commandPayloads.Remove(payload);
        }

        internal void RegisterChild(GraphicsObject child)
        {
            bool registeredWithDevice = false;
            try
            {
                lock (_childrenGate)
                {
                    ThrowIfUnavailable();
                    _children.Add(child);
                    registeredWithDevice = true;
                }
                _backend.Register(child);
            }
            catch
            {
                if (registeredWithDevice)
                {
                    lock (_childrenGate)
                        _children.Remove(child);
                }
                child.DisposeFromParent();
                throw;
            }
        }

        internal void UnregisterChild(GraphicsObject child)
        {
            lock (_childrenGate)
                _children.Remove(child);
            _backend.Unregister(child);
        }

        internal void CollectCompleted()
        {
            ThrowIfUnavailable();
            foreach (D3D12Queue queue in _queues.Values)
                queue.CollectCompleted();
        }

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            GraphicsObject[] children;
            D3D12RecordedCommandsLease[] commandPayloads;
            lock (_childrenGate)
            {
                MarkDisposed();
                children = [.. _children];
                commandPayloads = [.. _commandPayloads];
            }
            foreach (D3D12RecordedCommandsLease payload in commandPayloads)
                payload.DiscardExecutableFromDevice();
            foreach (GraphicsObject child in children)
                child.DisposeFromParent();
            lock (_childrenGate)
            {
                _children.Clear();
                _commandPayloads.Clear();
            }

            ReleaseNative();
            _backend.Unregister(this);
        }

        private void CreateQueues(ReadOnlySpan<DeviceQueueDesc> descriptions)
        {
            uint nodeIndex = (uint)BitOperations.TrailingZeroCount(EnabledNodeMask);
            uint nodeMask = 1u << checked((int)nodeIndex);

            foreach (ref readonly DeviceQueueDesc description in descriptions)
            {
                for (uint index = 0; index < description.Count; index++)
                {
                    CommandQueueDesc nativeDescription = new(
                        ToCommandListType(description.Type),
                        description.Priority > 0.5f
                            ? (int)CommandQueuePriority.High
                            : (int)CommandQueuePriority.Normal,
                        CommandQueueFlags.None,
                        nodeMask);

                    ID3D12CommandQueue* nativeQueue = null;
                    Guid queueIid = ID3D12CommandQueue.Guid;
                    NativeCall.ThrowIfFailed(
                        _native->CreateCommandQueue(
                            &nativeDescription,
                            &queueIid,
                            (void**)&nativeQueue),
                        "ID3D12Device::CreateCommandQueue");

                    ID3D12Fence* fence = null;
                    Guid fenceIid = ID3D12Fence.Guid;
                    try
                    {
                        NativeCall.ThrowIfFailed(
                            _native->CreateFence(
                                0,
                                FenceFlags.None,
                                &fenceIid,
                                (void**)&fence),
                            "ID3D12Device::CreateFence");

                        _queues.Add(
                            (description.Type, index),
                            new D3D12Queue(
                                this,
                                description.Type,
                                index,
                                description.Priority,
                                nodeIndex,
                                nativeQueue,
                                fence));
                    }
                    catch
                    {
                        _ = nativeQueue->Release();
                        if (fence is not null)
                            _ = fence->Release();
                        throw;
                    }
                }
            }
        }

        private void MaterializeCapabilities(in FeatureSnapshot features)
        {
            if ((features.AvailableFeatures & DeviceFeatures.SparseResources) != 0)
            {
                SparseCapability = new SparseResources(
                    this,
                    (uint)features.Options.TiledResourcesTier,
                    64 * 1024,
                    bufferSupported: true,
                    features.SparseTexture2DFormats,
                    features.SparseTexture3DFormats,
                    maximumMappingsPerCall: uint.MaxValue);
                Add(SparseCapability);
            }

            if ((features.AvailableFeatures & DeviceFeatures.SamplerFeedback) != 0)
            {
                Add(new SamplerFeedback(
                    this,
                    features.Options7.SamplerFeedbackTier == NativeSamplerFeedbackTier.Tier10
                        ? SomeEngine.Graphics.SamplerFeedbackTier.Tier1_0
                        : SomeEngine.Graphics.SamplerFeedbackTier.Tier0_9,
                    features.SamplerFeedbackFormats,
                    4,
                    4,
                    64 * 1024));
            }

            if ((features.AvailableFeatures & DeviceFeatures.Residency) != 0)
                Add(new Residency(this, localMemory: true, nonLocalMemory: true));

            if ((features.AvailableFeatures & DeviceFeatures.RayTracing) != 0)
            {
                InitializeRayDispatchSignature();
                Add(new RayTracing(
                    this,
                    features.Options5.RaytracingTier >= NativeRayTracingTier.Tier11
                        ? SomeEngine.Graphics.RayTracingTier.Tier1_1
                        : SomeEngine.Graphics.RayTracingTier.Tier1_0,
                    inlineRayQuery: features.Options5.RaytracingTier >= NativeRayTracingTier.Tier11,
                    indirectDispatch: features.Options5.RaytracingTier >= NativeRayTracingTier.Tier11,
                    accelerationStructureUpdate: true,
                    compaction: true,
                    serialization: true,
                    stateObjectAdditions: features.Options5.RaytracingTier >= NativeRayTracingTier.Tier11,
                    maximumRecursionDepth: 31,
                    maximumPayloadSize: uint.MaxValue,
                    maximumAttributeSize: 32,
                    maximumGeometriesPerBottomLevel: 16_777_216,
                    maximumInstancesPerTopLevel: 16_777_216,
                    maximumPrimitivesPerBottomLevel: 536_870_912,
                    maximumRayGenerationShaderThreads: 1_073_741_824,
                    maximumShaderRecordStride: 4_096,
                    accelerationStructureAlignment: 256,
                    scratchAlignment: 256,
                    shaderTableAlignment: 64,
                    shaderRecordAlignment: 32));
            }

            if ((features.AvailableFeatures & DeviceFeatures.MeshShaders) != 0)
            {
                InitializeMeshDispatchSignature();
                Add(new MeshShaders(
                    this,
                    amplificationShaders: true,
                    indirectDispatch: true,
                    maximumThreadGroupCountX: 65_535,
                    maximumThreadGroupCountY: 65_535,
                    maximumThreadGroupCountZ: 65_535,
                    maximumTotalThreadGroupCount: 4_194_303,
                    maximumThreadsPerGroup: 128,
                    maximumPayloadSize: 16 * 1024,
                    maximumSharedMemory: 28 * 1024,
                    maximumOutputVertices: 256,
                    maximumOutputPrimitives: 256));
            }

            if ((features.AvailableFeatures & DeviceFeatures.VariableRateShading) != 0)
            {
                ShadingRate[] rates = features.Options6.AdditionalShadingRatesSupported
                    ? [ShadingRate.Rate1x1, ShadingRate.Rate1x2, ShadingRate.Rate2x1,
                        ShadingRate.Rate2x2, ShadingRate.Rate2x4, ShadingRate.Rate4x2,
                        ShadingRate.Rate4x4]
                    : [ShadingRate.Rate1x1, ShadingRate.Rate1x2, ShadingRate.Rate2x1,
                        ShadingRate.Rate2x2];
                ShadingRateCombiner[] combiners =
                [
                    ShadingRateCombiner.Passthrough,
                    ShadingRateCombiner.Override,
                    ShadingRateCombiner.Minimum,
                    ShadingRateCombiner.Maximum,
                    ShadingRateCombiner.Sum,
                ];
                Add(new VariableRateShading(
                    this,
                    rates,
                    combiners,
                    features.Options6.PerPrimitiveShadingRateSupportedWithViewportIndexing,
                    features.Options6.VariableShadingRateTier >= VariableShadingRateTier.Tier2,
                    features.Options6.AdditionalShadingRatesSupported,
                    features.Options6.ShadingRateImageTileSize,
                    features.Options6.ShadingRateImageTileSize));
            }

            if ((features.AvailableFeatures & DeviceFeatures.WorkGraphs) != 0)
            {
                Add(new WorkGraphs(
                    this,
                    WorkGraphTier.Tier1_0,
                    cpuInput: true,
                    gpuInput: true,
                    maximumNodeCount: 0x00FF_FFFF,
                    maximumInputRecordSize: 0xFFFF_FFFC,
                    maximumOutputRecordSize: 32 * 1_024,
                    maximumInputRecordCount: uint.MaxValue));
            }

            Add(new IndirectCommands(
                this,
                IndirectArgumentTypes.Draw |
                IndirectArgumentTypes.DrawIndexed |
                IndirectArgumentTypes.Dispatch |
                (features.Options7.MeshShaderTier != MeshShaderTier.TierNotSupported
                    ? IndirectArgumentTypes.DispatchMesh
                    : IndirectArgumentTypes.None) |
                (features.Options5.RaytracingTier >= NativeRayTracingTier.Tier11
                    ? IndirectArgumentTypes.DispatchRays
                    : IndirectArgumentTypes.None) |
                IndirectArgumentTypes.VertexBuffer |
                IndirectArgumentTypes.IndexBuffer |
                IndirectArgumentTypes.Constants |
                IndirectArgumentTypes.ConstantBuffer |
                IndirectArgumentTypes.ShaderResource |
                IndirectArgumentTypes.UnorderedAccess,
                argumentBufferAlignment: 4,
                countBufferAlignment: 4,
                maximumCommandCount: uint.MaxValue,
                maximumStride: 0xFFFF_FFFC));
            Add(new CalibratedTimestamps(this));

            if (features.NodeCount > 1)
            {
                uint mask = features.NodeCount == 32
                    ? uint.MaxValue
                    : (1u << checked((int)features.NodeCount)) - 1u;
                Add(new LinkedAdapters(this, features.NodeCount, mask, mask, mask, mask));
            }

            Add(new ExternalResources(
                this,
                ExternalHandleTypes.OpaqueWin32,
                ExternalHandleTypes.OpaqueWin32,
                ExternalHandleTypes.OpaqueWin32,
                ExternalHandleTypes.OpaqueWin32,
                ExternalHandleTypes.OpaqueWin32,
                ExternalHandleTypes.OpaqueWin32));
            Add(new ExternalTimelines(
                this,
                ExternalHandleTypes.OpaqueWin32,
                ExternalHandleTypes.OpaqueWin32));
            Add(new D3D12NativeAccess(this));
            Add(new D3D12Diagnostics(
                this,
                _backend._debugLayerEnabled,
                _backend._gpuBasedValidationEnabled,
                _backend._synchronizedQueueValidationEnabled,
                _backend._dredEnabled));
        }

        private void Add(DeviceCapability capability) =>
            _capabilities.Add(capability.GetType(), capability);

        private void ReleaseNative()
        {
            ReleaseQueues();
            ReleaseAdvancedCommandSignatures();
            ReleaseResidencyInfrastructure();
            Descriptors.Dispose();
            DepthStencilDescriptors.Dispose();
            RenderTargetDescriptors.Dispose();
            SamplerDescriptors.Dispose();
            ResourceDescriptors.Dispose();

            ID3D12Device10* native = _native;
            _native = null;
            if (native is not null)
                _ = native->Release();

            IDXGIAdapter4* adapter = _adapter;
            _adapter = null;
            if (adapter is not null)
                _ = adapter->Release();
        }

        private void ReleaseQueues()
        {
            foreach (D3D12Queue queue in _queues.Values)
                queue.ReleaseNative();
            _queues.Clear();
        }

        private static DeviceCapabilities CreateDeviceCapabilities(in FeatureSnapshot features) =>
            new(
                features.AvailableFeatures,
                new DeviceLimits(
                    MaximumBufferSize: ulong.MaxValue,
                    MaximumTextureDimension1D: 16_384,
                    MaximumTextureDimension2D: 16_384,
                    MaximumTextureDimension3D: 2_048,
                    MaximumTextureArrayLayers: 2_048,
                    MaximumColorAttachments: 8,
                    MaximumViewports: 16,
                    ResourceDescriptorCapacity: 1_000_000,
                    SamplerDescriptorCapacity: 2_048,
                    ConstantBufferAlignment: 256,
                    TextureDataPitchAlignment: 256,
                    TextureDataPlacementAlignment: 512),
                supportsBundles: true,
                supportsPipelineStatistics: true,
                supportsStreamOutputStatistics: true);
    }

    private sealed partial class D3D12Queue : Queue
    {
        private readonly D3D12Device _device;
        private ulong _nextCompletion = 1;
        private int _released;

        internal D3D12Queue(
            D3D12Device device,
            QueueType type,
            uint index,
            float priority,
            uint nodeIndex,
            ID3D12CommandQueue* native,
            ID3D12Fence* fence)
            : base(device, type, index, priority, nodeIndex)
        {
            _device = device;
            Native = native;
            Fence = fence;
        }

        internal object Gate { get; } = new();
        internal ID3D12CommandQueue* Native { get; private set; }
        internal ID3D12Fence* Fence { get; private set; }

        internal QueueCompletion SignalCompletion()
        {
            lock (Gate)
                return SignalCompletionUnderGate();
        }

        internal QueueCompletion SignalCompletionUnderGate()
        {
            if (_nextCompletion == ulong.MaxValue)
                throw new InvalidOperationException("The Queue completion domain is exhausted.");
            ulong value = _nextCompletion;
            ThrowIfDeviceFailed(
                _device,
                Native->Signal(Fence, value),
                "ID3D12CommandQueue::Signal");
            _nextCompletion = value + 1;
            return new QueueCompletion(this, value);
        }

        internal bool IsComplete(ulong value)
        {
            _device.ThrowIfUnavailable();
            ulong completed = Fence->GetCompletedValue();
            if (completed == ulong.MaxValue)
            {
                throw CreateDeviceLoss(
                    _device,
                    DxgiErrorDeviceRemoved,
                    "D3D12 reported the device-removal completion sentinel.");
            }
            return completed >= value;
        }

        internal void CollectCompleted()
        {
            ulong completed = Fence->GetCompletedValue();
            CollectRetiredPayloads(completed);
            CollectCapabilityRetirements(completed);
        }

        internal void ReleaseNative()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            DrainOrAbandonPayloads();

            ID3D12Fence* fence = Fence;
            Fence = null;
            if (fence is not null)
                _ = fence->Release();

            ID3D12CommandQueue* queue = Native;
            Native = null;
            if (queue is not null)
                _ = queue->Release();
        }
    }

    private readonly struct FeatureSnapshot
    {
        internal FeatureSnapshot(
            uint nodeCount,
            DeviceFeatures availableFeatures,
            in FeatureDataD3D12Options options,
            in FeatureDataD3D12Options5 options5,
            in FeatureDataD3D12Options6 options6,
            in FeatureDataD3D12Options7 options7,
            in FeatureDataD3D12Options12 options12,
            in FeatureDataD3D12Options21 options21,
            Format[] sparseTexture2DFormats,
            Format[] sparseTexture3DFormats,
            Format[] samplerFeedbackFormats)
        {
            NodeCount = nodeCount;
            AvailableFeatures = availableFeatures;
            Options = options;
            Options5 = options5;
            Options6 = options6;
            Options7 = options7;
            Options12 = options12;
            Options21 = options21;
            SparseTexture2DFormats = sparseTexture2DFormats;
            SparseTexture3DFormats = sparseTexture3DFormats;
            SamplerFeedbackFormats = samplerFeedbackFormats;
        }

        internal uint NodeCount { get; }
        internal DeviceFeatures AvailableFeatures { get; }
        internal FeatureDataD3D12Options Options { get; }
        internal FeatureDataD3D12Options5 Options5 { get; }
        internal FeatureDataD3D12Options6 Options6 { get; }
        internal FeatureDataD3D12Options7 Options7 { get; }
        internal FeatureDataD3D12Options12 Options12 { get; }
        internal FeatureDataD3D12Options21 Options21 { get; }
        internal Format[] SparseTexture2DFormats { get; }
        internal Format[] SparseTexture3DFormats { get; }
        internal Format[] SamplerFeedbackFormats { get; }
        internal bool EnhancedBarriers => Options12.EnhancedBarriersSupported;

        internal static FeatureSnapshot Query(ID3D12Device10* device)
        {
            FeatureDataD3D12Options options = QueryFeature<FeatureDataD3D12Options>(
                device,
                NativeFeature.D3D12Options);
            FeatureDataD3D12Options5 options5 = QueryFeature<FeatureDataD3D12Options5>(
                device,
                NativeFeature.D3D12Options5);
            FeatureDataD3D12Options6 options6 = QueryFeature<FeatureDataD3D12Options6>(
                device,
                NativeFeature.D3D12Options6);
            FeatureDataD3D12Options7 options7 = QueryFeature<FeatureDataD3D12Options7>(
                device,
                NativeFeature.D3D12Options7);
            FeatureDataD3D12Options12 options12 = QueryFeature<FeatureDataD3D12Options12>(
                device,
                NativeFeature.D3D12Options12);
            FeatureDataD3D12Options21 options21 = QueryFeature<FeatureDataD3D12Options21>(
                device,
                NativeFeature.D3D12Options21);

            DeviceFeatures features =
                DeviceFeatures.Presentation |
                DeviceFeatures.Residency |
                DeviceFeatures.IndirectCommands |
                DeviceFeatures.CalibratedTimestamps |
                DeviceFeatures.ExternalResources |
                DeviceFeatures.ExternalTimelines;

            Format[] sparseTexture2DFormats = [];
            Format[] sparseTexture3DFormats = [];
            if (options.TiledResourcesTier != TiledResourcesTier.TierNotSupported)
            {
                features |= DeviceFeatures.SparseResources;
                (sparseTexture2DFormats, sparseTexture3DFormats) =
                    QuerySparseTextureFormats(device, options.TiledResourcesTier);
            }
            if (options5.RaytracingTier != NativeRayTracingTier.TierNotSupported)
                features |= DeviceFeatures.RayTracing;
            if (options6.VariableShadingRateTier != VariableShadingRateTier.TierNotSupported)
                features |= DeviceFeatures.VariableRateShading;
            if (options7.MeshShaderTier != MeshShaderTier.TierNotSupported)
                features |= DeviceFeatures.MeshShaders;
            Format[] samplerFeedbackFormats =
                options7.SamplerFeedbackTier == NativeSamplerFeedbackTier.TierNotSupported
                    ? []
                    : QuerySamplerFeedbackFormats(device);
            if (samplerFeedbackFormats.Length != 0)
                features |= DeviceFeatures.SamplerFeedback;
            if (options21.WorkGraphsTier != Silk.NET.Direct3D12.WorkGraphsTier.TierNotSupported)
                features |= DeviceFeatures.WorkGraphs;

            uint nodeCount = device->GetNodeCount();
            if (nodeCount > 1)
                features |= DeviceFeatures.LinkedAdapters;

            return new FeatureSnapshot(
                nodeCount,
                features,
                options,
                options5,
                options6,
                options7,
                options12,
                options21,
                sparseTexture2DFormats,
                sparseTexture3DFormats,
                samplerFeedbackFormats);
        }

        private static (Format[] Texture2D, Format[] Texture3D)
            QuerySparseTextureFormats(
                ID3D12Device10* device,
                TiledResourcesTier tier)
        {
            List<Format> texture2D = [];
            List<Format> texture3D = [];
            foreach (Format format in Enum.GetValues<Format>())
            {
                FeatureDataFormatSupport support = new(FormatMappings.ToDxgi(format));
                int query = device->CheckFeatureSupport(
                    NativeFeature.FormatSupport,
                    &support,
                    (uint)sizeof(FeatureDataFormatSupport));
                if (query < 0 || (support.Support2 & FormatSupport2.Tiled) == 0)
                    continue;

                if ((support.Support1 & FormatSupport1.Texture2D) != 0)
                    texture2D.Add(format);
                if (tier >= TiledResourcesTier.Tier3 &&
                    (support.Support1 & FormatSupport1.Texture3D) != 0)
                {
                    texture3D.Add(format);
                }
            }
            return ([.. texture2D], [.. texture3D]);
        }

        private static Format[] QuerySamplerFeedbackFormats(ID3D12Device10* device)
        {
            if (!SupportsSamplerFeedbackFormat(
                    device,
                    Silk.NET.DXGI.Format.FormatSamplerFeedbackMinMipOpaque) ||
                !SupportsSamplerFeedbackFormat(
                    device,
                    Silk.NET.DXGI.Format.FormatSamplerFeedbackMipRegionUsedOpaque))
            {
                return [];
            }

            List<Format> result = [];
            foreach (Format format in Enum.GetValues<Format>())
            {
                FeatureDataFormatSupport support = new(FormatMappings.ToDxgi(format));
                int query = device->CheckFeatureSupport(
                    NativeFeature.FormatSupport,
                    &support,
                    (uint)sizeof(FeatureDataFormatSupport));
                if (query >= 0 &&
                    (support.Support1 & FormatSupport1.Texture2D) != 0 &&
                    (support.Support1 & FormatSupport1.ShaderSample) != 0)
                {
                    result.Add(format);
                }
            }
            return [.. result];
        }

        private static bool SupportsSamplerFeedbackFormat(
            ID3D12Device10* device,
            Silk.NET.DXGI.Format format)
        {
            FeatureDataFormatSupport support = new(format);
            int query = device->CheckFeatureSupport(
                NativeFeature.FormatSupport,
                &support,
                (uint)sizeof(FeatureDataFormatSupport));
            return query >= 0 &&
                (support.Support1 & FormatSupport1.Texture2D) != 0 &&
                (support.Support2 & FormatSupport2.SamplerFeedback) != 0;
        }

        private static T QueryFeature<T>(ID3D12Device10* device, NativeFeature feature)
            where T : unmanaged
        {
            T value = default;
            NativeCall.ThrowIfFailed(
                device->CheckFeatureSupport(feature, &value, (uint)sizeof(T)),
                $"ID3D12Device::CheckFeatureSupport({feature})");
            return value;
        }
    }

    private static CommandListType ToCommandListType(QueueType type) => type switch
    {
        QueueType.Graphics => CommandListType.Direct,
        QueueType.Compute => CommandListType.Compute,
        QueueType.Copy => CommandListType.Copy,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
    private static partial class NativeCast
    {
        internal static D3D12Device Device(Device value)
        {
#if DEBUG
            return (D3D12Device)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<Device, D3D12Device>(ref value);
#endif
        }

        internal static D3D12Queue Queue(Queue value)
        {
#if DEBUG
            return (D3D12Queue)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<Queue, D3D12Queue>(ref value);
#endif
        }
    }
}
