using System.Globalization;
using System.Text;

namespace SomeEngine.ECS.Fuzz.Tests;

internal readonly record struct ModelEntity(
    bool Alive,
    int? Alpha,
    int? Beta,
    bool HasTag,
    int? Enableable,
    bool EnableableEnabled,
    int? Sparse,
    int? Shared,
    int? Indexed,
    int[]? Buffer,
    int? Parent);

internal sealed class ModelOperationException : InvalidOperationException
{
    internal ModelOperationException(string message)
        : base(message)
    {
    }
}

/// <summary>Pure Dictionary oracle with no dependency on ECS implementation types.</summary>
internal sealed class ReferenceWorld
{
    private readonly Dictionary<int, ModelEntity> _entities;
    private readonly Dictionary<(int Source, int Target), int> _relations;
    private Transaction? _activeTransaction;

    internal ReferenceWorld()
    {
        _entities = new Dictionary<int, ModelEntity>();
        _relations = new Dictionary<(int Source, int Target), int>();
    }

    internal IEnumerable<KeyValuePair<int, ModelEntity>> Entities => _entities;

    internal IReadOnlyList<(int Source, int Target)> Relations =>
        _relations.Keys
            .OrderBy(static pair => pair.Source)
            .ThenBy(static pair => pair.Target)
            .ToArray();

    internal int AliveCount => _entities.Values.Count(static entity => entity.Alive);

    internal int RelationCount => _relations.Count;

    internal Transaction BeginTransaction()
    {
        if (_activeTransaction is not null)
            throw new InvalidOperationException("Reference model transactions cannot be nested.");
        var transaction = new Transaction(this);
        _activeTransaction = transaction;
        return transaction;
    }

    internal ModelEntity Entity(int id) =>
        _entities.TryGetValue(id, out ModelEntity entity)
            ? entity
            : throw new ModelOperationException($"Logical entity {id} was never created.");

    internal int RelationValue(int source, int target) => _relations[(source, target)];

    internal bool HasRelation(int source, int target) => _relations.ContainsKey((source, target));

    internal bool WouldCreateHierarchyCycle(int child, int parent)
    {
        int? cursor = parent;
        while (cursor is int current)
        {
            if (current == child)
                return true;
            cursor = RequireAlive(current).Parent;
        }
        return false;
    }

    internal int[] EligibleIds(Func<ModelEntity, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return _entities
            .Where(pair => predicate(pair.Value))
            .Select(static pair => pair.Key)
            .OrderBy(static id => id)
            .ToArray();
    }

