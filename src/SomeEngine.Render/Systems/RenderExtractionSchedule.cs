using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Render.Components;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Systems;

/// <summary>
/// Owns an explicitly installed group of extraction systems for one RenderWorld. All systems
/// collect from one coherent main-world read and publish through one RenderWorld structural
/// transaction, so adding a feature never turns extraction into one global switch statement.
/// </summary>
public sealed class RenderExtractionSystems : IDisposable
{
    private readonly RenderWorld _renderWorld;
    private readonly List<IRenderExtractionSystem> _systems = [];
    private readonly MeshRenderExtractor _mesh;
    private readonly LightRenderExtractor _lights;
    private readonly RenderExtractionContext _context;
    private readonly object _gate = new();
    private QueryDefinition? _sourceQuery;
    private QueryHandle _sourceQueryHandle;
    private World? _mainWorld;
    private uint _lastSystemVersion;
    private uint _candidateSystemVersion;
    private long _lastTopologyRevision = -1;
    private bool _nonTransformChange;
    private bool _extractionActive;
    private bool _disposed;

    public RenderExtractionSystems(RenderWorld renderWorld)
    {
        _renderWorld = renderWorld ?? throw new ArgumentNullException(nameof(renderWorld));
        _context = new RenderExtractionContext(renderWorld);
        _mesh = new MeshRenderExtractor();
        _lights = new LightRenderExtractor();
        _systems.Add(_mesh);
        _systems.Add(_lights);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _systems.Count;
            }
        }
    }

    /// <summary>
    /// Resolves the persistent RenderWorld mirror for one authoritative main-world entity after
    /// extraction. This is intended for render-facing resources that keep a small prototype or
    /// template entity while storing the actual instance population outside ECS.
    /// </summary>
    public Entity RequireMirror(Entity source)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_extractionActive)
            {
                throw new InvalidOperationException(
                    "Render mirrors cannot be resolved while extraction is active.");
            }
            return _context.RequireMirror(source);
        }
    }

    /// <summary>Adds a feature-owned extractor before the first source snapshot is compiled.</summary>
    public void Add(IRenderExtractionSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_sourceQuery is not null || _extractionActive)
            {
                throw new InvalidOperationException(
                    "Extraction systems must be installed before the first extraction.");
            }
            _systems.Add(system);
        }
    }

    /// <summary>
    /// Reconciles render-facing snapshots from <paramref name="mainWorld"/>. The main world is
    /// read only; render-only components already attached to mirrored entities remain untouched.
    /// </summary>
    public void Extract(World mainWorld)
    {
        ArgumentNullException.ThrowIfNull(mainWorld);
        if (mainWorld is RenderWorld)
        {
            throw new ArgumentException(
                "A RenderWorld cannot be an authoritative extraction source; extraction " +
                "requires a main World.",
                nameof(mainWorld));
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_extractionActive)
                throw new InvalidOperationException("Render extraction is already active.");
            RequireAuthoritativeMainWorld(mainWorld);
            _sourceQuery ??= BuildSourceQuery();
            _extractionActive = true;
            try
            {
                bool bindingCandidate = _mainWorld is null;
                if (!_sourceQueryHandle.IsValid)
                    _sourceQueryHandle = mainWorld.Query(_sourceQuery);
                try
                {
                    ExtractCore(mainWorld);
                }
                catch
                {
                    if (bindingCandidate)
                    {
                        mainWorld.ReleaseQuery(_sourceQueryHandle);
                        _sourceQueryHandle = default;
                    }
                    throw;
                }
            }
            finally
            {
                _extractionActive = false;
            }
        }
    }

    private QueryDefinition BuildSourceQuery()
    {
        var query = new RenderExtractionQuery();
        for (int index = 0; index < _systems.Count; index++)
            _systems[index].DeclareReads(query);
        return query.Build();
    }

    private void ExtractCore(World mainWorld)
    {
        _candidateSystemVersion = mainWorld.AcquireSystemTick();
        long topologyRevision = mainWorld.PublishedTopologyRevision;
        bool fullReconciliation =
            _mainWorld is null ||
            topologyRevision != _lastTopologyRevision ||
            _systems.Count != 2;

        if (!fullReconciliation)
        {
            _context.EnsureTransformIndexCurrent();
            _context.BeginTransformChanges();
            _mesh.Reset();
            _lights.Reset();
            _nonTransformChange = false;
            try
            {
                RenderExtractionSystems state = this;
                mainWorld.ExecuteReadSnapshot(
                    _sourceQueryHandle,
                    _lastSystemVersion,
                    ref state,
                    static (QueryCursor cursor, ref RenderExtractionSystems systems) =>
                        systems.CollectDelta(cursor));
            }
            catch
            {
                _context.CancelTransformChanges();
                throw;
            }
        }

        if (fullReconciliation)
        {
            _context.CancelTransformChanges();
            CollectFullSnapshot(mainWorld);
        }

        if (fullReconciliation)
        {
            _context.RefreshMirrors();
            try
            {
                _renderWorld.ApplyExtraction(this);
            }
            catch
            {
                _context.RefreshMirrors();
                throw;
            }
            _context.RefreshMirrors();
        }
        else if (_nonTransformChange)
        {
            try
            {
                if (!_renderWorld.TryApplyExtractionChangesDirect(this))
                {
                    _renderWorld.ApplyExtractionChanges(this);
                    _context.AcceptValuePublication();
                }
            }
            catch
            {
                _context.CancelTransformChanges();
                throw;
            }
        }
        else
            _context.ApplyTransformChanges();

        _lastSystemVersion = _candidateSystemVersion;
        _lastTopologyRevision = topologyRevision;
        _mainWorld ??= mainWorld;
    }

    private void CollectFullSnapshot(World mainWorld)
    {
        for (int index = 0; index < _systems.Count; index++)
            _systems[index].Reset();

        RenderExtractionSystems state = this;
        mainWorld.ExecuteReadSnapshot(
            _sourceQueryHandle,
            ref state,
            static (QueryCursor cursor, ref RenderExtractionSystems systems) =>
                systems.Collect(cursor));
    }

    private void RequireAuthoritativeMainWorld(World mainWorld)
    {
        if (_mainWorld is null || ReferenceEquals(_mainWorld, mainWorld))
            return;

        throw new InvalidOperationException(
            "A RenderWorld is bound to one authoritative main World for its lifetime.");
    }

    private void Collect(QueryCursor cursor)
    {
        foreach (QueryChunkView chunk in cursor.Chunks)
        {
            for (int index = 0; index < _systems.Count; index++)
                _systems[index].Collect(chunk);
        }
    }

    private void CollectDelta(QueryCursor cursor)
    {
        foreach (QueryChunkView chunk in cursor.Chunks)
        {
            bool hasSpatialMesh =
                chunk.Has<MeshInstance>() || chunk.Has<InstancedMesh>();
            bool hasTransform = chunk.Has<WorldTransform>();
            if (hasSpatialMesh && hasTransform &&
                chunk.HasChangedSinceLastSystemVersion<WorldTransform>())
            {
                _context.CollectTransformChanges(chunk);
            }

            _nonTransformChange |= _mesh.CollectChanges(chunk);
            _nonTransformChange |= _lights.CollectChanges(chunk);
        }
    }

    internal void ApplyCandidate()
    {
        _context.BeginCandidate();
        // The clock belongs to the detached root and rolls back with every module mutation.
        _renderWorld.AcquireSystemVersion();
        for (int index = 0; index < _systems.Count; index++)
            _systems[index].Apply(_context);
        _context.DestroyUnusedMirrors();
    }

    internal void ApplyChangesCandidate()
    {
        // The detached value candidate gives transform, mesh/material, and light changes one
        // publication boundary without rediscovering or reconciling persistent mirror structure.
        _renderWorld.AcquireSystemVersion();
        _context.ApplyTransformChanges();
        _mesh.ApplyChanges(_context);
        _lights.ApplyChanges(_context);
    }

    internal bool TryApplyChangesDirect()
    {
        if (_renderWorld.HasExtractionValueHooks)
            return false;

        // All invariants are checked before the first in-place write. With the topology frontier
        // held and no user callbacks in the changed domains, the remaining operations contain no
        // ordinary failure point and do not need a detached structural root.
        _mesh.ValidateChanges(_context);
        _lights.ValidateChanges(_context);
        _renderWorld.AcquireSystemVersion();
        _context.ApplyTransformChanges();
        _mesh.ApplyChanges(_context);
        _lights.ApplyChanges(_context);
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_extractionActive)
                throw new InvalidOperationException("Render extraction is still active.");
            _context.Dispose();
            if (_sourceQueryHandle.IsValid)
            {
                (_mainWorld ?? throw new InvalidOperationException(
                    "A bound extraction query has no authoritative main World."))
                    .ReleaseQuery(_sourceQueryHandle);
                _sourceQueryHandle = default;
            }
            _disposed = true;
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>Restricts extraction modules to optional source reads.</summary>
public sealed class RenderExtractionQuery
{
    private readonly QueryDefinitionBuilder _builder = new();

