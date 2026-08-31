namespace SomeEngine.Graphics.Vulkan;

using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Vulkan.Extensions.EXT;

internal sealed unsafe partial class VulkanBackend
{
    private const string SwapchainExtension = "VK_KHR_swapchain";
    private const string MemoryBudgetExtension = "VK_EXT_memory_budget";
    private const string CalibratedTimestampsExtension = "VK_KHR_calibrated_timestamps";
    private const string ConditionalRenderingExtension = "VK_EXT_conditional_rendering";
    private const string TransformFeedbackExtension = "VK_EXT_transform_feedback";
    private const string ExtendedDynamicStateExtension = "VK_EXT_extended_dynamic_state";
    private const string ExtendedDynamicState2Extension = "VK_EXT_extended_dynamic_state2";
    private const string MeshShaderExtension = "VK_EXT_mesh_shader";
    private const string FragmentShadingRateExtension = "VK_KHR_fragment_shading_rate";
    private const string AccelerationStructureExtension = "VK_KHR_acceleration_structure";
    private const string RayTracingPipelineExtension = "VK_KHR_ray_tracing_pipeline";
    private const string RayQueryExtension = "VK_KHR_ray_query";
    private const string DeferredHostOperationsExtension = "VK_KHR_deferred_host_operations";
    private const string ExternalMemoryWin32Extension = "VK_KHR_external_memory_win32";
    private const string ExternalSemaphoreWin32Extension = "VK_KHR_external_semaphore_win32";

    internal RhiDevice CreateDevice(in DeviceDesc desc)
    {
        ThrowIfDisposed();
        AdapterRecord adapter = ResolveAdapter(desc.AdapterId);
        if (adapter.ApiVersion < Vk.Version13)
        {
            throw new PlatformNotSupportedException(
                "SomeEngine's Vulkan backend requires Vulkan 1.3 or newer.");
        }
        return VulkanDevice.Create(this, adapter, desc);
    }

    internal RhiQueue GetQueue(RhiDevice device, QueueType type, uint index = 0) =>
        RequireDevice(device, nameof(device)).GetQueue(type, index);

    public bool TryGetCapability<TCapability>(
        RhiDevice device,
        out TCapability? capability)
        where TCapability : DeviceCapability =>
        RequireDevice(device, nameof(device)).TryGetCapability(out capability);

    internal void CollectCompleted(RhiDevice device) =>
        RequireDevice(device, nameof(device)).CollectCompleted();

    private VulkanDevice RequireDevice(RhiDevice device, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(device, parameterName);
        if (device is not VulkanDevice native || !ReferenceEquals(native.Backend, this))
            throw new ArgumentException("The Device belongs to a different graphics backend.", parameterName);
        native.ThrowIfUnavailable();
        return native;
    }

    private static T RegisterChildOrDispose<T>(VulkanDevice device, T child)
        where T : GraphicsObject
    {
        try
        {
            device.RegisterChild(child);
            return child;
        }
        catch
        {
            child.Dispose();
            throw;
        }
    }

    private sealed partial class VulkanDevice : RhiDevice
    {
        private static readonly InvalidOperationException ChildDetachFailure =
            new("A Vulkan Device child did not detach during Device teardown.");

        private readonly object _gate = new();
        private readonly VulkanBackend _backend;
        private readonly GraphicsObjectRegistry _children;
        private readonly Dictionary<(QueueType Type, uint Index), VulkanQueue> _queues = [];
        private readonly Dictionary<Type, DeviceCapability> _capabilities = [];
        private readonly PhysicalDeviceMemoryProperties _memoryProperties;
        private readonly VulkanExtendedFeatureSupport _extendedFeatures;
        private readonly VulkanDescriptorAllocator _descriptorAllocator;
        private readonly VulkanBindlessPublisher _bindlessPublisher;
        private readonly KhrSwapchain? _swapchainApi;
        private readonly KhrCalibratedTimestamps? _calibratedTimestampsApi;
        private ExtConditionalRendering? _conditionalRenderingApi;
        private ExtTransformFeedback? _transformFeedbackApi;
        private ExtDeviceGeneratedCommands? _generatedCommandsApi;
        private ExtExtendedDynamicState? _extendedDynamicStateApi;
        private ExtExtendedDynamicState2? _extendedDynamicState2Api;
        private ExtMeshShader? _meshShaderApi;
        private KhrFragmentShadingRate? _fragmentShadingRateApi;
        private KhrAccelerationStructure? _accelerationStructureApi;
        private KhrRayTracingPipeline? _rayTracingPipelineApi;
        private KhrExternalMemoryWin32? _externalMemoryApi;
        private KhrExternalSemaphoreWin32? _externalSemaphoreApi;
        private VkPhysicalDevice _physicalDevice;
        private VkDevice _native;

        private VulkanDevice(
            VulkanBackend backend,
            VkPhysicalDevice physicalDevice,
            VkDevice native,
            in AdapterInfo adapter,
            DeviceCapabilities capabilities,
            in DeviceDesc desc,
            DeviceFeatures enabledFeatures,
            QueuePlan queuePlan,
            IReadOnlySet<string> extensions,
            in PhysicalDeviceFeatures nativeFeatures,
            bool bufferDeviceAddress,
            VulkanExtendedFeatureSupport extendedFeatures)
            : base(adapter, capabilities, 1, desc.Label)
        {
            _backend = backend;
            _children = new GraphicsObjectRegistry(_gate);
            _physicalDevice = physicalDevice;
            _native = native;
            BackendOwner = backend;
            backend.Api.GetPhysicalDeviceMemoryProperties(physicalDevice, out _memoryProperties);
            PhysicalDeviceProperties nativeProperties;
            backend.Api.GetPhysicalDeviceProperties(physicalDevice, &nativeProperties);
            NonCoherentAtomSize = nativeProperties.Limits.NonCoherentAtomSize;
            MinimumUniformBufferOffsetAlignment =
                nativeProperties.Limits.MinUniformBufferOffsetAlignment;
            TimestampPeriod = nativeProperties.Limits.TimestampPeriod;
            MaxBoundDescriptorSets = nativeProperties.Limits.MaxBoundDescriptorSets;
            MaxDrawIndirectCount = nativeProperties.Limits.MaxDrawIndirectCount;
            MaxVertexOutputComponents = nativeProperties.Limits.MaxVertexOutputComponents;
            SupportsBufferDeviceAddress = bufferDeviceAddress;
            _extendedFeatures = extendedFeatures;
            _descriptorAllocator = new VulkanDescriptorAllocator(this);
            _bindlessPublisher = new VulkanBindlessPublisher(this);
            if ((enabledFeatures & DeviceFeatures.Presentation) != 0 &&
                !backend.Api.TryGetDeviceExtension(backend.Instance, native, out _swapchainApi))
                throw new PlatformNotSupportedException("VK_KHR_swapchain entry points could not be loaded.");
            if ((enabledFeatures & DeviceFeatures.CalibratedTimestamps) != 0 &&
                !backend.Api.TryGetDeviceExtension(backend.Instance, native, out _calibratedTimestampsApi))
                throw new PlatformNotSupportedException("VK_EXT_calibrated_timestamps entry points could not be loaded.");
            LoadExtendedEntryPoints(enabledFeatures, extendedFeatures);
            CreateQueues(queuePlan);
            CreateCapabilities(enabledFeatures, extensions, nativeFeatures);
            _pipelineWorker = new VulkanPipelineWorker();
        }

