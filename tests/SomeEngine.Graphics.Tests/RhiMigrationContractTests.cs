using System.Reflection;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

/// <summary>
/// Locks the public surface accepted by grill run 0004 without taking a compile-time dependency
/// on APIs that are intentionally red during harness authoring. Naming evidence recorded during
/// the grill: Veldrid CommandList uses DrawIndirect/DrawIndexedIndirect/DispatchIndirect, Stride
/// uses QueryPool, and established C# Vulkan/Veldrid surfaces use Swapchain and AcquireNextImage.
/// </summary>
public sealed class RhiMigrationContractTests
{
    private static readonly Assembly GraphicsAssembly = typeof(IDevice).Assembly;

    [Fact]
    public void Indirect_contract_covers_draw_indexed_dispatch_cpu_and_gpu_counts()
    {
        Type context = typeof(ICommandContext);
        Type[] signature =
        [
            typeof(BufferHandle), // argument buffer; may also be the count buffer
            typeof(ulong),        // argument byte offset
            typeof(uint),         // maximum command count, including the CPU-count path
            typeof(uint),         // command stride
            typeof(BufferHandle), // optional GPU count buffer, separate or the same buffer
            typeof(ulong),        // GPU count byte offset
        ];

        foreach (string operation in new[] { "DrawIndirect", "DrawIndexedIndirect", "DispatchIndirect" })
        {
            MethodInfo method = RequireMethod(context, operation, signature);
            ParameterInfo[] parameters = method.GetParameters();
            Assert.True(parameters[4].HasDefaultValue,
                $"{operation} must make the GPU count buffer optional so CPU-count execution remains first-class.");
            Assert.True(parameters[5].HasDefaultValue,
                $"{operation} must make the GPU count-buffer offset optional.");
        }
    }

    [Fact]
    public void Query_contract_exposes_pools_all_required_query_kinds_resolution_and_real_calibration()
    {
        Type queryPool = RequirePublicType("QueryPoolHandle");
        Type queryDescription = RequirePublicType("QueryPoolDesc");
        Type queryType = RequirePublicType("QueryType");
        Type calibration = RequirePublicType("TimestampCalibration");

        AssertEnumMembers(queryType, "Timestamp", "Occlusion", "PipelineStatistics");
        AssertProperties(calibration, "Queue", "CpuTimestamp", "GpuTimestamp", "TimestampFrequency");
        RequireMethodReturning(typeof(IDevice), "CreateQueryPool", queryPool, queryDescription.MakeByRefType());
        RequireMethod(typeof(IDevice), "DestroyQueryPool", queryPool);
        RequireMethodReturning(typeof(IDevice), "GetTimestampCalibration", calibration, typeof(QueueType));

        foreach (string operation in new[]
        {
            "ResetQueryPool",
            "BeginQuery",
            "EndQuery",
            "WriteTimestamp",
            "ResolveQueryPool",
        })
        {
            RequireNamedMethod(typeof(ICommandContext), operation);
        }
    }

    [Fact]
    public void Swapchain_contract_has_explicit_acquire_present_resize_and_destruction()
    {
        Type swapchain = RequirePublicType("SwapchainHandle");
        Type description = RequirePublicType("SwapchainDesc");

        RequireMethodReturning(typeof(IDevice), "CreateSwapchain", swapchain, description.MakeByRefType());
        RequireNamedMethod(typeof(IDevice), "AcquireNextImage");
        RequireNamedMethod(typeof(IDevice), "Present");
        RequireNamedMethod(typeof(IDevice), "Resize");
        RequireMethod(typeof(IDevice), "DestroySwapchain", swapchain);
    }

    [Fact]
    public void Pipeline_readiness_and_cache_are_observable_and_invalidatable()
    {
        Type status = RequirePublicType("PipelineStatus");
        Type statistics = RequirePublicType("PipelineCacheStats");
        Type key = RequirePublicType("PipelineCacheKey");

        AssertEnumMembers(status, "Ready", "Pending", "Failed");
        RequireMethodReturning(typeof(IDevice), "GetPipelineStatus", status, typeof(PipelineHandle));
        RequireMethodReturning(typeof(IDevice), "GetPipelineCacheStats", statistics);
        RequireMethod(typeof(IDevice), "InvalidatePipelineCache", key);
        RequireMethod(typeof(IDevice), "InvalidateAllPipelines");
    }

