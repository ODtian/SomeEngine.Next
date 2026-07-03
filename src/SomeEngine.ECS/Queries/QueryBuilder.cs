using SomeEngine.ECS.Components;

namespace SomeEngine.ECS.Queries;

/// <summary>
/// Compatibility fluent builder over Query Model v2.
/// </summary>
public sealed class QueryBuilder
{
    internal readonly World _world;
    private readonly QueryDefinitionBuilder _spec = new();

    internal QueryBuilder(World world)
    {
        _world = world;
    }

    public QueryBuilder With<T>() where T : struct
    {
        _spec.All<T>();
        return this;
    }

    public QueryBuilder Without<T>() where T : struct
    {
        _spec.None<T>();
        return this;
    }

    public QueryBuilder WithEnabled<T>() where T : struct, IEnableableComponent
    {
        _spec.Enabled<T>();
        return this;
    }

    public QueryBuilder WithDisabled<T>() where T : struct, IEnableableComponent
    {
        _spec.Disabled<T>();
        return this;
    }

    public QueryBuilder WithAny<T>() where T : struct
    {
        _spec.Any<T>();
        return this;
    }

    public QueryBuilder Optional<T>(QueryAccess access = QueryAccess.None) where T : struct
    {
        _spec.Optional<T>(access);
        return this;
    }

    public QueryBuilder Added<T>() where T : struct
    {
        _spec.Added<T>();
        return this;
    }

    public QueryBuilder Changed<T>() where T : struct
    {
        _spec.Changed<T>();
        return this;
    }

    public QueryBuilder ChunkChanged<T>() where T : struct
    {
        _spec.ChunkChanged<T>();
        return this;
    }

    public QueryBuilder Removed<T>() where T : struct, IComponent
    {
        _spec.Removed<T>();
        return this;
    }

    public QueryBuilder Read<T>() where T : struct
    {
        _spec.Read<T>();
        return this;
    }

    public QueryBuilder Write<T>() where T : struct
    {
        _spec.Write<T>();
        return this;
    }

    public QueryBuilder ReadWrite<T>() where T : struct
    {
        _spec.ReadWrite<T>();
        return this;
    }

    public QueryBuilder WithBuffer<T>() where T : struct, IBufferElement
    {
        _spec.Buffer<T>();
        return this;
    }

    public QueryBuilder ReadBuffer<T>() where T : struct, IBufferElement
    {
        _spec.ReadBuffer<T>();
        return this;
    }

    public QueryBuilder WriteBuffer<T>() where T : struct, IBufferElement
    {
        _spec.WriteBuffer<T>();
        return this;
    }

    public QueryBuilder ChangedBuffer<T>() where T : struct, IBufferElement
    {
        _spec.ChangedBuffer<T>();
        return this;
    }

    public QueryBuilder Shared<T>() where T : struct, ISharedComponent
    {
        _spec.Shared<T>();
        return this;
    }

    public QueryView Build()
    {
        QueryDefinition spec = _spec.Build();
        return _world.RegisterQuery(spec);
    }
}