    public void ReadOptional<T>() where T : struct
        => _builder.Optional<T>(QueryAccess.Read);

    public void ReadOptionalBuffer<T>() where T : struct, IBufferElement
        => _builder.OptionalBuffer<T>(QueryAccess.Read);

    internal QueryDefinition Build() => _builder.Build();
}

public interface IRenderExtractionSystem
{
    void DeclareReads(RenderExtractionQuery query);

    void Reset();

    void Collect(QueryChunkView chunk);

    void Apply(RenderExtractionContext context);
}

internal readonly record struct RenderMirror(Entity RenderEntity, Entity Source);

/// <summary>Shared entity-identity and mutation services for one candidate publication.</summary>
public sealed class RenderExtractionContext : IDisposable
{
    private readonly RenderWorld _world;
    private readonly QueryHandle _renderSources;
    private readonly QueryHandle _renderTransformSources;
    private readonly QueryHandle _renderTransforms;
    private readonly Dictionary<Entity, Entity> _mirrorBySource = [];
    private readonly HashSet<Entity> _retainedSources = [];
    private readonly List<RenderMirror> _mirrors = [];
    private Entity[] _mirrorBySourceSlot = [Entity.Null];
    private Entity[] _transformSourceBySlot = [Entity.Null];
    private int[] _transformOrdinalBySourceSlot = [-1];
    private RenderTransform[] _transformValueByOrdinal = [];
    private uint[] _transformChangeEpochByOrdinal = [];
    private uint _transformChangeEpoch;
    private int _transformChangeCount;
    private int _renderTransformCount;
    private int _transformWriteOrdinal;
    private long _transformIndexTopologyRevision = -1;
    private bool _transformChangesActive;

