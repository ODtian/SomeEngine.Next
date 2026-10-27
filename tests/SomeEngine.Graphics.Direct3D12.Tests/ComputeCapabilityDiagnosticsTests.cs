using Silk.NET.Direct3D12;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;
using Xunit.Abstractions;
using NativeFeature = Silk.NET.Direct3D12.Feature;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed unsafe class ComputeCapabilityDiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Hardware")]
    public void Restored_compute_capabilities_match_native_device_and_report_current_budget()
    {
        Assert.True(OperatingSystem.IsWindows(), "This diagnostic requires the Windows D3D12 backend.");
        using ValidationLayer backend = new(D3D12GraphicsBackend.Create());
        AdapterEnumerationOptions options = new(AdapterPreference.HighPerformance, IncludeSoftware: false);
        _ = backend.TryEnumerateAdapters(options, [], out int adapterCount);
        AdapterInfo[] adapters = new AdapterInfo[adapterCount];
        Assert.True(backend.TryEnumerateAdapters(options, adapters, out int confirmed));
        Assert.Equal(adapterCount, confirmed);
        AdapterInfo adapter = adapters.FirstOrDefault(static item => item.HardwareAccelerated);
        Assert.True(adapter.HardwareAccelerated, "A hardware adapter is required; WARP is not this observation.");
        DeviceQueueDesc[] queues = [new(QueueType.Compute)];
        using Device device = backend.CreateDevice(new DeviceDesc(adapter.Id, queues,
            label: "compute capability diagnostic"));

        // The supported getter borrows the exact backend device. Do not Release this pointer.
        ID3D12Device10* native = backend.GetNativeDevice(device);
        Assert.NotEqual((nint)0, (nint)native);
        FeatureDataD3D12Options1 nativeOptions1 = default;
        int options1Result = native->CheckFeatureSupport(NativeFeature.D3D12Options1,
            &nativeOptions1, (uint)sizeof(FeatureDataD3D12Options1));
        output.WriteLine($"COMPUTE_CAPS_ADAPTER name={adapter.Name} driver={adapter.DriverVersion} " +
            $"id={adapter.Id.Low:X16}:{adapter.Id.High:X16}");
        output.WriteLine($"COMPUTE_CAPS_NATIVE_OPTIONS1 hr=0x{unchecked((uint)options1Result):X8}");
        Assert.True(options1Result >= 0, "The direct native Options1 query must succeed.");

        bool waveOps = nativeOptions1.WaveOps;
        bool int64ShaderOps = nativeOptions1.Int64ShaderOps;
        DeviceCapabilities caps = device.Capabilities;
        uint groupBudget = Math.Max(1u,
            Math.Max(caps.ShaderLaneCount, caps.Limits.MaximumComputeThreadsPerGroup) / 64u);
        output.WriteLine($"COMPUTE_CAPS_NATIVE totalLanes={nativeOptions1.TotalLaneCount} " +
            $"waveOps={waveOps} waveMin={nativeOptions1.WaveLaneCountMin} " +
            $"waveMax={nativeOptions1.WaveLaneCountMax} int64ShaderOps={int64ShaderOps}");
        output.WriteLine($"COMPUTE_CAPS_PUBLIC maxThreadsPerGroup={caps.Limits.MaximumComputeThreadsPerGroup} " +
            $"shaderLaneCount={caps.ShaderLaneCount} forwardProgress={caps.SupportsComputeForwardProgress} " +
            $"bufferInt64={caps.SupportsBufferInt64Atomics} imageInt64={caps.SupportsImageInt64Atomics}");
        output.WriteLine($"COMPUTE_CAPS_DERIVED currentFormulaGroupBudget={groupBudget} " +
            "dispatchDimensionLimit=65535 capturedGpuDispatchCount=false");

        Assert.Equal(1_024u, caps.Limits.MaximumComputeThreadsPerGroup);
        Assert.Equal(nativeOptions1.TotalLaneCount, caps.ShaderLaneCount);
        Assert.False(caps.SupportsComputeForwardProgress);
        Assert.True(nativeOptions1.TotalLaneCount > 0u, "This hardware observation must report native lanes.");
        Assert.InRange(groupBudget, 16u, 65_535u);

        // Preserve the existing Int64 predicates while retaining the full Options1 value.
        FeatureDataShaderModel nativeShaderModel = new(D3DShaderModel.ShaderModel66);
        int shaderModelResult = native->CheckFeatureSupport(NativeFeature.ShaderModel,
            &nativeShaderModel, (uint)sizeof(FeatureDataShaderModel));
        bool shaderModel66 = shaderModelResult >= 0 &&
            nativeShaderModel.HighestShaderModel >= D3DShaderModel.ShaderModel66;
        FeatureDataD3D12Options9 nativeOptions9 = default;
        int options9Result = native->CheckFeatureSupport(NativeFeature.D3D12Options9,
            &nativeOptions9, (uint)sizeof(FeatureDataD3D12Options9));
        bool typedInt64 = options9Result >= 0 && nativeOptions9.AtomicInt64OnTypedResourceSupported;
        const FormatFeatures requiredFormatFeatures =
            FormatFeatures.ShaderLoad | FormatFeatures.Storage | FormatFeatures.StorageStore;
        FormatFeatures r64 = caps.GetFormatSupport(Format.R64UInt).Features;
        bool expectedBufferInt64 = shaderModel66 && int64ShaderOps;
        bool expectedImageInt64 = expectedBufferInt64 && typedInt64 &&
            (r64 & requiredFormatFeatures) == requiredFormatFeatures;
        output.WriteLine($"COMPUTE_CAPS_INT64 shaderModelHr=0x{unchecked((uint)shaderModelResult):X8} " +
            $"shaderModel66={shaderModel66} options9Hr=0x{unchecked((uint)options9Result):X8} " +
            $"typedInt64={typedInt64} r64Features={r64}");
        Assert.Equal(expectedBufferInt64, caps.SupportsBufferInt64Atomics);
        Assert.Equal(expectedImageInt64, caps.SupportsImageInt64Atomics);

        // Diagnostic reproduction of the current consumer formula, not a public contract,
        // an override of production dispatch, or a captured renderer/GPU dispatch count.
        ulong fullBudgetWork = checked((ulong)groupBudget * 16ul);
        ulong[] workSamples = [0ul, 1ul, 16ul, 17ul, 65ul, 1_024ul, fullBudgetWork];
        foreach (ulong work in workSamples)
        {
            uint workerGroups = Math.Max(1u,
                Math.Min(groupBudget, checked((uint)((work + 15ul) / 16ul))));
            output.WriteLine($"COMPUTE_CAPS_DERIVED_WORK work={work} workerGroups={workerGroups}");
            Assert.InRange(workerGroups, 1u, 65_535u);
        }
    }
}