    internal void Apply(FuzzCommand command)
    {
        if (command.EntityId <= 0)
            throw new ModelOperationException($"Logical entity id {command.EntityId} is invalid.");

        switch (command.Kind)
        {
            case FuzzCommandKind.CreateEntity:
                if (_entities.ContainsKey(command.EntityId))
                    throw new ModelOperationException($"Logical entity {command.EntityId} was already created.");
                SetEntity(
                    command.EntityId,
                    new ModelEntity(
                        true,
                        null,
                        null,
                        false,
                        null,
                        false,
                        null,
                        null,
                        null,
                        null,
                        null));
                return;

            case FuzzCommandKind.DestroyEntity:
            {
                ModelEntity entity = RequireAlive(command.EntityId);
                SetEntity(command.EntityId, entity with { Alive = false, Parent = null });
                int[] children = EligibleIds(candidate =>
                    candidate.Alive && candidate.Parent == command.EntityId);
                for (int i = 0; i < children.Length; i++)
                {
                    ModelEntity child = _entities[children[i]];
                    SetEntity(children[i], child with { Parent = null });
                }
                foreach ((int source, int target) in _relations.Keys
                             .Where(pair => pair.Source == command.EntityId || pair.Target == command.EntityId)
                             .ToArray())
                {
                    RemoveRelation((source, target));
                }
                return;
            }

            case FuzzCommandKind.AddAlpha:
                SetOptional(command, static entity => entity.Alpha, (entity, value) => entity with { Alpha = value }, add: true);
                return;
            case FuzzCommandKind.ReplaceAlpha:
                SetOptional(command, static entity => entity.Alpha, (entity, value) => entity with { Alpha = value }, add: false);
                return;
            case FuzzCommandKind.RemoveAlpha:
                RemoveOptional(command, static entity => entity.Alpha, entity => entity with { Alpha = null });
                return;
            case FuzzCommandKind.AddBeta:
                SetOptional(command, static entity => entity.Beta, (entity, value) => entity with { Beta = value }, add: true);
                return;
            case FuzzCommandKind.ReplaceBeta:
                SetOptional(command, static entity => entity.Beta, (entity, value) => entity with { Beta = value }, add: false);
                return;
            case FuzzCommandKind.RemoveBeta:
                RemoveOptional(command, static entity => entity.Beta, entity => entity with { Beta = null });
                return;

            case FuzzCommandKind.AddTag:
            {
                ModelEntity entity = RequireAlive(command.EntityId);
                if (entity.HasTag)
                    throw new ModelOperationException($"Logical entity {command.EntityId} already has Tag.");
                SetEntity(command.EntityId, entity with { HasTag = true });
                return;
            }
            case FuzzCommandKind.RemoveTag:
            {
                ModelEntity entity = RequireAlive(command.EntityId);
                if (!entity.HasTag)
                    throw new ModelOperationException($"Logical entity {command.EntityId} has no Tag to remove.");
                SetEntity(command.EntityId, entity with { HasTag = false });
                return;
            }

            case FuzzCommandKind.AddEnableable:
            {
                ModelEntity entity = RequireAlive(command.EntityId);
                if (entity.Enableable is not null)
                    throw new ModelOperationException($"Logical entity {command.EntityId} already has Enableable.");
                SetEntity(command.EntityId, entity with
                {
                    Enableable = command.Value,
                    EnableableEnabled = true,
                });
                return;
            }
            case FuzzCommandKind.ReplaceEnableable:
                SetOptional(
                    command,
                    static entity => entity.Enableable,
                    (entity, value) => entity with { Enableable = value },
                    add: false);
                return;
            case FuzzCommandKind.RemoveEnableable:
            {
                ModelEntity entity = RequireAlive(command.EntityId);
                if (entity.Enableable is null)
                    throw new ModelOperationException($"Logical entity {command.EntityId} has no Enableable to remove.");
                SetEntity(command.EntityId, entity with
                {
                    Enableable = null,
                    EnableableEnabled = false,
                });
                return;
            }
            case FuzzCommandKind.Enable:
            case FuzzCommandKind.Disable:
            {
                ModelEntity entity = RequireAlive(command.EntityId);
                if (entity.Enableable is null)
                    throw new ModelOperationException($"Logical entity {command.EntityId} has no Enableable.");
                bool enabled = command.Kind == FuzzCommandKind.Enable;
                if (entity.EnableableEnabled == enabled)
                    throw new ModelOperationException($"Logical entity {command.EntityId} Enableable already has requested state.");
                SetEntity(command.EntityId, entity with { EnableableEnabled = enabled });
                return;
            }

            case FuzzCommandKind.AddSparse:
                SetOptional(command, static entity => entity.Sparse, (entity, value) => entity with { Sparse = value }, add: true);
                return;
            case FuzzCommandKind.ReplaceSparse:
                SetOptional(command, static entity => entity.Sparse, (entity, value) => entity with { Sparse = value }, add: false);
                return;
            case FuzzCommandKind.RemoveSparse:
                RemoveOptional(command, static entity => entity.Sparse, entity => entity with { Sparse = null });
                return;
            case FuzzCommandKind.AddShared:
                SetOptional(command, static entity => entity.Shared, (entity, value) => entity with { Shared = value }, add: true);
                return;
            case FuzzCommandKind.ReplaceShared:
                SetOptional(command, static entity => entity.Shared, (entity, value) => entity with { Shared = value }, add: false);
                return;
            case FuzzCommandKind.RemoveShared:
                RemoveOptional(command, static entity => entity.Shared, entity => entity with { Shared = null });
                return;
            case FuzzCommandKind.AddIndexed:
                SetOptional(command, static entity => entity.Indexed, (entity, value) => entity with { Indexed = value }, add: true);
                return;
            case FuzzCommandKind.ReplaceIndexed:
                SetOptional(command, static entity => entity.Indexed, (entity, value) => entity with { Indexed = value }, add: false);
                return;
            case FuzzCommandKind.RemoveIndexed:
                RemoveOptional(command, static entity => entity.Indexed, entity => entity with { Indexed = null });
                return;

            case FuzzCommandKind.AddBuffer:
            {
                ModelEntity entity = RequireAlive(command.EntityId);
                if (entity.Buffer is not null)
                    throw new ModelOperationException($"Logical entity {command.EntityId} already has Buffer.");
                SetEntity(command.EntityId, entity with { Buffer = Array.Empty<int>() });
                return;
            }
            case FuzzCommandKind.AppendBuffer:
            {
                ModelEntity entity = RequireAlive(command.EntityId);
                if (entity.Buffer is null)
                    throw new ModelOperationException($"Logical entity {command.EntityId} has no Buffer.");
                SetEntity(command.EntityId, entity with { Buffer = [.. entity.Buffer, command.Value] });
                return;
            }
            case FuzzCommandKind.SetBufferFirst:
            {
                ModelEntity entity = RequireAlive(command.EntityId);
                if (entity.Buffer is not { Length: > 0 })
                    throw new ModelOperationException($"Logical entity {command.EntityId} has no first Buffer item.");
                int[] buffer = entity.Buffer.ToArray();
                buffer[0] = command.Value;
                SetEntity(command.EntityId, entity with { Buffer = buffer });
                return;
            }
            case FuzzCommandKind.RemoveBuffer:
            {
                ModelEntity entity = RequireAlive(command.EntityId);
                if (entity.Buffer is null)
                    throw new ModelOperationException($"Logical entity {command.EntityId} has no Buffer to remove.");
                SetEntity(command.EntityId, entity with { Buffer = null });
                return;
            }

            case FuzzCommandKind.SetParent:
            {
                ModelEntity child = RequireAlive(command.EntityId);
                _ = RequireAlive(command.OtherEntityId);
                if (command.EntityId == command.OtherEntityId ||
                    WouldCreateHierarchyCycle(command.EntityId, command.OtherEntityId))
                {
                    throw new ModelOperationException("Hierarchy parent would create a cycle.");
                }
                SetEntity(command.EntityId, child with { Parent = command.OtherEntityId });
                return;
            }
            case FuzzCommandKind.Detach:
            {
                ModelEntity child = RequireAlive(command.EntityId);
                if (child.Parent is null)
                    throw new ModelOperationException($"Logical entity {command.EntityId} has no Parent.");
                SetEntity(command.EntityId, child with { Parent = null });
                return;
            }
            case FuzzCommandKind.CreateRelation:
                _ = RequireAlive(command.EntityId);
                _ = RequireAlive(command.OtherEntityId);
                if (command.EntityId == command.OtherEntityId ||
                    !TryAddRelation((command.EntityId, command.OtherEntityId), command.Value))
                {
                    throw new ModelOperationException("Relation pair already exists or is a self-edge.");
                }
                return;
            case FuzzCommandKind.DestroyRelation:
                if (!RemoveRelation((command.EntityId, command.OtherEntityId)))
                    throw new ModelOperationException("Relation pair does not exist.");
                return;

            default:
                throw new ModelOperationException($"Unknown fuzz command {command.Kind}.");
        }
    }