    internal RenderExtractionContext(RenderWorld world)
    {
        _world = world;
        _renderSources = world.Query(new QueryDefinitionBuilder().Read<RenderSource>());
        _renderTransformSources = world.Query(
            new QueryDefinitionBuilder()
                .Read<RenderSource>()
                .Read<RenderTransform>()
                .Read<RenderPreviousTransform>());
        _renderTransforms = world.Query(
            new QueryDefinitionBuilder()
                .Read<RenderSource>()
                .ReadWrite<RenderTransform>()
                .ReadWrite<RenderPreviousTransform>());
    }

    public RenderWorld World => _world;

    internal IReadOnlyList<RenderMirror> Mirrors => _mirrors;

    internal Entity RequireMirror(Entity source)
    {
        if (source.Index <= 0 ||
            (uint)source.Index >= (uint)_mirrorBySourceSlot.Length)
        {
            throw new InvalidOperationException(
                $"RenderWorld has no persistent mirror slot for changed source {source}.");
        }

        Entity renderEntity = _mirrorBySourceSlot[source.Index];
        if (renderEntity == Entity.Null ||
            _world.Read<RenderSource>(renderEntity).Entity != source)
        {
            throw new InvalidOperationException(
                $"RenderWorld has no persistent mirror for changed source {source}.");
        }
        return renderEntity;
    }

