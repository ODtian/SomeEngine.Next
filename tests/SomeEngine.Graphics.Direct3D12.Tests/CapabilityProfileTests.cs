using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Vortice.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class CapabilityProfileTests
{
    [Fact]
    public void Warp_reports_adapter_driver_format_limits_and_optional_profiles()
    {
        Assert.True(OperatingSystem.IsWindows(), "The required WARP capability lane must execute on Windows.");
        using Device device = CreateDevice();

        Assert.Equal(BackendKind.Direct3D12, device.Info.Backend);
        Assert.False(device.Info.HardwareAccelerated);
        Assert.False(string.IsNullOrWhiteSpace(device.Info.Name));
        Assert.False(string.IsNullOrWhiteSpace(device.Info.DriverVersion));
        Assert.Contains("D3D12 FL12_0", device.Info.ApiVersion, StringComparison.Ordinal);
        Assert.True(device.Info.ValidationEnabled);

        DeviceCapabilities capabilities = device.Capabilities;
        Assert.True(capabilities.SupportsTraditionalBinding);
        Assert.True(capabilities.SupportsIndirectDraw);
        Assert.True(capabilities.SupportsTimestampQueries);
        Assert.True(capabilities.SupportsSwapchain);
        Assert.True(capabilities.SupportsMemoryBudget);
        Assert.False(capabilities.SupportsBindless);
        Assert.False(capabilities.SupportsMeshShaders);
        Assert.False(capabilities.SupportsVariableRateShading);
        Assert.False(capabilities.SupportsRayTracing);
        Assert.False(capabilities.SupportsSparseResources);
        Assert.False(capabilities.SupportsSamplerFeedback);
        Assert.False(capabilities.SupportsWorkGraphs);
        Assert.True(capabilities.HighestShaderModel >= new Version(6, 2));
        Assert.False(device.Compilation.SupportsBindless);
        Assert.True(capabilities.Limits.MaxBufferSize > 0);
        Assert.True(capabilities.Limits.MaxTextureDimension2D >= 16_384);
        Assert.Equal(256u, capabilities.Limits.TextureDataPitchAlignment);
        Assert.Equal(512u, capabilities.Limits.TextureDataPlacementAlignment);
    }

    [Fact]
    public void Warp_format_support_matches_native_feature_queries()
    {
        Assert.True(OperatingSystem.IsWindows(), "The required WARP format lane must execute on Windows.");
        using Device device = CreateDevice();

        FeatureDataFormatSupport native = new() { Format = Mappings.Format(Format.R8G8B8A8UNorm) };
        Assert.True(device.NativeDevice.CheckFeatureSupport(Feature.FormatSupport, ref native));
        FormatSupport portable = device.GetFormatSupport(Format.R8G8B8A8UNorm);

        Assert.Equal((native.Support1 & FormatSupport1.RenderTarget) != 0, portable.HasFlag(FormatSupport.RenderTarget));
        Assert.Equal((native.Support1 & FormatSupport1.ShaderSample) != 0, portable.HasFlag(FormatSupport.Sampled));
        Assert.Equal((native.Support1 & FormatSupport1.TypedUnorderedAccessView) != 0, portable.HasFlag(FormatSupport.Storage));
        Assert.Equal((native.Support1 & FormatSupport1.MultisampleResolve) != 0, portable.HasFlag(FormatSupport.Resolve));
    }

    [Fact]
    public void Warp_traditional_binding_does_not_require_sm66_tier3_or_direct_heap_indexing()
    {
        Assert.True(OperatingSystem.IsWindows(), "The required WARP traditional-binding lane must execute on Windows.");
        using Device device = CreateDevice();
        BindGroupLayoutHandle layout = device.CreateBindGroupLayout([
            new BindingDesc(0, BindingKind.ReadOnlyBuffer, 1, ShaderStage.Compute),
        ]);

        Assert.True(layout.IsValid);
        Assert.True(device.Capabilities.SupportsTraditionalBinding);
        Assert.False(device.Capabilities.SupportsBindless);
        Assert.Throws<NotSupportedException>(() => device.CreateBindlessTable(
            new BindlessTableDesc(BindingKind.ReadOnlyBuffer, 8)));

        device.DestroyBindGroupLayout(layout);
        device.CollectGarbage();
    }

    private static Device CreateDevice() => new(new Options
    {
        UseWarpAdapter = true,
        EnableDebugLayer = true,
        EnableGpuValidation = false,
    });
}
