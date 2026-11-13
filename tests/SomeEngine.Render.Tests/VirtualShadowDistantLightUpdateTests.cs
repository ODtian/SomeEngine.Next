using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Tests;

public sealed class VirtualShadowDistantLightUpdateTests
{
    [Fact]
    public void OnlyCompletedFramesAdvanceDistantLightUpdateAge()
    {
        var updates = new VirtualShadowDistantLightUpdates();
        VirtualShadowLocalLight[] lights =
        [
            new(VirtualShadowLightKind.Point, default, 0),
            new(VirtualShadowLightKind.Point, default, 1),
        ];

        VirtualShadowLocalLight[] first = updates.Select(lights, 1);
        VirtualShadowLocalLight[] unsubmitted = updates.Select(lights, 1);

        Assert.Equal(first, unsubmitted);
        VirtualShadowLocalLight firstLight = Assert.Single(first);
        updates.CompleteFrame(lights, first);
        VirtualShadowLocalLight nextLight = Assert.Single(updates.Select(lights, 1));
        Assert.NotEqual(firstLight, nextLight);
    }

    [Fact]
    public void ReturnedSelectionsKeepTheirContentsAcrossLaterSelections()
    {
        var updates = new VirtualShadowDistantLightUpdates();
        var firstLight = new VirtualShadowLocalLight(VirtualShadowLightKind.Point, default, 0);
        var secondLight = new VirtualShadowLocalLight(VirtualShadowLightKind.Point, default, 1);

        VirtualShadowLocalLight[] first = updates.Select([firstLight], 1);
        VirtualShadowLocalLight[] second = updates.Select([secondLight], 1);

        Assert.Equal(new[] { firstLight }, first);
        Assert.Equal(new[] { secondLight }, second);
    }
}