        internal VulkanBackend Backend => _backend;
        internal VkPhysicalDevice PhysicalDevice => _physicalDevice;
        internal VkDevice Native => _native;
        internal PhysicalDeviceMemoryProperties MemoryProperties => _memoryProperties;
        internal ulong NonCoherentAtomSize { get; }
        internal ulong MinimumUniformBufferOffsetAlignment { get; }
        internal float TimestampPeriod { get; }
        internal uint MaxBoundDescriptorSets { get; }
        internal uint MaxDrawIndirectCount { get; }
        internal uint MaxVertexOutputComponents { get; }
        internal bool SupportsBufferDeviceAddress { get; }
        internal VulkanExtendedFeatureSupport ExtendedFeatures => _extendedFeatures;
        internal VulkanDescriptorAllocator DescriptorAllocator => _descriptorAllocator;
        internal VulkanBindlessPublisher BindlessPublisher => _bindlessPublisher;
        internal KhrSwapchain SwapchainApi => _swapchainApi
            ?? throw new NotSupportedException("The Device was not created with Presentation support.");
        internal KhrCalibratedTimestamps CalibratedTimestampsApi => _calibratedTimestampsApi
            ?? throw new NotSupportedException("The Device was not created with CalibratedTimestamps support.");
        internal ExtConditionalRendering ConditionalRenderingApi => _conditionalRenderingApi
            ?? throw new NotSupportedException("VK_EXT_conditional_rendering is unavailable.");
        internal ExtTransformFeedback TransformFeedbackApi => _transformFeedbackApi
            ?? throw new NotSupportedException("VK_EXT_transform_feedback is unavailable.");
        internal ExtDeviceGeneratedCommands GeneratedCommandsApi => _generatedCommandsApi
            ?? throw new NotSupportedException("VK_EXT_device_generated_commands is unavailable.");
        internal ExtExtendedDynamicState ExtendedDynamicStateApi => _extendedDynamicStateApi
            ?? throw new NotSupportedException("VK_EXT_extended_dynamic_state is unavailable.");
        internal ExtExtendedDynamicState2 ExtendedDynamicState2Api => _extendedDynamicState2Api
            ?? throw new NotSupportedException("VK_EXT_extended_dynamic_state2 is unavailable.");
        internal ExtMeshShader MeshShaderApi => _meshShaderApi
            ?? throw new NotSupportedException("The Device was not created with MeshShaders support.");
        internal KhrFragmentShadingRate FragmentShadingRateApi => _fragmentShadingRateApi
            ?? throw new NotSupportedException("The Device was not created with VariableRateShading support.");
        internal KhrAccelerationStructure AccelerationStructureApi => _accelerationStructureApi
            ?? throw new NotSupportedException("The Device was not created with RayTracing support.");
        internal KhrRayTracingPipeline RayTracingPipelineApi => _rayTracingPipelineApi
            ?? throw new NotSupportedException("The Device was not created with RayTracing support.");
        internal KhrExternalMemoryWin32 ExternalMemoryApi => _externalMemoryApi
            ?? throw new NotSupportedException("The Device was not created with ExternalResources support.");
        internal KhrExternalSemaphoreWin32 ExternalSemaphoreApi => _externalSemaphoreApi
            ?? throw new NotSupportedException("The Device was not created with ExternalTimelines support.");

        internal static VulkanDevice Create(
            VulkanBackend backend,
            in AdapterRecord adapter,
            in DeviceDesc desc)
        {
            if (desc.EnabledNodeMask != 1)
            {
                throw new NotSupportedException(
                    "The Vulkan backend currently exposes one physical-device node per Device.");
            }
            DeviceQueueDesc[] queueDescriptions = desc.Queues.ToArray();
            if (queueDescriptions.Length == 0)
                throw new ArgumentException("A Vulkan Device requires at least one Queue.", nameof(desc));

            Vk vk = backend.Api;
            string[] extensionArray = EnumerateDeviceExtensions(vk, adapter.PhysicalDevice);
            HashSet<string> extensions = new(extensionArray, StringComparer.Ordinal);
            QueryFeatures(
                vk,
                adapter.PhysicalDevice,
                out PhysicalDeviceFeatures2 availableFeatures,
                out PhysicalDeviceVulkan12Features available12,
                out PhysicalDeviceVulkan13Features available13);
            VulkanExtendedFeatureSupport extendedFeatures = VulkanExtendedFeatureSupport.Query(
                vk,
                adapter.PhysicalDevice,
                extensions);
            RequireCoreFeatures(available12, available13);
            QueuePlan queuePlan = QueuePlan.Create(
                vk,
                adapter.PhysicalDevice,
                queueDescriptions);

            DeviceFeatures supportedFeatures = GetSupportedFeatures(
                vk,
                adapter.PhysicalDevice,
                extensions,
                availableFeatures.Features,
                extendedFeatures,
                queuePlan);
            DeviceFeatures missing = desc.RequiredFeatures & ~supportedFeatures;
            if (missing != DeviceFeatures.None)
            {
                throw new NotSupportedException(
                    $"The Vulkan adapter does not support required Device features: {missing}.");
            }
            DeviceFeatures enabledFeatures = desc.RequiredFeatures |
                (desc.OptionalFeatures & supportedFeatures);

            List<string> enabledExtensions = [];
            if ((enabledFeatures & DeviceFeatures.Presentation) != 0)
                enabledExtensions.Add(SwapchainExtension);
            if ((enabledFeatures & DeviceFeatures.Residency) != 0)
                enabledExtensions.Add(MemoryBudgetExtension);
            if ((enabledFeatures & DeviceFeatures.CalibratedTimestamps) != 0)
                enabledExtensions.Add(CalibratedTimestampsExtension);
            AddExtendedExtensions(enabledExtensions, enabledFeatures, extendedFeatures);

            VkDevice native = CreateNativeDevice(
                vk,
                adapter.PhysicalDevice,
                queuePlan,
                enabledExtensions,
                availableFeatures,
                available12,
                available13,
                extendedFeatures,
                enabledFeatures);
            VulkanDevice? device = null;
            try
            {
                DeviceCapabilities capabilities = VulkanFormats.CreateCapabilities(
                    vk,
                    adapter.PhysicalDevice,
                    availableFeatures.Features,
                    extendedFeatures);
                device = new VulkanDevice(
                    backend,
                    adapter.PhysicalDevice,
                    native,
                    adapter.Info,
                    capabilities,
                    desc,
                    enabledFeatures,
                    queuePlan,
                    extensions,
                    availableFeatures.Features,
                    available12.BufferDeviceAddress,
                    extendedFeatures);
                backend.RegisterDevice(device);
                return device;
            }
            catch
            {
                if (device is not null)
                    device.DisposeFromParent();
                else
                    vk.DestroyDevice(native, null);
                throw;
            }
        }

