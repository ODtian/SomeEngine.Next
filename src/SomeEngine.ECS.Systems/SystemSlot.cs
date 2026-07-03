namespace SomeEngine.ECS.Systems;

/// <summary>
/// Per-system runtime state owned by <see cref="SystemGroup{TContext}"/> and its context driver.
/// </summary>
public struct SystemSlot
{
    public int Index;
    public bool Created;
    public bool Enabled;
    public uint LastSystemVersion;
    public uint CurrentSystemVersion;
}