    internal string Digest()
    {
        var builder = new StringBuilder();
        foreach ((int id, ModelEntity entity) in _entities.OrderBy(static pair => pair.Key))
        {
            builder.Append(id.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entity.Alive ? '1' : '0');
            builder.Append(':');
            builder.Append(entity.Alpha?.ToString(CultureInfo.InvariantCulture) ?? "-");
            builder.Append(':');
            builder.Append(entity.Beta?.ToString(CultureInfo.InvariantCulture) ?? "-");
            builder.Append(':');
            builder.Append(entity.HasTag ? '1' : '0');
            builder.Append(':');
            builder.Append(entity.Enableable?.ToString(CultureInfo.InvariantCulture) ?? "-");
            builder.Append(entity.EnableableEnabled ? 'e' : 'd');
            builder.Append(':');
            builder.Append(entity.Sparse?.ToString(CultureInfo.InvariantCulture) ?? "-");
            builder.Append(':');
            builder.Append(entity.Shared?.ToString(CultureInfo.InvariantCulture) ?? "-");
            builder.Append(':');
            builder.Append(entity.Indexed?.ToString(CultureInfo.InvariantCulture) ?? "-");
            builder.Append(':');
            builder.Append(entity.Buffer is null
                ? "-"
                : string.Join(',', entity.Buffer.Select(value => value.ToString(CultureInfo.InvariantCulture))));
            builder.Append(':');
            builder.Append(entity.Parent?.ToString(CultureInfo.InvariantCulture) ?? "-");
            builder.Append(';');
        }
        builder.Append("relations=");
        foreach (((int source, int target), int value) in _relations.OrderBy(static pair => pair.Key.Source).ThenBy(static pair => pair.Key.Target))
        {
            builder.Append(source.ToString(CultureInfo.InvariantCulture));
            builder.Append('>');
            builder.Append(target.ToString(CultureInfo.InvariantCulture));
            builder.Append('=');
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
        }
        return builder.ToString();
    }

