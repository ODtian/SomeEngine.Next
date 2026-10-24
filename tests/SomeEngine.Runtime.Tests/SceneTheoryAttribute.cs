using System.Collections.Frozen;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Vulkan;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SomeEngine.Runtime.Tests;

[XunitTestCaseDiscoverer("SomeEngine.Runtime.Tests.SceneTheoryDiscoverer", "SomeEngine.Runtime.Tests")]
public sealed class SceneTheoryAttribute : TheoryAttribute;

public sealed class SceneTheoryDiscoverer : TheoryDiscoverer
{
    private readonly record struct BackendSupport(string? Reason, bool MeshShaders);
    private static readonly FrozenDictionary<string, BackendSupport> Backends =
        new[] { "d3d12", "vulkan" }.ToFrozenDictionary(name => name, ReadBackendSupport);

    public SceneTheoryDiscoverer(IMessageSink diagnosticMessages) : base(diagnosticMessages) { }
    protected override IEnumerable<IXunitTestCase> CreateTestCasesForDataRow(
        ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo theoryAttribute, object[] dataRow)
    {
        string backendName = (string)dataRow[0];
        BackendSupport support = Backends[backendName];
        string? reason = support.Reason;
        if (reason is null && dataRow.Contains("mesh") && !support.MeshShaders)
            reason = $"{backendName}: this case requires Mesh shaders.";
        return reason is null ? base.CreateTestCasesForDataRow(discoveryOptions, testMethod, theoryAttribute, dataRow) :
            [new XunitSkippedDataRowTestCase(DiagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(),
                discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod, reason, dataRow)];
    }

    private static BackendSupport ReadBackendSupport(string name)
    {
        if (!OperatingSystem.IsWindows()) return new($"{name}: the application window requires Windows.", false);
        try
        {
            using IGraphicsBackend backend = name == "vulkan" ? VulkanGraphicsBackend.Create() : D3D12GraphicsBackend.Create();
            var enumeration = new AdapterEnumerationOptions(AdapterPreference.HighPerformance);
            backend.TryEnumerateAdapters(enumeration, [], out int count);
            var adapters = new AdapterInfo[count];
            backend.TryEnumerateAdapters(enumeration, adapters, out _);
            AdapterInfo[] hardware = adapters.Where(adapter => adapter.HardwareAccelerated).ToArray();
            if (hardware.Length == 0) return new($"{name}: a hardware adapter is required; #128 tracks WARP frame completion timeouts.", false);
            using var device = backend.CreateDevice(new DeviceDesc(hardware[0].Id,
                [new(QueueType.Graphics), new(QueueType.Compute), new(QueueType.Copy)],
                requiredFeatures: DeviceFeatures.Presentation | DeviceFeatures.IndirectCommands,
                optionalFeatures: DeviceFeatures.MeshShaders));
            if (!device.Capabilities.SupportsBufferInt64Atomics || !device.Capabilities.SupportsImageInt64Atomics)
                return new($"{name}: Cluster visibility requires 64-bit buffer and image atomics on {device.Adapter.Name}.", false);
            return new(null, backend.TryGetCapability(device, out MeshShaders? mesh) && mesh is not null);
        }
        catch (Exception error) when (error is NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return new($"{name}: {error.GetType().Name}: {error.Message}", false);
        }
    }
}
