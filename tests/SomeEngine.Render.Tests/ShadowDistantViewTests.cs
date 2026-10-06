using System.Numerics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Tests;

public sealed class ShadowDistantViewTests
{
    [Fact]
    public void ConservativeLocalMipReachesOnePageAtTheBoundingSphereBoundary()
    {
        var settings = new VirtualShadowMapSettings { VirtualResolution = 512, AtlasSize = 128, PageSize = 128 };
        var view = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 1000), 128, 128);
        Assert.Equal(1u, settings.CalculateConservativeLocalMip(view, new Vector3(0, 0, -19), 10, 1, 0));
        Assert.Equal(2u, settings.CalculateConservativeLocalMip(view, new Vector3(0, 0, -20), 10, 1, 0));
    }
}
