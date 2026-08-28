using SomeEngine.Render.Lighting;

namespace SomeEngine.Render.Tests;

public sealed class RenderLightSetMailboxTests
{
    [Fact]
    public void Publication_is_consumed_exactly_once()
    {
        var mailbox = new RenderLightSetMailbox();
        var lights = new RenderLightSet();

        mailbox.Publish(lights);
        Assert.Throws<InvalidOperationException>(() => mailbox.Publish(new RenderLightSet()));
        Assert.Same(lights, mailbox.TakeRequired());
        Assert.Throws<InvalidOperationException>(() => mailbox.TakeRequired());
    }
}
