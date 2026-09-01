using SomeEngine.Render.Lighting;

namespace SomeEngine.Render.Tests;

public sealed class RenderLightSetPublicationTests
{
    [Fact]
    public void Publication_is_shared_and_replaced_per_frame()
    {
        var publication = new RenderLightSetPublication();
        Assert.Throws<ArgumentNullException>(() => publication.Publish(null!));
        var lights = new RenderLightSet();

        publication.Publish(lights);
        Assert.Same(lights, publication.RequireCurrent());

        var replacement = new RenderLightSet();
        publication.Publish(replacement);
        Assert.Same(replacement, publication.RequireCurrent());
    }
}
