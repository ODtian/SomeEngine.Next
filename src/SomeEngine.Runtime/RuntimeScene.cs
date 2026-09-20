using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;

namespace SomeEngine.Runtime;

internal sealed class RuntimeScene : IDisposable
{
    private readonly World _world;
    private Mesh[] _meshes = [];
    private readonly SceneMotion[] _motions;
    private readonly (Entity Entity, PointLight Value)[] _playbackLights;
    private readonly Material[] _playbackMaterials;
    private readonly Vector3 _initialCameraPosition;
    private static readonly SomeEngine.Diagnostics.TraceSite MovingMeshesTrace = new("scene", "MovingMeshes");
    private static readonly SomeEngine.Diagnostics.TraceSite MovingLightsTrace = new("scene", "MovingLights");
    private static readonly SomeEngine.Diagnostics.TraceSite ChangingMaterialsTrace = new("scene", "ChangingMaterials");
    private readonly QueryHandle _motionQuery;
    private int[] _motionByEntitySlot;
    private readonly float _verticalFieldOfView;
    private readonly float _nearPlane;
    private readonly float _farPlane;
    private float _elapsedSeconds;
    private bool _motionIndexInvalid;
    private bool _disposed;

    private RuntimeScene(
        World world,
        SceneMotion[] motions,
        (Entity Entity, PointLight Value)[] playbackLights,
        Material[] playbackMaterials,
        QueryHandle motionQuery,
        int[] motionByEntitySlot,
        RuntimeCamera camera,
        float verticalFieldOfView,
        float nearPlane,
        float farPlane,
        int meshInstanceCount,
        Vector3 meshPositionMin,
        Vector3 meshPositionMax)
    {
        _world = world;
        _motions = motions;
        _playbackLights = playbackLights;
        _playbackMaterials = playbackMaterials;
        _initialCameraPosition = camera.Position;
        _motionQuery = motionQuery;
        _motionByEntitySlot = motionByEntitySlot;
        Camera = camera;
        _verticalFieldOfView = verticalFieldOfView;
        _nearPlane = nearPlane;
        _farPlane = farPlane;
        MeshInstanceCount = meshInstanceCount;
        MeshPositionMin = meshPositionMin;
        MeshPositionMax = meshPositionMax;
    }

    internal RuntimeCamera Camera { get; }

    internal int MeshInstanceCount { get; }

    internal Vector3 MeshPositionMin { get; }

    internal Vector3 MeshPositionMax { get; }

