#if DEBUG
namespace SomeEngine.Views.Tests;

public sealed class AsyncPrimitiveUsageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreatingATaskOrResourceOutsideAViewReportsADevelopmentCheck(bool resource)
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (resource)
                Scope.Resource(1, static (source, cancel) => ValueTask.FromResult(source));
            else
                Scope.Launch(1, static (key, cancel) => default);
        });
    }
}
#endif
