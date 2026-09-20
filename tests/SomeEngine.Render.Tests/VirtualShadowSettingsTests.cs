using SomeEngine.Render.Shadows;
using Xunit;

namespace SomeEngine.Render.Tests;

public sealed class VirtualShadowSettingsTests
{
    [Theory]
    [InlineData(16384u, 128u)]
    [InlineData(4096u, 64u)]
    [InlineData(2048u, 128u)]
    public void FallbackMipCoversTheWholeLocalLightProjection(uint resolution, uint pageSize)
    {
        var settings = new VirtualShadowMapSettings
        {
            VirtualResolution = resolution,
            PageSize = pageSize,
            AtlasSize = pageSize * 4,
        };
        settings.Validate();
        Assert.Equal(resolution, settings.PageSize * settings.CoarsePageScale);
    }
}
