namespace SomeEngine.Graphics.Vulkan;

using System.Diagnostics;

internal sealed unsafe partial class VulkanBackend
{
    private ResidencyInfo GetResidencyInfoCore(RhiDevice device)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        if (!nativeDevice.TryGetCapability(out Residency? residency) || residency is null)
            throw new NotSupportedException("The Device was not created with Residency support.");
        PhysicalDeviceMemoryBudgetPropertiesEXT budget = new()
        {
            SType = StructureType.PhysicalDeviceMemoryBudgetPropertiesExt,
        };
        PhysicalDeviceMemoryProperties2 properties = new()
        {
            SType = StructureType.PhysicalDeviceMemoryProperties2,
            PNext = &budget,
        };
        Api.GetPhysicalDeviceMemoryProperties2(nativeDevice.PhysicalDevice, &properties);
        ulong localBudget = 0;
        ulong localUsage = 0;
        ulong nonLocalBudget = 0;
        ulong nonLocalUsage = 0;
        ulong* budgets = budget.HeapBudget;
        ulong* usages = budget.HeapUsage;
        for (uint index = 0; index < properties.MemoryProperties.MemoryHeapCount; index++)
        {
            bool local = (properties.MemoryProperties.MemoryHeaps[(int)index].Flags &
                MemoryHeapFlags.DeviceLocalBit) != 0;
            if (local)
            {
                localBudget = checked(localBudget + budgets[index]);
                localUsage = checked(localUsage + usages[index]);
            }
            else
            {
                nonLocalBudget = checked(nonLocalBudget + budgets[index]);
                nonLocalUsage = checked(nonLocalUsage + usages[index]);
            }
        }
        return new ResidencyInfo(localBudget, localUsage, nonLocalBudget, nonLocalUsage);
    }

    private ResidencyResource GetResidencyResourceCore(object value, RhiDevice device)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        if (!nativeDevice.TryGetCapability(out Residency? residency) || residency is null)
            throw new NotSupportedException("The Device was not created with Residency support.");
        return new ResidencyResource(nativeDevice, value);
    }

    private QueueCompletion EnqueueMakeResidentCore(
        RhiQueue queue,
        ReadOnlySpan<ResidencyResource> resources)
    {
        VulkanQueue nativeQueue = RequireQueue(queue, nameof(queue));
        ValidateResidencyResources((VulkanDevice)nativeQueue.Device, resources);
        return Submit(nativeQueue, new QueueSubmitDesc([], [], [], [], []));
    }

    private void EvictCore(RhiDevice device, ReadOnlySpan<ResidencyResource> resources)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        ValidateResidencyResources(nativeDevice, resources);
    }

    private static void ValidateResidencyResources(
        VulkanDevice device,
        ReadOnlySpan<ResidencyResource> resources)
    {
        foreach (ref readonly ResidencyResource resource in resources)
        {
            if (resource.IsDefault || !ReferenceEquals(resource.Device, device))
                throw new ArgumentException("Residency resources must be initialized and belong to the target Device.", nameof(resources));
        }
    }

    private CalibratedTimestampInfo CalibrateTimestampsCore(RhiQueue queue)
    {
        VulkanQueue nativeQueue = RequireQueue(queue, nameof(queue));
        VulkanDevice device = (VulkanDevice)nativeQueue.Device;
        if (!device.TryGetCapability(out CalibratedTimestamps? capability) || capability is null)
            throw new NotSupportedException("The Device was not created with CalibratedTimestamps support.");
        uint domainCount = 0;
        delegate* unmanaged<VkPhysicalDevice, uint*, TimeDomainKHR*, Result> getDomains =
            (delegate* unmanaged<VkPhysicalDevice, uint*, TimeDomainKHR*, Result>)
            GetCalibratedTimestampFunction(
                instanceFunction: true,
                "vkGetPhysicalDeviceCalibrateableTimeDomainsKHR",
                "vkGetPhysicalDeviceCalibrateableTimeDomainsEXT",
                device);
        ThrowIfFailed(
            getDomains(device.PhysicalDevice, &domainCount, null),
            "vkGetPhysicalDeviceCalibrateableTimeDomainsEXT(count)");
        TimeDomainKHR[] domains = new TimeDomainKHR[domainCount];
        fixed (TimeDomainKHR* domainPointer = domains)
        {
            ThrowIfFailed(
                getDomains(device.PhysicalDevice, &domainCount, domainPointer),
                "vkGetPhysicalDeviceCalibrateableTimeDomainsEXT(data)");
        }
        if (!domains.Contains(TimeDomainKHR.QueryPerformanceCounterKhr) ||
            !domains.Contains(TimeDomainKHR.DeviceKhr))
            throw new NotSupportedException("The Vulkan driver does not expose QPC/device calibrated time domains.");
        CalibratedTimestampInfoKHR* infos = stackalloc CalibratedTimestampInfoKHR[2];
        infos[0] = new CalibratedTimestampInfoKHR
        {
            SType = StructureType.CalibratedTimestampInfoKhr,
            TimeDomain = TimeDomainKHR.QueryPerformanceCounterKhr,
        };
        infos[1] = new CalibratedTimestampInfoKHR
        {
            SType = StructureType.CalibratedTimestampInfoKhr,
            TimeDomain = TimeDomainKHR.DeviceKhr,
        };
        ulong* timestamps = stackalloc ulong[2];
        ulong maximumDeviation = 0;
        delegate* unmanaged<VkDevice, uint, CalibratedTimestampInfoKHR*, ulong*, ulong*, Result>
            getTimestamps =
            (delegate* unmanaged<VkDevice, uint, CalibratedTimestampInfoKHR*, ulong*, ulong*, Result>)
            GetCalibratedTimestampFunction(
                instanceFunction: false,
                "vkGetCalibratedTimestampsKHR",
                "vkGetCalibratedTimestampsEXT",
                device);
        device.ThrowIfDeviceCallFailed(
            getTimestamps(device.Native, 2, infos, timestamps, &maximumDeviation),
            "vkGetCalibratedTimestampsEXT");
        ulong queueFrequency = checked((ulong)Math.Round(1_000_000_000d / device.TimestampPeriod));
        return new CalibratedTimestampInfo(
            checked((long)timestamps[0]),
            Stopwatch.Frequency,
            timestamps[1],
            queueFrequency);
    }

    private void* GetCalibratedTimestampFunction(
        bool instanceFunction,
        string preferredName,
        string fallbackName,
        VulkanDevice device)
    {
        void* address = instanceFunction
            ? Api.GetInstanceProcAddr(Instance, preferredName).Handle
            : Api.GetDeviceProcAddr(device.Native, preferredName).Handle;
        if (address is null)
        {
            address = instanceFunction
                ? Api.GetInstanceProcAddr(Instance, fallbackName).Handle
                : Api.GetDeviceProcAddr(device.Native, fallbackName).Handle;
        }
        return address is not null
            ? address
            : throw new NotSupportedException(
                $"The Vulkan driver exposes calibrated timestamps but neither '{preferredName}' nor '{fallbackName}' is loadable.");
    }
}
