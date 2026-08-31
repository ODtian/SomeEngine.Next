using System.Buffers.Binary;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace SomeEngine.Graphics.Vulkan;

/// <summary>Controls Vulkan instance creation and diagnostics.</summary>
public readonly record struct VulkanBackendOptions(
    bool EnableValidation = false,
    bool EnableDebugMessages = true,
    uint ApiVersion = 0);

/// <summary>Creates the Vulkan implementation of the backend-neutral graphics contract.</summary>
public static class VulkanGraphicsBackend
{
    public static IGraphicsBackend Create(in VulkanBackendOptions options = default) =>
        new VulkanBackend(options);
}

internal sealed unsafe partial class VulkanBackend : IGraphicsBackend, INativeValidationControl
{
    private const string SurfaceExtension = "VK_KHR_surface";
    private const string Win32SurfaceExtension = "VK_KHR_win32_surface";
    private const string DebugUtilsExtension = "VK_EXT_debug_utils";
    private const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    private readonly object _gate = new();
    private readonly GraphicsObjectRegistry _devices;
    private readonly GraphicsObjectRegistry _surfaces;
    private readonly Vk _vk;
    private readonly VulkanBackendOptions _options;
    private readonly AdapterRecord[] _adapters;
    private VkInstance _instance;
    private KhrSurface? _surfaceApi;
    private KhrWin32Surface? _win32SurfaceApi;
    private ExtDebugUtils? _debugUtilsApi;
    private DebugUtilsMessengerEXT _debugMessenger;
    private PfnDebugUtilsMessengerCallbackEXT _debugCallback;
    private DisposeGate _disposeGate;
    private Exception? _releaseFailure;

    internal VulkanBackend(in VulkanBackendOptions options = default)
    {
        _options = options;
        _devices = new GraphicsObjectRegistry(_gate);
        _surfaces = new GraphicsObjectRegistry(_gate);
        _vk = Vk.GetApi();
        _instance = CreateInstance();
        try
        {
            if (!_vk.TryGetInstanceExtension(_instance, out _surfaceApi) ||
                !_vk.TryGetInstanceExtension(_instance, out _win32SurfaceApi))
                throw new PlatformNotSupportedException("The Vulkan Win32 surface extensions could not be loaded.");
            CreateDebugMessenger();
            _adapters = EnumerateAdapters();
        }
        catch
        {
            _surfaceApi?.Dispose();
            _win32SurfaceApi?.Dispose();
            DestroyDebugMessenger();
            _vk.DestroyInstance(_instance, null);
            _instance = default;
            _vk.Dispose();
            throw;
        }
    }

    internal Vk Api => _vk;
    internal VkInstance Instance => _instance;
    internal ExtDebugUtils? DebugUtilsApi => _debugUtilsApi;
    internal ReadOnlySpan<AdapterRecord> Adapters => _adapters;
    internal Exception? ReleaseFailure => Volatile.Read(ref _releaseFailure);
#if SOMEENGINE_TESTING
    internal VulkanFaultHooks FaultHooks { get; } = new();
#endif

    internal bool TryEnumerateAdapters(
        in AdapterEnumerationOptions options,
        Span<AdapterInfo> destination,
        out int requiredCount)
    {
        ThrowIfDisposed();
        AdapterRecord[] selected = SelectAdapters(options);
        requiredCount = selected.Length;
        int copyCount = Math.Min(destination.Length, selected.Length);
        for (int index = 0; index < copyCount; index++)
            destination[index] = selected[index].Info;
        return destination.Length >= selected.Length;
    }

    internal AdapterRecord ResolveAdapter(in AdapterId id)
    {
        ThrowIfDisposed();
        if (id.IsDefault)
        {
            foreach (AdapterRecord adapter in _adapters)
                if (adapter.Info.Type == AdapterType.Discrete)
                    return adapter;
            if (_adapters.Length != 0)
                return _adapters[0];
        }
        else
        {
            foreach (AdapterRecord adapter in _adapters)
                if (adapter.Info.Id == id)
                    return adapter;
        }
        throw new ArgumentException("The requested Vulkan adapter is unavailable.", nameof(id));
    }

    public void Dispose()
    {
        if (!_disposeGate.TryEnter())
            return;

        try
        {
            ReleaseBackend();
        }
        catch (Exception exception)
        {
            RecordReleaseFailure(exception);
        }
        finally
        {
            _disposeGate.Exit();
        }
    }