    [Fact]
    public void Device_loss_is_a_stable_queryable_error_not_only_a_drained_message()
    {
        Type error = RequirePublicType("DeviceError");
        Type errorKind = RequirePublicType("DeviceErrorKind");
        AssertEnumMembers(errorKind, "None", "DeviceLost");

        PropertyInfo? lastError = typeof(IDevice).GetProperty("LastError", BindingFlags.Instance | BindingFlags.Public);
        Assert.True(lastError is not null, "IDevice.LastError is required for durable device-loss observation.");
        Assert.Equal(error, lastError!.PropertyType);
        Assert.NotNull(typeof(IDevice).GetMethod(nameof(IDevice.DrainDiagnostics)));
    }

    [Fact]
    public void Bindless_is_optional_advertised_and_fails_closed_when_disabled()
    {
        Type table = RequirePublicType("BindlessTableHandle");
        Type description = RequirePublicType("BindlessTableDesc");
        RequirePublicType("BindlessSlot");

        PropertyInfo? compilationSupport = typeof(DeviceCompilationSnapshot).GetProperty("SupportsBindless");
        Assert.True(compilationSupport is not null,
            "The immutable compilation snapshot must advertise optional bindless support explicitly.");
        Assert.Equal(typeof(bool), compilationSupport!.PropertyType);

        Type optionsType = typeof(Options);
        PropertyInfo? option = optionsType.GetProperty("SupportsBindless");
        Assert.True(option is not null && option.CanWrite,
            "The Null backend must expose an unsupported bindless mode so fail-close behavior is executable.");
        object options = Activator.CreateInstance(optionsType)!;
        option!.SetValue(options, false);

        using var device = (Device)Activator.CreateInstance(typeof(Device), options)!;
        Assert.False((bool)compilationSupport.GetValue(device.Compilation)!);

        MethodInfo create = RequireMethodReturning(typeof(IDevice), "CreateBindlessTable", table, description.MakeByRefType());
        object bindlessDescription = Activator.CreateInstance(description)!;
        TargetInvocationException invocation = Assert.Throws<TargetInvocationException>(() =>
            create.Invoke(device, [bindlessDescription]));
        Assert.IsType<NotSupportedException>(invocation.InnerException);
    }

    private static Type RequirePublicType(string name)
    {
        Type? result = GraphicsAssembly.GetType($"SomeEngine.Graphics.{name}", throwOnError: false);
        Assert.True(result is not null && result.IsPublic, $"Missing public RHI type SomeEngine.Graphics.{name}.");
        return result!;
    }

    private static MethodInfo RequireNamedMethod(Type owner, string name)
    {
        MethodInfo[] methods = owner.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
            .ToArray();
        Assert.True(methods.Length != 0, $"{owner.Name}.{name} is required by the accepted RHI contract.");
        Assert.True(methods.Length == 1, $"{owner.Name}.{name} must have one unambiguous portable overload.");
        return methods[0];
    }

    private static MethodInfo RequireMethod(Type owner, string name, params Type[] parameters)
    {
        MethodInfo? method = owner.GetMethod(name, BindingFlags.Instance | BindingFlags.Public, parameters);
        Assert.True(method is not null,
            $"Missing {owner.Name}.{name}({string.Join(", ", parameters.Select(static type => type.Name))}).");
        Assert.Equal(typeof(void), method!.ReturnType);
        return method;
    }

    private static MethodInfo RequireMethodReturning(Type owner, string name, Type returnType, params Type[] parameters)
    {
        MethodInfo? method = owner.GetMethod(name, BindingFlags.Instance | BindingFlags.Public, parameters);
        Assert.True(method is not null,
            $"Missing {owner.Name}.{name}({string.Join(", ", parameters.Select(static type => type.Name))}).");
        Assert.Equal(returnType, method!.ReturnType);
        return method;
    }

    private static void AssertEnumMembers(Type enumType, params string[] members)
    {
        Assert.True(enumType.IsEnum, $"{enumType.Name} must be an enum.");
        string[] available = Enum.GetNames(enumType);
        foreach (string member in members)
        {
            Assert.Contains(member, available);
        }
    }

    private static void AssertProperties(Type owner, params string[] names)
    {
        foreach (string name in names)
        {
            Assert.NotNull(owner.GetProperty(name, BindingFlags.Instance | BindingFlags.Public));
        }
    }
}
