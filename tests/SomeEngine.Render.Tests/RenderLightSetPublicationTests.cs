using SomeEngine.Render.Lighting;

namespace SomeEngine.Render.Tests;

public sealed class RenderLightSetSystemContractTests
{
    [Fact]
    public void Light_set_is_owned_by_the_query_system()
    {
        var system = new RenderLightSetSystem();
        Assert.Throws<InvalidOperationException>(system.RequireCurrent);
        Assert.Null(typeof(RenderLightSetSystem).Assembly.GetType(
            "SomeEngine.Render.Lighting.RenderLightSetPublication"));
    }
}
