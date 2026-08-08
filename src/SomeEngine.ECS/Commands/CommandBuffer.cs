using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Relations;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Commands;

/// <summary>
/// 命令类型枚举。
/// </summary>
internal enum CommandType : byte
{
    CreateEntity,
    DestroyEntity,
    AddComponent,
    RemoveComponent,
    ReplaceComponent,
    AddTag,
    RemoveTag,
    AddBuffer,
    ReplaceBuffer,
    RemoveBuffer,
    TypedRelationship,
}

/// <summary>
/// 命令头。固定大小，存储命令元信息。
/// </summary>
internal struct CommandHeader
{
    public CommandType Type;
    public CommandEntity Entity;
    public int ComponentId;
    public int DataIndex; // 指向 CommandDataList 的 index，-1 = 无数据
}

/// <summary>
/// Command-local identity for an entity that will be allocated during playback.
/// No live <see cref="Entity"/> exists until a successful playback reaches its Create command.
/// </summary>
public readonly struct DeferredEntity : IEquatable<DeferredEntity>
{
    private readonly DeferredEntityCell? _cell;

    internal DeferredEntity(DeferredEntityCell cell)
    {
        _cell = cell;
    }

    public bool IsResolved => _cell?.IsResolved == true;

    public Entity Resolve()
    {
        if (_cell is null)
            throw new InvalidOperationException("The deferred entity handle is default/uninitialized.");
        return _cell.Resolve();
    }

    public bool TryResolve(out Entity entity)
    {
        if (_cell is not null && _cell.TryResolve(out entity))
            return true;

        entity = Entity.Null;
        return false;
    }

    public bool Equals(DeferredEntity other) => ReferenceEquals(_cell, other._cell);

    public override bool Equals(object? obj) => obj is DeferredEntity other && Equals(other);

    public override int GetHashCode() =>
        _cell is null ? 0 : RuntimeHelpers.GetHashCode(_cell);

    public static bool operator ==(DeferredEntity left, DeferredEntity right) => left.Equals(right);

    public static bool operator !=(DeferredEntity left, DeferredEntity right) => !left.Equals(right);

    internal CommandEntity AsCommandEntity(CommandBuffer owner)
    {
        if (_cell is null)
            throw new InvalidOperationException("The deferred entity handle is default/uninitialized.");
        _cell.RequireOwner(owner);
        return new CommandEntity(_cell);
    }
}

internal sealed class DeferredEntityCell
{
    private readonly CommandBuffer _owner;
    private World? _world;
    private Entity _entity;
    private long _publicationEpoch;
    private bool _prepared;
    private bool _invalidated;

    internal DeferredEntityCell(CommandBuffer owner)
    {
        _owner = owner;
    }

    internal bool IsResolved
    {
        get
        {
            lock (_owner.CommandGate)
                return IsResolvedUnderGate;
        }
    }

    private bool IsResolvedUnderGate =>
        _prepared &&
        !_invalidated &&
        _world!.IsStructureEpochPublished(_publicationEpoch);

    internal void Prepare(World world, Entity entity, long publicationEpoch)
    {
        lock (_owner.CommandGate)
        {
            ArgumentNullException.ThrowIfNull(world);
            if (_invalidated)
                throw new InvalidOperationException("Deferred entity was invalidated before playback.");
            if (_prepared)
                throw new InvalidOperationException("Entity Create command has already been played back.");
            if (publicationEpoch <= 0)
                throw new ArgumentOutOfRangeException(nameof(publicationEpoch));

            _world = world;
            _entity = entity;
            _publicationEpoch = publicationEpoch;
            _prepared = true;
        }
    }

    internal Entity Resolve()
    {
        lock (_owner.CommandGate)
        {
            if (_invalidated)
                throw new InvalidOperationException("Deferred entity command was cleared, disposed, or failed.");
            if (!IsResolvedUnderGate)
            {
                throw new InvalidOperationException(
                    _prepared
                        ? "Deferred entity belongs to a structural transaction that has not been published."
                        : "Deferred entity has not been created by playback yet.");
            }
            return _entity;
        }
    }

    internal bool TryResolve(out Entity entity)
    {
        lock (_owner.CommandGate)
        {
            if (IsResolvedUnderGate)
            {
                entity = _entity;
                return true;
            }

            entity = Entity.Null;
            return false;
        }
    }

    internal void InvalidatePending()
    {
        lock (_owner.CommandGate)
        {
            if (!IsResolvedUnderGate)
                _invalidated = true;
        }
    }