    internal void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposeGate.IsDisposed, this);

    void INativeValidationControl.EnableNativeValidation()
    {
        if (!_options.EnableValidation)
        {
            throw new InvalidOperationException(
                "Vulkan native validation must be requested in VulkanBackendOptions before instance creation.");
        }
    }

    private void RegisterDevice(VulkanDevice device)
    {
        ThrowIfDisposed();
        _devices.Add(device);
    }

    private void UnregisterDevice(VulkanDevice device)
    {
        _devices.Remove(device);
    }

    private void RegisterSurface(VulkanSurface surface)
    {
        ThrowIfDisposed();
        _surfaces.Add(surface);
    }

    private void UnregisterSurface(VulkanSurface surface)
    {
        _surfaces.Remove(surface);
    }

    private void ReleaseBackend()
    {
        DrainRegistry(_devices);
        DrainRegistry(_surfaces);

        if (_devices.HasRetainedFailures || _surfaces.HasRetainedFailures)
        {
            RecordReleaseFailure(new InvalidOperationException(
                "A Vulkan root object did not detach during backend teardown."));
            return;
        }

        _surfaceApi?.Dispose();
        _surfaceApi = null;
        _win32SurfaceApi?.Dispose();
        _win32SurfaceApi = null;
        DestroyDebugMessenger();
        if (_instance.Handle != 0)
        {
            _vk.DestroyInstance(_instance, null);
            _instance = default;
        }
        _vk.Dispose();
    }

    private static void DrainRegistry(GraphicsObjectRegistry registry)
    {
        GraphicsObject? values = registry.CloseAndBuildDrainList();
        while (values is GraphicsObject value)
        {
            values = value.RegistryDrainNext;
            value.RegistryDrainNext = null;
            value.DisposeFromParent();
            _ = registry.CompleteDrain(value);
        }
    }

    private void RecordReleaseFailure(Exception exception) =>
        Interlocked.CompareExchange(ref _releaseFailure, exception, null);

    private VkInstance CreateInstance()
    {
        uint apiVersion = _options.ApiVersion == 0
            ? Vk.Version13
            : _options.ApiVersion;
        string[] availableExtensions = EnumerateInstanceExtensions();
        RequireName(availableExtensions, SurfaceExtension, "instance extension");
        RequireName(availableExtensions, Win32SurfaceExtension, "instance extension");

        List<string> extensions = [SurfaceExtension, Win32SurfaceExtension];
        if (_options.EnableDebugMessages && availableExtensions.Contains(DebugUtilsExtension))
            extensions.Add(DebugUtilsExtension);

        string[] layers = [];
        if (_options.EnableValidation)
        {
            string[] availableLayers = EnumerateInstanceLayers();
            RequireName(availableLayers, ValidationLayer, "instance layer");
            layers = [ValidationLayer];
        }

        nint applicationName = SilkMarshal.StringToPtr("SomeEngine.Next");
        nint engineName = SilkMarshal.StringToPtr("SomeEngine");
        nint extensionNames = AllocateNames(extensions);
        nint layerNames = AllocateNames(layers);
        try
        {
            ApplicationInfo application = new()
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)applicationName,
                ApplicationVersion = Vk.MakeVersion(1, 0, 0),
                PEngineName = (byte*)engineName,
                EngineVersion = Vk.MakeVersion(1, 0, 0),
                ApiVersion = apiVersion,
            };
            InstanceCreateInfo createInfo = new()
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &application,
                EnabledExtensionCount = checked((uint)extensions.Count),
                PpEnabledExtensionNames = (byte**)extensionNames,
                EnabledLayerCount = checked((uint)layers.Length),
                PpEnabledLayerNames = (byte**)layerNames,
            };
            VkInstance instance = default;
            ThrowIfFailed(
                _vk.CreateInstance(&createInfo, null, &instance),
                "vkCreateInstance");
            return instance;
        }
        finally
        {
            SilkMarshal.Free(applicationName);
            SilkMarshal.Free(engineName);
            FreeNames(extensionNames, extensions.Count);
            FreeNames(layerNames, layers.Length);
        }
    }

    private void CreateDebugMessenger()
    {
        if (!_options.EnableDebugMessages)
            return;
        if (!_vk.TryGetInstanceExtension(
                _instance,
                out ExtDebugUtils? debugUtils) ||
            debugUtils is null)
            return;
        _debugUtilsApi = debugUtils;
        _debugCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugMessage);
        DebugUtilsMessengerCreateInfoEXT createInfo = new()
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity =
                DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType =
                DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = _debugCallback,
        };
        ThrowIfFailed(
            debugUtils.CreateDebugUtilsMessenger(
                _instance,
                &createInfo,
                null,
                out _debugMessenger),
            "vkCreateDebugUtilsMessengerEXT");
    }

    private void DestroyDebugMessenger()
    {
        if (_debugUtilsApi is not null && _debugMessenger.Handle != 0)
            _debugUtilsApi.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
        _debugMessenger = default;
        _debugUtilsApi?.Dispose();
        _debugUtilsApi = null;
        _debugCallback = default;
    }

    private static uint DebugMessage(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT type,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        _ = userData;
        string message = data is null || data->PMessage is null
            ? "Vulkan emitted an empty debug message."
            : Marshal.PtrToStringUTF8((nint)data->PMessage) ??
                "Vulkan emitted an invalid UTF-8 debug message.";
        System.Diagnostics.Trace.WriteLine($"Vulkan [{severity}/{type}] {message}");
        return Vk.False;
    }

    private string[] EnumerateInstanceExtensions()
    {
        uint count = 0;
        ThrowIfFailed(
            _vk.EnumerateInstanceExtensionProperties((byte*)null, &count, null),
            "vkEnumerateInstanceExtensionProperties(count)");
        if (count == 0)
            return [];
        ExtensionProperties[] properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* pointer = properties)
        {
            ThrowIfFailed(
                _vk.EnumerateInstanceExtensionProperties((byte*)null, &count, pointer),
                "vkEnumerateInstanceExtensionProperties(data)");
        }
        string[] names = new string[count];
        for (int index = 0; index < names.Length; index++)
        {
            fixed (byte* name = properties[index].ExtensionName)
                names[index] = ReadUtf8(name, Vk.MaxExtensionNameSize);
        }
        return names;
    }

    private string[] EnumerateInstanceLayers()
    {
        uint count = 0;
        ThrowIfFailed(
            _vk.EnumerateInstanceLayerProperties(&count, null),
            "vkEnumerateInstanceLayerProperties(count)");
        if (count == 0)
            return [];
        LayerProperties[] properties = new LayerProperties[count];
        fixed (LayerProperties* pointer = properties)
        {
            ThrowIfFailed(
                _vk.EnumerateInstanceLayerProperties(&count, pointer),
                "vkEnumerateInstanceLayerProperties(data)");
        }
        string[] names = new string[count];
        for (int index = 0; index < names.Length; index++)
        {
            fixed (byte* name = properties[index].LayerName)
                names[index] = ReadUtf8(name, Vk.MaxExtensionNameSize);
        }
        return names;
    }

    private AdapterRecord[] EnumerateAdapters()
    {
        uint count = 0;
        ThrowIfFailed(
            _vk.EnumeratePhysicalDevices(_instance, &count, null),
            "vkEnumeratePhysicalDevices(count)");
        if (count == 0)
            return [];
        VkPhysicalDevice[] physicalDevices = new VkPhysicalDevice[count];
        fixed (VkPhysicalDevice* pointer = physicalDevices)
        {
            ThrowIfFailed(
                _vk.EnumeratePhysicalDevices(_instance, &count, pointer),
                "vkEnumeratePhysicalDevices(data)");
        }

        AdapterRecord[] result = new AdapterRecord[count];
        for (int index = 0; index < result.Length; index++)
            result[index] = CreateAdapterRecord(physicalDevices[index]);
        return result;
    }

    private AdapterRecord CreateAdapterRecord(VkPhysicalDevice physicalDevice)
    {
        PhysicalDeviceDriverProperties driver = new()
        {
            SType = StructureType.PhysicalDeviceDriverProperties,
        };
        PhysicalDeviceIDProperties identity = new()
        {
            SType = StructureType.PhysicalDeviceIDProperties,
            PNext = &driver,
        };
        PhysicalDeviceProperties2 properties = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &identity,
        };
        _vk.GetPhysicalDeviceProperties2(physicalDevice, &properties);

        PhysicalDeviceMemoryProperties memory;
        _vk.GetPhysicalDeviceMemoryProperties(physicalDevice, &memory);
        ulong dedicatedVideoMemory = 0;
        ulong sharedSystemMemory = 0;
        for (uint heapIndex = 0; heapIndex < memory.MemoryHeapCount; heapIndex++)
        {
            MemoryHeap heap = memory.MemoryHeaps[(int)heapIndex];
            if ((heap.Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
                dedicatedVideoMemory = checked(dedicatedVideoMemory + heap.Size);
            else
                sharedSystemMemory = checked(sharedSystemMemory + heap.Size);
        }

        AdapterId id;
        byte* uuid = identity.DeviceUuid;
        id = new AdapterId(
            BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(uuid, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(uuid + 8, 8)));
        if (id.IsDefault)
        {
            id = new AdapterId(
                ((ulong)properties.Properties.VendorID << 32) | properties.Properties.DeviceID,
                unchecked((ulong)(nuint)physicalDevice.Handle));
        }

        string name;
        name = ReadUtf8(properties.Properties.DeviceName, Vk.MaxPhysicalDeviceNameSize);
        string driverVersion;
        driverVersion = ReadUtf8(driver.DriverInfo, Vk.MaxDriverInfoSize);
        if (string.IsNullOrWhiteSpace(driverVersion))
            driverVersion = $"0x{properties.Properties.DriverVersion:X8}";

        AdapterInfo info = new(
            id,
            ToAdapterType(properties.Properties.DeviceType),
            name,
            properties.Properties.VendorID,
            properties.Properties.DeviceID,
            dedicatedVideoMemory,
            0,
            sharedSystemMemory,
            driverVersion,
            properties.Properties.DeviceType != PhysicalDeviceType.Cpu);
        return new AdapterRecord(physicalDevice, info, properties.Properties.ApiVersion);
    }

    private AdapterRecord[] SelectAdapters(in AdapterEnumerationOptions options)
    {
        IEnumerable<AdapterRecord> selected = options.IncludeSoftware
            ? _adapters
            : _adapters.Where(static adapter => adapter.Info.HardwareAccelerated);
        selected = options.Preference switch
        {
            AdapterPreference.HighPerformance => selected
                .OrderByDescending(static adapter => adapter.Info.Type == AdapterType.Discrete)
                .ThenByDescending(static adapter => adapter.Info.DedicatedVideoMemory),
            AdapterPreference.MinimumPower => selected
                .OrderByDescending(static adapter => adapter.Info.Type == AdapterType.Integrated)
                .ThenBy(static adapter => adapter.Info.DedicatedVideoMemory),
            _ => selected,
        };
        return selected.ToArray();
    }

    private static AdapterType ToAdapterType(PhysicalDeviceType type) => type switch
    {
        PhysicalDeviceType.IntegratedGpu => AdapterType.Integrated,
        PhysicalDeviceType.DiscreteGpu => AdapterType.Discrete,
        PhysicalDeviceType.VirtualGpu => AdapterType.Virtual,
        PhysicalDeviceType.Cpu => AdapterType.Cpu,
        _ => AdapterType.Other,
    };

    private static void RequireName(string[] available, string required, string kind)
    {
        if (!available.Contains(required, StringComparer.Ordinal))
            throw new PlatformNotSupportedException($"Required Vulkan {kind} '{required}' is unavailable.");
    }

    internal static nint AllocateNames(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return 0;
        nint array = Marshal.AllocHGlobal(checked(names.Count * nint.Size));
        nint* values = (nint*)array;
        int initialized = 0;
        try
        {
            for (; initialized < names.Count; initialized++)
                values[initialized] = SilkMarshal.StringToPtr(names[initialized]);
            return array;
        }
        catch
        {
            for (int index = 0; index < initialized; index++)
                SilkMarshal.Free(values[index]);
            Marshal.FreeHGlobal(array);
            throw;
        }
    }

    internal static void FreeNames(nint array, int count)
    {
        if (array == 0)
            return;
        nint* values = (nint*)array;
        for (int index = 0; index < count; index++)
            SilkMarshal.Free(values[index]);
        Marshal.FreeHGlobal(array);
    }

    internal static string ReadUtf8(byte* value, uint capacity)
    {
        ReadOnlySpan<byte> bytes = new(value, checked((int)capacity));
        int length = bytes.IndexOf((byte)0);
        if (length >= 0)
            bytes = bytes[..length];
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    internal static void ThrowIfFailed(Result result, string operation)
    {
        if (result == Result.Success)
            return;
        GraphicsError error = result switch
        {
            Result.ErrorOutOfHostMemory or Result.ErrorOutOfDeviceMemory => GraphicsError.OutOfMemory,
            Result.ErrorDeviceLost => GraphicsError.DeviceLost,
            _ => GraphicsError.NativeFailure,
        };
        throw new GraphicsException(
            error,
            $"{operation} failed with Vulkan result {result}.",
            (long)result);
    }

    internal readonly record struct AdapterRecord(
        VkPhysicalDevice PhysicalDevice,
        AdapterInfo Info,
        uint ApiVersion);
}
