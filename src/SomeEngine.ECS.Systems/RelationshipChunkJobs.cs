using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// A serial chunk job that writes canonical <see cref="Parent{TDomain}"/> components.
/// </summary>
/// <remarks>
/// The spans are valid only for the duration of <see cref="Execute"/>. Returning from all chunk
/// calls validates the complete forest image and commits it; throwing or producing an invalid
/// forest rolls the owner scope back.
/// </remarks>
public interface IParentWriteChunkJob<TDomain>
    where TDomain : IHierarchyDomain
{
    void Execute(
        ReadOnlySpan<Entity> entities,
        Span<Parent<TDomain>> parents);
}

/// <summary>
/// A serial chunk job that reads canonical <see cref="Parent{TDomain}"/> components.
/// </summary>
/// <remarks>The spans are valid only for the duration of <see cref="Execute"/>.</remarks>
public interface IParentReadChunkJob<TDomain>
    where TDomain : IHierarchyDomain
{
    void Execute(
        ReadOnlySpan<Entity> entities,
        ReadOnlySpan<Parent<TDomain>> parents);
}

/// <summary>
/// A serial chunk job that writes canonical directed relation endpoint components.
/// </summary>
/// <remarks>
/// The spans are valid only for the duration of <see cref="Execute"/>. Cardinality and endpoint
/// invariants are validated against the complete final image after all chunk calls.
/// </remarks>
public interface IDirectedRelationEndpointsWriteChunkJob<T>
    where T : struct, IComponent
{
    void Execute(
        ReadOnlySpan<Entity> entities,
        Span<DirectedRelationEndpoints<T>> endpoints);
}

/// <summary>
/// A serial chunk job that reads canonical directed relation endpoint components.
/// </summary>
/// <remarks>The spans are valid only for the duration of <see cref="Execute"/>.</remarks>
public interface IDirectedRelationEndpointsReadChunkJob<T>
    where T : struct, IComponent
{
    void Execute(
        ReadOnlySpan<Entity> entities,
        ReadOnlySpan<DirectedRelationEndpoints<T>> endpoints);
}

/// <summary>
/// A serial chunk job that writes canonical undirected relation endpoint components.
/// </summary>
/// <remarks>
/// The spans are valid only for the duration of <see cref="Execute"/>. Cardinality and endpoint
/// invariants are validated against the complete final image after all chunk calls.
/// </remarks>
public interface IUndirectedRelationEndpointsWriteChunkJob<T>
    where T : struct, IComponent
{
    void Execute(
        ReadOnlySpan<Entity> entities,
        Span<UndirectedRelationEndpoints<T>> endpoints);
}

/// <summary>
/// A serial chunk job that reads canonical undirected relation endpoint components.
/// </summary>
/// <remarks>The spans are valid only for the duration of <see cref="Execute"/>.</remarks>
public interface IUndirectedRelationEndpointsReadChunkJob<T>
    where T : struct, IComponent
{
    void Execute(
        ReadOnlySpan<Entity> entities,
        ReadOnlySpan<UndirectedRelationEndpoints<T>> endpoints);
}

/// <summary>
/// Validates the persistent query shape before a specialized whole-chunk adapter is scheduled.
/// Doing this at the API boundary keeps malformed-query diagnostics deterministic: query
/// admission must never fail first merely because an optional or filtered term requests a
/// storage capability which the specialized adapter intentionally does not own.
/// </summary>
internal static class RelationshipChunkQueryGuards
{
    private const QueryTermFilter RowFilters =
        QueryTermFilter.Added |
        QueryTermFilter.Changed |
        QueryTermFilter.Enabled |
        QueryTermFilter.Disabled;

    internal static void RequireWholeChunkRead<TComponent>(World world, QueryHandle query)
        where TComponent : struct =>
        RequireWholeChunkRead<TComponent>(
            world.PublishedStructureRoot.Queries.Get(query).Definition);

    internal static void RequireWholeChunkWrite<TComponent>(World world, QueryHandle query)
        where TComponent : struct =>
        RequireWholeChunkWrite<TComponent>(
            world.PublishedStructureRoot.Queries.Get(query).Definition);

    internal static void RequireWholeChunkRead<TComponent>(QueryDefinition definition)
        where TComponent : struct =>
        RequireWholeChunkAccess<TComponent>(definition, requireWrite: false);

    internal static void RequireWholeChunkWrite<TComponent>(QueryDefinition definition)
        where TComponent : struct =>
        RequireWholeChunkAccess<TComponent>(definition, requireWrite: true);

    private static void RequireWholeChunkAccess<TComponent>(
        QueryDefinition definition,
        bool requireWrite)
        where TComponent : struct
    {
        ArgumentNullException.ThrowIfNull(definition);
        int componentId = ComponentMetadata<TComponent>.Id;
        bool guaranteedByAll = false;
        bool canRead = false;
        bool canWrite = false;

        ReadOnlySpan<QueryTerm> terms = definition.Terms;
        for (int i = 0; i < terms.Length; i++)
        {
            QueryTerm term = terms[i];
            if ((term.Filters & RowFilters) != 0)
            {
                throw new InvalidOperationException(
                    "Whole-chunk relationship access cannot satisfy row filters.");
            }

            if (term.ComponentId != componentId || term.Kind != QueryTermKind.All)
                continue;

            guaranteedByAll = true;
            canRead |= term.Access is QueryAccess.Read or QueryAccess.ReadWrite;
            canWrite |= term.Access is QueryAccess.Write or QueryAccess.ReadWrite;
        }

        if (!guaranteedByAll)
        {
            throw new InvalidOperationException(
                $"{typeof(TComponent).Name} requires a non-optional All term in a whole-chunk relationship query.");
        }

        if (requireWrite)
        {
            if (!canWrite)
            {
                throw new InvalidOperationException(
                    $"{typeof(TComponent).Name} was not declared for query write access.");
            }
        }
        else if (!canRead || canWrite)
        {
            throw new InvalidOperationException(
                $"{typeof(TComponent).Name} must be declared with read-only query access.");
        }
    }
}
