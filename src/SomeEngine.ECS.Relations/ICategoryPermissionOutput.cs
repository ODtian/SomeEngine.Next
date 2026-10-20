namespace SomeEngine.ECS.Relations;

/// <summary>Consumes a concrete permission result while preserving the caller's output state.</summary>
public interface ICategoryPermissionOutput<TItem, TOutput>
    where TItem : allows ref struct
    where TOutput : allows ref struct
{
    static abstract bool Matched(scoped in TItem item, scoped ref TOutput output);

    static abstract bool Unmatched(scoped in TItem item, scoped ref TOutput output);
}
