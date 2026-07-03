using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Systems;

public enum AccessResourceKind : byte
{
    Component,
    Shared,
    Sparse,
    Relation,
    Structural,
    CommandBuffer,
}

public readonly record struct SystemAccessResource
{
    public SystemAccessResource(AccessResourceKind kind, int id)
    {
        if (!IsKnownKind(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown system access resource kind.");

        bool requiresId =
            kind == AccessResourceKind.Component ||
            kind == AccessResourceKind.Shared ||
            kind == AccessResourceKind.Sparse ||
            kind == AccessResourceKind.Relation;

        if (requiresId && id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), id, $"{kind} resources require a positive component id.");

        if (!requiresId && id != 0)
            throw new ArgumentOutOfRangeException(nameof(id), id, $"{kind} resources do not accept a component id.");

        Kind = kind;
        Id = id;
    }

    public AccessResourceKind Kind { get; }

    public int Id { get; }

    public static SystemAccessResource Component(int componentId) =>
        new(AccessResourceKind.Component, componentId);

    public static SystemAccessResource Shared(int componentId) =>
        new(AccessResourceKind.Shared, componentId);

    public static SystemAccessResource Sparse(int componentId) =>
        new(AccessResourceKind.Sparse, componentId);

    public static SystemAccessResource Relation(int componentId) =>
        new(AccessResourceKind.Relation, componentId);

    public static SystemAccessResource Structural =>
        new(AccessResourceKind.Structural, 0);

    public static SystemAccessResource CommandBuffer =>
        new(AccessResourceKind.CommandBuffer, 0);

    private static bool IsKnownKind(AccessResourceKind kind) =>
        kind == AccessResourceKind.Component ||
        kind == AccessResourceKind.Shared ||
        kind == AccessResourceKind.Sparse ||
        kind == AccessResourceKind.Relation ||
        kind == AccessResourceKind.Structural ||
        kind == AccessResourceKind.CommandBuffer;
}

public readonly record struct SystemAccessEntry(SystemAccessResource Resource, QueryAccess Access);

public sealed class SystemAccessManifest
{
    private readonly SystemAccessEntry[] _entries;

    internal SystemAccessManifest(
        SystemAccessEntry[] entries,
        bool requiresExclusiveStage,
        bool requiresBarrierAfter)
    {
        _entries = entries;
        Entries = Array.AsReadOnly(_entries);
        RequiresExclusiveStage = requiresExclusiveStage;
        RequiresBarrierAfter = requiresBarrierAfter;
    }

    public static SystemAccessManifest Empty { get; } =
        new(Array.Empty<SystemAccessEntry>(), requiresExclusiveStage: false, requiresBarrierAfter: false);

    public IReadOnlyList<SystemAccessEntry> Entries { get; }

    public bool RequiresExclusiveStage { get; }

    public bool RequiresBarrierAfter { get; }

    internal ReadOnlySpan<SystemAccessEntry> EntriesSpan => _entries;

    public static Builder CreateBuilder() => new();

    public static SystemAccessManifest FromQuery(QueryDefinition spec) =>
        CreateBuilder().AddQuery(spec).Build();

    public sealed class Builder
    {
        private readonly Dictionary<SystemAccessResource, QueryAccess> _entries = new();
        private bool _requiresExclusiveStage;
        private bool _requiresBarrierAfter;

        public Builder AddQuery(QueryDefinition spec)
        {
            ArgumentNullException.ThrowIfNull(spec);

            foreach (var access in spec.Accesses)
                Add(SystemAccessResource.Component(access.ComponentId), access.Access);

            return this;
        }

        public Builder Read<T>() where T : struct =>
            AddTable<T>(QueryAccess.Read);

        public Builder Write<T>() where T : struct =>
            AddTable<T>(QueryAccess.Write);

        public Builder ReadWrite<T>() where T : struct =>
            AddTable<T>(QueryAccess.ReadWrite);

        public Builder ReadShared<T>() where T : struct, ISharedComponent
        {
            AddStore<T>(StoragePath.Shared, AccessResourceKind.Shared, QueryAccess.Read);
            return this;
        }

        public Builder WriteShared<T>() where T : struct, ISharedComponent
        {
            AddStore<T>(StoragePath.Shared, AccessResourceKind.Shared, QueryAccess.Write);
            _requiresExclusiveStage = true;
            _requiresBarrierAfter = true;
            return this;
        }

        public Builder ReadSparse<T>() where T : struct, ISparseComponent
        {
            AddStore<T>(StoragePath.Sparse, AccessResourceKind.Sparse, QueryAccess.Read);
            return this;
        }

        public Builder WriteSparse<T>() where T : struct, ISparseComponent
        {
            AddStore<T>(StoragePath.Sparse, AccessResourceKind.Sparse, QueryAccess.Write);
            return this;
        }

        public Builder ReadRelation<T>() where T : struct, IRelation
        {
            AddRelation<T>(QueryAccess.Read);
            return this;
        }

        public Builder WriteRelation<T>() where T : struct, IRelation
        {
            AddRelation<T>(QueryAccess.Write);
            return this;
        }

        public Builder StructuralChange()
        {
            Add(SystemAccessResource.Structural, QueryAccess.Write);
            _requiresExclusiveStage = true;
            _requiresBarrierAfter = true;
            return this;
        }

        public Builder CommandBufferWrite()
        {
            Add(SystemAccessResource.CommandBuffer, QueryAccess.Write);
            _requiresBarrierAfter = true;
            return this;
        }

        public SystemAccessManifest Build()
        {
            if (_entries.Count == 0 && !_requiresExclusiveStage && !_requiresBarrierAfter)
                return SystemAccessManifest.Empty;

            var entries = new SystemAccessEntry[_entries.Count];
            int index = 0;
            foreach (var pair in _entries)
                entries[index++] = new SystemAccessEntry(pair.Key, pair.Value);

            Array.Sort(entries, static (left, right) =>
            {
                int kind = left.Resource.Kind.CompareTo(right.Resource.Kind);
                return kind != 0 ? kind : left.Resource.Id.CompareTo(right.Resource.Id);
            });

            return new SystemAccessManifest(entries, _requiresExclusiveStage, _requiresBarrierAfter);
        }

        private Builder AddTable<T>(QueryAccess access) where T : struct
        {
            if (ComponentMetadata<T>.Storage != StoragePath.Table)
            {
                throw new InvalidOperationException(
                    $"Type {typeof(T).Name} uses {ComponentMetadata<T>.Storage} storage; table access helpers require an IComponent table component. " +
                    "Use the shared, sparse, or relation access helper that matches the component storage.");
            }

            Add(SystemAccessResource.Component(ComponentMetadata<T>.Id), access);
            return this;
        }

        private void AddStore<T>(
            StoragePath expected,
            AccessResourceKind kind,
            QueryAccess access)
            where T : struct
        {
            if (ComponentMetadata<T>.Storage != expected)
            {
                throw new InvalidOperationException(
                    $"Type {typeof(T).Name} uses {ComponentMetadata<T>.Storage} storage; {expected} access helpers require {expected} storage.");
            }

            Add(StoreResource(kind, ComponentMetadata<T>.Id), access);
        }

        private void AddRelation<T>(QueryAccess access)
            where T : struct, IRelation
        {
            var storage = ComponentMetadata<T>.Storage;
            if (storage != StoragePath.Relation && storage != StoragePath.ExclusiveRelation)
            {
                throw new InvalidOperationException(
                    $"Type {typeof(T).Name} uses {storage} storage; relation access helpers require relation storage.");
            }

            Add(SystemAccessResource.Relation(ComponentMetadata<T>.Id), access);
        }

        private static SystemAccessResource StoreResource(AccessResourceKind kind, int componentId)
        {
            return kind switch
            {
                AccessResourceKind.Shared => SystemAccessResource.Shared(componentId),
                AccessResourceKind.Sparse => SystemAccessResource.Sparse(componentId),
                AccessResourceKind.Relation => SystemAccessResource.Relation(componentId),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported side-store resource kind."),
            };
        }

        private void Add(SystemAccessResource resource, QueryAccess access)
        {
            if (access == QueryAccess.None)
                return;

            if (!IsKnownAccess(access))
                throw new ArgumentOutOfRangeException(nameof(access), access, "Unknown query access mode.");

            if (_entries.TryGetValue(resource, out var existing))
                _entries[resource] = Merge(existing, access);
            else
                _entries.Add(resource, access);
        }

        private static QueryAccess Merge(QueryAccess left, QueryAccess right)
        {
            bool read = CanRead(left) || CanRead(right);
            bool write = CanWrite(left) || CanWrite(right);

            return (read, write) switch
            {
                (true, true) => QueryAccess.ReadWrite,
                (true, false) => QueryAccess.Read,
                (false, true) => QueryAccess.Write,
                _ => QueryAccess.None,
            };
        }

        private static bool CanRead(QueryAccess access) =>
            access == QueryAccess.Read || access == QueryAccess.ReadWrite;

        private static bool CanWrite(QueryAccess access) =>
            access == QueryAccess.Write || access == QueryAccess.ReadWrite;

        private static bool IsKnownAccess(QueryAccess access) =>
            access == QueryAccess.Read ||
            access == QueryAccess.Write ||
            access == QueryAccess.ReadWrite;

    }
}