    internal void UpdateExisting<T>(Entity entity, in T value)
        where T : struct, SomeEngine.ECS.IComponent, IEquatable<T>
    {
        RequireExisting<T>(entity);

        T current = _world.Read<T>(entity);
        if (!current.Equals(value))
            _world.Replace(entity, value);
    }

    internal void RequireExisting<T>(Entity entity)
        where T : struct, SomeEngine.ECS.IComponent
    {
        if (!_world.Has<T>(entity))
        {
            throw new InvalidOperationException(
                $"Render mirror {entity} is missing required extracted component " +
                $"{typeof(T).Name} on a value-only extraction path.");
        }
    }

    internal void BeginCandidate()
    {
        _retainedSources.Clear();
    }

    internal void RefreshMirrors()
    {
        _mirrorBySource.Clear();
        _mirrors.Clear();
        _mirrorBySourceSlot.AsSpan().Fill(Entity.Null);
        RenderExtractionContext state = this;
        _world.ExecuteQuery(
            _renderSources,
            ref state,
            static (QueryCursor cursor, ref RenderExtractionContext context) =>
                context.CollectMirrors(cursor));
        RebuildTransformIndex();
    }

    private void CollectMirrors(QueryCursor cursor)
    {
        foreach (QueryChunkView chunk in cursor.Chunks)
        {
            ReadOnlySpan<Entity> entities = chunk.Entities;
            ReadOnlySpan<RenderSource> sources = chunk.Read<RenderSource>();
            for (int row = 0; row < entities.Length; row++)
            {
                Entity source = sources[row].Entity;
                Entity renderEntity = entities[row];
                if (!_mirrorBySource.TryAdd(source, renderEntity))
                {
                    throw new InvalidOperationException(
                        $"RenderWorld contains more than one mirror for {source}.");
                }
                _mirrors.Add(new RenderMirror(renderEntity, source));
                EnsureSourceSlot(source.Index);
                _mirrorBySourceSlot[source.Index] = renderEntity;
            }
        }
    }

    internal void EnsureTransformIndexCurrent()
    {
        if (_transformIndexTopologyRevision != _world.PublishedTopologyRevision)
            RebuildTransformIndex();
    }

    internal void AcceptValuePublication()
    {
        // A detached value candidate retains entity/chunk traversal shape. Its root publication
        // advances the World revision, so carry the already-valid primitive index to that revision.
        _transformIndexTopologyRevision = _world.PublishedTopologyRevision;
    }

    private void RebuildTransformIndex()
    {
        _transformSourceBySlot.AsSpan().Fill(Entity.Null);
        _transformOrdinalBySourceSlot.AsSpan().Fill(-1);
        _renderTransformCount = 0;
        RenderExtractionContext state = this;
        _world.ExecuteQuery(
            _renderTransformSources,
            ref state,
            static (QueryCursor cursor, ref RenderExtractionContext context) =>
                context.CollectTransformIndex(cursor));
        EnsureTransformOrdinalCapacity(_renderTransformCount);
        _transformChangeEpochByOrdinal.AsSpan(0, _renderTransformCount).Clear();
        _transformIndexTopologyRevision = _world.PublishedTopologyRevision;
    }

    private void CollectTransformIndex(QueryCursor cursor)
    {
        foreach (QueryChunkView chunk in cursor.Chunks)
        {
            ReadOnlySpan<Entity> renderEntities = chunk.Entities;
            ReadOnlySpan<RenderSource> sources = chunk.Read<RenderSource>();
            for (int row = 0; row < renderEntities.Length; row++)
            {
                Entity source = sources[row].Entity;
                EnsureSourceSlot(source.Index);
                if (_mirrorBySourceSlot[source.Index] != renderEntities[row])
                {
                    throw new InvalidOperationException(
                        $"Render mirror index does not match {renderEntities[row]} for source {source}.");
                }
                if (_transformOrdinalBySourceSlot[source.Index] >= 0)
                {
                    throw new InvalidOperationException(
                        $"RenderWorld contains more than one transform mirror for {source}.");
                }
                _transformSourceBySlot[source.Index] = source;
                _transformOrdinalBySourceSlot[source.Index] = _renderTransformCount++;
            }
        }
    }

