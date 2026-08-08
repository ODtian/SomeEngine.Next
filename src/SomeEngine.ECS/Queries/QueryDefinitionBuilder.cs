using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Queries;

public sealed class QueryDefinitionBuilder
{
    private readonly List<QueryTerm> _terms = new();

    public QueryDefinitionBuilder All<T>() where T : struct =>
        Add<T>(QueryTermKind.All, QueryAccess.None, QueryTermFilter.None);

    public QueryDefinitionBuilder None<T>() where T : struct =>
        Add<T>(QueryTermKind.None, QueryAccess.None, QueryTermFilter.None);

    public QueryDefinitionBuilder Any<T>() where T : struct =>
        Add<T>(QueryTermKind.Any, QueryAccess.None, QueryTermFilter.None);

    public QueryDefinitionBuilder Optional<T>(QueryAccess access = QueryAccess.None) where T : struct =>
        Add<T>(QueryTermKind.Optional, access, QueryTermFilter.None);

    public QueryDefinitionBuilder Read<T>() where T : struct =>
        Add<T>(QueryTermKind.All, QueryAccess.Read, QueryTermFilter.None);

    public QueryDefinitionBuilder Write<T>() where T : struct =>
        Add<T>(QueryTermKind.All, QueryAccess.Write, QueryTermFilter.None);

    public QueryDefinitionBuilder ReadWrite<T>() where T : struct =>
        Add<T>(QueryTermKind.All, QueryAccess.ReadWrite, QueryTermFilter.None);

    public QueryDefinitionBuilder Added<T>() where T : struct =>
        Add<T>(QueryTermKind.All, QueryAccess.None, QueryTermFilter.Added);

    public QueryDefinitionBuilder Changed<T>() where T : struct =>
        Add<T>(QueryTermKind.All, QueryAccess.None, QueryTermFilter.Changed);

    public QueryDefinitionBuilder ChunkChanged<T>() where T : struct =>
        Add<T>(QueryTermKind.All, QueryAccess.None, QueryTermFilter.ChunkChanged);

    public QueryDefinitionBuilder Removed<T>() where T : struct, IComponent =>
        Add<Removed<T>>(QueryTermKind.All, QueryAccess.Read, QueryTermFilter.None);

    public QueryDefinitionBuilder Enabled<T>() where T : struct, IEnableableComponent =>
        Add<T>(QueryTermKind.All, QueryAccess.None, QueryTermFilter.Enabled);

    public QueryDefinitionBuilder Disabled<T>() where T : struct, IEnableableComponent =>
        Add<T>(QueryTermKind.All, QueryAccess.None, QueryTermFilter.Disabled);

    public QueryDefinitionBuilder Shared<T>() where T : struct, ISharedComponent =>
        All<T>();

    public QueryDefinitionBuilder Buffer<T>(QueryAccess access = QueryAccess.None)
        where T : struct, IBufferElement
    {
        // Registration establishes the single logical buffer resource identity used to collapse
        // the internal header and inline table columns when QueryDefinition compiles admission.
        _ = BufferComponents.Header<T>();
        AddBacking<DynamicBufferHeader<T>>(QueryTermKind.All, access, QueryTermFilter.None);
        AddBacking<DynamicBufferInline<T>>(QueryTermKind.All, access, QueryTermFilter.None);
        return this;
    }

    public QueryDefinitionBuilder ReadBuffer<T>() where T : struct, IBufferElement =>
        Buffer<T>(QueryAccess.Read);

    /// <summary>
    /// Declares optional access to a dynamic buffer. Archetypes without the buffer still match;
    /// callers can probe each matched chunk through <see cref="QueryChunkView.HasBuffer{T}"/> and
    /// then use the access mode declared here.
    /// </summary>
    public QueryDefinitionBuilder OptionalBuffer<T>(QueryAccess access = QueryAccess.None)
        where T : struct, IBufferElement
    {
        _ = BufferComponents.Header<T>();
        AddBacking<DynamicBufferHeader<T>>(QueryTermKind.Optional, access, QueryTermFilter.None);
        AddBacking<DynamicBufferInline<T>>(QueryTermKind.Optional, access, QueryTermFilter.None);
        return this;
    }

    public QueryDefinitionBuilder WriteBuffer<T>() where T : struct, IBufferElement =>
        Buffer<T>(QueryAccess.ReadWrite);

    public QueryDefinitionBuilder ChangedBuffer<T>() where T : struct, IBufferElement
    {
        Buffer<T>();
        AddBacking<DynamicBufferHeader<T>>(
            QueryTermKind.All,
            QueryAccess.None,
            QueryTermFilter.Changed);
        return this;
    }

    public QueryDefinition Build()
    {
        if (_terms.Count == 0)
            return QueryDefinition.Empty;
        return QueryDefinition.CreateNormalized(CollectionsMarshal.AsSpan(_terms));
    }

    private QueryDefinitionBuilder Add<T>(
        QueryTermKind kind,
        QueryAccess access,
        QueryTermFilter filters)
        where T : struct
    {
        var info = QueryableTypeInfo.For<T>();
        info.Validate(kind, access, filters);
        _terms.Add(new QueryTerm(info.ComponentId, kind, access, filters));
        return this;
    }

    private void AddBacking<T>(
        QueryTermKind kind,
        QueryAccess access,
        QueryTermFilter filters)
        where T : struct
    {
        int componentId = ComponentMetadata<T>.Id;
        var info = QueryableTypeInfo.ForComponentId(componentId);
        info.Validate(kind, access, filters);
        _terms.Add(new QueryTerm(componentId, kind, access, filters));
    }

}