        internal VulkanQueue GetQueue(QueueType type, uint index)
        {
            ThrowIfUnavailable();
            return _queues.TryGetValue((type, index), out VulkanQueue? queue)
                ? queue
                : throw new ArgumentOutOfRangeException(nameof(index),
                    $"The Device has no {type} Queue at index {index}.");
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

        internal void CollectCompleted()
        {
            ThrowIfUnavailable();
            foreach (VulkanQueue queue in _queues.Values)
                queue.CollectCompleted();
        }

        internal GraphicsException PublishDeviceLoss(Result result, string operation)
        {
            var candidate = new GraphicsException(
                GraphicsError.DeviceLost,
                $"{operation} detected Vulkan device loss.",
                (long)result);

            if (TryMarkLost(candidate))
                OnFirstDeviceLoss();

            return Loss ?? candidate;
        }

        internal GraphicsException PublishInternalDeviceLoss(string operation)
        {
            var candidate = new GraphicsException(
                GraphicsError.DeviceLost,
                operation);

            if (TryMarkLost(candidate))
                OnFirstDeviceLoss();

            return Loss ?? candidate;
        }

        internal void ThrowIfDeviceCallFailed(
            Result result,
            string operation,
            GraphicsError ordinaryError = GraphicsError.NativeFailure)
        {
            if (result == Result.Success)
                return;
            if (result == Result.ErrorDeviceLost)
                throw PublishDeviceLoss(result, operation);
            if (result is Result.ErrorOutOfHostMemory or Result.ErrorOutOfDeviceMemory)
            {
                throw new GraphicsException(
                    GraphicsError.OutOfMemory,
                    $"{operation} ran out of memory.",
                    (long)result);
            }
            throw new GraphicsException(
                ordinaryError,
                $"{operation} failed with Vulkan result {result}.",
                (long)result);
        }

        private void OnFirstDeviceLoss()
        {
            _pipelineWorker.StopAccepting(Loss!);
            GraphicsObject? work = _children.BuildWorkList(static child =>
                child is VulkanCommandContext or VulkanSwapchain);
            while (work is GraphicsObject child)
            {
                work = child.DeviceLossWorkNext;
                child.DeviceLossWorkNext = null;
                if (child is VulkanCommandContext context)
                    context.MarkDeviceLostNoThrow();
                else if (child is VulkanSwapchain swapchain)
                    swapchain.MarkDeviceLostNoThrow();
            }

            foreach (VulkanQueue queue in _queues.Values)
                queue.MarkWorkDeviceLostNoThrow();
        }

        internal void RegisterChild(GraphicsObject child)
        {
            ThrowIfUnavailable();
            _children.Add(child);
        }

        internal void UnregisterChild(GraphicsObject child) => _children.Remove(child);

        internal override void Release(bool fromParent)
        {
            VkDevice native;
            lock (_gate)
            {
                native = _native;
                if (native.Handle == 0)
                    return;
            }

            try
            {
                _pipelineWorker.StopAndJoin(new ObjectDisposedException(
                    nameof(VulkanDevice),
                    "The Vulkan device is being disposed."));
            }
            catch (Exception exception)
            {
                RecordReleaseFailure(exception);
            }

            Result idleResult = _backend.Api.DeviceWaitIdle(native);
            bool deviceLost = Status == DeviceStatus.Lost ||
                idleResult == Result.ErrorDeviceLost;
            if (idleResult == Result.ErrorDeviceLost && Status == DeviceStatus.Active)
                _ = PublishDeviceLoss(idleResult, "vkDeviceWaitIdle");
            else if (!deviceLost && idleResult != Result.Success)
            {
                try
                {
                    ThrowIfDeviceCallFailed(idleResult, "vkDeviceWaitIdle");
                }
                catch (Exception exception)
                {
                    RecordReleaseFailure(exception);
                }
            }

            if (!deviceLost && idleResult == Result.Success)
            {
                foreach (VulkanQueue queue in _queues.Values)
                    queue.CollectCompletedAfterIdle();
            }

            lock (_gate)
                MarkDisposed();

            GraphicsObject? children = _children.CloseAndBuildDrainList();
            while (children is GraphicsObject child)
            {
                children = child.RegistryDrainNext;
                child.RegistryDrainNext = null;
                if (child is VulkanCommandContext context)
                    context.ReleaseFromDeviceNoThrow();
                child.DisposeFromParent();
                if (_children.CompleteDrain(child))
                    RecordReleaseFailure(ChildDetachFailure);
            }

            if (_children.HasRetainedFailures || TeardownFailure is not null)
                return;

            _bindlessPublisher.Release();
            _descriptorAllocator.Release();
            foreach (VulkanQueue queue in _queues.Values)
                queue.Release(native);
            _queues.Clear();
            _capabilities.Clear();
            _swapchainApi?.Dispose();
            _calibratedTimestampsApi?.Dispose();
            _conditionalRenderingApi?.Dispose();
            _transformFeedbackApi?.Dispose();
            _generatedCommandsApi?.Dispose();
            _extendedDynamicStateApi?.Dispose();
            _extendedDynamicState2Api?.Dispose();
            _meshShaderApi?.Dispose();
            _fragmentShadingRateApi?.Dispose();
            _accelerationStructureApi?.Dispose();
            _rayTracingPipelineApi?.Dispose();
            _externalMemoryApi?.Dispose();
            _externalSemaphoreApi?.Dispose();
            _backend.Api.DestroyDevice(native, null);
            _native = default;
            _physicalDevice = default;
            _backend.UnregisterDevice(this);
        }

        private void CreateQueues(QueuePlan plan)
        {
            try
            {
                foreach (QueueAssignment assignment in plan.Assignments)
                {
                    VkQueue nativeQueue;
                    _backend.Api.GetDeviceQueue(
                        _native,
                        assignment.FamilyIndex,
                        assignment.NativeQueueIndex,
                        &nativeQueue);
                    var queue = new VulkanQueue(
                        this,
                        assignment.Type,
                        assignment.RhiIndex,
                        assignment.Priority,
                        assignment.FamilyIndex,
                        assignment.SupportsSparseBinding,
                        nativeQueue);
                    _queues.Add((assignment.Type, assignment.RhiIndex), queue);
                }
            }
            catch
            {
                foreach (VulkanQueue queue in _queues.Values)
                    queue.Release(_native);
                _queues.Clear();
                throw;
            }
        }

        private void CreateCapabilities(
            DeviceFeatures enabledFeatures,
            IReadOnlySet<string> extensions,
            in PhysicalDeviceFeatures nativeFeatures)
        {
            Add(new PipelineCreationSupport(
                this,
                PipelineCreationFeatures.PersistentCacheData |
                PipelineCreationFeatures.PipelineSpecialization));
            if ((enabledFeatures & DeviceFeatures.Presentation) != 0)
                Add(new Presentation(this));
            if ((enabledFeatures & DeviceFeatures.IndirectCommands) != 0)
            {
                IndirectArgumentType[] arguments =
                [
                    IndirectArgumentType.Draw,
                    IndirectArgumentType.DrawIndexed,
                    IndirectArgumentType.Dispatch,
                ];
                Add(new IndirectCommands(
                    this,
                    arguments,
                    argumentBufferAlignment: 4,
                    countBufferAlignment: 4,
                    maximumCommandCount: _extendedFeatures.DeviceGeneratedCommands
                        ? Math.Min(
                            MaxDrawIndirectCount,
                            _extendedFeatures.MaximumIndirectSequenceCount)
                        : MaxDrawIndirectCount,
                    maximumStride: uint.MaxValue - 3));
            }
            if ((enabledFeatures & DeviceFeatures.SparseResources) != 0)
            {
                RhiFormat[] texture2D = Capabilities.Formats
                    .ToArray()
                    .Where(static format =>
                        (format.Features & FormatFeatures.SparseTexture2D) != 0)
                    .Select(static format => format.Format)
                    .ToArray();
                RhiFormat[] texture3D = Capabilities.Formats
                    .ToArray()
                    .Where(static format =>
                        (format.Features & FormatFeatures.SparseTexture3D) != 0)
                    .Select(static format => format.Format)
                    .ToArray();
                Add(new SparseResources(
                    this,
                    tier: 2,
                    tileSizeInBytes: 64 * 1024,
                    bufferSupported: nativeFeatures.SparseResidencyBuffer,
                    texture2D,
                    texture3D,
                    maximumMappingsPerCall: uint.MaxValue));
            }
            if ((enabledFeatures & DeviceFeatures.Residency) != 0)
                Add(new Residency(this, localMemory: true, nonLocalMemory: true));
            if ((enabledFeatures & DeviceFeatures.CalibratedTimestamps) != 0)
                Add(new CalibratedTimestamps(this));
            AddMeshAndShadingRateCapabilities(enabledFeatures);
        }

        private void Add(DeviceCapability capability) =>
            _capabilities.Add(capability.GetType(), capability);

        private void LoadExtendedEntryPoints(
            DeviceFeatures enabledFeatures,
            VulkanExtendedFeatureSupport features)
        {
            if (features.ConditionalRendering &&
                !_backend.Api.TryGetDeviceExtension(_backend.Instance, _native, out _conditionalRenderingApi))
                throw new PlatformNotSupportedException("VK_EXT_conditional_rendering entry points could not be loaded.");
            if (features.TransformFeedback &&
                !_backend.Api.TryGetDeviceExtension(_backend.Instance, _native, out _transformFeedbackApi))
                throw new PlatformNotSupportedException("VK_EXT_transform_feedback entry points could not be loaded.");
            if ((enabledFeatures & DeviceFeatures.IndirectCommands) != 0 &&
                features.DeviceGeneratedCommands &&
                !_backend.Api.TryGetDeviceExtension(
                    _backend.Instance,
                    _native,
                    out _generatedCommandsApi))
                throw new PlatformNotSupportedException("VK_EXT_device_generated_commands entry points could not be loaded.");
            if (features.ExtendedDynamicState &&
                !_backend.Api.TryGetDeviceExtension(_backend.Instance, _native, out _extendedDynamicStateApi))
                throw new PlatformNotSupportedException("VK_EXT_extended_dynamic_state entry points could not be loaded.");
            if (features.ExtendedDynamicState2 &&
                !_backend.Api.TryGetDeviceExtension(_backend.Instance, _native, out _extendedDynamicState2Api))
                throw new PlatformNotSupportedException("VK_EXT_extended_dynamic_state2 entry points could not be loaded.");
            if ((enabledFeatures & DeviceFeatures.MeshShaders) != 0 &&
                !_backend.Api.TryGetDeviceExtension(_backend.Instance, _native, out _meshShaderApi))
                throw new PlatformNotSupportedException("VK_EXT_mesh_shader entry points could not be loaded.");
            if ((enabledFeatures & DeviceFeatures.VariableRateShading) != 0 &&
                !_backend.Api.TryGetDeviceExtension(_backend.Instance, _native, out _fragmentShadingRateApi))
                throw new PlatformNotSupportedException("VK_KHR_fragment_shading_rate entry points could not be loaded.");
            if ((enabledFeatures & DeviceFeatures.RayTracing) != 0 &&
                (!_backend.Api.TryGetDeviceExtension(_backend.Instance, _native, out _accelerationStructureApi) ||
                 !_backend.Api.TryGetDeviceExtension(_backend.Instance, _native, out _rayTracingPipelineApi)))
                throw new PlatformNotSupportedException("Vulkan KHR ray-tracing entry points could not be loaded.");
            if ((enabledFeatures & DeviceFeatures.ExternalResources) != 0 &&
                !_backend.Api.TryGetDeviceExtension(_backend.Instance, _native, out _externalMemoryApi))
                throw new PlatformNotSupportedException("VK_KHR_external_memory_win32 entry points could not be loaded.");
            if ((enabledFeatures & DeviceFeatures.ExternalTimelines) != 0 &&
                !_backend.Api.TryGetDeviceExtension(_backend.Instance, _native, out _externalSemaphoreApi))
                throw new PlatformNotSupportedException("VK_KHR_external_semaphore_win32 entry points could not be loaded.");
        }

        private void AddMeshAndShadingRateCapabilities(DeviceFeatures enabledFeatures)
        {
            AddRayTracingCapability(enabledFeatures);
            if ((enabledFeatures & DeviceFeatures.MeshShaders) != 0)
            {
                PhysicalDeviceMeshShaderPropertiesEXT properties = new()
                {
                    SType = StructureType.PhysicalDeviceMeshShaderPropertiesExt,
                };
                PhysicalDeviceProperties2 root = new()
                {
                    SType = StructureType.PhysicalDeviceProperties2,
                    PNext = &properties,
                };
                _backend.Api.GetPhysicalDeviceProperties2(_physicalDevice, &root);
                uint* counts = properties.MaxMeshWorkGroupCount;
                Add(new MeshShaders(
                    this,
                    _extendedFeatures.TaskShader,
                    indirectDispatch: true,
                    counts[0],
                    counts[1],
                    counts[2],
                    properties.MaxMeshWorkGroupTotalCount,
                    properties.MaxMeshWorkGroupInvocations,
                    properties.MaxTaskPayloadSize,
                    properties.MaxMeshSharedMemorySize,
                    properties.MaxMeshOutputVertices,
                    properties.MaxMeshOutputPrimitives));
            }
            if ((enabledFeatures & DeviceFeatures.VariableRateShading) == 0)
            {
                AddExternalCapabilities(enabledFeatures);
                return;
            }
            PhysicalDeviceFragmentShadingRatePropertiesKHR shading = new()
            {
                SType = StructureType.PhysicalDeviceFragmentShadingRatePropertiesKhr,
            };
            PhysicalDeviceProperties2 shadingRoot = new()
            {
                SType = StructureType.PhysicalDeviceProperties2,
                PNext = &shading,
            };
            _backend.Api.GetPhysicalDeviceProperties2(_physicalDevice, &shadingRoot);
            ShadingRate[] rates = QueryShadingRates();
            ShadingRateCombiner[] combiners = shading.FragmentShadingRateNonTrivialCombinerOps
                ? [ShadingRateCombiner.Passthrough, ShadingRateCombiner.Override,
                    ShadingRateCombiner.Minimum, ShadingRateCombiner.Maximum]
                : [ShadingRateCombiner.Passthrough, ShadingRateCombiner.Override];
            Add(new VariableRateShading(
                this,
                rates,
                combiners,
                _extendedFeatures.PrimitiveFragmentShadingRate,
                _extendedFeatures.AttachmentFragmentShadingRate,
                rates.Any(static rate => rate is
                    ShadingRate.Rate2x4 or
                    ShadingRate.Rate4x2 or
                    ShadingRate.Rate4x4),
                shading.MinFragmentShadingRateAttachmentTexelSize.Width,
                shading.MinFragmentShadingRateAttachmentTexelSize.Height));
            AddExternalCapabilities(enabledFeatures);
        }

        private ShadingRate[] QueryShadingRates()
        {
            void* address = _backend.Api.GetInstanceProcAddr(
                _backend.Instance,
                "vkGetPhysicalDeviceFragmentShadingRatesKHR").Handle;
            if (address is null)
            {
                throw new PlatformNotSupportedException(
                    "VK_KHR_fragment_shading_rate physical-device entry point is unavailable.");
            }
            var getRates = (delegate* unmanaged<
                VkPhysicalDevice,
                uint*,
                PhysicalDeviceFragmentShadingRateKHR*,
                Result>)address;
            uint count = 0;
            ThrowIfFailed(
                getRates(
                    _physicalDevice,
                    &count,
                    null),
                "vkGetPhysicalDeviceFragmentShadingRatesKHR(count)");
            PhysicalDeviceFragmentShadingRateKHR[] native =
                new PhysicalDeviceFragmentShadingRateKHR[count];
            for (int index = 0; index < native.Length; index++)
                native[index].SType = StructureType.PhysicalDeviceFragmentShadingRateKhr;
            fixed (PhysicalDeviceFragmentShadingRateKHR* pointer = native)
            {
                ThrowIfFailed(
                    getRates(
                        _physicalDevice,
                        &count,
                        pointer),
                    "vkGetPhysicalDeviceFragmentShadingRatesKHR(data)");
            }
            return native
                .Where(static value =>
                    (value.SampleCounts & SampleCountFlags.Count1Bit) != 0)
                .Select(static value => ToShadingRate(value.FragmentSize))
                .Where(static value => value.HasValue)
                .Select(static value => value!.Value)
                .Distinct()
                .Order()
                .ToArray();
        }

        private static ShadingRate? ToShadingRate(Extent2D size) =>
            (size.Width, size.Height) switch
            {
                (1, 1) => ShadingRate.Rate1x1,
                (1, 2) => ShadingRate.Rate1x2,
                (2, 1) => ShadingRate.Rate2x1,
                (2, 2) => ShadingRate.Rate2x2,
                (2, 4) => ShadingRate.Rate2x4,
                (4, 2) => ShadingRate.Rate4x2,
                (4, 4) => ShadingRate.Rate4x4,
                _ => null,
            };

        private void AddExternalCapabilities(DeviceFeatures enabledFeatures)
        {
            if ((enabledFeatures & DeviceFeatures.ExternalResources) != 0)
            {
                ExternalHandleSupport heap = QueryExternalHeapSupport(
                    _backend.Api,
                    _physicalDevice,
                    ExternalHandleType.OpaqueWin32);
                Add(new ExternalResources(
                    this,
                    [],
                    [],
                    [],
                    [],
                    heap.Importable ? [ExternalHandleType.OpaqueWin32] : [],
                    heap.Exportable ? [ExternalHandleType.OpaqueWin32] : []));
            }
            if ((enabledFeatures & DeviceFeatures.ExternalTimelines) != 0)
            {
                var import = new List<ExternalHandleType>(2);
                var export = new List<ExternalHandleType>(2);
                AddTimelineHandle(ExternalHandleType.OpaqueWin32);
                AddTimelineHandle(ExternalHandleType.OpaqueWin32Kmt);
                Add(new ExternalTimelines(this, import.ToArray(), export.ToArray()));

                void AddTimelineHandle(ExternalHandleType type)
                {
                    ExternalHandleSupport support = QueryExternalTimelineSupport(
                        _backend.Api,
                        _physicalDevice,
                        type);
                    if (support.Importable)
                        import.Add(type);
                    if (support.Exportable)
                        export.Add(type);
                }
            }
        }

        private void AddRayTracingCapability(DeviceFeatures enabledFeatures)
        {
            if ((enabledFeatures & DeviceFeatures.RayTracing) == 0)
                return;
            PhysicalDeviceAccelerationStructurePropertiesKHR acceleration = new()
            {
                SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr,
            };
            PhysicalDeviceRayTracingPipelinePropertiesKHR pipeline = new()
            {
                SType = StructureType.PhysicalDeviceRayTracingPipelinePropertiesKhr,
                PNext = &acceleration,
            };
            PhysicalDeviceProperties2 root = new()
            {
                SType = StructureType.PhysicalDeviceProperties2,
                PNext = &pipeline,
            };
            _backend.Api.GetPhysicalDeviceProperties2(_physicalDevice, &root);
            bool tier11 = _extendedFeatures.RayQuery ||
                _extendedFeatures.RayTracingPipelineTraceRaysIndirect;
            Add(new RayTracing(
                this,
                tier11 ? RayTracingTier.Tier1_1 : RayTracingTier.Tier1_0,
                pipelineRayTracing: true,
                inlineRayQuery: _extendedFeatures.RayQuery,
                // The portable DispatchRays packet carries a ShaderTable object separately;
                // Vulkan's direct indirect command only supplies dimensions, so the RHI cannot
                // expose this until its indirect packet has a portable shader-table identity.
                indirectDispatch: false,
                accelerationStructureUpdate: true,
                compaction: true,
                serialization: true,
                stateObjectAdditions: false,
                shaderRecordResourceBindings: false,
                pipeline.MaxRayRecursionDepth,
                maximumPayloadSize: uint.MaxValue,
                pipeline.MaxRayHitAttributeSize,
                checked((uint)Math.Min(acceleration.MaxGeometryCount, uint.MaxValue)),
                checked((uint)Math.Min(acceleration.MaxInstanceCount, uint.MaxValue)),
                checked((uint)Math.Min(acceleration.MaxPrimitiveCount, uint.MaxValue)),
                pipeline.MaxRayDispatchInvocationCount,
                pipeline.MaxShaderGroupStride,
                accelerationStructureAlignment: 256,
                acceleration.MinAccelerationStructureScratchOffsetAlignment,
                pipeline.ShaderGroupBaseAlignment,
                pipeline.ShaderGroupHandleAlignment));
        }

        private static void AddExtendedExtensions(
            List<string> extensions,
            DeviceFeatures enabledFeatures,
            VulkanExtendedFeatureSupport features)
        {
            if (features.ConditionalRendering) extensions.Add(ConditionalRenderingExtension);
            if (features.TransformFeedback) extensions.Add(TransformFeedbackExtension);
            if (features.ConservativeRasterization)
                extensions.Add(
                    VulkanExtendedFeatureSupport.ConservativeRasterizationExtensionName);
            if (features.VertexAttributeInstanceRateDivisor ||
                features.VertexAttributeInstanceRateZeroDivisor)
            {
                extensions.Add(
                    VulkanExtendedFeatureSupport.VertexAttributeDivisorExtensionName);
            }
            if (features.CustomBorderColorWithoutFormat)
            {
                extensions.Add(
                    VulkanExtendedFeatureSupport.CustomBorderColorExtensionName);
            }
            if ((enabledFeatures & DeviceFeatures.IndirectCommands) != 0 &&
                features.DeviceGeneratedCommands)
            {
                extensions.Add(
                    VulkanExtendedFeatureSupport.DeviceGeneratedCommandsExtensionName);
            }
            if (features.NullDescriptor)
                extensions.Add(VulkanExtendedFeatureSupport.Robustness2ExtensionName);
            if (features.ExtendedDynamicState) extensions.Add(ExtendedDynamicStateExtension);
            if (features.ExtendedDynamicState2) extensions.Add(ExtendedDynamicState2Extension);
            if ((enabledFeatures & DeviceFeatures.MeshShaders) != 0) extensions.Add(MeshShaderExtension);
            if ((enabledFeatures & DeviceFeatures.VariableRateShading) != 0) extensions.Add(FragmentShadingRateExtension);
            if ((enabledFeatures & DeviceFeatures.RayTracing) != 0)
            {
                extensions.Add(DeferredHostOperationsExtension);
                extensions.Add(AccelerationStructureExtension);
                extensions.Add(RayTracingPipelineExtension);
                if (features.RayQuery)
                    extensions.Add(RayQueryExtension);
            }
            if ((enabledFeatures & DeviceFeatures.ExternalResources) != 0)
                extensions.Add(ExternalMemoryWin32Extension);
            if ((enabledFeatures & DeviceFeatures.ExternalTimelines) != 0)
                extensions.Add(ExternalSemaphoreWin32Extension);
        }

        private static void PrependExtendedFeatures(
            ref PhysicalDeviceVulkan13Features features13,
            VulkanExtendedFeatureSupport available,
            DeviceFeatures enabledDeviceFeatures,
            out PhysicalDeviceConditionalRenderingFeaturesEXT conditional,
            out PhysicalDeviceTransformFeedbackFeaturesEXT transform,
            out PhysicalDeviceVertexAttributeDivisorFeaturesEXT divisor,
            out PhysicalDeviceCustomBorderColorFeaturesEXT customBorder,
            out PhysicalDeviceDeviceGeneratedCommandsFeaturesEXT generatedCommands,
            out PhysicalDeviceRobustness2FeaturesEXT robustness2,
            out PhysicalDeviceExtendedDynamicStateFeaturesEXT dynamic,
            out PhysicalDeviceExtendedDynamicState2FeaturesEXT dynamic2,
            out PhysicalDeviceMeshShaderFeaturesEXT mesh,
            out PhysicalDeviceFragmentShadingRateFeaturesKHR shadingRate,
            out PhysicalDeviceAccelerationStructureFeaturesKHR acceleration,
            out PhysicalDeviceRayTracingPipelineFeaturesKHR rayTracing,
            out PhysicalDeviceRayQueryFeaturesKHR rayQuery)
        {
            conditional = new PhysicalDeviceConditionalRenderingFeaturesEXT
            {
                SType = StructureType.PhysicalDeviceConditionalRenderingFeaturesExt,
                ConditionalRendering = available.ConditionalRendering,
                InheritedConditionalRendering = available.ConditionalRendering,
            };
            transform = new PhysicalDeviceTransformFeedbackFeaturesEXT
            {
                SType = StructureType.PhysicalDeviceTransformFeedbackFeaturesExt,
                TransformFeedback = available.TransformFeedback,
                GeometryStreams = available.GeometryStreams,
            };
            divisor = new PhysicalDeviceVertexAttributeDivisorFeaturesEXT
            {
                SType = StructureType.PhysicalDeviceVertexAttributeDivisorFeaturesExt,
                VertexAttributeInstanceRateDivisor =
                    available.VertexAttributeInstanceRateDivisor,
                VertexAttributeInstanceRateZeroDivisor =
                    available.VertexAttributeInstanceRateZeroDivisor,
            };
            customBorder = new PhysicalDeviceCustomBorderColorFeaturesEXT
            {
                SType = StructureType.PhysicalDeviceCustomBorderColorFeaturesExt,
                CustomBorderColors = available.CustomBorderColorWithoutFormat,
                CustomBorderColorWithoutFormat =
                    available.CustomBorderColorWithoutFormat,
            };
            generatedCommands = new PhysicalDeviceDeviceGeneratedCommandsFeaturesEXT
            {
                SType = StructureType.PhysicalDeviceDeviceGeneratedCommandsFeaturesExt,
                DeviceGeneratedCommands =
                    (enabledDeviceFeatures & DeviceFeatures.IndirectCommands) != 0 &&
                    available.DeviceGeneratedCommands,
            };
            robustness2 = new PhysicalDeviceRobustness2FeaturesEXT
            {
                SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
                NullDescriptor = available.NullDescriptor,
            };
            dynamic = new PhysicalDeviceExtendedDynamicStateFeaturesEXT
            {
                SType = StructureType.PhysicalDeviceExtendedDynamicStateFeaturesExt,
                ExtendedDynamicState = available.ExtendedDynamicState,
            };
            dynamic2 = new PhysicalDeviceExtendedDynamicState2FeaturesEXT
            {
                SType = StructureType.PhysicalDeviceExtendedDynamicState2FeaturesExt,
                ExtendedDynamicState2 = available.ExtendedDynamicState2,
            };
            mesh = new PhysicalDeviceMeshShaderFeaturesEXT
            {
                SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt,
                MeshShader = (enabledDeviceFeatures & DeviceFeatures.MeshShaders) != 0 && available.MeshShader,
                TaskShader = (enabledDeviceFeatures & DeviceFeatures.MeshShaders) != 0 && available.TaskShader,
            };
            shadingRate = new PhysicalDeviceFragmentShadingRateFeaturesKHR
            {
                SType = StructureType.PhysicalDeviceFragmentShadingRateFeaturesKhr,
                PipelineFragmentShadingRate = (enabledDeviceFeatures & DeviceFeatures.VariableRateShading) != 0 && available.PipelineFragmentShadingRate,
                PrimitiveFragmentShadingRate = (enabledDeviceFeatures & DeviceFeatures.VariableRateShading) != 0 && available.PrimitiveFragmentShadingRate,
                AttachmentFragmentShadingRate = (enabledDeviceFeatures & DeviceFeatures.VariableRateShading) != 0 && available.AttachmentFragmentShadingRate,
            };
            acceleration = new PhysicalDeviceAccelerationStructureFeaturesKHR
            {
                SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
                AccelerationStructure = (enabledDeviceFeatures & DeviceFeatures.RayTracing) != 0 && available.AccelerationStructure,
                DescriptorBindingAccelerationStructureUpdateAfterBind =
                    (enabledDeviceFeatures & DeviceFeatures.RayTracing) != 0 &&
                    available.DescriptorBindingAccelerationStructureUpdateAfterBind,
            };
            rayTracing = new PhysicalDeviceRayTracingPipelineFeaturesKHR
            {
                SType = StructureType.PhysicalDeviceRayTracingPipelineFeaturesKhr,
                RayTracingPipeline = (enabledDeviceFeatures & DeviceFeatures.RayTracing) != 0 && available.RayTracingPipeline,
                RayTracingPipelineTraceRaysIndirect = (enabledDeviceFeatures & DeviceFeatures.RayTracing) != 0 && available.RayTracingPipelineTraceRaysIndirect,
            };
            rayQuery = new PhysicalDeviceRayQueryFeaturesKHR
            {
                SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
                RayQuery = (enabledDeviceFeatures & DeviceFeatures.RayTracing) != 0 && available.RayQuery,
            };
            void* chain = features13.PNext;
            Prepend(ref chain, ref conditional, available.ConditionalRendering);
            Prepend(ref chain, ref transform, available.TransformFeedback);
            Prepend(
                ref chain,
                ref divisor,
                available.VertexAttributeInstanceRateDivisor ||
                available.VertexAttributeInstanceRateZeroDivisor);
            Prepend(
                ref chain,
                ref customBorder,
                available.CustomBorderColorWithoutFormat);
            Prepend(
                ref chain,
                ref generatedCommands,
                (enabledDeviceFeatures & DeviceFeatures.IndirectCommands) != 0 &&
                available.DeviceGeneratedCommands);
            Prepend(ref chain, ref robustness2, available.NullDescriptor);
            Prepend(ref chain, ref dynamic, available.ExtendedDynamicState);
            Prepend(ref chain, ref dynamic2, available.ExtendedDynamicState2);
            Prepend(ref chain, ref mesh, (enabledDeviceFeatures & DeviceFeatures.MeshShaders) != 0);
            Prepend(ref chain, ref shadingRate, (enabledDeviceFeatures & DeviceFeatures.VariableRateShading) != 0);
            Prepend(ref chain, ref acceleration, (enabledDeviceFeatures & DeviceFeatures.RayTracing) != 0);
            Prepend(ref chain, ref rayTracing, (enabledDeviceFeatures & DeviceFeatures.RayTracing) != 0);
            Prepend(ref chain, ref rayQuery, (enabledDeviceFeatures & DeviceFeatures.RayTracing) != 0 && available.RayQuery);
            features13.PNext = chain;

            static void Prepend<T>(ref void* chain, ref T value, bool include)
                where T : unmanaged
            {
                if (!include)
                    return;
                void** next = (void**)((byte*)Unsafe.AsPointer(ref value) + nint.Size);
                *next = chain;
                chain = Unsafe.AsPointer(ref value);
            }
        }

        private static VkDevice CreateNativeDevice(
            Vk vk,
            VkPhysicalDevice physicalDevice,
            QueuePlan queuePlan,
            IReadOnlyList<string> extensions,
            in PhysicalDeviceFeatures2 availableFeatures,
            in PhysicalDeviceVulkan12Features available12,
            in PhysicalDeviceVulkan13Features available13,
            VulkanExtendedFeatureSupport extended,
            DeviceFeatures enabledDeviceFeatures)
        {
            DeviceQueueCreateInfo[] queues = queuePlan.CreateNativeQueueInfos(out nint[] priorities);
            nint extensionNames = AllocateNames(extensions);
            try
            {
                PhysicalDeviceFeatures2 enabledFeatures = new()
                {
                    SType = StructureType.PhysicalDeviceFeatures2,
                    Features = availableFeatures.Features,
                };
                PhysicalDeviceVulkan12Features enabled12 = CreateEnabledFeatures(available12);
                PhysicalDeviceVulkan13Features enabled13 = new()
                {
                    SType = StructureType.PhysicalDeviceVulkan13Features,
                    Synchronization2 = available13.Synchronization2,
                    DynamicRendering = available13.DynamicRendering,
                    Maintenance4 = available13.Maintenance4,
                    PipelineCreationCacheControl = available13.PipelineCreationCacheControl,
                };
                enabledFeatures.PNext = &enabled12;
                enabled12.PNext = &enabled13;
                PrependExtendedFeatures(
                    ref enabled13,
                    extended,
                    enabledDeviceFeatures,
                    out PhysicalDeviceConditionalRenderingFeaturesEXT conditional,
                    out PhysicalDeviceTransformFeedbackFeaturesEXT transform,
                    out PhysicalDeviceVertexAttributeDivisorFeaturesEXT divisor,
                    out PhysicalDeviceCustomBorderColorFeaturesEXT customBorder,
                    out PhysicalDeviceDeviceGeneratedCommandsFeaturesEXT generatedCommands,
                    out PhysicalDeviceRobustness2FeaturesEXT robustness2,
                    out PhysicalDeviceExtendedDynamicStateFeaturesEXT dynamic,
                    out PhysicalDeviceExtendedDynamicState2FeaturesEXT dynamic2,
                    out PhysicalDeviceMeshShaderFeaturesEXT mesh,
                    out PhysicalDeviceFragmentShadingRateFeaturesKHR shadingRate,
                    out PhysicalDeviceAccelerationStructureFeaturesKHR acceleration,
                    out PhysicalDeviceRayTracingPipelineFeaturesKHR rayTracing,
                    out PhysicalDeviceRayQueryFeaturesKHR rayQuery);
                fixed (DeviceQueueCreateInfo* queuePointer = queues)
                {
                    DeviceCreateInfo createInfo = new()
                    {
                        SType = StructureType.DeviceCreateInfo,
                        PNext = &enabledFeatures,
                        QueueCreateInfoCount = checked((uint)queues.Length),
                        PQueueCreateInfos = queuePointer,
                        EnabledExtensionCount = checked((uint)extensions.Count),
                        PpEnabledExtensionNames = (byte**)extensionNames,
                    };
                    VkDevice native = default;
                    ThrowIfFailed(
                        vk.CreateDevice(physicalDevice, &createInfo, null, &native),
                        "vkCreateDevice");
                    return native;
                }
            }
            finally
            {
                FreeNames(extensionNames, extensions.Count);
                foreach (nint priority in priorities)
                    Marshal.FreeHGlobal(priority);
            }
        }

        private static PhysicalDeviceVulkan12Features CreateEnabledFeatures(
            in PhysicalDeviceVulkan12Features available) => new()
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            TimelineSemaphore = available.TimelineSemaphore,
            DescriptorIndexing = available.DescriptorIndexing,
            RuntimeDescriptorArray = available.RuntimeDescriptorArray,
            DescriptorBindingPartiallyBound = available.DescriptorBindingPartiallyBound,
            DescriptorBindingVariableDescriptorCount = available.DescriptorBindingVariableDescriptorCount,
            DescriptorBindingSampledImageUpdateAfterBind = available.DescriptorBindingSampledImageUpdateAfterBind,
            DescriptorBindingStorageImageUpdateAfterBind = available.DescriptorBindingStorageImageUpdateAfterBind,
            DescriptorBindingStorageBufferUpdateAfterBind = available.DescriptorBindingStorageBufferUpdateAfterBind,
            DescriptorBindingUniformBufferUpdateAfterBind = available.DescriptorBindingUniformBufferUpdateAfterBind,
            DescriptorBindingUniformTexelBufferUpdateAfterBind = available.DescriptorBindingUniformTexelBufferUpdateAfterBind,
            DescriptorBindingStorageTexelBufferUpdateAfterBind = available.DescriptorBindingStorageTexelBufferUpdateAfterBind,
            DescriptorBindingUpdateUnusedWhilePending = available.DescriptorBindingUpdateUnusedWhilePending,
            ShaderSampledImageArrayNonUniformIndexing = available.ShaderSampledImageArrayNonUniformIndexing,
            ShaderStorageImageArrayNonUniformIndexing = available.ShaderStorageImageArrayNonUniformIndexing,
            ShaderStorageBufferArrayNonUniformIndexing = available.ShaderStorageBufferArrayNonUniformIndexing,
            ShaderUniformBufferArrayNonUniformIndexing = available.ShaderUniformBufferArrayNonUniformIndexing,
            ShaderUniformTexelBufferArrayNonUniformIndexing = available.ShaderUniformTexelBufferArrayNonUniformIndexing,
            ShaderStorageTexelBufferArrayNonUniformIndexing = available.ShaderStorageTexelBufferArrayNonUniformIndexing,
            DrawIndirectCount = available.DrawIndirectCount,
            BufferDeviceAddress = available.BufferDeviceAddress,
            ScalarBlockLayout = available.ScalarBlockLayout,
            HostQueryReset = available.HostQueryReset,
        };