    public Entity RetainMirror(Entity source)
    {
        _retainedSources.Add(source);
        if (_mirrorBySource.TryGetValue(source, out Entity entity))
            return entity;

        entity = _world.CreateEntity();
        _world.Add(entity, new RenderSource(source));
        _mirrorBySource.Add(source, entity);
        EnsureSourceSlot(source.Index);
        _mirrorBySourceSlot[source.Index] = entity;
        return entity;
    }

    internal void BeginTransformChanges()
    {
        if (_transformChangesActive)
            throw new InvalidOperationException("Transform extraction is already active.");
        if (_transformChangeEpoch == uint.MaxValue)
        {
            _transformChangeEpochByOrdinal.AsSpan(0, _renderTransformCount).Clear();
            _transformChangeEpoch = 1;
        }
        else
            _transformChangeEpoch++;
        _transformChangeCount = 0;
        _transformChangesActive = true;
    }

    internal void CollectTransformChanges(QueryChunkView chunk)
    {
        if (!_transformChangesActive)
            throw new InvalidOperationException("Transform extraction is not active.");
        if (!chunk.TryRead<WorldTransform>(out ReadOnlySpan<WorldTransform> transforms) ||
            (!chunk.Has<MeshInstance>() && !chunk.Has<InstancedMesh>()))
        {
            return;
        }

        ReadOnlySpan<Entity> entities = chunk.Entities;
        ReadOnlySpan<uint> writeVersions =
            chunk.ReadWriteVersions<WorldTransform>();
        uint lastSystemVersion = chunk.LastSystemVersion;
        for (int row = 0; row < entities.Length; row++)
        {
            if (unchecked((int)(writeVersions[row] - lastSystemVersion)) <= 0)
                continue;
            Entity source = entities[row];
            if ((uint)source.Index >= (uint)_transformOrdinalBySourceSlot.Length ||
                _transformSourceBySlot[source.Index] != source)
            {
                throw new InvalidOperationException(
                    $"RenderWorld has no persistent transform mirror for changed source {source}.");
            }
            int ordinal = _transformOrdinalBySourceSlot[source.Index];
            if (ordinal < 0)
            {
                throw new InvalidOperationException(
                    $"RenderWorld has no persistent transform ordinal for changed source {source}.");
            }
            if (_transformChangeEpochByOrdinal[ordinal] != _transformChangeEpoch)
                _transformChangeCount++;
            _transformValueByOrdinal[ordinal] =
                new RenderTransform(transforms[row].Qvvs);
            _transformChangeEpochByOrdinal[ordinal] = _transformChangeEpoch;
        }
    }

    internal void ApplyTransformChanges()
    {
        if (!_transformChangesActive)
            return;
        try
        {
            if (_transformChangeCount == 0)
                return;
            _transformWriteOrdinal = 0;
            RenderExtractionContext state = this;
            _world.ExecuteQuery(
                _renderTransforms,
                ref state,
                static (QueryCursor cursor, ref RenderExtractionContext context) =>
                    context.WriteTransformRows(cursor));
            if (_transformWriteOrdinal != _renderTransformCount)
            {
                throw new InvalidOperationException(
                    "Render transform traversal no longer matches its structural index.");
            }
        }
        finally
        {
            _transformChangeCount = 0;
            _transformChangesActive = false;
        }
    }

    internal void CancelTransformChanges()
    {
        _transformChangeCount = 0;
        _transformChangesActive = false;
    }

