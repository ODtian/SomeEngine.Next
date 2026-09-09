namespace SomeEngine.Assets;

/// <summary>
/// Marks the point at which a reload has replaced the resident content. A reload validates and
/// constructs its replacement before changing the asset, then calls Accept before notifications
/// or cleanup that may fail. Accepted content is not rolled back by those later failures.
/// </summary>
public sealed class AssetUpdate
{
    private Action? _accept;
    internal AssetUpdate(Action accept) => _accept = accept;
    internal bool IsAccepted => _accept is null;

    public void Accept()
    {
        Action accept = _accept ?? throw new InvalidOperationException("The asset update was already accepted.");
        accept();
        _accept = null;
    }
    internal static void Notify<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        List<Exception>? failures = null;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try { handler(value); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        if (failures is not null) throw new AggregateException("Asset change notifications failed.", failures);
    }
}
