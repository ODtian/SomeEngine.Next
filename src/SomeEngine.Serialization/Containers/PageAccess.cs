namespace SomeEngine.Serialization.Containers;

/// <summary>Coordinates a content branch whose fields use several page buffers.</summary>
public sealed class PageAccess
{
    private bool _writing;
    public object SyncRoot { get; } = new();

    public void RequireIdle()
    {
        if (Volatile.Read(ref _writing))
            throw new InvalidOperationException("Finish the current page write before accessing this content branch.");
    }

    internal void BeginWrite() { RequireIdle(); _writing = true; }
    internal void EndWrite() => _writing = false;
}