    private void WriteTransformRows(QueryCursor cursor)
    {
        foreach (QueryChunkView chunk in cursor.Chunks)
        {
            Span<RenderTransform> current = chunk.ReadWrite<RenderTransform>();
            Span<RenderPreviousTransform> previous =
                chunk.ReadWrite<RenderPreviousTransform>();
            int firstOrdinal = _transformWriteOrdinal;
            int afterLastOrdinal = checked(firstOrdinal + current.Length);
            if (afterLastOrdinal > _renderTransformCount)
            {
                throw new InvalidOperationException(
                    "Render transform traversal exceeds its structural index.");
            }

            int row = 0;
            while (row < current.Length)
            {
                while (row < current.Length &&
                       _transformChangeEpochByOrdinal[firstOrdinal + row] !=
                       _transformChangeEpoch)
                    row++;
                int runStart = row;
                while (row < current.Length &&
                       _transformChangeEpochByOrdinal[firstOrdinal + row] ==
                       _transformChangeEpoch)
                    row++;
                int runCount = row - runStart;
                if (runCount == 0) continue;

                Span<RenderTransform> currentRun = current.Slice(runStart, runCount);
                MemoryMarshal.Cast<RenderTransform, RenderPreviousTransform>(currentRun)
                    .CopyTo(previous.Slice(runStart, runCount));
                _transformValueByOrdinal
                    .AsSpan(firstOrdinal + runStart, runCount)
                    .CopyTo(currentRun);
            }
            _transformWriteOrdinal = afterLastOrdinal;
        }
    }

    private void EnsureSourceSlot(int sourceSlot)
    {
        if ((uint)sourceSlot < (uint)_mirrorBySourceSlot.Length)
            return;
        int oldLength = _mirrorBySourceSlot.Length;
        int newLength = checked(sourceSlot + 1);
        Array.Resize(ref _mirrorBySourceSlot, newLength);
        Array.Resize(ref _transformSourceBySlot, newLength);
        Array.Resize(ref _transformOrdinalBySourceSlot, newLength);
        _mirrorBySourceSlot.AsSpan(oldLength).Fill(Entity.Null);
        _transformSourceBySlot.AsSpan(oldLength).Fill(Entity.Null);
        _transformOrdinalBySourceSlot.AsSpan(oldLength).Fill(-1);
    }

    private void EnsureTransformOrdinalCapacity(int count)
    {
        if (_transformValueByOrdinal.Length >= count) return;
        Array.Resize(ref _transformValueByOrdinal, count);
        Array.Resize(ref _transformChangeEpochByOrdinal, count);
    }

    internal void DestroyUnusedMirrors()
    {
        for (int index = 0; index < _mirrors.Count; index++)
        {
            RenderMirror mirror = _mirrors[index];
            if (!_retainedSources.Contains(mirror.Source))
                _world.DestroyEntity(mirror.RenderEntity);
        }
    }

    public void RemoveIfExists<T>(Entity entity)
        where T : struct, SomeEngine.ECS.IComponent
    {
        if (_world.Has<T>(entity))
            _world.Remove<T>(entity);
    }

    public void Upsert<T>(Entity entity, in T value)
        where T : struct, SomeEngine.ECS.IComponent, IEquatable<T>
    {
        if (!_world.Has<T>(entity))
        {
            _world.Add(entity, value);
            return;
        }

        T current = _world.Read<T>(entity);
        if (!current.Equals(value))
            _world.Replace(entity, value);
    }

    internal void UpsertTransform(Entity entity, in RenderTransform value)
    {
        if (!_world.Has<RenderTransform>(entity))
        {
            _world.Add(entity, value);
            _world.Add(entity, new RenderPreviousTransform(value));
            return;
        }

        RenderTransform current = _world.Read<RenderTransform>(entity);
        Upsert(entity, new RenderPreviousTransform(current));
        if (current != value)
            _world.Replace(entity, value);
    }

    public void Dispose()
    {
        _world.ReleaseQuery(_renderTransforms);
        _world.ReleaseQuery(_renderTransformSources);
        _world.ReleaseQuery(_renderSources);
    }
}
