using Xunit;
namespace SomeEngine.Graphics.Tests;
public sealed class ContractValueTests
{
    [Fact]
    public void Default_completion_has_no_usable_queue_or_value()
    {
        QueueCompletion completion = default;

        Assert.Throws<InvalidOperationException>(() => completion.Queue);
        Assert.Throws<InvalidOperationException>(() => completion.Value);
    }
}
