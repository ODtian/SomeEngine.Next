namespace SomeEngine.ECS.Serialization.Tests;

internal static class DisposalChecks
{
    internal static async Task WaitForReadRejectionAsync(Action read)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { read(); }
            catch (ObjectDisposedException) { return; }
            await Task.Yield();
        }
    }
}
