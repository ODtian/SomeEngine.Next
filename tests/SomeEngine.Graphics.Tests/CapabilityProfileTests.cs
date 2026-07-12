using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class CapabilityProfileTests
{
    [Fact]
    public void Null_reports_the_mandatory_fl12_0_sm62_profile_without_bindless()
    {
        using var device = new Device();

        Assert.Equal(BackendKind.Null, device.Info.Backend);
        Assert.False(device.Info.HardwareAccelerated);
        Assert.NotEmpty(device.Info.DriverVersion);
        Assert.NotEmpty(device.Info.ApiVersion);
        Assert.True(device.Info.ValidationEnabled);
        Assert.True(device.Capabilities.SupportsTraditionalBinding);
        Assert.True(device.Capabilities.SupportsIndirectDraw);
        Assert.True(device.Capabilities.SupportsIndirectDrawIndexed);
        Assert.True(device.Capabilities.SupportsIndirectDispatch);
        Assert.True(device.Capabilities.SupportsTimestampQueries);
        Assert.True(device.Capabilities.SupportsOcclusionQueries);
        Assert.True(device.Capabilities.SupportsPipelineStatisticsQueries);
        Assert.False(device.Capabilities.SupportsBindless);
        Assert.False(device.Compilation.SupportsBindless);
    }

    [Fact]
    public void Format_support_and_limits_fail_closed()
    {
        using var device = new Device();

        Assert.Equal(FormatSupport.None, device.GetFormatSupport(Format.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => device.GetFormatSupport((Format)ushort.MaxValue));
        Assert.True(device.GetFormatSupport(Format.R8G8B8A8UNorm).HasFlag(FormatSupport.RenderTarget));
        Assert.True(device.GetFormatSupport(Format.B8G8R8A8UNorm).HasFlag(FormatSupport.Present));
        Assert.False(device.GetFormatSupport(Format.D32Float).HasFlag(FormatSupport.Storage));
        Assert.True(device.GetFormatSupport(Format.D32Float).HasFlag(FormatSupport.DepthStencil));
        Assert.True(device.Capabilities.Limits.MaxBufferSize > 0);
        Assert.True(device.Capabilities.Limits.MaxTextureDimension2D > 0);
        Assert.True(device.Capabilities.Limits.MaxBindingsPerGroup > 0);
        Assert.True(device.Capabilities.Limits.MaxPushConstantBytes > 0);
    }

    [Fact]
    public void Bindless_false_rejects_optional_api_without_affecting_traditional_binding()
    {
        using var device = new Device(new Options { SupportsBindless = false });
        BindGroupLayoutHandle layout = device.CreateBindGroupLayout([]);
        BindGroupHandle group = device.CreateBindGroup(layout, []);
        Assert.True(group.IsValid);

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            device.CreateBindlessTable(new BindlessTableDesc(BindingKind.SampledTexture, 8)));
        Assert.Contains("not supported", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DeviceErrorKind.Unsupported, device.LastError.Kind);

        device.DestroyBindGroup(group);
        device.DestroyBindGroupLayout(layout);
    }
}
