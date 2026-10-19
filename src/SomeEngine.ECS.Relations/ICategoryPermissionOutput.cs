namespace SomeEngine.ECS.Relations;

/// <summary>Consumes a concrete permission result while preserving the caller's output state.</summary>
public interface ICategoryPermissionOutput<TOutput> where TOutput : allows ref struct
{
    bool Matched(scoped ref TOutput output);

    bool Unmatched(scoped ref TOutput output);
}
