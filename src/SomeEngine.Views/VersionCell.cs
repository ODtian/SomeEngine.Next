namespace SomeEngine.Views;

/// <summary>A versioned source for data stored outside the ECS.</summary>
public class VersionCell
{
    /// <summary>The source version. Even versions describe completed writes.</summary>
    public uint Version;

    /// <summary>Advances the version after a write by a single writer.</summary>
    public void Advance() => Version = unchecked(Version + 2);

    /// <summary>Atomically advances the version after a cross-thread write.</summary>
    public void AdvanceShared() => Interlocked.Add(ref Version, 2);

    internal virtual void Refresh(ulong frame) { }
    internal virtual Exception? ReadError => null;
}
