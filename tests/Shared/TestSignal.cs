namespace SomeEngine.Testing;

internal sealed class TestSignal(bool signaled = false) : IDisposable
{
    private readonly ManualResetEventSlim _event = new(signaled);

    public Action? SetCallback { get; set; }
    public Action? WaitCallback { get; set; }
    public bool IsSet => _event.IsSet;

    public void Set()
    {
        SetCallback?.Invoke();
        _event.Set();
    }

    public void Reset() => _event.Reset();

    public bool Wait(TimeSpan timeout)
    {
        if (!_event.Wait(timeout))
            return false;
        WaitCallback?.Invoke();
        return true;
    }

    public void Wait()
    {
        if (!Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The test signal was not released.");
    }

    public void Dispose() => _event.Dispose();
}