    private void SetOptional(
        FuzzCommand command,
        Func<ModelEntity, int?> read,
        Func<ModelEntity, int, ModelEntity> write,
        bool add)
    {
        ModelEntity entity = RequireAlive(command.EntityId);
        bool present = read(entity) is not null;
        if (present == add)
        {
            throw new ModelOperationException(
                $"Logical entity {command.EntityId} has invalid {command.Kind} presence.");
        }
        SetEntity(command.EntityId, write(entity, command.Value));
    }

    private void RemoveOptional(
        FuzzCommand command,
        Func<ModelEntity, int?> read,
        Func<ModelEntity, ModelEntity> remove)
    {
        ModelEntity entity = RequireAlive(command.EntityId);
        if (read(entity) is null)
        {
            throw new ModelOperationException(
                $"Logical entity {command.EntityId} has nothing for {command.Kind}.");
        }
        SetEntity(command.EntityId, remove(entity));
    }

    private ModelEntity RequireAlive(int entityId)
    {
        if (!_entities.TryGetValue(entityId, out ModelEntity entity) || !entity.Alive)
            throw new ModelOperationException($"Logical entity {entityId} is stale or was never created.");
        return entity;
    }

    private void SetEntity(int entityId, ModelEntity value)
    {
        _activeTransaction?.RecordEntity(entityId);
        _entities[entityId] = value;
    }

    private bool TryAddRelation((int Source, int Target) key, int value)
    {
        _activeTransaction?.RecordRelation(key);
        return _relations.TryAdd(key, value);
    }

    private bool RemoveRelation((int Source, int Target) key)
    {
        _activeTransaction?.RecordRelation(key);
        return _relations.Remove(key);
    }

    private void CompleteTransaction(Transaction transaction)
    {
        if (!ReferenceEquals(_activeTransaction, transaction))
            throw new InvalidOperationException("Reference model transaction ownership was lost.");
        _activeTransaction = null;
    }

    internal sealed class Transaction : IDisposable
    {
        private readonly ReferenceWorld _owner;
        private readonly Dictionary<int, EntityImage> _entities = new();
        private readonly Dictionary<(int Source, int Target), RelationImage> _relations = new();
        private bool _completed;

        internal Transaction(ReferenceWorld owner)
        {
            _owner = owner;
        }

        internal void RecordEntity(int entityId)
        {
            if (_entities.ContainsKey(entityId))
                return;
            _entities.Add(
                entityId,
                _owner._entities.TryGetValue(entityId, out ModelEntity value)
                    ? new EntityImage(true, value)
                    : new EntityImage(false, default));
        }

        internal void RecordRelation((int Source, int Target) key)
        {
            if (_relations.ContainsKey(key))
                return;
            _relations.Add(
                key,
                _owner._relations.TryGetValue(key, out int value)
                    ? new RelationImage(true, value)
                    : new RelationImage(false, default));
        }

        internal void Commit()
        {
            if (_completed)
                throw new InvalidOperationException("Reference model transaction is already complete.");
            _completed = true;
            _owner.CompleteTransaction(this);
        }

        public void Dispose()
        {
            if (_completed)
                return;
            foreach ((int entityId, EntityImage image) in _entities)
            {
                if (image.Existed)
                    _owner._entities[entityId] = image.Value;
                else
                    _owner._entities.Remove(entityId);
            }
            foreach (((int source, int target) key, RelationImage image) in _relations)
            {
                if (image.Existed)
                    _owner._relations[key] = image.Value;
                else
                    _owner._relations.Remove(key);
            }
            _completed = true;
            _owner.CompleteTransaction(this);
        }

        private readonly record struct EntityImage(bool Existed, ModelEntity Value);

        private readonly record struct RelationImage(bool Existed, int Value);
    }
}
