namespace SomeEngine.ECS;

/// <summary>
/// Controls which parts of an entity's same-world logical storage surface are copied.
/// <see cref="Default"/> is treated as <see cref="Standard"/> by clone/copy APIs.
/// </summary>
[Flags]
public enum EntityCopyOptions
{
    /// <summary>
    /// Standard shallow copy surface: table components, tags, enableable state, shared components,
    /// dynamic buffers, and sparse components. Cleanup components and relations are excluded.
    /// </summary>
    Default = 0,

    /// <summary>Copy non-cleanup table components.</summary>
    TableComponents = 1 << 0,

    /// <summary>Copy normal tags. Relation tags are managed by relation copy options.</summary>
    Tags = 1 << 1,

    /// <summary>Copy enableable component enabled/disabled bits for copied table components.</summary>
    EnableableState = 1 << 2,

    /// <summary>Copy shared component values and chunk placement.</summary>
    SharedComponents = 1 << 3,

    /// <summary>Copy dynamic buffer sequences into independent backing storage.</summary>
    DynamicBuffers = 1 << 4,

    /// <summary>Copy sparse component values.</summary>
    SparseComponents = 1 << 5,

    /// <summary>Copy cleanup components. Cleanup components are excluded from <see cref="Standard"/>.</summary>
    CleanupComponents = 1 << 6,

    /// <summary>
    /// Replace the target's outgoing relation edges with the source's outgoing relation edges.
    /// Incoming relation edges are never copied.
    /// </summary>
    OutgoingRelations = 1 << 7,

    /// <summary>
    /// Standard shallow copy surface used when callers pass <see cref="Default"/>.
    /// </summary>
    Standard = TableComponents |
               Tags |
               EnableableState |
               SharedComponents |
               DynamicBuffers |
               SparseComponents,
}

