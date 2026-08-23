using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using NativeRayTracingTier = Silk.NET.Direct3D12.RaytracingTier;
using NativeSamplerFeedbackTier = Silk.NET.Direct3D12.SamplerFeedbackTier;
using NativeFeature = Silk.NET.Direct3D12.Feature;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    private enum NativeOperationType : byte
    {
        Ordinary,
        PipelineCreation,
    }

    private const int EOutOfMemory = unchecked((int)0x8007000E);
    private const int HResultNotEnoughMemory = unchecked((int)0x80070008);
    private const int DxgiErrorOutOfMemory = unchecked((int)0x887A000E);
    private const int DxgiErrorInvalidCall = unchecked((int)0x887A0001);
    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceHung = unchecked((int)0x887A0006);
    private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);
    private const int DxgiErrorDriverInternalError = unchecked((int)0x887A0020);

    public Queue GetQueue(Device device, QueueType type, uint index = 0) =>
        RequireDevice(device, nameof(device)).GetQueue(type, index);

    public bool TryGetCapability<TCapability>(
        Device device,
        out TCapability? capability)
        where TCapability : DeviceCapability =>
        RequireDevice(device, nameof(device)).TryGetCapability(out capability);

    public void CollectCompleted(Device device) =>
        RequireDevice(device, nameof(device)).CollectCompleted();

    private static bool IsDirectDeviceRemovalCode(long result) =>
        result is DxgiErrorDeviceRemoved or
            DxgiErrorDeviceHung or
            DxgiErrorDeviceReset or
            DxgiErrorDriverInternalError;

    private static bool IsOutOfMemoryCode(long result) =>
        result is EOutOfMemory or HResultNotEnoughMemory or DxgiErrorOutOfMemory;

    private static bool IsDeviceRemovedReason(long result) =>
        result == DxgiErrorInvalidCall || IsDirectDeviceRemovalCode(result);

    private static GraphicsException PublishDeviceLoss(
        D3D12Device device,
        int nativeCode,
        string message,
        int? queriedReason = null,
        string? nativeDiagnostic = null)
    {
        D3D12DeviceLossReport? dred = device.CaptureDredReport();
        string? diagnostic = queriedReason is int reason
            ? FormatDeviceRemovalDiagnostic(reason)
            : null;
        if (!string.IsNullOrWhiteSpace(nativeDiagnostic))
        {
            diagnostic = diagnostic is null
                ? nativeDiagnostic
                : $"{diagnostic}{Environment.NewLine}{nativeDiagnostic}";
        }
        if (dred is not null)
        {
            diagnostic = diagnostic is null
                ? dred.Text
                : $"{diagnostic}{Environment.NewLine}{dred.Text}";
        }
        GraphicsException loss = new(
            GraphicsError.DeviceLost,
            message,
            nativeCode,
            diagnostic);
        device.ConfirmNativeDeviceLoss();
        return device.MarkLost(loss);
    }

    private static string FormatDeviceRemovalDiagnostic(long reason)
    {
        string name = reason switch
        {
            DxgiErrorInvalidCall => "DXGI_ERROR_INVALID_CALL",
            DxgiErrorDeviceRemoved => "DXGI_ERROR_DEVICE_REMOVED",
            DxgiErrorDeviceHung => "DXGI_ERROR_DEVICE_HUNG",
            DxgiErrorDeviceReset => "DXGI_ERROR_DEVICE_RESET",
            DxgiErrorDriverInternalError => "DXGI_ERROR_DRIVER_INTERNAL_ERROR",
            _ => "unknown device-removal reason",
        };
        return $"ID3D12Device::GetDeviceRemovedReason returned {name} " +
            $"(0x{unchecked((uint)reason):X8}).";
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void ThrowIfFailed(
        D3D12Device? device,
        int result,
        NativeOperationType type,
        string operation,
        string? diagnostic = null)
    {
        if (result >= 0)
            return;
        ThrowFailure(device, result, type, operation, diagnostic);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowFailure(
        D3D12Device? device,
        int result,
        NativeOperationType type,
        string operation,
        string? diagnostic)
    {
        diagnostic = AppendDebugMessages(device, diagnostic);
        if (device is not null && IsDirectDeviceRemovalCode(result))
        {
            int? queriedReason = device.Native is null
                ? null
                : device.Native->GetDeviceRemovedReason();
            throw PublishDeviceLoss(
                device,
                result,
                $"{operation} detected D3D12 device removal.",
                queriedReason,
                diagnostic);
        }

        ThrowClassifiedFailure(result, type, operation, diagnostic);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowClassifiedFailure(
        int result,
        NativeOperationType type,
        string operation,
        string? diagnostic)
    {
        GraphicsError error = IsOutOfMemoryCode(result)
            ? GraphicsError.OutOfMemory
            : type == NativeOperationType.PipelineCreation
                ? GraphicsError.PipelineCreation
                : GraphicsError.NativeFailure;
        throw new GraphicsException(
            error,
            $"{operation} failed with HRESULT 0x{unchecked((uint)result):X8}.",
            result,
            diagnostic);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowAfterDeviceRemovedReasonQuery(
        D3D12Device device,
        int nativeCode,
        string operation)
    {
        int reason = device.Native is null
            ? 0
            : device.Native->GetDeviceRemovedReason();
        if (IsDeviceRemovedReason(reason))
        {
            throw PublishDeviceLoss(
                device,
                nativeCode,
                $"{operation} reported D3D12 device removal.",
                reason);
        }
        ThrowClassifiedFailure(
            nativeCode,
            NativeOperationType.Ordinary,
            operation,
            null);
    }

    private sealed partial class D3D12Device : Device
    {
        private static readonly InvalidOperationException CommandPayloadDetachFailure =
            new("A command payload did not detach during Device teardown.");
        private static readonly InvalidOperationException ChildDetachFailure =
            new("A Device child did not detach during teardown.");
        private readonly D3D12Backend _backend;
        private readonly object _childrenGate = new();
        private readonly System.Threading.Lock _commandPayloadGate = new();
        private readonly GraphicsObjectRegistry _children;
        private D3D12RecordedCommandsLease? _commandPayloadHead;
        private D3D12RecordedCommandsLease? _drainingCommandPayload;
        private D3D12RecordedCommandsLease? _retainedCommandPayloadHead;
        private readonly Dictionary<Type, DeviceCapability> _capabilities = [];
        private readonly Dictionary<(QueueType Type, uint Index), D3D12Queue> _queues = [];
        private readonly DescriptorPublisher?[] _descriptorsByNode =
            new DescriptorPublisher?[32];
        private readonly DescriptorAllocator?[] _resourceDescriptorsByNode =
            new DescriptorAllocator?[32];
        private readonly DescriptorAllocator?[] _samplerDescriptorsByNode =
            new DescriptorAllocator?[32];
        private readonly DescriptorAllocator?[] _renderTargetDescriptorsByNode =
            new DescriptorAllocator?[32];
        private readonly DescriptorAllocator?[] _depthStencilDescriptorsByNode =
            new DescriptorAllocator?[32];

        private IDXGIAdapter4* _adapter;
        private ID3D12Device10* _native;
        private int _nativeDeviceLossConfirmed;

        private D3D12Device(
            D3D12Backend backend,
            IDXGIAdapter4* adapter,
            ID3D12Device10* native,
            in AdapterInfo adapterInfo,
            in DeviceDesc description,
            in D3D12FeatureSupport features,
            DeviceFeatures enabledFeatures)
            : base(
                adapterInfo,
                CreateDeviceCapabilities(features),
                description.EnabledNodeMask,
                description.Label)
        {
            _backend = backend;
            _children = new GraphicsObjectRegistry(_childrenGate);
            _adapter = adapter;
            _native = native;
            SetNativeName(native, description.Label ?? "SomeEngine D3D12 Device");
            _resourceAllocator = new D3D12ResourceAllocator(this);
            _pipelineCompiler = new D3D12PipelineCompiler(this);
            PrimaryNodeIndex = checked((uint)BitOperations.TrailingZeroCount(description.EnabledNodeMask));
            PrimaryNodeMask = 1u << checked((int)PrimaryNodeIndex);
            BackendOwner = backend;
            try
            {
                uint descriptorNodes = EnabledNodeMask;
                while (descriptorNodes != 0)
                {
                    uint nodeIndex = checked((uint)BitOperations.TrailingZeroCount(descriptorNodes));
                    uint nodeMask = 1u << checked((int)nodeIndex);
                    _resourceDescriptorsByNode[nodeIndex] = new DescriptorAllocator(
                        this,
                        DescriptorHeapType.CbvSrvUav,
                        4_096,
                        shaderVisible: false,
                        maximumHeapCount: int.MaxValue,
                        nodeMask);
                    _samplerDescriptorsByNode[nodeIndex] = new DescriptorAllocator(
                        this,
                        DescriptorHeapType.Sampler,
                        1_024,
                        shaderVisible: false,
                        maximumHeapCount: int.MaxValue,
                        nodeMask);
                    _renderTargetDescriptorsByNode[nodeIndex] = new DescriptorAllocator(
                        this,
                        DescriptorHeapType.Rtv,
                        512,
                        shaderVisible: false,
                        maximumHeapCount: int.MaxValue,
                        nodeMask);
                    _depthStencilDescriptorsByNode[nodeIndex] = new DescriptorAllocator(
                        this,
                        DescriptorHeapType.Dsv,
                        512,
                        shaderVisible: false,
                        maximumHeapCount: int.MaxValue,
                        nodeMask);
                    _descriptorsByNode[nodeIndex] = new DescriptorPublisher(
                        this,
                        nodeMask: nodeMask);
                    descriptorNodes &= ~nodeMask;
                }

                CreateQueues(description.Queues);
                CreateCapabilities(features, enabledFeatures);
            }
            catch
            {
                ReleaseQueues();
                ReleaseAdvancedCommandSignatures();
                ReleaseResidencyInfrastructure();
                ReleaseDescriptorPublishers();
                ReleaseDescriptorAllocators();
                _pipelineCompiler.Dispose();
                _resourceAllocator.Dispose();
                _capabilities.Clear();
                throw;
            }
        }

        internal ID3D12Device10* Native => _native;
        internal D3D12Backend Backend => _backend;
        internal IDXGIAdapter4* NativeAdapter => _adapter;
        internal uint PrimaryNodeIndex { get; }
        internal uint PrimaryNodeMask { get; }
        internal bool EnhancedBarriers => FeatureSupport.EnhancedBarriers;
        internal D3D12FeatureSupport FeatureSupport { get; private init; }
        internal DescriptorAllocator ResourceDescriptors =>
            GetResourceDescriptors(PrimaryNodeIndex);
        internal DescriptorAllocator SamplerDescriptors =>
            GetSamplerDescriptors(PrimaryNodeIndex);
        internal DescriptorAllocator RenderTargetDescriptors =>
            GetRenderTargetDescriptors(PrimaryNodeIndex);
        internal DescriptorAllocator DepthStencilDescriptors =>
            GetDepthStencilDescriptors(PrimaryNodeIndex);
        internal DescriptorPublisher Descriptors => GetDescriptorPublisher(PrimaryNodeIndex);

        internal DescriptorPublisher GetDescriptorPublisher(uint nodeIndex)
        {
            uint resolved = ResolveNodeIndex(nodeIndex, nameof(nodeIndex));
            return _descriptorsByNode[resolved]
                ?? throw new InvalidOperationException(
                    "The enabled linked-adapter node has no descriptor publisher.");
        }

        internal DescriptorAllocator GetResourceDescriptors(uint nodeIndex) =>
            GetDescriptorAllocator(_resourceDescriptorsByNode, nodeIndex);

        internal DescriptorAllocator GetSamplerDescriptors(uint nodeIndex) =>
            GetDescriptorAllocator(_samplerDescriptorsByNode, nodeIndex);

        internal DescriptorAllocator GetRenderTargetDescriptors(uint nodeIndex) =>
            GetDescriptorAllocator(_renderTargetDescriptorsByNode, nodeIndex);

        internal DescriptorAllocator GetDepthStencilDescriptors(uint nodeIndex) =>
            GetDescriptorAllocator(_depthStencilDescriptorsByNode, nodeIndex);

        private DescriptorAllocator GetDescriptorAllocator(
            DescriptorAllocator?[] allocators,
            uint nodeIndex)
        {
            uint resolved = ResolveNodeIndex(nodeIndex, nameof(nodeIndex));
            return allocators[resolved]
                ?? throw new InvalidOperationException(
                    "The enabled linked-adapter node has no descriptor allocator.");
        }

        internal uint ResolveNodeIndex(uint requestedNodeIndex, string parameterName)
        {
            uint nodeIndex = requestedNodeIndex == uint.MaxValue
                ? PrimaryNodeIndex
                : requestedNodeIndex;
            if (nodeIndex >= 32 ||
                (EnabledNodeMask & (1u << checked((int)nodeIndex))) == 0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "The node index must select one enabled linked-adapter node.");
            }
            return nodeIndex;
        }

        internal uint ResolveNodeMask(uint requestedNodeIndex, string parameterName) =>
            1u << checked((int)ResolveNodeIndex(requestedNodeIndex, parameterName));

        internal uint ResolveResourceHomeNodeIndex(uint creationNodeMask)
        {
            if (creationNodeMask == 0)
                return PrimaryNodeIndex;
            if (!BitOperations.IsPow2(creationNodeMask) ||
                (creationNodeMask & ~EnabledNodeMask) != 0)
            {
                throw new InvalidOperationException(
                    "The resource reports an invalid linked-adapter creation-node mask.");
            }
            return checked((uint)BitOperations.TrailingZeroCount(creationNodeMask));
        }

        internal (uint CreationMask, uint VisibleMask) ResolveResourcePlacement(
            in ResourceNodePlacement placement,
            string parameterName)
        {
            uint creation = placement.CreationNodeMask == 0
                ? PrimaryNodeMask
                : placement.CreationNodeMask;
            if (!BitOperations.IsPow2(creation) || (creation & ~EnabledNodeMask) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "A resource creation node mask must contain exactly one enabled node.");
            }

            uint visible = placement.VisibleNodeMask == 0
                ? creation
                : placement.VisibleNodeMask;
            if ((visible & ~EnabledNodeMask) != 0 || (visible & creation) != creation)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "A resource visibility mask must contain its creation node and only enabled nodes.");
            }
            return (creation, visible);
        }

        private void ReleaseDescriptorPublishers()
        {
            foreach (DescriptorPublisher? publisher in _descriptorsByNode)
                publisher?.Dispose();
            Array.Clear(_descriptorsByNode);
        }

        private void ReleaseDescriptorAllocators()
        {
            ReleaseDescriptorAllocators(_depthStencilDescriptorsByNode);
            ReleaseDescriptorAllocators(_renderTargetDescriptorsByNode);
            ReleaseDescriptorAllocators(_samplerDescriptorsByNode);
            ReleaseDescriptorAllocators(_resourceDescriptorsByNode);
        }

        private static void ReleaseDescriptorAllocators(DescriptorAllocator?[] allocators)
        {
            foreach (DescriptorAllocator? allocator in allocators)
                allocator?.Dispose();
            Array.Clear(allocators);
        }

        internal static D3D12Device Create(
            D3D12Backend backend,
            IDXGIAdapter4* adapter,
            in AdapterInfo adapterInfo,
            in DeviceDesc description)
        {
            ID3D12Device10* native = null;
            Guid iid = ID3D12Device10.Guid;
            ThrowIfFailed(
                null,
                backend._deviceFactory->CreateDevice(
                    (IUnknown*)adapter,
                    D3DFeatureLevel.Level120,
                    &iid,
                    (void**)&native),
                NativeOperationType.Ordinary,
                "D3D12CreateDevice");

            try
            {
                D3D12FeatureSupport features = D3D12FeatureSupport.Query(native);
                DeviceFeatures usableFeatures = features.AvailableFeatures;
                bool hasGraphicsQueue = false;
                foreach (ref readonly DeviceQueueDesc queue in description.Queues)
                {
                    if (queue.Type == QueueType.Graphics)
                    {
                        hasGraphicsQueue = true;
                        break;
                    }
                }
                if (!hasGraphicsQueue)
                    usableFeatures &= ~DeviceFeatures.Presentation;

                DeviceFeatures missing = description.RequiredFeatures & ~usableFeatures;
                if (missing != DeviceFeatures.None)
                {
                    throw new GraphicsException(
                        GraphicsError.NativeFailure,
                        $"The selected adapter does not provide required Device features: {missing}.");
                }

                DeviceFeatures requested =
                    description.RequiredFeatures | description.OptionalFeatures;
                DeviceFeatures enabled = requested & usableFeatures;

                uint validNodeMask = features.NodeCount == 32
                    ? uint.MaxValue
                    : (1u << checked((int)features.NodeCount)) - 1u;
                if (description.EnabledNodeMask == 0 ||
                    (description.EnabledNodeMask & ~validNodeMask) != 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(description),
                        "DeviceDesc.EnabledNodeMask selects a node that is not available.");
                }

                D3D12Device device = new(
                    backend,
                    adapter,
                    native,
                    adapterInfo,
                    description,
                    features,
                    enabled)
                {
                    FeatureSupport = features,
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

        internal TCapability RequireCapability<TCapability>(string operation)
            where TCapability : DeviceCapability
        {
            ThrowIfUnavailable();
            if (_capabilities.TryGetValue(typeof(TCapability), out DeviceCapability? value))
                return (TCapability)value;
            throw new NotSupportedException(
                $"{operation} requires the unavailable {typeof(TCapability).Name} capability.");
        }

        internal GraphicsException MarkLost(GraphicsException exception)
        {
            if (!TryMarkLost(exception))
                return Loss ?? exception;

            _pipelineCompiler.MarkDeviceLost(exception);

            foreach (D3D12Queue queue in _queues.Values)
                queue.InvalidateNativeLock();

            GraphicsObject? lostChildren = null;
            D3D12RecordedCommandsLease? lostPayloads = null;
            lock (_childrenGate)
            {
                lostChildren = _children.BuildWorkList(
                    static child => child is D3D12Swapchain or D3D12CommandContext);
            }
            lock (_commandPayloadGate)
            {
                for (D3D12RecordedCommandsLease? payload = _commandPayloadHead;
                     payload is not null;
                     payload = payload.DeviceNext)
                {
                    payload.DeviceLossWorkNext = lostPayloads;
                    lostPayloads = payload;
                }
            }

            while (lostChildren is GraphicsObject child)
            {
                lostChildren = child.DeviceLossWorkNext;
                child.DeviceLossWorkNext = null;
                if (child is D3D12Swapchain swapchain)
                    swapchain.MarkDeviceLost();
                else
                    ((D3D12CommandContext)child).MarkDeviceLost();
            }
            while (lostPayloads is D3D12RecordedCommandsLease payload)
            {
                lostPayloads = payload.DeviceLossWorkNext;
                payload.DeviceLossWorkNext = null;
                payload.MarkDeviceLostFromDevice();
            }
            return exception;
        }

        internal bool NativeDeviceLossConfirmed =>
            Volatile.Read(ref _nativeDeviceLossConfirmed) != 0;

        internal void ConfirmNativeDeviceLoss() =>
            Interlocked.Exchange(ref _nativeDeviceLossConfirmed, 1);

        internal void ActivateCommandPayload(
            D3D12RecordedCommandsLease payload,
            ulong sequence)
        {
            payload.ActivateCommands(sequence);
            try
            {
                lock (_commandPayloadGate)
                {
                    ThrowIfUnavailable();
                    if (payload.DeviceRegistered)
                    {
                        throw new InvalidOperationException(
                            "The command payload is already active.");
                    }
                    payload.DeviceRegistered = true;
                    payload.DevicePrevious = null;
                    payload.DeviceNext = _commandPayloadHead;
                    if (_commandPayloadHead is not null)
                        _commandPayloadHead.DevicePrevious = payload;
                    _commandPayloadHead = payload;
                }
            }
            catch
            {
                payload.CancelCommandsActivation(sequence);
                throw;
            }
        }

        internal void UnregisterCommandPayload(D3D12RecordedCommandsLease payload)
        {
            lock (_commandPayloadGate)
                RemoveCommandPayloadUnderGate(payload);
        }

        internal void RegisterChild(GraphicsObject child)
        {
            try
            {
                ThrowIfUnavailable();
                _children.Add(child);
            }
            catch
            {
                child.DisposeFromParent();
                throw;
            }
        }

        internal void UnregisterChild(GraphicsObject child)
        {
            _children.Remove(child);
        }

        internal void CollectCompleted()
        {
            ThrowIfUnavailable();
            foreach (D3D12Queue queue in _queues.Values)
                queue.CollectCompleted();
        }

        internal override void Release(bool fromParent)
        {
            lock (_childrenGate)
                MarkDisposed();
            _pipelineCompiler.Dispose();
            foreach (D3D12Queue queue in _queues.Values)
                queue.InvalidateNativeLock();
            foreach (D3D12Queue queue in _queues.Values)
            {
                try
                {
                    queue.QuiescePayloads();
                }
                catch (Exception exception)
                {
                    RecordReleaseFailure(exception);
                }
            }
            while (TakeCommandPayloadForDrain() is D3D12RecordedCommandsLease payload)
            {
                payload.DiscardExecutableFromDevice();
                if (CompleteCommandPayloadDrain(payload))
                    RecordReleaseFailure(CommandPayloadDetachFailure);
            }
            GraphicsObject? children = _children.CloseAndBuildDrainList();
            while (children is GraphicsObject child)
            {
                children = child.RegistryDrainNext;
                child.RegistryDrainNext = null;
                child.DisposeFromParent();
                if (_children.CompleteDrain(child))
                    RecordReleaseFailure(ChildDetachFailure);
            }

            if (_retainedCommandPayloadHead is not null ||
                _children.HasRetainedFailures ||
                TeardownFailure is not null)
            {
                return;
            }
            ReleaseNative();
            _backend.Unregister(this);
        }

        private void RemoveCommandPayloadUnderGate(D3D12RecordedCommandsLease payload)
        {
            if (!payload.DeviceRegistered)
                return;
            if (ReferenceEquals(_drainingCommandPayload, payload))
            {
                payload.DeviceRegistered = false;
                return;
            }
            if (payload.DevicePrevious is null)
                _commandPayloadHead = payload.DeviceNext;
            else
                payload.DevicePrevious.DeviceNext = payload.DeviceNext;
            if (payload.DeviceNext is not null)
                payload.DeviceNext.DevicePrevious = payload.DevicePrevious;
            payload.DeviceRegistered = false;
            payload.DevicePrevious = null;
            payload.DeviceNext = null;
        }

        private D3D12RecordedCommandsLease? TakeCommandPayloadForDrain()
        {
            lock (_commandPayloadGate)
            {
                D3D12RecordedCommandsLease? payload = _commandPayloadHead;
                if (payload is null)
                    return null;
                _commandPayloadHead = payload.DeviceNext;
                if (_commandPayloadHead is not null)
                    _commandPayloadHead.DevicePrevious = null;
                payload.DevicePrevious = null;
                payload.DeviceNext = null;
                _drainingCommandPayload = payload;
                return payload;
            }
        }

        private bool CompleteCommandPayloadDrain(D3D12RecordedCommandsLease payload)
        {
            lock (_commandPayloadGate)
            {
                _drainingCommandPayload = null;
                if (!payload.DeviceRegistered)
                    return false;
                payload.DeviceNext = _retainedCommandPayloadHead;
                _retainedCommandPayloadHead = payload;
                return true;
            }
        }

        private void CreateQueues(ReadOnlySpan<DeviceQueueDesc> descriptions)
        {
            var nextIndices = new Dictionary<QueueType, uint>();
            foreach (ref readonly DeviceQueueDesc description in descriptions)
            {
                uint nodeMask = 1u << checked((int)description.NodeIndex);
                nextIndices.TryGetValue(description.Type, out uint firstIndex);
                for (uint offset = 0; offset < description.Count; offset++)
                {
                    uint index = checked(firstIndex + offset);
                    CommandQueueDesc nativeDescription = new(
                        ToCommandListType(description.Type),
                        description.Priority > 0.5f
                            ? (int)CommandQueuePriority.High
                            : (int)CommandQueuePriority.Normal,
                        CommandQueueFlags.None,
                        nodeMask);

                    ID3D12CommandQueue* nativeQueue = null;
                    Guid queueIid = ID3D12CommandQueue.Guid;
                    ThrowIfFailed(
                        this,
                        _native->CreateCommandQueue(
                            &nativeDescription,
                            &queueIid,
                            (void**)&nativeQueue),
                        NativeOperationType.Ordinary,
                        "ID3D12Device::CreateCommandQueue");
                    string queueName = $"{description.Type} Queue[{index}]";
                    SetNativeName(nativeQueue, queueName);

                    ID3D12Fence* fence = null;
                    Guid fenceIid = ID3D12Fence.Guid;
                    try
                    {
                        ThrowIfFailed(
                            this,
                            _native->CreateFence(
                                0,
                                FenceFlags.None,
                                &fenceIid,
                                (void**)&fence),
                            NativeOperationType.Ordinary,
                            "ID3D12Device::CreateFence");
                        SetNativeName(fence, $"{queueName} Completion Fence");

                        _queues.Add(
                            (description.Type, index),
                            new D3D12Queue(
                                this,
                                description.Type,
                                index,
                                description.Priority,
                                nodeMask,
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
                nextIndices[description.Type] = checked(firstIndex + description.Count);
            }
        }

        private void CreateCapabilities(
            in D3D12FeatureSupport features,
            DeviceFeatures enabledFeatures)
        {
            AddSparseCapabilities(features, enabledFeatures);
            AddRayTracingCapability(features, enabledFeatures);
            AddMeshAndShadingCapabilities(features, enabledFeatures);
            AddDispatchCapabilities(features, enabledFeatures);
            AddPlatformCapabilities(features, enabledFeatures);
            Add(new PipelineCreationSupport(
                this,
                PipelineCreationFeatures.PersistentCacheData));
            Add(new D3D12NativeAccess(this));
            Add(new D3D12Diagnostics(
                this,
                _backend._debugLayerEnabled,
                _backend._gpuBasedValidationEnabled,
                _backend._synchronizedQueueValidationEnabled,
                _backend._dredEnabled));
        }

        private void AddSparseCapabilities(
            in D3D12FeatureSupport features,
            DeviceFeatures enabledFeatures)
        {
            if ((enabledFeatures & DeviceFeatures.SparseResources) != 0)
            {
                Add(new SparseResources(
                    this,
                    (uint)features.Options.TiledResourcesTier,
                    64 * 1024,
                    bufferSupported: true,
                    features.SparseTexture2DFormats,
                    features.SparseTexture3DFormats,
                    maximumMappingsPerCall: uint.MaxValue));
            }

            if ((enabledFeatures & DeviceFeatures.SamplerFeedback) != 0)
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

            if ((enabledFeatures & DeviceFeatures.Residency) != 0)
                Add(new Residency(this, localMemory: true, nonLocalMemory: true));
        }

        private void AddRayTracingCapability(
            in D3D12FeatureSupport features,
            DeviceFeatures enabledFeatures)
        {
            if ((enabledFeatures & DeviceFeatures.RayTracing) == 0)
                return;

            InitializeRayDispatchSignature();
            bool tier11 = features.Options5.RaytracingTier >= NativeRayTracingTier.Tier11;
            Add(new RayTracing(
                this,
                tier11
                    ? SomeEngine.Graphics.RayTracingTier.Tier1_1
                    : SomeEngine.Graphics.RayTracingTier.Tier1_0,
                pipelineRayTracing: true,
                inlineRayQuery: tier11,
                indirectDispatch: tier11,
                accelerationStructureUpdate: true,
                compaction: true,
                serialization: true,
                stateObjectAdditions: tier11,
                shaderRecordResourceBindings: true,
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

        private void AddMeshAndShadingCapabilities(
            in D3D12FeatureSupport features,
            DeviceFeatures enabledFeatures)
        {
            if ((enabledFeatures & DeviceFeatures.MeshShaders) != 0)
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

            if ((enabledFeatures & DeviceFeatures.VariableRateShading) == 0)
                return;

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

        private void AddDispatchCapabilities(
            in D3D12FeatureSupport features,
            DeviceFeatures enabledFeatures)
        {
            if ((enabledFeatures & DeviceFeatures.WorkGraphs) != 0)
            {
                Add(new WorkGraphs(
                    this,
                    WorkGraphTier.Tier1_0,
                    cpuInput: true,
                    gpuInput: true,
                    maximumNodeCount: 0x00FF_FFFF,
                    maximumInputRecordSize: 0xFFFF_FFFC,
                    maximumOutputRecordSize: 32 * 1_024,
                    maximumDispatchGridDimension: 65_535,
                    maximumDispatchGridVolume: 0x00FF_FFFF));
            }

            if ((enabledFeatures & DeviceFeatures.IndirectCommands) == 0)
                return;

            Span<IndirectArgumentType> supportedArguments = stackalloc IndirectArgumentType[9];
            int count = 0;
            supportedArguments[count++] = IndirectArgumentType.Draw;
            supportedArguments[count++] = IndirectArgumentType.DrawIndexed;
            supportedArguments[count++] = IndirectArgumentType.Dispatch;
            if ((enabledFeatures & DeviceFeatures.MeshShaders) != 0)
                supportedArguments[count++] = IndirectArgumentType.DispatchMesh;
            if ((enabledFeatures & DeviceFeatures.RayTracing) != 0 &&
                features.Options5.RaytracingTier >= NativeRayTracingTier.Tier11)
            {
                supportedArguments[count++] = IndirectArgumentType.DispatchRays;
            }
            supportedArguments[count++] = IndirectArgumentType.VertexBuffer;
            supportedArguments[count++] = IndirectArgumentType.IndexBuffer;
            supportedArguments[count++] = IndirectArgumentType.Constants;
            supportedArguments[count++] = IndirectArgumentType.ConstantBuffer;

            Add(new IndirectCommands(
                this,
                supportedArguments[..count],
                argumentBufferAlignment: 4,
                countBufferAlignment: 4,
                maximumCommandCount: uint.MaxValue,
                maximumStride: 0xFFFF_FFFC));
        }

        private void AddPlatformCapabilities(
            in D3D12FeatureSupport features,
            DeviceFeatures enabledFeatures)
        {
            if ((enabledFeatures & DeviceFeatures.Presentation) != 0)
                Add(new Presentation(this));

            if ((enabledFeatures & DeviceFeatures.CalibratedTimestamps) != 0)
                Add(new CalibratedTimestamps(this));

            if ((enabledFeatures & DeviceFeatures.LinkedAdapters) != 0)
            {
                uint mask = features.NodeCount == 32
                    ? uint.MaxValue
                    : (1u << checked((int)features.NodeCount)) - 1u;
                Add(new LinkedAdapters(this, features.NodeCount, mask, mask, mask, mask));
            }

            ExternalHandleType[] win32Handles = [ExternalHandleType.OpaqueWin32];
            if ((enabledFeatures & DeviceFeatures.ExternalResources) != 0)
            {
                Add(new ExternalResources(
                    this,
                    win32Handles,
                    win32Handles,
                    win32Handles,
                    win32Handles,
                    win32Handles,
                    win32Handles));
            }
            if ((enabledFeatures & DeviceFeatures.ExternalTimelines) != 0)
                Add(new ExternalTimelines(this, win32Handles, win32Handles));
        }

        private void Add(DeviceCapability capability) =>
            _capabilities.Add(capability.GetType(), capability);

        private void ReleaseNative()
        {
            ReleaseQueues();
            ReleaseAdvancedCommandSignatures();
            ReleaseResidencyInfrastructure();
            ReleaseDescriptorPublishers();
            ReleaseDescriptorAllocators();
            _resourceAllocator.Dispose();

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

        private static DeviceCapabilities CreateDeviceCapabilities(
            in D3D12FeatureSupport features) =>
            new(
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
                supportsStreamOutputStatistics: true,
                supportsDepthBounds: features.DepthBoundsSupported,
                supportedDynamicStates: GetSupportedDynamicStates(features),
                features.Formats);

        private static DynamicStates GetSupportedDynamicStates(
            in D3D12FeatureSupport features)
        {
            DynamicStates result =
                DynamicStates.Viewport |
                DynamicStates.Scissor |
                DynamicStates.BlendConstants |
                DynamicStates.StencilReference |
                DynamicStates.PrimitiveTopology;
            if (features.DepthBoundsSupported)
                result |= DynamicStates.DepthBounds;
            if (features.DynamicDepthBiasSupported)
                result |= DynamicStates.DepthBias;
            if (features.DynamicIndexBufferStripCutSupported)
                result |= DynamicStates.StripCut;
            return result;
        }
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
            uint nodeMask,
            ID3D12CommandQueue* native,
            ID3D12Fence* fence)
            : base(
                device,
                type,
                index,
                priority,
                checked((uint)BitOperations.TrailingZeroCount(nodeMask)))
        {
            _device = device;
            _nativeLock = new D3D12NativeQueueLockLease(this);
            NodeMask = nodeMask;
            Native = native;
            Fence = fence;
        }

        internal QueueExclusion Gate { get; } = new();
        internal uint NodeMask { get; }
        internal ID3D12CommandQueue* Native { get; private set; }
        internal ID3D12Fence* Fence { get; private set; }

        internal QueueCompletion SignalCompletion()
        {
            using (Gate.EnterScope())
                return SignalCompletionUnderGate();
        }

        internal QueueCompletion SignalCompletionUnderGate()
        {
            if (_nextCompletion == ulong.MaxValue)
                throw new InvalidOperationException("The Queue completion domain is exhausted.");
            ulong value = _nextCompletion;
            ThrowIfFailed(
                _device,
                Native->Signal(Fence, value),
                NativeOperationType.Ordinary,
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
                throw PublishDeviceLoss(
                    _device,
                    DxgiErrorDeviceRemoved,
                    "D3D12 reported the device-removal completion sentinel.",
                    DxgiErrorDeviceRemoved);
            }
            return completed >= value;
        }

        internal void CollectCompleted()
        {
            using (Gate.EnterScope())
                CollectCompletedUnderGate();
        }

        internal void ReleaseNative()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            ID3D12Fence* fence = Fence;
            Fence = null;
            if (fence is not null)
                _ = fence->Release();

            ID3D12CommandQueue* queue = Native;
            Native = null;
            if (queue is not null)
                _ = queue->Release();
        }

        internal void QuiescePayloads() => DrainOrAbandonPayloads();
    }

    private readonly struct D3D12FeatureSupport
    {
        internal D3D12FeatureSupport(
            uint nodeCount,
            DeviceFeatures availableFeatures,
            in FeatureDataD3D12Options options,
            in FeatureDataD3D12Options2 options2,
            in FeatureDataD3D12Options5 options5,
            in FeatureDataD3D12Options6 options6,
            in FeatureDataD3D12Options7 options7,
            in FeatureDataD3D12Options12 options12,
            in FeatureDataD3D12Options15 options15,
            in FeatureDataD3D12Options16 options16,
            in FeatureDataD3D12Options21 options21,
            FormatSupport[] formats,
            Format[] sparseTexture2DFormats,
            Format[] sparseTexture3DFormats,
            Format[] samplerFeedbackFormats)
        {
            NodeCount = nodeCount;
            AvailableFeatures = availableFeatures;
            Options = options;
            Options2 = options2;
            Options5 = options5;
            Options6 = options6;
            Options7 = options7;
            Options12 = options12;
            Options15 = options15;
            Options16 = options16;
            Options21 = options21;
            Formats = formats;
            SparseTexture2DFormats = sparseTexture2DFormats;
            SparseTexture3DFormats = sparseTexture3DFormats;
            SamplerFeedbackFormats = samplerFeedbackFormats;
        }

        internal uint NodeCount { get; }
        internal DeviceFeatures AvailableFeatures { get; }
        internal FeatureDataD3D12Options Options { get; }
        internal FeatureDataD3D12Options2 Options2 { get; }
        internal FeatureDataD3D12Options5 Options5 { get; }
        internal FeatureDataD3D12Options6 Options6 { get; }
        internal FeatureDataD3D12Options7 Options7 { get; }
        internal FeatureDataD3D12Options12 Options12 { get; }
        internal FeatureDataD3D12Options15 Options15 { get; }
        internal FeatureDataD3D12Options16 Options16 { get; }
        internal FeatureDataD3D12Options21 Options21 { get; }
        internal FormatSupport[] Formats { get; }
        internal Format[] SparseTexture2DFormats { get; }
        internal Format[] SparseTexture3DFormats { get; }
        internal Format[] SamplerFeedbackFormats { get; }
        internal bool EnhancedBarriers => Options12.EnhancedBarriersSupported;
        internal bool DepthBoundsSupported => Options2.DepthBoundsTestSupported;
        internal bool DynamicIndexBufferStripCutSupported =>
            Options15.DynamicIndexBufferStripCutSupported;
        internal bool DynamicDepthBiasSupported => Options16.DynamicDepthBiasSupported;

        internal static D3D12FeatureSupport Query(ID3D12Device10* device)
        {
            FeatureDataD3D12Options options = QueryFeature<FeatureDataD3D12Options>(
                device,
                NativeFeature.D3D12Options);
            FeatureDataD3D12Options2 options2 = QueryFeature<FeatureDataD3D12Options2>(
                device,
                NativeFeature.D3D12Options2);
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
            FeatureDataD3D12Options15 options15 = QueryFeature<FeatureDataD3D12Options15>(
                device,
                NativeFeature.D3D12Options15);
            FeatureDataD3D12Options16 options16 = QueryFeature<FeatureDataD3D12Options16>(
                device,
                NativeFeature.D3D12Options16);
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

            bool samplerFeedbackFormatsAvailable =
                options7.SamplerFeedbackTier != NativeSamplerFeedbackTier.TierNotSupported &&
                SupportsSamplerFeedbackOpaqueFormats(device);
            FormatSupport[] formats = QueryFormatSupport(
                device,
                options.TiledResourcesTier,
                samplerFeedbackFormatsAvailable);
            Format[] sparseTexture2DFormats = SelectFormats(
                formats,
                FormatFeatures.SparseTexture2D);
            Format[] sparseTexture3DFormats = SelectFormats(
                formats,
                FormatFeatures.SparseTexture3D);
            if (options.TiledResourcesTier != TiledResourcesTier.TierNotSupported)
                features |= DeviceFeatures.SparseResources;
            if (options5.RaytracingTier != NativeRayTracingTier.TierNotSupported)
                features |= DeviceFeatures.RayTracing;
            if (options6.VariableShadingRateTier != VariableShadingRateTier.TierNotSupported)
                features |= DeviceFeatures.VariableRateShading;
            if (options7.MeshShaderTier != MeshShaderTier.TierNotSupported)
                features |= DeviceFeatures.MeshShaders;
            Format[] samplerFeedbackFormats = SelectFormats(
                formats,
                FormatFeatures.SamplerFeedbackTarget);
            if (samplerFeedbackFormats.Length != 0)
                features |= DeviceFeatures.SamplerFeedback;
            if (options21.WorkGraphsTier != Silk.NET.Direct3D12.WorkGraphsTier.TierNotSupported)
                features |= DeviceFeatures.WorkGraphs;

            uint nodeCount = device->GetNodeCount();
            if (nodeCount > 1)
                features |= DeviceFeatures.LinkedAdapters;

            return new D3D12FeatureSupport(
                nodeCount,
                features,
                options,
                options2,
                options5,
                options6,
                options7,
                options12,
                options15,
                options16,
                options21,
                formats,
                sparseTexture2DFormats,
                sparseTexture3DFormats,
                samplerFeedbackFormats);
        }

        private static FormatSupport[] QueryFormatSupport(
            ID3D12Device10* device,
            TiledResourcesTier tiledResourcesTier,
            bool samplerFeedbackFormatsAvailable)
        {
            Format[] formats = Enum.GetValues<Format>();
            FormatSupport[] result = new FormatSupport[formats.Length];
            for (int index = 0; index < formats.Length; index++)
            {
                Format format = formats[index];
                FeatureDataFormatSupport support = new(FormatMappings.ToDxgi(format));
                int query = device->CheckFeatureSupport(
                    NativeFeature.FormatSupport,
                    &support,
                    (uint)sizeof(FeatureDataFormatSupport));
                ThrowIfFailed(
                    null,
                    query,
                    NativeOperationType.Ordinary,
                    $"ID3D12Device::CheckFeatureSupport(FormatSupport:{format})");

                FormatFeatures features = ToFormatFeatures(
                    support.Support1,
                    support.Support2,
                    tiledResourcesTier,
                    samplerFeedbackFormatsAvailable);
                if (FormatMappings.IsDepthStencil(format))
                {
                    features |= QueryDepthStencilShaderFeatures(
                        device,
                        format,
                        tiledResourcesTier);
                }
                SampleCounts sampleCounts = QuerySampleCounts(
                    device,
                    FormatMappings.ToDxgi(format),
                    MultisampleQualityLevelFlags.None,
                    (features & FormatFeatures.Texture2D) != 0);
                SampleCounts sparseSampleCounts = QuerySampleCounts(
                    device,
                    FormatMappings.ToDxgi(format),
                    MultisampleQualityLevelFlags.TiledResource,
                    (features & FormatFeatures.SparseTexture2D) != 0);
                result[index] = new FormatSupport(
                    format,
                    features,
                    sampleCounts,
                    sparseSampleCounts);
            }
            return result;
        }

        private static FormatFeatures QueryDepthStencilShaderFeatures(
            ID3D12Device10* device,
            Format format,
            TiledResourcesTier tiledResourcesTier)
        {
            const FormatFeatures shaderFeatures =
                FormatFeatures.ShaderLoad |
                FormatFeatures.ShaderSample |
                FormatFeatures.ShaderSampleComparison |
                FormatFeatures.MultisampleLoad;
            FormatFeatures result = Query(TextureAspects.Depth);
            if (FormatMappings.PlaneCount(format) == 2)
                result |= Query(TextureAspects.Stencil);
            return result & shaderFeatures;

            FormatFeatures Query(TextureAspects aspect)
            {
                FeatureDataFormatSupport support = new(
                    FormatMappings.ToShaderViewFormat(format, aspect));
                ThrowIfFailed(
                    null,
                    device->CheckFeatureSupport(
                        NativeFeature.FormatSupport,
                        &support,
                        (uint)sizeof(FeatureDataFormatSupport)),
                    NativeOperationType.Ordinary,
                    $"ID3D12Device::CheckFeatureSupport(FormatSupport:{format}:{aspect})");
                return ToFormatFeatures(
                    support.Support1,
                    support.Support2,
                    tiledResourcesTier,
                    samplerFeedbackFormatsAvailable: false);
            }
        }

        private static FormatFeatures ToFormatFeatures(
            FormatSupport1 support1,
            FormatSupport2 support2,
            TiledResourcesTier tiledResourcesTier,
            bool samplerFeedbackFormatsAvailable)
        {
            FormatFeatures result = FormatFeatures.None;
            Add(FormatSupport1.Buffer, FormatFeatures.Buffer);
            Add(FormatSupport1.IAVertexBuffer, FormatFeatures.VertexBuffer);
            Add(FormatSupport1.IAIndexBuffer, FormatFeatures.IndexBuffer);
            Add(FormatSupport1.SOBuffer, FormatFeatures.StreamOutput);
            Add(FormatSupport1.Texture1D, FormatFeatures.Texture1D);
            Add(FormatSupport1.Texture2D, FormatFeatures.Texture2D);
            Add(FormatSupport1.Texture3D, FormatFeatures.Texture3D);
            Add(FormatSupport1.Texturecube, FormatFeatures.TextureCube);
            Add(FormatSupport1.ShaderLoad, FormatFeatures.ShaderLoad);
            Add(FormatSupport1.ShaderSample, FormatFeatures.ShaderSample);
            Add(FormatSupport1.ShaderSampleComparison, FormatFeatures.ShaderSampleComparison);
            Add(FormatSupport1.Mip, FormatFeatures.Mipmaps);
            Add(FormatSupport1.RenderTarget, FormatFeatures.ColorAttachment);
            Add(FormatSupport1.Blendable, FormatFeatures.ColorAttachmentBlend);
            Add(FormatSupport1.DepthStencil, FormatFeatures.DepthStencilAttachment);
            Add(
                FormatSupport1.MultisampleRendertarget,
                FormatFeatures.MultisampleColorAttachment);
            Add(FormatSupport1.MultisampleLoad, FormatFeatures.MultisampleLoad);
            Add(FormatSupport1.MultisampleResolve, FormatFeatures.MultisampleResolve);
            Add(FormatSupport1.TypedUnorderedAccessView, FormatFeatures.Storage);
            Add2(FormatSupport2.UavTypedLoad, FormatFeatures.StorageLoad);
            Add2(FormatSupport2.UavTypedStore, FormatFeatures.StorageStore);
            Add2(FormatSupport2.OutputMergerLogicOp, FormatFeatures.LogicOperation);

            const FormatSupport2 atomic =
                FormatSupport2.UavAtomicAdd |
                FormatSupport2.UavAtomicBitwiseOps |
                FormatSupport2.UavAtomicCompareStoreOrCompareExchange |
                FormatSupport2.UavAtomicExchange |
                FormatSupport2.UavAtomicSignedMinOrMax |
                FormatSupport2.UavAtomicUnsignedMinOrMax;
            if ((support2 & atomic) != 0)
                result |= FormatFeatures.StorageAtomic;

            if ((support2 & FormatSupport2.Tiled) != 0)
            {
                if ((support1 & FormatSupport1.Texture2D) != 0)
                    result |= FormatFeatures.SparseTexture2D;
                if (tiledResourcesTier >= TiledResourcesTier.Tier3 &&
                    (support1 & FormatSupport1.Texture3D) != 0)
                {
                    result |= FormatFeatures.SparseTexture3D;
                }
            }

            if (samplerFeedbackFormatsAvailable &&
                (support1 & (FormatSupport1.Texture2D | FormatSupport1.ShaderSample)) ==
                    (FormatSupport1.Texture2D | FormatSupport1.ShaderSample))
            {
                result |= FormatFeatures.SamplerFeedbackTarget;
            }
            return result;

            void Add(FormatSupport1 native, FormatFeatures portable)
            {
                if ((support1 & native) != 0)
                    result |= portable;
            }

            void Add2(FormatSupport2 native, FormatFeatures portable)
            {
                if ((support2 & native) != 0)
                    result |= portable;
            }
        }

        private static SampleCounts QuerySampleCounts(
            ID3D12Device10* device,
            Silk.NET.DXGI.Format format,
            MultisampleQualityLevelFlags flags,
            bool singleSampleSupported)
        {
            if (!singleSampleSupported)
                return SampleCounts.None;

            SampleCounts result = SampleCounts.One;
            ReadOnlySpan<uint> counts = [2, 4, 8, 16, 32];
            foreach (uint count in counts)
            {
                FeatureDataMultisampleQualityLevels levels = new(format, count, flags, 0);
                int query = device->CheckFeatureSupport(
                    NativeFeature.MultisampleQualityLevels,
                    &levels,
                    (uint)sizeof(FeatureDataMultisampleQualityLevels));
                ThrowIfFailed(
                    null,
                    query,
                    NativeOperationType.Ordinary,
                    $"ID3D12Device::CheckFeatureSupport(MultisampleQualityLevels:{format}:{count})");
                if (levels.NumQualityLevels != 0)
                    result |= (SampleCounts)count;
            }
            return result;
        }

        private static Format[] SelectFormats(
            ReadOnlySpan<FormatSupport> supports,
            FormatFeatures required)
        {
            List<Format> result = [];
            foreach (ref readonly FormatSupport support in supports)
            {
                if ((support.Features & required) == required)
                    result.Add(support.Format);
            }
            return [.. result];
        }

        private static bool SupportsSamplerFeedbackOpaqueFormats(ID3D12Device10* device)
        {
            return SupportsSamplerFeedbackFormat(
                    device,
                    Silk.NET.DXGI.Format.FormatSamplerFeedbackMinMipOpaque) &&
                SupportsSamplerFeedbackFormat(
                    device,
                    Silk.NET.DXGI.Format.FormatSamplerFeedbackMipRegionUsedOpaque);
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
            ThrowIfFailed(
                null,
                query,
                NativeOperationType.Ordinary,
                $"ID3D12Device::CheckFeatureSupport(SamplerFeedbackFormat:{format})");
            return (support.Support1 & FormatSupport1.Texture2D) != 0 &&
                (support.Support2 & FormatSupport2.SamplerFeedback) != 0;
        }

        private static T QueryFeature<T>(ID3D12Device10* device, NativeFeature feature)
            where T : unmanaged
        {
            T value = default;
            ThrowIfFailed(
                null,
                device->CheckFeatureSupport(feature, &value, (uint)sizeof(T)),
                NativeOperationType.Ordinary,
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
    private static partial class RequireD3D12
    {
        internal static D3D12Device Device(Device value) =>
            value as D3D12Device ??
            throw new ArgumentException(
                "The Device was not created by the Direct3D 12 backend.",
                nameof(value));

        internal static D3D12Queue Queue(Queue value) =>
            value as D3D12Queue ??
            throw new ArgumentException(
                "The Queue was not created by the Direct3D 12 backend.",
                nameof(value));
    }
}