    internal void RequireOwner(CommandBuffer owner)
    {
        if (!ReferenceEquals(_owner, owner))
        {
            throw new InvalidOperationException(
                "A deferred entity may only be referenced by the CommandBuffer that records its Create. " +
                "After successful playback, Resolve it to a live Entity before using another buffer.");
        }

        lock (_owner.CommandGate)
        {
            if (_invalidated)
                throw new InvalidOperationException("Deferred entity command was cleared, disposed, or failed.");
            if (_prepared)
            {
                throw new InvalidOperationException(
                    "A played-back deferred entity cannot be recorded again as command-local identity. " +
                    "Resolve it to a live Entity first.");
            }
        }
    }
}

internal readonly struct CommandEntity
{
    private readonly Entity _live;
    private readonly DeferredEntityCell? _deferred;

    internal CommandEntity(Entity live)
    {
        _live = live;
        _deferred = null;
    }

    internal CommandEntity(DeferredEntityCell deferred)
    {
        _live = Entity.Null;
        _deferred = deferred;
    }

    internal Entity Resolve(CommandPlaybackContext context) =>
        _deferred is null ? _live : context.Resolve(_deferred);

    internal void Complete(CommandPlaybackContext context, Entity entity)
    {
        if (_deferred is null)
            throw new InvalidOperationException("A CreateEntity command requires a deferred entity identity.");
        context.Complete(_deferred, entity);
    }
}

internal sealed class CommandPlaybackContext
{
    private readonly World _world;
    private readonly long _publicationEpoch;
    private readonly Dictionary<object, Entity> _edges = new();
    private readonly Dictionary<DeferredEntityCell, Entity> _entities = new();

    internal CommandPlaybackContext(World world, long publicationEpoch)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        if (publicationEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(publicationEpoch));
        _publicationEpoch = publicationEpoch;
    }

    internal void Complete(DeferredEntityCell cell, Entity entity)
    {
        if (!_entities.TryAdd(cell, entity))
            throw new InvalidOperationException("Entity Create command has already been played back.");
        cell.Prepare(_world, entity, _publicationEpoch);
    }

    internal Entity Resolve(DeferredEntityCell cell)
    {
        if (!_entities.TryGetValue(cell, out Entity entity))
            throw new InvalidOperationException("Deferred entity has not been created in the command image.");
        return entity;
    }

    internal bool IsResolved<T>(DeferredRelationEdgeCell<T> cell)
        where T : struct, IComponent =>
        _edges.ContainsKey(cell);

    internal void Complete<T>(DeferredRelationEdgeCell<T> cell, RelationEdge<T> edge)
        where T : struct, IComponent
    {
        if (!_edges.TryAdd(cell, edge.Entity))
            throw new InvalidOperationException("Relation Create command has already been played back.");
        cell.Prepare(_world, edge, _publicationEpoch);
    }

    internal RelationEdge<T> Resolve<T>(DeferredRelationEdgeCell<T> cell)
        where T : struct, IComponent
    {
        if (!_edges.TryGetValue(cell, out Entity edge))
            throw new InvalidOperationException("Deferred relation edge has not been created in the command image.");
        return new RelationEdge<T>(edge);
    }
}

/// <summary>
/// CommandBuffer：在迭代期间收集延迟的结构变更命令，迭代结束后统一回放。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md 决策 #30, #34
/// - 命令头 + typed 数据分桶，零 boxing
/// - FIFO 顺序回放
/// - CreateEntity 仅分配命令局部句柄，回放时才分配真实 ID
/// </remarks>
public sealed partial class CommandBuffer : IDisposable
{
    private readonly World _world;
    private readonly bool _worldOwned;
    private readonly bool _jobProducerOwned;
    private readonly object _commandGate;
    private readonly List<CommandHeader> _commands = new();
    private ICommandPayloadList?[] _dataLists = new ICommandPayloadList?[8];
    private readonly List<ITypedRelationshipCommand> _typedRelationshipCommands = new();
    private readonly List<DeferredEntityCell> _deferredEntities = new();
    private bool _playbackAttempted;
    private bool _playbackReserved;
    private bool _sealedForWorldPlayback;
    private bool _jobProducerCompleted;
    private bool _disposed;

    public CommandBuffer(World world)
        : this(world, worldOwned: false, jobProducerOwned: false, commandGate: null)
    {
    }