    internal static async ValueTask<RuntimeScene> CreateAsync(World world, AssetLoader assets)
    {
        Entity[] instances = Entities(world, new QueryDefinitionBuilder().Read<MeshSource>().Read<WorldTransform>().ReadBuffer<MaterialSource>());
        Entity[] cameras = Entities(world, new QueryDefinitionBuilder().Read<SceneCamera>());
        if (cameras.Length != 1) throw new InvalidDataException("The Game world must contain one scene camera.");
        SceneCamera camera = world.Read<SceneCamera>(cameras[0]);
        var meshes = new Dictionary<Guid, Mesh>();
        var materials = new Dictionary<Guid, Material>();
        var motions = new List<SceneMotion>();
        Vector3 meshPositionMin = new(float.PositiveInfinity);
        Vector3 meshPositionMax = new(float.NegativeInfinity);
        try
        {
            foreach (Entity entity in instances)
            {
                MeshSource source = world.Read<MeshSource>(entity);
                if (!meshes.TryGetValue(source.Mesh.Value, out Mesh? mesh))
                {
                    using Mesh.Asset file = await Mesh.Asset.OpenAsync(assets, source.Mesh).ConfigureAwait(false);
                    mesh = await file.LoadAsync().ConfigureAwait(false);
                    meshes.Add(source.Mesh.Value, mesh);
                }
                MaterialSource[] sources = [];
                world.ExecuteBufferRead<MaterialSource, MaterialSource[]>(entity, ref sources,
                    static (BufferView<MaterialSource> buffer, ref MaterialSource[] values) => values = buffer.AsSpan().ToArray());
                var bindings = new MeshMaterialBinding[sources.Length];
                for (int index = 0; index < sources.Length; index++)
                {
                    AssetId<Material> id = sources[index].Material;
                    if (!materials.TryGetValue(id.Value, out Material? material))
                    {
                        using Material.Asset file = await Material.Asset.OpenAsync(assets, id).ConfigureAwait(false);
                        material = await file.LoadAsync(assets).ConfigureAwait(false);
                        materials.Add(id.Value, material);
                    }
                    bindings[index] = new(material);
                }
                TransformQvvs transform = world.Read<WorldTransform>(entity).Qvvs;
                meshPositionMin = Vector3.Min(meshPositionMin, transform.Position);
                meshPositionMax = Vector3.Max(meshPositionMax, transform.Position);
                world.Add(entity, new MeshInstance { Mesh = mesh, BoundsExpansion = source.BoundsExpansion });
                world.AddBuffer<MeshMaterialBinding>(entity);
                world.ExecuteBufferWrite<MeshMaterialBinding, MeshMaterialBinding[]>(entity, ref bindings,
                    static (DynamicBuffer<MeshMaterialBinding> buffer, ref MeshMaterialBinding[] values) =>
                    {
                        buffer.EnsureCapacity(values.Length);
                        foreach (MeshMaterialBinding value in values) buffer.Add(value);
                    });
                if (world.Has<MeshMotion>(entity))
                {
                    MeshMotion motion = world.Read<MeshMotion>(entity);
                    if (motion.Amplitude != Vector3.Zero) motions.Add(SceneMotion.Create(entity, transform, motion.Amplitude, motion.Seed));
                    world.Remove<MeshMotion>(entity);
                }
                world.Remove<MeshSource>(entity);
                world.RemoveBuffer<MaterialSource>(entity);
            }
            var playbackLights = Entities(world, new QueryDefinitionBuilder().Read<PointLight>())
                .Select(entity => (entity, world.Read<PointLight>(entity))).ToArray();
            var runtimeCamera = new RuntimeCamera(camera.Position, camera.Target, camera.Up);
            world.Remove<SceneCamera>(cameras[0]);
            if (instances.Length == 0) meshPositionMin = meshPositionMax = Vector3.Zero;
            SceneMotion[] motionRows = [.. motions];
            QueryHandle motionQuery = world.Query(new QueryDefinitionBuilder().ReadWrite<WorldTransform>());
            try
            {
                return new RuntimeScene(world, motionRows, playbackLights, [.. materials.Values], motionQuery,
                    BuildMotionIndex(motionRows), runtimeCamera, camera.VerticalFieldOfView, camera.NearPlane,
                    camera.FarPlane, instances.Length, meshPositionMin, meshPositionMax) { _meshes = [.. meshes.Values] };
            }
            catch { world.ReleaseQuery(motionQuery); throw; }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (Material material in materials.Values)
                try { material.Dispose(); } catch (Exception cleanup) { errors.Add(cleanup); }
            foreach (Mesh mesh in meshes.Values)
                try { mesh.Dispose(); } catch (Exception cleanup) { errors.Add(cleanup); }
            if (errors.Count != 1) throw new AggregateException("Scene loading and cleanup failed.", errors);
            throw;
        }
    }

    private static Entity[] Entities(World world, QueryDefinitionBuilder definition)
    {
        QueryHandle query = world.Query(definition);
        var entities = new List<Entity>();
        try
        {
            world.ExecuteQuery(query, ref entities, static (QueryCursor rows, ref List<Entity> values) =>
            {
                foreach (var row in rows.Rows) values.Add(row.Entity);
            });
            return [.. entities];
        }
        finally { world.ReleaseQuery(query); }
    }

