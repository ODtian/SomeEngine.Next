using SomeEngine.ECS.Commands;

namespace SomeEngine.ECS;

/// <summary>
/// ECS World——实体生命周期与组件操作的公开门面。
/// 内部组合领域 owner，并把领域规则交给对应 owner。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md §1.3, §5.3, §5.4, §5.5
/// </remarks>
public partial class World
{
    private readonly Owners.Entities _entities;
    private readonly Owners.Tables _tables;
    private readonly Owners.Sparse _sparse;
    private readonly Owners.Indices _indices;
    private readonly Owners.Relations _relations;
    private readonly Owners.Hooks _hooks;
    private readonly Owners.Components _components;
    private readonly Owners.Queries _queries;
    private readonly Owners.Buffers _buffers;
    private readonly Owners.Bundles _bundles;
    private readonly Owners.Copy _copy;
    private readonly Owners.Shared _shared;
    private readonly Owners.Journal _journal;
    private readonly Owners.Clock _clock;
    private readonly Owners.Iteration _iteration;
    private readonly Owners.Commands _commands;
    private readonly Owners.Hierarchy _hierarchy;

    public World(int initialEntityCapacity = 256)
    {
        _entities = new Owners.Entities(initialEntityCapacity);
        _queries = new Owners.Queries();
        _tables = new Owners.Tables(_entities, OnArchetype);
        _sparse = new Owners.Sparse();
        _indices = new Owners.Indices();
        _relations = new Owners.Relations();
        _hooks = new Owners.Hooks();
        _hooks.Bind(this);
        _components = new Owners.Components();
        _buffers = new Owners.Buffers();
        _bundles = new Owners.Bundles();
        _copy = new Owners.Copy();
        _shared = new Owners.Shared();
        _journal = new Owners.Journal();
        _clock = new Owners.Clock();
        _iteration = new Owners.Iteration();
        _commands = new Owners.Commands();
        _hierarchy = new Owners.Hierarchy();
        _hierarchy.Bind(
            _entities,
            _tables,
            _components,
            _clock);
        _entities.Bind(
            _tables,
            _relations,
            _components,
            _journal,
            _clock,
            _iteration,
            _hierarchy);
        _sparse.Bind(
            _entities,
            _journal,
            _clock,
            _iteration);
        _shared.Bind(
            _entities,
            _tables,
            _journal,
            _clock,
            _iteration);
        _relations.Bind(
            _entities,
            _tables,
            _journal,
            _clock);
        _components.Bind(
            _entities,
            _tables,
            _indices,
            _hooks,
            _journal,
            _clock,
            _iteration,
            _hierarchy);
        _buffers.Bind(
            _entities,
            _components,
            _bundles,
            _journal,
            _clock,
            _iteration);
        _bundles.Bind(
            _entities,
            _tables,
            _components,
            _buffers,
            _shared,
            _sparse,
            _indices,
            _hooks,
            _journal,
            _clock,
            _iteration,
            _hierarchy);
        _copy.Bind(
            _entities,
            _tables,
            _components,
            _buffers,
            _sparse,
            _relations,
            _indices,
            _journal,
            _clock,
            _iteration,
            _hierarchy);
    }

    internal Owners.Entities Entities => _entities;

    internal Owners.Tables Tables => _tables;

    internal Owners.Sparse Sparse => _sparse;

    internal Owners.Components Components => _components;

    internal Owners.Buffers Buffers => _buffers;

    internal Owners.Indices Indices => _indices;

    internal Owners.Relations Relations => _relations;

    internal Owners.Bundles Bundles => _bundles;

    internal Owners.Copy Copy => _copy;

    internal Owners.Shared Shared => _shared;

    internal Owners.Hierarchy Hierarchy => _hierarchy;

    internal Owners.Journal Journal => _journal;

    internal Owners.Clock Clock => _clock;

    internal Owners.Hooks HookStore => _hooks;

    public CommandBuffer Commands()
    {
        return _commands.Get(this);
    }

    public void Flush()
    {
        _commands.Flush();
    }

    public IDisposable SuppressSerializationJournal()
    {
        return _journal.Suppress();
    }
}

