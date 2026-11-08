using Silk.NET.Vulkan;
using System.Runtime.InteropServices;

namespace SomeEngine.Tools;

internal sealed record GpuDevice(string Id, string Name, string Address, int FreeMiB, int Utilization);
internal sealed record LocalGpuDevice(string Id, string Name, string HardwareId);

internal static unsafe class GpuDevices
{
    public static LocalGpuDevice[] ReadLocal()
    {
        if (!OperatingSystem.IsWindows())
            return Read().Select(gpu => new LocalGpuDevice(gpu.Id, gpu.Name, gpu.Address)).ToArray();
        var devices = new List<LocalGpuDevice>();
        var display = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        for (uint index = 0; EnumDisplayDevices(null, index, ref display, 0); index++)
            devices.Add(new(display.Key, display.Description, display.Id));
        return devices.ToArray();
    }

    public static GpuDevice[] Read()
    {
        var result = ProcessCommands.Run(ProcessCommands.StartInfo("nvidia-smi",
            "--query-gpu=uuid,name,pci.bus_id,memory.free,utilization.gpu", "--format=csv,noheader,nounits"), 5);
        if (result.ExitCode != 0) throw new IOException("GPU resource query failed: " + result.Errors + result.Output);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            string[] values = line.Split(',').Select(value => value.Trim()).ToArray();
            return new GpuDevice(values[0], values[1], values[2], int.Parse(values[3]), int.Parse(values[4]));
        }).ToArray();
    }

    public static Dictionary<string, string> EnvironmentFor(GpuDevice gpu)
    {
        string address = gpu.Address[^12..].ToLowerInvariant().Replace(':', '_').Replace('.', '_');
        return new()
        {
            ["DRI_PRIME"] = "pci-" + address,
            ["MESA_VK_DEVICE_SELECT_FORCE_DEFAULT_DEVICE"] = "1",
            ["VK_INSTANCE_LAYERS"] = "VK_LAYER_MESA_device_select",
            ["VK_ICD_FILENAMES"] = "/usr/share/vulkan/icd.d/nvidia_icd.json",
        };
    }

    public static void Select(GpuDevice gpu)
    {
        foreach (var variable in EnvironmentFor(gpu))
        {
            Environment.SetEnvironmentVariable(variable.Key, variable.Value);
            SetEnvironment(variable.Key, variable.Value, 1);
        }
        using var vk = Vk.GetApi();
        ApplicationInfo application = new() { SType = StructureType.ApplicationInfo, ApiVersion = Vk.Version11 };
        InstanceCreateInfo create = new() { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &application };
        Result result = vk.CreateInstance(&create, null, out Instance instance);
        if (result != Result.Success) throw new IOException("Vulkan device selection: " + result);
        try
        {
            uint count = 0;
            vk.EnumeratePhysicalDevices(instance, &count, null);
            if (count != 1) throw new IOException("The selected Vulkan device must be the only visible device.");
            PhysicalDevice device;
            vk.EnumeratePhysicalDevices(instance, &count, &device);
            PhysicalDeviceIDProperties identity = new() { SType = StructureType.PhysicalDeviceIDProperties };
            PhysicalDeviceProperties2 properties = new() { SType = StructureType.PhysicalDeviceProperties2, PNext = &identity };
            vk.GetPhysicalDeviceProperties2(device, &properties);
            byte[] requested = Convert.FromHexString(gpu.Id[4..].Replace("-", ""));
            if (!new ReadOnlySpan<byte>(identity.DeviceUuid, 16).SequenceEqual(requested))
                throw new IOException("The Vulkan device differs from the allocated GPU.");
        }
        finally { vk.DestroyInstance(instance, null); }
    }

    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int SetEnvironment(string name, string value, int overwrite);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice display, uint flags);
}
