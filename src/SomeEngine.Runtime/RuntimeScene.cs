using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;

namespace SomeEngine.Runtime;

internal sealed class RuntimeScene : IDisposable
{
    private sealed record SceneMeshData(
        Mesh Mesh,
        MeshPayloadSource PageSource,
        RegionMaterial[] Materials);
    private readonly record struct RegionMaterial(int Region, AssetId<MaterialAsset> Material);

    private readonly World _world;
    private (Mesh Mesh, MeshPayloadSource Source, MeshMaterialBinding[] Bindings)[] _meshes = [];
    private readonly (Entity Entity, PointLight Value)[] _playbackLights;
    private readonly Material[] _playbackMaterials;
    private Texture[] _textures = [];
    private readonly Vector3 _initialCameraPosition;
    private readonly Vector3 _cameraOrbitTarget;
    private static readonly SomeEngine.Diagnostics.TraceSite MovingMeshesTrace = new("scene", "MovingMeshes");
    private static readonly SomeEngine.Diagnostics.TraceSite MovingLightsTrace = new("scene", "MovingLights");
    private static readonly SomeEngine.Diagnostics.TraceSite ChangingMaterialsTrace = new("scene", "ChangingMaterials");
    private readonly QueryHandle _motionQuery;
    private readonly float _verticalFieldOfView;
    private readonly float _nearPlane;
    private readonly float _farPlane;
    private bool _disposed;

    private RuntimeScene(
        World world,
        (Entity Entity, PointLight Value)[] playbackLights,
        Material[] playbackMaterials,
        QueryHandle motionQuery,
        RuntimeCamera camera,
        float verticalFieldOfView,
        float nearPlane,
        float farPlane,
        int meshInstanceCount,
        Vector3 meshPositionMin,
        Vector3 meshPositionMax)
    {
        _world = world;
        _playbackLights = playbackLights;
        _playbackMaterials = playbackMaterials;
        _initialCameraPosition = camera.Position;
        float cameraDistance = MathF.Max(Vector3.Distance(camera.Position,
            (meshPositionMin + meshPositionMax) * 0.5f), 1f);
        _cameraOrbitTarget = camera.Position + camera.Forward * cameraDistance;
        _motionQuery = motionQuery;
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

    internal static async ValueTask<RuntimeScene> CreateAsync(
        World world,
        AssetLoader assets,
        Func<AssetId<ShaderAsset>, CancellationToken, ValueTask<Shader>> loadShader)
    {
        ArgumentNullException.ThrowIfNull(loadShader);
        Entity[] instances = Entities(world, new QueryDefinitionBuilder().Read<MeshSource>().Read<WorldTransform>());
        Entity[] cameras = Entities(world, new QueryDefinitionBuilder().Read<SceneCamera>());
        if (cameras.Length != 1) throw new InvalidDataException("The Game world must contain one scene camera.");
        SceneCamera camera = world.Read<SceneCamera>(cameras[0]);
        var meshes = new Dictionary<Guid, (Mesh Mesh, MeshPayloadSource Source, MeshMaterialBinding[] Bindings)>();
        var materials = new Dictionary<Guid, Material>();
        var textureReads = new Dictionary<Guid, Lazy<Task<Texture>>>();
        Vector3 meshPositionMin = new(float.PositiveInfinity);
        Vector3 meshPositionMax = new(float.NegativeInfinity);
        AssetId<MeshAsset>[] meshIds = instances
            .Select(entity => world.Read<MeshSource>(entity).Mesh)
            .Distinct()
            .ToArray();
        var loadedMeshes = new SceneMeshData?[meshIds.Length];
        Material?[] loadedMaterials = [];
        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, meshIds.Length),
                new ParallelOptions { MaxDegreeOfParallelism = assets.MaxConcurrentReads },
                async (index, cancellationToken) =>
                {
                    using MeshAsset file = await MeshAsset.OpenAsync(assets, meshIds[index], cancellationToken)
                        .ConfigureAwait(false);
                    MeshBlock geometry = await file.GetBlockAsync<MeshBlock>(cancellationToken).ConfigureAwait(false);
                    ClusterGeometryBlock? clusters = await file.TryGetBlockAsync<ClusterGeometryBlock>(cancellationToken).ConfigureAwait(false);
                    if (clusters is null) return;
                    MeshMaterialsBlock references = await file.GetBlockAsync<MeshMaterialsBlock>(cancellationToken)
                        .ConfigureAwait(false);
                    ClusterVertexAttributesBlock attributes = await file.GetBlockAsync<ClusterVertexAttributesBlock>(cancellationToken).ConfigureAwait(false);
                    Mesh mesh = await clusters.CreateAsync(geometry, attributes, file.Name, cancellationToken).ConfigureAwait(false);
                    MeshPayloadSource? pages = null;
                    try
                    {
                        pages = await clusters.OpenPayloadSourceAsync(attributes, cancellationToken).ConfigureAwait(false);
                        int[] regions = clusters.Clusters.SelectMany(cluster => cluster.TriangleRegions.IsEmpty
                                ? cluster.TriangleMaterials : cluster.TriangleRegions)
                            .Select(region => Math.Max(0, region)).Distinct().Order().ToArray();
                        RegionMaterial[] regionMaterials = regions
                            .Select(region => new RegionMaterial(region, references.Materials[region])).ToArray();
                        foreach (RegionMaterial value in regionMaterials)
                            if (value.Material.IsEmpty)
                                throw new InvalidDataException($"Mesh {file.Id} region {value.Region} requires a material.");
                        loadedMeshes[index] = new SceneMeshData(mesh, pages, regionMaterials);
                    }
                    catch { pages?.Dispose(); throw; }
                }).ConfigureAwait(false);