    internal Matrix4x4 Projection(int viewportWidth, int viewportHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewportWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewportHeight);
        return Matrix4x4.CreatePerspectiveFieldOfView(
            _verticalFieldOfView,
            (float)viewportWidth / viewportHeight,
            _nearPlane,
            _farPlane);
    }

    internal void Update(float elapsedSeconds)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(ApplicationTrace.Scene);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _elapsedSeconds = elapsedSeconds;
        UpdateMotionRows();
        if (_motionIndexInvalid)
            RebuildMotionIndex();
        SomeEngine.Diagnostics.EngineTrace.Counter(MovingMeshesTrace, _motions.Length);
    }

    // Reproducible input for renderer measurements; this is scene behavior, not trace policy.
    internal void Play(float seconds, bool animateMaterials)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(ApplicationTrace.Playback);
        Update(seconds);
        Camera.SetPosition(_initialCameraPosition + new Vector3(MathF.Sin(seconds) * 0.3f, MathF.Sin(seconds * 0.7f) * 0.15f, 0));
        for (int index = 0; index < _playbackLights.Length; index++)
        {
            (Entity entity, PointLight light) = _playbackLights[index];
            _world.Replace(entity, new PointLight(
                light.Position + new Vector3(MathF.Sin(seconds + index) * 0.5f, 0, 0), light.Range, light.Color,
                light.Intensity * (0.9f + MathF.Cos(seconds + index) * 0.1f), light.LayerMask));
        }
        int changingMaterials = 0;
        if (animateMaterials)
            foreach (Material material in _playbackMaterials)
                if (material is StandardMaterial standard)
                {
                    standard.Roughness = 0.5f + MathF.Sin(seconds) * 0.2f;
                    changingMaterials++;
                }
        SomeEngine.Diagnostics.EngineTrace.Counter(MovingLightsTrace, _playbackLights.Length);
        SomeEngine.Diagnostics.EngineTrace.Counter(ChangingMaterialsTrace, changingMaterials);
    }

    private void UpdateMotionRows()
    {
        if (_motions.Length == 0)
            return;
        RuntimeScene state = this;
        _world.ExecuteQuery(
            _motionQuery,
            ref state,
            static (QueryCursor cursor, ref RuntimeScene scene) =>
                scene.WriteMotionRows(cursor));
    }

    private void WriteMotionRows(QueryCursor cursor)
    {
        foreach (QueryChunkView chunk in cursor.Chunks)
        {
            ReadOnlySpan<Entity> entities = chunk.Entities;
            for (int row = 0; row < entities.Length; row++)
            {
                Entity entity = entities[row];
                int motionOrdinal = (uint)entity.Index < (uint)_motionByEntitySlot.Length
                    ? _motionByEntitySlot[entity.Index]
                    : -1;
                if (motionOrdinal < 0)
                    continue;

                ref readonly SceneMotion motion = ref _motions[motionOrdinal];
                if (motion.Entity != entity)
                {
                    _motionIndexInvalid = true;
                    continue;
                }

                Vector3 offset = new(
                    MathF.Sin(_elapsedSeconds * motion.Frequency.X) * motion.Amplitude.X,
                    MathF.Sin(_elapsedSeconds * motion.Frequency.Y) * motion.Amplitude.Y,
                    MathF.Sin(_elapsedSeconds * motion.Frequency.Z) * motion.Amplitude.Z);
                TransformQvvs value = motion.BaseTransform;
                value.Position += offset;
                chunk.ReadWrite<WorldTransform>(row) = new WorldTransform { Qvvs = value };
            }
        }
    }

    private static int[] BuildMotionIndex(ReadOnlySpan<SceneMotion> motions)
    {
        int slotCount = 1;
        for (int index = 0; index < motions.Length; index++)
            slotCount = Math.Max(slotCount, checked(motions[index].Entity.Index + 1));

        int[] result = new int[slotCount];
        result.AsSpan().Fill(-1);
        for (int ordinal = 0; ordinal < motions.Length; ordinal++)
        {
            int slot = motions[ordinal].Entity.Index;
            if (result[slot] >= 0)
                throw new InvalidOperationException($"Scene motion entity slot {slot} is duplicated.");
            result[slot] = ordinal;
        }
        return result;
    }

    private void RebuildMotionIndex()
    {
        int[] rebuilt = BuildMotionIndex(_motions);
        for (int ordinal = 0; ordinal < _motions.Length; ordinal++)
        {
            Entity entity = _motions[ordinal].Entity;
            if (!_world.IsAlive(entity) || !_world.Has<WorldTransform>(entity))
                rebuilt[entity.Index] = -1;
        }
        _motionByEntitySlot = rebuilt;
        _motionIndexInvalid = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var errors = new List<Exception>();
        try { _world.ReleaseQuery(_motionQuery); }
        catch (Exception error) { errors.Add(error); }
        foreach (Material material in _playbackMaterials)
            try { material.Dispose(); }
            catch (Exception error) { errors.Add(error); }
        foreach (Mesh mesh in _meshes)
            try { mesh.Dispose(); }
            catch (Exception error) { errors.Add(error); }
        _meshes = [];
        if (errors.Count != 0)
            throw new AggregateException("Some scene resources could not be closed.", errors);
    }

    private readonly struct SceneMotion
    {
        internal SceneMotion(
            Entity entity,
            TransformQvvs baseTransform,
            Vector3 amplitude,
            Vector3 frequency)
        {
            Entity = entity;
            BaseTransform = baseTransform;
            Amplitude = amplitude;
            Frequency = frequency;
        }

        internal readonly Entity Entity;
        internal readonly TransformQvvs BaseTransform;
        internal readonly Vector3 Amplitude;
        internal readonly Vector3 Frequency;

        internal static SceneMotion Create(
            Entity entity,
            TransformQvvs transform,
            Vector3 amplitude,
            uint seed)
        {
            float x = Unit(Hash(seed ^ 0xA511E9B3u));
            float y = Unit(Hash(seed ^ 0x63D83595u));
            float z = Unit(Hash(seed ^ 0xB5297A4Du));
            return new SceneMotion(
                entity,
                transform,
                amplitude,
                new Vector3(0.55f + x * 0.35f, 0.70f + y * 0.45f, 0.42f + z * 0.31f));
        }

        private static uint Hash(uint value)
        {
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            return value ^ (value >> 16);
        }

        private static float Unit(uint value) => (value >> 8) * (1.0f / 16_777_215.0f);
    }
}
