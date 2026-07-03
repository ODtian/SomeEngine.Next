namespace SomeEngine.ECS.Systems;

/// <summary>
/// Authoring interface for systems bound to a concrete scheduling context.
/// </summary>
/// <typeparam name="TContext">The domain-specific context exposed to systems in a group.</typeparam>
public interface ISystem<TContext>
{
    /// <summary>Runs once before the first update of this system.</summary>
    void OnCreate(ref TContext context) { }

    /// <summary>Runs on every enabled group update.</summary>
    void OnUpdate(ref TContext context);

    /// <summary>Runs once when the owning group is disposed, if the system was created.</summary>
    void OnDestroy(ref TContext context) { }
}