    internal CommandBuffer(World world, bool worldOwned)
        : this(world, worldOwned, jobProducerOwned: false, commandGate: null)
    {
    }

    internal CommandBuffer(
        World world,
        bool worldOwned,
        bool jobProducerOwned,
        object? commandGate)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _worldOwned = worldOwned;
        _jobProducerOwned = jobProducerOwned;
        _commandGate = commandGate ?? world.CommandGate;
        if (worldOwned && jobProducerOwned)
        {
            throw new ArgumentException(
                "A CommandBuffer cannot be both World-owned and Job-producer-owned.");
        }
    }

    /// <summary>已录入的命令数。</summary>
    public int CommandCount
    {
        get
        {
            _world.ThrowIfJobCommandBufferAccess();
            lock (_commandGate)
            {
                ThrowIfDisposedCore();
                if (_playbackReserved)
                {
                    throw new InvalidOperationException(
                        "Cannot inspect CommandCount while CommandBuffer playback is reserved or running.");
                }
                return _commands.Count;
            }
        }
    }

    internal object CommandGate => _commandGate;

    internal bool IsJobProducerOwned => _jobProducerOwned;

    internal World OwnerWorld => _world;

    internal bool HasRecordedCommandsUnderGate => _commands.Count != 0;

    internal int JobProducerCommandCount
    {
        get
        {
            if (!_jobProducerOwned)
                throw new InvalidOperationException("Only a Job producer segment has a producer count.");
            lock (_commandGate)
                return _commands.Count;
        }
    }

    internal void SealJobProducerSegment()
    {
        if (!_jobProducerOwned)
            throw new InvalidOperationException("Only a Job producer segment can be sealed.");
        lock (_commandGate)
        {
            ThrowIfDisposedCore();
            _jobProducerCompleted = true;
        }
    }

    // ════════════════════════════════════════════════
    // 延迟命令录入
    // ════════════════════════════════════════════════

    /// <summary>
    /// Records an entity creation without touching the World allocator. Resolve the returned handle
    /// only after successful playback.
    /// </summary>
    public DeferredEntity CreateEntity()
    {
        using RecordAccessScope access = EnterRecordAccess();
        return CreateEntityUnderGate();
    }

    internal DeferredEntity CreateEntity(HookCommandToken token)
    {
        using RecordAccessScope access = EnterRecordAccess(token);
        return CreateEntityUnderGate();
    }

    private DeferredEntity CreateEntityUnderGate()
    {
        var cell = new DeferredEntityCell(this);
        _deferredEntities.Add(cell);
        _commands.Add(new CommandHeader
        {
            Type = CommandType.CreateEntity,
            Entity = new CommandEntity(cell),
            ComponentId = 0,
            DataIndex = -1,
        });
        return new DeferredEntity(cell);
    }

    /// <summary>延迟销毁 entity。</summary>
    public void DestroyEntity(Entity entity)
    {
        using RecordAccessScope access = EnterRecordAccess();
        DestroyEntityUnderGate(new CommandEntity(entity));
    }

    public void DestroyEntity(DeferredEntity entity)
    {
        using RecordAccessScope access = EnterRecordAccess();
        DestroyEntityUnderGate(entity.AsCommandEntity(this));
    }

    internal void DestroyEntity(HookCommandToken token, Entity entity)
    {
        using RecordAccessScope access = EnterRecordAccess(token);
        DestroyEntityUnderGate(new CommandEntity(entity));
    }

    internal void DestroyEntity(HookCommandToken token, DeferredEntity entity)
    {
        using RecordAccessScope access = EnterRecordAccess(token);
        DestroyEntityUnderGate(entity.AsCommandEntity(this));
    }

    private void DestroyEntityUnderGate(CommandEntity entity)
    {
        _commands.Add(new CommandHeader
        {
            Type = CommandType.DestroyEntity,
            Entity = entity,
            ComponentId = 0,
            DataIndex = -1,
        });
    }

    /// <summary>延迟添加 Table 组件。</summary>
    public void Add<T>(Entity entity, in T value) where T : struct, IComponent
    {
        using RecordAccessScope access = EnterRecordAccess();
        AddUnderGate(new CommandEntity(entity), in value);
    }

    public void Add<T>(DeferredEntity entity, in T value) where T : struct, IComponent
    {
        using RecordAccessScope access = EnterRecordAccess();
        AddUnderGate(entity.AsCommandEntity(this), in value);
    }

    internal void Add<T>(HookCommandToken token, Entity entity, in T value)
        where T : struct, IComponent
    {
        using RecordAccessScope access = EnterRecordAccess(token);
        AddUnderGate(new CommandEntity(entity), in value);
    }

    internal void Add<T>(HookCommandToken token, DeferredEntity entity, in T value)
        where T : struct, IComponent
    {
        using RecordAccessScope access = EnterRecordAccess(token);
        AddUnderGate(entity.AsCommandEntity(this), in value);
    }

    private void AddUnderGate<T>(CommandEntity entity, in T value) where T : struct, IComponent
    {
        PublicComponentMutationGuard.Structural<T>("CommandBuffer.Add");
        int componentId = ComponentMetadata<T>.Id;
        var list = DataList<T>(componentId);
        int dataIndex = list.Append(in value);
        _commands.Add(new CommandHeader
        {
            Type = CommandType.AddComponent,
            Entity = entity,
            ComponentId = componentId,
            DataIndex = dataIndex,
        });
    }

    /// <summary>延迟整值替换 Table 组件（组件必须已存在）。</summary>
    public void Replace<T>(Entity entity, in T value) where T : struct, IComponent
    {
        using RecordAccessScope access = EnterRecordAccess();
        ReplaceUnderGate(new CommandEntity(entity), in value);
    }

    public void Replace<T>(DeferredEntity entity, in T value) where T : struct, IComponent
    {
        using RecordAccessScope access = EnterRecordAccess();
        ReplaceUnderGate(entity.AsCommandEntity(this), in value);
    }

    internal void Replace<T>(HookCommandToken token, Entity entity, in T value)
        where T : struct, IComponent
    {
        using RecordAccessScope access = EnterRecordAccess(token);
        ReplaceUnderGate(new CommandEntity(entity), in value);
    }

    internal void Replace<T>(HookCommandToken token, DeferredEntity entity, in T value)
        where T : struct, IComponent
    {
        using RecordAccessScope access = EnterRecordAccess(token);
        ReplaceUnderGate(entity.AsCommandEntity(this), in value);
    }

    private void ReplaceUnderGate<T>(CommandEntity entity, in T value) where T : struct, IComponent
    {
        PublicComponentMutationGuard.Value<T>("CommandBuffer.Replace");
        int componentId = ComponentMetadata<T>.Id;
        var list = DataList<T>(componentId);
        int dataIndex = list.Append(in value);
        _commands.Add(new CommandHeader
        {
            Type = CommandType.ReplaceComponent,
            Entity = entity,
            ComponentId = componentId,
            DataIndex = dataIndex,
        });
    }

    /// <summary>延迟移除 Table 组件。</summary>
    public void Remove<T>(Entity entity) where T : struct, IComponent
    {
        using RecordAccessScope access = EnterRecordAccess();
        RemoveUnderGate<T>(new CommandEntity(entity));
    }

    public void Remove<T>(DeferredEntity entity) where T : struct, IComponent
    {
        using RecordAccessScope access = EnterRecordAccess();
        RemoveUnderGate<T>(entity.AsCommandEntity(this));
    }

    internal void Remove<T>(HookCommandToken token, Entity entity)
        where T : struct, IComponent
    {
        using RecordAccessScope access = EnterRecordAccess(token);
        RemoveUnderGate<T>(new CommandEntity(entity));
    }

    internal void Remove<T>(HookCommandToken token, DeferredEntity entity)
        where T : struct, IComponent
    {
        using RecordAccessScope access = EnterRecordAccess(token);
        RemoveUnderGate<T>(entity.AsCommandEntity(this));
    }

    private void RemoveUnderGate<T>(CommandEntity entity) where T : struct, IComponent
    {
        PublicComponentMutationGuard.Structural<T>("CommandBuffer.Remove");
        int componentId = ComponentMetadata<T>.Id;
        DataList<T>(componentId);
        _commands.Add(new CommandHeader
        {
            Type = CommandType.RemoveComponent,
            Entity = entity,
            ComponentId = componentId,
            DataIndex = -1,
        });
    }

    /// <summary>延迟添加 Tag。</summary>
    public void AddTag<T>(Entity entity) where T : struct, ITag
    {
        using RecordAccessScope access = EnterRecordAccess();
        AddTagUnderGate<T>(new CommandEntity(entity));
    }

    public void AddTag<T>(DeferredEntity entity) where T : struct, ITag
    {
        using RecordAccessScope access = EnterRecordAccess();
        AddTagUnderGate<T>(entity.AsCommandEntity(this));
    }

    internal void AddTag<T>(HookCommandToken token, Entity entity)
        where T : struct, ITag
    {
        using RecordAccessScope access = EnterRecordAccess(token);
        AddTagUnderGate<T>(new CommandEntity(entity));
    }

    internal void AddTag<T>(HookCommandToken token, DeferredEntity entity)
        where T : struct, ITag
    {
        using RecordAccessScope access = EnterRecordAccess(token);
        AddTagUnderGate<T>(entity.AsCommandEntity(this));
    }

    private void AddTagUnderGate<T>(CommandEntity entity) where T : struct, ITag
    {
        _commands.Add(new CommandHeader
        {
            Type = CommandType.AddTag,
            Entity = entity,
            ComponentId = ComponentMetadata<T>.Id,
            DataIndex = -1,
        });
    }

    /// <summary>延迟移除 Tag。</summary>
    public void RemoveTag<T>(Entity entity) where T : struct, ITag
    {
        using RecordAccessScope access = EnterRecordAccess();
        RemoveTagUnderGate<T>(new CommandEntity(entity));
    }

    public void RemoveTag<T>(DeferredEntity entity) where T : struct, ITag
    {
        using RecordAccessScope access = EnterRecordAccess();
        RemoveTagUnderGate<T>(entity.AsCommandEntity(this));
    }

    internal void RemoveTag<T>(HookCommandToken token, Entity entity)
        where T : struct, ITag
    {
        using RecordAccessScope access = EnterRecordAccess(token);
        RemoveTagUnderGate<T>(new CommandEntity(entity));
    }

    internal void RemoveTag<T>(HookCommandToken token, DeferredEntity entity)
        where T : struct, ITag
    {
        using RecordAccessScope access = EnterRecordAccess(token);
        RemoveTagUnderGate<T>(entity.AsCommandEntity(this));
    }

    private void RemoveTagUnderGate<T>(CommandEntity entity) where T : struct, ITag
    {
        _commands.Add(new CommandHeader
        {
            Type = CommandType.RemoveTag,
            Entity = entity,
            ComponentId = ComponentMetadata<T>.Id,
            DataIndex = -1,
        });
    }

    // ════════════════════════════════════════════════
    // 回放
    // ════════════════════════════════════════════════

    /// <summary>按 FIFO 顺序回放所有命令到 World。</summary>
    public void Playback()
    {
        if (!ReservePublicPlayback())
            return;

        try
        {
            using WorldJobAdmissionScope admission = _world.EnterJobTopologyWrite();
            lock (_commandGate)
                PlaybackReservedUnderExistingTopologyAdmissionUnderGate();
        }
        catch
        {
            lock (_commandGate)
                CancelOwnedPlaybackReservationUnderGate();
            throw;
        }
    }

    private bool ReservePublicPlayback()
    {
        _world.ThrowIfJobCommandBufferAccess();
        lock (_commandGate)
        {
            ThrowIfWorldOwnedLifecycleAccessUnderGate();
            ThrowIfDisposedCore();
            ThrowIfPlaybackUnavailableUnderGate();
            if (_world.IsIterating)
                throw new InvalidOperationException("Cannot playback CommandBuffer during iteration.");

            if (_commands.Count == 0)
            {
                _playbackAttempted = true;
                return false;
            }

            _playbackReserved = true;
            return true;
        }
    }

    internal void ReserveOwnedPlaybackUnderGate()
    {
        ThrowIfDisposedCore();
        ThrowIfPlaybackUnavailableUnderGate();
        if (_world.IsIterating)
            throw new InvalidOperationException("Cannot playback CommandBuffer during iteration.");
        _playbackReserved = true;
    }

    internal void CancelOwnedPlaybackReservationUnderGate()
    {
        if (_playbackReserved && !_playbackAttempted)
            _playbackReserved = false;
    }

    internal void PlaybackReservedUnderExistingTopologyAdmissionUnderGate()
    {
        if (!_playbackReserved)
            throw new InvalidOperationException("CommandBuffer playback was not reserved.");

        try
        {
            ThrowIfDisposedCore();
            if (_world.IsIterating)
                throw new InvalidOperationException("Cannot playback CommandBuffer during iteration.");
            if (_playbackAttempted)
            {
                throw new InvalidOperationException(
                    "CommandBuffer is single-playback. Call Clear before recording/replaying another command set.");
            }

            _playbackAttempted = true;
            if (_commands.Count != 0)
                PlaybackUnderAdmission();
        }
        finally
        {
            _playbackReserved = false;
        }
    }

    private void ThrowIfPlaybackUnavailableUnderGate()
    {
        if (_playbackReserved)
            throw new InvalidOperationException("CommandBuffer playback is already reserved.");
        if (_playbackAttempted)
        {
            throw new InvalidOperationException(
                "CommandBuffer is single-playback. Call Clear before recording/replaying another command set.");
        }
    }

    private void PlaybackUnderAdmission()
    {
        bool published = false;
        try
        {
            using StructuralMutationScope mutation = _world.BeginStructuralMutation();
            var context = new CommandPlaybackContext(_world, mutation.PublicationEpoch);
            PlaybackInto(_world, context);
            mutation.Commit();
            published = true;
        }
        catch
        {
            if (!published)
            {
                FailTypedRelationshipCommands();
                InvalidateDeferredEntities();
            }
            throw;
        }
    }

    private void PlaybackInto(World world, CommandPlaybackContext context)
    {
        bool completed = false;
        world.RelationGraph.BeginCommandBatch();
        try
        {
            PlaybackCommandsInto(world, context);
            completed = true;
        }
        finally
        {
            world.RelationGraph.EndCommandBatch(world, completed);
        }
    }

    private void PlaybackCommandsInto(World world, CommandPlaybackContext context)
    {
        foreach (var command in _commands)
        {
            switch (command.Type)
            {
                case CommandType.CreateEntity:
                    command.Entity.Complete(context, world.CreateEntity());
                    break;

                case CommandType.DestroyEntity:
                    world.DestroyEntity(command.Entity.Resolve(context));
                    break;

                case CommandType.AddComponent:
                    var addList = DataList(command.ComponentId);
                    addList.PlaybackAdd(world, command.Entity.Resolve(context), command.DataIndex);
                    break;

                case CommandType.ReplaceComponent:
                    var replaceList = DataList(command.ComponentId);
                    replaceList.PlaybackReplace(world, command.Entity.Resolve(context), command.DataIndex);
                    break;

                case CommandType.RemoveComponent:
                    var removeList = DataList(command.ComponentId);
                    removeList.PlaybackRemove(world, command.Entity.Resolve(context));
                    break;

                case CommandType.AddTag:
                    world.AddTagId(command.Entity.Resolve(context), command.ComponentId);
                    break;

                case CommandType.RemoveTag:
                    world.RemoveTagId(command.Entity.Resolve(context), command.ComponentId);
                    break;

                case CommandType.AddBuffer:
                    var addBuffer = BufferDataList(command.ComponentId);
                    addBuffer.PlaybackAdd(world, command.Entity.Resolve(context), command.DataIndex);
                    break;

                case CommandType.ReplaceBuffer:
                    var replaceBuffer = BufferDataList(command.ComponentId);
                    replaceBuffer.PlaybackReplace(world, command.Entity.Resolve(context), command.DataIndex);
                    break;

                case CommandType.RemoveBuffer:
                    var removeBuffer = BufferDataList(command.ComponentId);
                    removeBuffer.PlaybackRemove(world, command.Entity.Resolve(context));
                    break;

                case CommandType.TypedRelationship:
                    _typedRelationshipCommands[command.DataIndex].Playback(world, context);
                    break;
            }
        }
    }

    /// <summary>
    /// Owns the one relationship validation/publication batch shared by every stable producer
    /// segment in a JobCommandBuffer publication. Direct CommandBuffer playback continues to own
    /// its batch inside <see cref="Playback"/>.
    /// </summary>
    internal sealed class JobProducerPlaybackBatch : IDisposable
    {
        private World? _world;

        internal JobProducerPlaybackBatch(World world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            world.RelationGraph.BeginCommandBatch();
        }

        internal void RequireActiveFor(World world)
        {
            if (!ReferenceEquals(_world, world))
            {
                throw new InvalidOperationException(
                    "The Job producer segment does not belong to the active playback batch.");
            }
        }

        internal void Complete()
        {
            World world = _world ??
                throw new InvalidOperationException("The Job producer playback batch is no longer active.");
            _world = null;
            world.RelationGraph.EndCommandBatch(world, completed: true);
        }

        public void Dispose()
        {
            World? world = _world;
            _world = null;
            if (world is not null)
                world.RelationGraph.EndCommandBatch(world, completed: false);
        }
    }

    internal static JobProducerPlaybackBatch BeginJobProducerPlaybackBatch(World world) =>
        new(world);

    /// <summary>
    /// Replays one producer-private segment into an already-active structural candidate. The
    /// caller owns the single topology admission and publication transaction for the complete
    /// stable producer ordering.
    /// </summary>
    internal bool PlaybackJobProducerSegment(
        long publicationEpoch,
        JobProducerPlaybackBatch playbackBatch)
    {
        if (!_jobProducerOwned)
            throw new InvalidOperationException("Only a Job producer segment can use batch playback.");
        ArgumentNullException.ThrowIfNull(playbackBatch);
        playbackBatch.RequireActiveFor(_world);

        lock (_commandGate)
        {
            ThrowIfDisposedCore();
            ThrowIfPlaybackUnavailableUnderGate();
            if (_world.IsIterating)
                throw new InvalidOperationException("Cannot playback Job command segments during iteration.");

            _playbackAttempted = true;
            if (_commands.Count == 0)
                return false;

            try
            {
                var context = new CommandPlaybackContext(_world, publicationEpoch);
                PlaybackCommandsInto(_world, context);
                return true;
            }
            catch
            {
                FailTypedRelationshipCommands();
                InvalidateDeferredEntities();
                throw;
            }
        }
    }

    /// <summary>Invalidates a segment whose candidate image will not be published.</summary>
    internal void AbortJobProducerSegment(bool playbackCompleted)
    {
        if (!_jobProducerOwned)
            throw new InvalidOperationException("Only a Job producer segment can be aborted as a batch segment.");

        lock (_commandGate)
        {
            if (_disposed)
                return;
            if (playbackCompleted)
                FailTypedRelationshipCommands();
            InvalidateDeferredEntities();
            DisposeCore();
        }
    }

    /// <summary>Releases producer storage after its candidate was atomically published.</summary>
    internal void CompleteJobProducerSegment()
    {
        if (!_jobProducerOwned)
            throw new InvalidOperationException("Only a Job producer segment can complete batch playback.");

        lock (_commandGate)
            DisposeCore();
    }

    // ════════════════════════════════════════════════
    // 清理
    // ════════════════════════════════════════════════

    /// <summary>清空命令但保留内部数组容量以复用。</summary>
    public void Clear()
    {
        _world.ThrowIfJobCommandBufferAccess();
        lock (_commandGate)
        {
            ThrowIfWorldOwnedLifecycleAccessUnderGate();
            ThrowIfLifecycleMutationUnavailableUnderGate();
            ClearCore();
        }
    }

    internal void ClearOwned()
    {
        lock (_commandGate)
            ClearOwnedUnderGate();
    }

    internal void ClearOwnedUnderGate()
    {
        ThrowIfLifecycleMutationUnavailableUnderGate();
        ClearCore();
    }

    private void ClearCore()
    {
        InvalidateDeferredEntities();
        CancelTypedRelationshipCommands();
        _commands.Clear();
        ClearDataLists();
        _playbackAttempted = false;
    }

    public void Dispose()
    {
        _world.ThrowIfJobCommandBufferAccess();
        lock (_commandGate)
        {
            ThrowIfWorldOwnedLifecycleAccessUnderGate();
            ThrowIfLifecycleMutationUnavailableUnderGate();
            DisposeCore();
        }
    }

    internal void DisposeOwned()
    {
        lock (_commandGate)
            DisposeOwnedUnderGate();
    }

    internal void DisposeOwnedUnderGate()
    {
        DisposeCore();
    }

    private void DisposeCore()
    {
        if (!_disposed)
        {
            InvalidateDeferredEntities();
            CancelTypedRelationshipCommands();
            _commands.Clear();
            _dataLists = Array.Empty<ICommandPayloadList?>();
            _playbackReserved = false;
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════
    // 内部
    // ════════════════════════════════════════════════

    private CommandDataList<T> DataList<T>(int componentId) where T : struct, IComponent
    {
        ArrayGrowthExtensions.EnsureCapacity(ref _dataLists, componentId + 1, 8);

        var existing = _dataLists[componentId];
        if (existing != null)
            return (CommandDataList<T>)existing;

        var list = new CommandDataList<T>();
        _dataLists[componentId] = list;
        return list;
    }

    private IComponentCommandList DataList(int componentId)
    {
        return (IComponentCommandList)_dataLists[componentId]!;
    }

    private void ClearDataLists()
    {
        foreach (var obj in _dataLists)
            obj?.Clear();
    }

    internal void RecordTypedRelationshipUnderGate(ITypedRelationshipCommand command)
    {
        if (!Monitor.IsEntered(_commandGate))
        {
            throw new InvalidOperationException(
                "Typed relationship commands must be appended while the CommandBuffer gate is held.");
        }
        ArgumentNullException.ThrowIfNull(command);
        int dataIndex = _typedRelationshipCommands.Count;
        _typedRelationshipCommands.Add(command);
        _commands.Add(new CommandHeader
        {
            Type = CommandType.TypedRelationship,
            Entity = new CommandEntity(Entity.Null),
            ComponentId = 0,
            DataIndex = dataIndex,
        });
    }

    private void CancelTypedRelationshipCommands()
    {
        for (int i = 0; i < _typedRelationshipCommands.Count; i++)
            _typedRelationshipCommands[i].Cancel();
        _typedRelationshipCommands.Clear();
    }

    private void FailTypedRelationshipCommands()
    {
        for (int i = 0; i < _typedRelationshipCommands.Count; i++)
            _typedRelationshipCommands[i].PlaybackFailed();
    }

    private void InvalidateDeferredEntities()
    {
        for (int i = 0; i < _deferredEntities.Count; i++)
            _deferredEntities[i].InvalidatePending();
        _deferredEntities.Clear();
    }

    internal RecordAccessScope EnterRecordAccess()
    {
        _world.ThrowIfJobCommandBufferAccess(this);
        Monitor.Enter(_commandGate);
        try
        {
            ThrowIfRecordUnavailableUnderGate();
            return new RecordAccessScope(_commandGate);
        }
        catch
        {
            Monitor.Exit(_commandGate);
            throw;
        }
    }

    internal RecordAccessScope EnterRecordAccess(HookCommandToken token)
    {
        Monitor.Enter(_commandGate);
        try
        {
            _world.ValidateHookCommandBufferRecordAccessUnderGate(this, token);
            ThrowIfRecordUnavailableUnderGate();
            return new RecordAccessScope(_commandGate);
        }
        catch
        {
            Monitor.Exit(_commandGate);
            throw;
        }
    }

    internal void ValidateRecordAccess()
    {
        using RecordAccessScope access = EnterRecordAccess();
    }

    internal void ValidateRecordAccess(HookCommandToken token)
    {
        using RecordAccessScope access = EnterRecordAccess(token);
    }

    private void ThrowIfRecordUnavailableUnderGate()
    {
        ThrowIfDisposedCore();
        if (_sealedForWorldPlayback)
        {
            throw new InvalidOperationException(
                "This World-owned CommandBuffer wave is sealed for FIFO playback. " +
                "Call World.Commands() again to record into a later wave.");
        }
        if (_jobProducerCompleted)
        {
            throw new InvalidOperationException(
                "This producer-private CommandBuffer segment is sealed when its Job callback returns.");
        }
        if (_playbackAttempted)
        {
            throw new InvalidOperationException(
                "CommandBuffer has already attempted playback. Call Clear before recording again.");
        }
        if (_playbackReserved)
        {
            throw new InvalidOperationException(
                "Cannot record commands while CommandBuffer playback is reserved or running.");
        }
    }

    private void ThrowIfLifecycleMutationUnavailableUnderGate()
    {
        if (_playbackReserved)
        {
            throw new InvalidOperationException(
                "Cannot clear or dispose CommandBuffer while playback is reserved or running.");
        }
    }

    private void ThrowIfWorldOwnedLifecycleAccessUnderGate()
    {
        if (_worldOwned)
        {
            throw new InvalidOperationException(
                "World-owned CommandBuffer playback, clearing, and disposal are managed by World.Flush().");
        }
    }

    internal void SealOwnedForPlaybackUnderGate()
    {
        if (!_worldOwned)
            throw new InvalidOperationException("Only a World-owned CommandBuffer wave can be sealed.");
        ThrowIfDisposedCore();
        if (_playbackReserved)
            throw new InvalidOperationException("Cannot seal a CommandBuffer after playback was reserved.");
        _sealedForWorldPlayback = true;
    }

    private void ThrowIfDisposedCore()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CommandBuffer));
    }

    internal readonly struct RecordAccessScope : IDisposable
    {
        private readonly object? _gate;

        internal RecordAccessScope(object gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            if (_gate is not null)
                Monitor.Exit(_gate);
        }
    }
}