        private static void QueryFeatures(
            Vk vk,
            VkPhysicalDevice physicalDevice,
            out PhysicalDeviceFeatures2 features,
            out PhysicalDeviceVulkan12Features features12,
            out PhysicalDeviceVulkan13Features features13)
        {
            features13 = new PhysicalDeviceVulkan13Features
            {
                SType = StructureType.PhysicalDeviceVulkan13Features,
            };
            features12 = new PhysicalDeviceVulkan12Features
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
            };
            features = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2,
            };
            fixed (PhysicalDeviceVulkan13Features* pointer13 = &features13)
            fixed (PhysicalDeviceVulkan12Features* pointer12 = &features12)
            fixed (PhysicalDeviceFeatures2* pointer = &features)
            {
                pointer->PNext = pointer12;
                pointer12->PNext = pointer13;
                vk.GetPhysicalDeviceFeatures2(physicalDevice, pointer);
            }
            features.PNext = null;
            features12.PNext = null;
            features13.PNext = null;
        }

        private static void RequireCoreFeatures(
            in PhysicalDeviceVulkan12Features features12,
            in PhysicalDeviceVulkan13Features features13)
        {
            if (!features12.TimelineSemaphore)
                throw new PlatformNotSupportedException("Vulkan timeline semaphores are required.");
            if (!features13.Synchronization2)
                throw new PlatformNotSupportedException("Vulkan synchronization2 is required.");
            if (!features13.DynamicRendering)
                throw new PlatformNotSupportedException("Vulkan dynamic rendering is required.");
            if (!features12.DescriptorIndexing ||
                !features12.RuntimeDescriptorArray ||
                !features12.DescriptorBindingPartiallyBound ||
                !features12.DescriptorBindingSampledImageUpdateAfterBind ||
                !features12.DescriptorBindingStorageImageUpdateAfterBind ||
                !features12.DescriptorBindingStorageBufferUpdateAfterBind ||
                !features12.DescriptorBindingUniformBufferUpdateAfterBind ||
                !features12.DescriptorBindingUniformTexelBufferUpdateAfterBind ||
                !features12.DescriptorBindingStorageTexelBufferUpdateAfterBind ||
                !features12.DescriptorBindingUpdateUnusedWhilePending)
            {
                throw new PlatformNotSupportedException(
                    "SomeEngine's Vulkan backend requires descriptor-indexing update-after-bind support.");
            }
        }

        private static DeviceFeatures GetSupportedFeatures(
            Vk vk,
            VkPhysicalDevice physicalDevice,
            IReadOnlySet<string> extensions,
            in PhysicalDeviceFeatures features,
            VulkanExtendedFeatureSupport extended,
            QueuePlan queuePlan)
        {
            DeviceFeatures supported = DeviceFeatures.IndirectCommands;
            if (features.SparseBinding &&
                features.SparseResidencyBuffer &&
                queuePlan.SupportsSparseGraphicsQueue0)
            {
                supported |= DeviceFeatures.SparseResources;
            }
            if (extensions.Contains(CalibratedTimestampsExtension))
                supported |= DeviceFeatures.CalibratedTimestamps;
            if (extended.MeshShader)
                supported |= DeviceFeatures.MeshShaders;
            if (extended.PipelineFragmentShadingRate)
                supported |= DeviceFeatures.VariableRateShading;
            if (extended.AccelerationStructure && extended.RayTracingPipeline &&
                extensions.Contains(DeferredHostOperationsExtension))
                supported |= DeviceFeatures.RayTracing;
            if (extensions.Contains(ExternalMemoryWin32Extension))
            {
                ExternalHandleSupport heap = QueryExternalHeapSupport(
                    vk,
                    physicalDevice,
                    ExternalHandleType.OpaqueWin32);
                if (heap.Importable || heap.Exportable)
                    supported |= DeviceFeatures.ExternalResources;
            }
            if (extensions.Contains(ExternalSemaphoreWin32Extension))
            {
                ExternalHandleSupport opaque = QueryExternalTimelineSupport(
                    vk,
                    physicalDevice,
                    ExternalHandleType.OpaqueWin32);
                ExternalHandleSupport kmt = QueryExternalTimelineSupport(
                    vk,
                    physicalDevice,
                    ExternalHandleType.OpaqueWin32Kmt);
                if (opaque.Importable || opaque.Exportable ||
                    kmt.Importable || kmt.Exportable)
                    supported |= DeviceFeatures.ExternalTimelines;
            }
            return supported;
        }

        private static ExternalHandleSupport QueryExternalHeapSupport(
            Vk vk,
            VkPhysicalDevice physicalDevice,
            ExternalHandleType type)
        {
            PhysicalDeviceExternalBufferInfo info = new()
            {
                SType = StructureType.PhysicalDeviceExternalBufferInfo,
                Usage = BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
                HandleType = ToNativeMemoryHandleType(type),
            };
            ExternalBufferProperties properties = new()
            {
                SType = StructureType.ExternalBufferProperties,
            };
            vk.GetPhysicalDeviceExternalBufferProperties(
                physicalDevice,
                &info,
                &properties);
            ExternalMemoryFeatureFlags features =
                properties.ExternalMemoryProperties.ExternalMemoryFeatures;
            return new ExternalHandleSupport(
                (features & ExternalMemoryFeatureFlags.ImportableBit) != 0,
                (features & ExternalMemoryFeatureFlags.ExportableBit) != 0);
        }

        private static ExternalHandleSupport QueryExternalTimelineSupport(
            Vk vk,
            VkPhysicalDevice physicalDevice,
            ExternalHandleType type)
        {
            SemaphoreTypeCreateInfo timeline = new()
            {
                SType = StructureType.SemaphoreTypeCreateInfo,
                SemaphoreType = SemaphoreType.Timeline,
            };
            PhysicalDeviceExternalSemaphoreInfo info = new()
            {
                SType = StructureType.PhysicalDeviceExternalSemaphoreInfo,
                PNext = &timeline,
                HandleType = ToNativeSemaphoreHandleType(type),
            };
            ExternalSemaphoreProperties properties = new()
            {
                SType = StructureType.ExternalSemaphoreProperties,
            };
            vk.GetPhysicalDeviceExternalSemaphoreProperties(
                physicalDevice,
                &info,
                &properties);
            ExternalSemaphoreFeatureFlags features = properties.ExternalSemaphoreFeatures;
            return new ExternalHandleSupport(
                (features & ExternalSemaphoreFeatureFlags.ImportableBit) != 0,
                (features & ExternalSemaphoreFeatureFlags.ExportableBit) != 0);
        }

        private readonly record struct ExternalHandleSupport(
            bool Importable,
            bool Exportable);

        private static string[] EnumerateDeviceExtensions(Vk vk, VkPhysicalDevice physicalDevice)
        {
            uint count = 0;
            ThrowIfFailed(
                vk.EnumerateDeviceExtensionProperties(physicalDevice, (byte*)null, &count, null),
                "vkEnumerateDeviceExtensionProperties(count)");
            ExtensionProperties[] properties = new ExtensionProperties[count];
            fixed (ExtensionProperties* pointer = properties)
            {
                ThrowIfFailed(
                    vk.EnumerateDeviceExtensionProperties(
                        physicalDevice,
                        (byte*)null,
                        &count,
                        pointer),
                    "vkEnumerateDeviceExtensionProperties(data)");
            }
            string[] names = new string[count];
            for (int index = 0; index < names.Length; index++)
            {
                fixed (byte* name = properties[index].ExtensionName)
                    names[index] = ReadUtf8(name, Vk.MaxExtensionNameSize);
            }
            return names;
        }
    }

    private sealed partial class VulkanQueue : RhiQueue
    {
        private readonly VulkanDevice _device;
        private readonly object _gate = new();
        private VkQueue _native;
        private VkSemaphore _completion;
        private ulong _nextCompletion;

        internal VulkanQueue(
            VulkanDevice device,
            QueueType type,
            uint index,
            float priority,
            uint familyIndex,
            bool supportsSparseBinding,
            VkQueue native)
            : base(device, type, index, priority, 0)
        {
            _device = device;
            FamilyIndex = familyIndex;
            SupportsSparseBinding = supportsSparseBinding;
            _native = native;
            SemaphoreTypeCreateInfo timeline = new()
            {
                SType = StructureType.SemaphoreTypeCreateInfo,
                SemaphoreType = SemaphoreType.Timeline,
                InitialValue = 0,
            };
            SemaphoreCreateInfo createInfo = new()
            {
                SType = StructureType.SemaphoreCreateInfo,
                PNext = &timeline,
            };
            VkSemaphore semaphore = default;
            device.ThrowIfDeviceCallFailed(
                device.Backend.Api.CreateSemaphore(device.Native, &createInfo, null, &semaphore),
                "vkCreateSemaphore(queue completion)");
            _completion = semaphore;
        }

        internal uint FamilyIndex { get; }
        internal bool SupportsSparseBinding { get; }
        internal VkQueue Native => _native;
        internal VkSemaphore CompletionSemaphore => _completion;
        internal object Gate => _gate;

        internal ulong ReserveCompletionValue() => checked(++_nextCompletion);

        internal void Release(VkDevice nativeDevice)
        {
            ReleaseWorkNoThrow();
            if (_completion.Handle != 0)
            {
                _device.Backend.Api.DestroySemaphore(nativeDevice, _completion, null);
                _completion = default;
            }
            _native = default;
        }
    }

    private sealed class QueuePlan
    {
        private QueuePlan(FamilyPlan[] families, QueueAssignment[] assignments)
        {
            Families = families;
            Assignments = assignments;
        }

        internal FamilyPlan[] Families { get; }
        internal QueueAssignment[] Assignments { get; }
        internal bool SupportsSparseGraphicsQueue0 => Assignments.Any(static assignment =>
            assignment.Type == QueueType.Graphics &&
            assignment.RhiIndex == 0 &&
            assignment.SupportsSparseBinding);

        internal static QueuePlan Create(
            Vk vk,
            VkPhysicalDevice physicalDevice,
            ReadOnlySpan<DeviceQueueDesc> descriptions)
        {
            uint count = 0;
            vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &count, null);
            QueueFamilyProperties[] properties = new QueueFamilyProperties[count];
            fixed (QueueFamilyProperties* pointer = properties)
                vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &count, pointer);

            var families = new Dictionary<uint, FamilyPlan>();
            var assignments = new List<QueueAssignment>();
            Dictionary<QueueType, uint> nextRhiIndex = [];
            foreach (DeviceQueueDesc description in descriptions)
            {
                if (description.Count == 0)
                    throw new ArgumentOutOfRangeException(nameof(descriptions), "Queue Count must be positive.");
                if (description.NodeIndex != 0)
                    throw new NotSupportedException("Vulkan queue NodeIndex must be zero.");
                if (!float.IsFinite(description.Priority) || description.Priority is < 0 or > 1)
                    throw new ArgumentOutOfRangeException(nameof(descriptions), "Queue Priority must be in [0, 1].");

                uint familyIndex = SelectFamily(properties, families, description.Type, description.Count);
                if (!families.TryGetValue(familyIndex, out FamilyPlan? family))
                {
                    family = new FamilyPlan(familyIndex);
                    families.Add(familyIndex, family);
                }
                uint firstRhiIndex = nextRhiIndex.GetValueOrDefault(description.Type);
                for (uint index = 0; index < description.Count; index++)
                {
                    uint nativeIndex = checked((uint)family.Priorities.Count);
                    family.Priorities.Add(description.Priority);
                    assignments.Add(new QueueAssignment(
                        description.Type,
                        firstRhiIndex + index,
                        description.Priority,
                        familyIndex,
                        (properties[(int)familyIndex].QueueFlags &
                         QueueFlags.SparseBindingBit) != 0,
                        nativeIndex));
                }
                nextRhiIndex[description.Type] = checked(firstRhiIndex + description.Count);
            }
            return new QueuePlan(families.Values.ToArray(), assignments.ToArray());
        }

        internal DeviceQueueCreateInfo[] CreateNativeQueueInfos(out nint[] priorities)
        {
            var result = new DeviceQueueCreateInfo[Families.Length];
            priorities = new nint[Families.Length];
            for (int index = 0; index < Families.Length; index++)
            {
                FamilyPlan family = Families[index];
                int byteCount = checked(family.Priorities.Count * sizeof(float));
                priorities[index] = Marshal.AllocHGlobal(byteCount);
                Span<float> destination = new((void*)priorities[index], family.Priorities.Count);
                CollectionsMarshal.AsSpan(family.Priorities).CopyTo(destination);
                result[index] = new DeviceQueueCreateInfo
                {
                    SType = StructureType.DeviceQueueCreateInfo,
                    QueueFamilyIndex = family.FamilyIndex,
                    QueueCount = checked((uint)family.Priorities.Count),
                    PQueuePriorities = (float*)priorities[index],
                };
            }
            return result;
        }

        private static uint SelectFamily(
            QueueFamilyProperties[] properties,
            IReadOnlyDictionary<uint, FamilyPlan> existing,
            QueueType type,
            uint requestedCount)
        {
            uint selected = uint.MaxValue;
            int selectedScore = int.MinValue;
            for (uint index = 0; index < properties.Length; index++)
            {
                QueueFamilyProperties candidate = properties[index];
                uint used = existing.TryGetValue(index, out FamilyPlan? family)
                    ? checked((uint)family.Priorities.Count)
                    : 0;
                if (candidate.QueueCount - Math.Min(candidate.QueueCount, used) < requestedCount ||
                    !Supports(candidate.QueueFlags, type))
                    continue;
                int score = Score(candidate.QueueFlags, type);
                if (score <= selectedScore)
                    continue;
                selected = index;
                selectedScore = score;
            }
            if (selected == uint.MaxValue)
                throw new NotSupportedException($"No Vulkan queue family can satisfy {requestedCount} {type} Queue(s).");
            return selected;
        }

        private static bool Supports(QueueFlags flags, QueueType type) => type switch
        {
            QueueType.Graphics => (flags & QueueFlags.GraphicsBit) != 0,
            QueueType.Compute => (flags & QueueFlags.ComputeBit) != 0,
            QueueType.Copy => (flags & QueueFlags.TransferBit) != 0,
            _ => false,
        };

        private static int Score(QueueFlags flags, QueueType type) => type switch
        {
            QueueType.Graphics => 100,
            QueueType.Compute => (flags & QueueFlags.GraphicsBit) == 0 ? 200 : 100,
            QueueType.Copy => (flags & (QueueFlags.GraphicsBit | QueueFlags.ComputeBit)) == 0
                ? 300
                : (flags & QueueFlags.GraphicsBit) == 0 ? 200 : 100,
            _ => 0,
        };
    }

    private sealed class FamilyPlan(uint familyIndex)
    {
        internal uint FamilyIndex { get; } = familyIndex;
        internal List<float> Priorities { get; } = [];
    }

    private readonly record struct QueueAssignment(
        QueueType Type,
        uint RhiIndex,
        float Priority,
        uint FamilyIndex,
        bool SupportsSparseBinding,
        uint NativeQueueIndex);
}