            AssetId<MaterialAsset>[] materialIds = loadedMeshes
                .Where(mesh => mesh is not null).SelectMany(mesh => mesh!.Materials.Select(value => value.Material))
                .Distinct()
                .ToArray();
            loadedMaterials = new Material?[materialIds.Length];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, materialIds.Length),
                new ParallelOptions { MaxDegreeOfParallelism = assets.MaxConcurrentReads },
                async (index, cancellationToken) =>
                {
                    using MaterialAsset file = await MaterialAsset.OpenAsync(assets, materialIds[index], cancellationToken)
                        .ConfigureAwait(false);
                    loadedMaterials[index] = await file.CreateAsync(assets, loadShader, LoadTextureAsync,
                        cancellationToken, loadTexturesConcurrently: true).ConfigureAwait(false);
                }).ConfigureAwait(false);
            for (int index = 0; index < materialIds.Length; index++)
            {
                materials.Add(materialIds[index].Value, loadedMaterials[index]!);
                loadedMaterials[index] = null;
            }

            for (int meshIndex = 0; meshIndex < meshIds.Length; meshIndex++)
            {
                if (loadedMeshes[meshIndex] is not SceneMeshData data) continue;
                Guid meshId = meshIds[meshIndex].Value;
                meshes.Add(meshId, (data.Mesh, data.PageSource, []));
                loadedMeshes[meshIndex] = null;
                var bindings = new MeshMaterialBinding[data.Materials[^1].Region + 1];
                // Gaps keep original region indices and share one valid binding; only retained regions draw.
                Array.Fill(bindings, new MeshMaterialBinding(materials[data.Materials[0].Material.Value]));
                foreach (RegionMaterial value in data.Materials)
                    bindings[value.Region] = new(materials[value.Material.Value]);
                meshes[meshId] = (data.Mesh, data.PageSource, bindings);
            }
            instances = instances.Where(entity => meshes.ContainsKey(world.Read<MeshSource>(entity).Mesh.Value)).ToArray();

            foreach (Entity entity in instances)
            {
                MeshSource source = world.Read<MeshSource>(entity);
                var content = meshes[source.Mesh.Value];
                TransformQvvs transform = world.Read<WorldTransform>(entity).Qvvs;
                meshPositionMin = Vector3.Min(meshPositionMin, transform.Position);
                meshPositionMax = Vector3.Max(meshPositionMax, transform.Position);
                world.Add(entity, new MeshInstance { Mesh = content.Mesh, PageSource = content.Source,
                    BoundsExpansion = source.BoundsExpansion });
                world.AddBuffer<MeshMaterialBinding>(entity);
                MeshMaterialBinding[] bindings = content.Bindings;
                world.ExecuteBufferWrite<MeshMaterialBinding, MeshMaterialBinding[]>(entity, ref bindings,
                    static (DynamicBuffer<MeshMaterialBinding> buffer, ref MeshMaterialBinding[] values) =>
                    {
                        buffer.EnsureCapacity(values.Length);
                        foreach (MeshMaterialBinding value in values) buffer.Add(value);
                    });
                if (world.Has<MeshMotion>(entity))
                {
                    MeshMotion motion = world.Read<MeshMotion>(entity);
                    if (motion.Amplitude != Vector3.Zero) world.Add(entity, SceneMotion.Create(transform, motion.Amplitude, motion.Seed));
                    world.Remove<MeshMotion>(entity);
                }
                world.Remove<MeshSource>(entity);
            }
            var playbackLights = Entities(world, new QueryDefinitionBuilder().Read<PointLight>())
                .Select(entity => (entity, world.Read<PointLight>(entity))).ToArray();
            var runtimeCamera = new RuntimeCamera(camera.Position, camera.Target, camera.Up);
            world.Remove<SceneCamera>(cameras[0]);
            if (instances.Length == 0) meshPositionMin = meshPositionMax = Vector3.Zero;
            QueryHandle motionQuery = world.Query(new QueryDefinitionBuilder().Read<SceneMotion>().ReadWrite<WorldTransform>());
            try
            {
                return new RuntimeScene(world, playbackLights, [.. materials.Values], motionQuery,
                    runtimeCamera, camera.VerticalFieldOfView, camera.NearPlane,
                    camera.FarPlane, instances.Length, meshPositionMin, meshPositionMax)
                    { _meshes = [.. meshes.Values],
                        _textures = [.. textureReads.Values.Select(read => read.Value.Result)] };
            }
            catch { world.ReleaseQuery(motionQuery); throw; }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (Material material in materials.Values)
                try { material.Dispose(); } catch (Exception cleanup) { errors.Add(cleanup); }
            foreach (Material? material in loadedMaterials)
                if (material is not null)
                    try { material.Dispose(); } catch (Exception cleanup) { errors.Add(cleanup); }
            foreach (Lazy<Task<Texture>> read in textureReads.Values)
            {
                if (!read.IsValueCreated) continue;
                try { (await read.Value.ConfigureAwait(false)).Dispose(); }
                catch (Exception cleanup) { errors.Add(cleanup); }
            }
            foreach (var mesh in meshes.Values)
            {
                try { } catch (Exception cleanup) { errors.Add(cleanup); }
                try { mesh.Source.Dispose(); } catch (Exception cleanup) { errors.Add(cleanup); }
            }
            foreach (SceneMeshData? mesh in loadedMeshes)
            {
                if (mesh is null) continue;
                try { } catch (Exception cleanup) { errors.Add(cleanup); }
                try { mesh.PageSource.Dispose(); } catch (Exception cleanup) { errors.Add(cleanup); }
            }
            if (errors.Count != 1) throw new AggregateException("Scene loading and cleanup failed.", errors);
            throw;
        }

        async ValueTask<Texture> LoadTextureAsync(AssetId<TextureAsset> id, CancellationToken cancellationToken)
        {
            Lazy<Task<Texture>> read;
            lock (textureReads)
            {
                if (!textureReads.TryGetValue(id.Value, out read!))
                {
                    read = new Lazy<Task<Texture>>(() => ReadTextureAsync(id, cancellationToken));
                    textureReads.Add(id.Value, read);
                }
            }
            return await read.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        async Task<Texture> ReadTextureAsync(AssetId<TextureAsset> id, CancellationToken cancellationToken)
        {
            using TextureAsset file = await TextureAsset.OpenAsync(assets, id, cancellationToken).ConfigureAwait(false);
            TextureBlock pixels = await file.GetBlockAsync<TextureBlock>(cancellationToken).ConfigureAwait(false);
            return await pixels.CreateAsync(file.Name, cancellationToken).ConfigureAwait(false);
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
        UpdateMotionRows(elapsedSeconds);
    }

    // Reproducible input for renderer measurements; this is scene behavior, not trace policy.
    internal void Play(float seconds, bool animateMaterials,
        Func<Material, float, bool>? animateMaterial = null, bool orbitCamera = false)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(ApplicationTrace.Playback);
        Update(seconds);
        if (orbitCamera)
        {
            Quaternion rotation = Quaternion.CreateFromYawPitchRoll(
                MathF.Sin(seconds * 0.31f) * 0.45f, MathF.Sin(seconds * 0.17f) * 0.1f, 0);
            Camera.SetView(_cameraOrbitTarget + Vector3.Transform(
                _initialCameraPosition - _cameraOrbitTarget, rotation), _cameraOrbitTarget);
        }
        else
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
                if (animateMaterial?.Invoke(material, seconds) == true) changingMaterials++;
        SomeEngine.Diagnostics.EngineTrace.Counter(MovingLightsTrace, _playbackLights.Length);
        SomeEngine.Diagnostics.EngineTrace.Counter(ChangingMaterialsTrace, changingMaterials);
    }

    private void UpdateMotionRows(float seconds)
    {
        // Motion belongs to each Game-world entity. Querying those entities directly
        // avoids a second entity index and lets each chunk publish its writes once.
        var state = (Seconds: seconds, Count: 0);
        _world.ExecuteQuery(_motionQuery, ref state,
            static (QueryCursor query, ref (float Seconds, int Count) motionState) =>
            {
                foreach (QueryChunkView chunk in query.Chunks)
                {
                    ReadOnlySpan<SceneMotion> motions = chunk.Read<SceneMotion>();
                    Span<WorldTransform> transforms = chunk.ReadWrite<WorldTransform>();
                    motionState.Count += motions.Length;
                    for (int row = 0; row < motions.Length; row++)
                    {
                        ref readonly SceneMotion motion = ref motions[row];
                        Vector3 offset = new(
                            MathF.Sin(motionState.Seconds * motion.Frequency.X) * motion.Amplitude.X,
                            MathF.Sin(motionState.Seconds * motion.Frequency.Y) * motion.Amplitude.Y,
                            MathF.Sin(motionState.Seconds * motion.Frequency.Z) * motion.Amplitude.Z);
                        TransformQvvs value = motion.BaseTransform;
                        value.Position += offset;
                        transforms[row] = new WorldTransform { Qvvs = value };
                    }
                }
            });
        SomeEngine.Diagnostics.EngineTrace.Counter(MovingMeshesTrace, state.Count);
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
            try { material.Dispose(); } catch (Exception error) { errors.Add(error); }
        foreach (Texture texture in _textures)
            try { texture.Dispose(); } catch (Exception error) { errors.Add(error); }
        foreach (var mesh in _meshes)
        {
            try { } catch (Exception error) { errors.Add(error); }
            try { mesh.Source.Dispose(); } catch (Exception error) { errors.Add(error); }
        }
        _meshes = [];
        if (errors.Count != 0)
            throw new AggregateException("Some scene resources could not be closed.", errors);
    }

    private readonly struct SceneMotion : IComponent
    {
        internal SceneMotion(
            TransformQvvs baseTransform,
            Vector3 amplitude,
            Vector3 frequency)
        {
            BaseTransform = baseTransform;
            Amplitude = amplitude;
            Frequency = frequency;
        }

        internal readonly TransformQvvs BaseTransform;
        internal readonly Vector3 Amplitude;
        internal readonly Vector3 Frequency;

        internal static SceneMotion Create(
            TransformQvvs transform,
            Vector3 amplitude,
            uint seed)
        {
            float x = Unit(Hash(seed ^ 0xA511E9B3u));
            float y = Unit(Hash(seed ^ 0x63D83595u));
            float z = Unit(Hash(seed ^ 0xB5297A4Du));
            return new SceneMotion(
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
