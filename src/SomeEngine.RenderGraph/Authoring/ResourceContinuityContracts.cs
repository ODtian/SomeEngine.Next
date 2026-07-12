namespace SomeEngine.RenderGraph;

/// <summary>Defines how a graph-owned resource survives immediate invocations.</summary>
public enum ResourceLifetime : byte
{
    Transient,
    Persistent,
    Temporal,
}

/// <summary>
/// Describes one graph buffer together with its immediate-invocation lifetime. A default stable
/// identity is scoped to the resource's declaration ordinal; callers may provide a stable identity
/// when declarations can move while the logical resource remains the same.
/// </summary>
public readonly record struct BufferResourceDesc
{
    public BufferResourceDesc(
        BufferDesc description,
        ResourceLifetime lifetime = ResourceLifetime.Transient,
        int historyCount = 0,
        Guid stableId = default)
    {
        Description = description;
        Lifetime = lifetime;
        HistoryCount = historyCount;
        StableId = stableId;
        Validate();
    }

    public BufferDesc Description { get; init; }
    public ResourceLifetime Lifetime { get; init; }
    public int HistoryCount { get; init; }
    public Guid StableId { get; init; }

    public static BufferResourceDesc Transient(BufferDesc description) =>
        new(description, ResourceLifetime.Transient);

    public static BufferResourceDesc Persistent(BufferDesc description, Guid stableId = default) =>
        new(description, ResourceLifetime.Persistent, stableId: stableId);

    public static BufferResourceDesc Temporal(BufferDesc description, int historyCount, Guid stableId = default) =>
        new(description, ResourceLifetime.Temporal, historyCount, stableId);

    internal void Validate()
    {
        Description.Validate();
        if (!Enum.IsDefined(Lifetime)) throw new ArgumentOutOfRangeException(nameof(Lifetime));
        if (Lifetime == ResourceLifetime.Temporal)
        {
            if (HistoryCount <= 0) throw new ArgumentOutOfRangeException(nameof(HistoryCount), "A temporal buffer must retain at least one prior frame.");
        }
        else if (HistoryCount != 0)
        {
            throw new ArgumentException("Only temporal buffers may declare a history count.", nameof(HistoryCount));
        }
    }
}

/// <summary>Describes one graph texture together with its immediate-invocation lifetime.</summary>
public readonly record struct TextureResourceDesc
{
    public TextureResourceDesc(
        TextureDesc description,
        ResourceLifetime lifetime = ResourceLifetime.Transient,
        int historyCount = 0,
        Guid stableId = default)
    {
        Description = description;
        Lifetime = lifetime;
        HistoryCount = historyCount;
        StableId = stableId;
        Validate();
    }

    public TextureDesc Description { get; init; }
    public ResourceLifetime Lifetime { get; init; }
    public int HistoryCount { get; init; }
    public Guid StableId { get; init; }

    public static TextureResourceDesc Transient(TextureDesc description) =>
        new(description, ResourceLifetime.Transient);

    public static TextureResourceDesc Persistent(TextureDesc description, Guid stableId = default) =>
        new(description, ResourceLifetime.Persistent, stableId: stableId);

    public static TextureResourceDesc Temporal(TextureDesc description, int historyCount, Guid stableId = default) =>
        new(description, ResourceLifetime.Temporal, historyCount, stableId);

    internal void Validate()
    {
        Description.Validate();
        if (!Enum.IsDefined(Lifetime)) throw new ArgumentOutOfRangeException(nameof(Lifetime));
        if (Lifetime == ResourceLifetime.Temporal)
        {
            if (HistoryCount <= 0) throw new ArgumentOutOfRangeException(nameof(HistoryCount), "A temporal texture must retain at least one prior frame.");
        }
        else if (HistoryCount != 0)
        {
            throw new ArgumentException("Only temporal textures may declare a history count.", nameof(HistoryCount));
        }
    }
}
