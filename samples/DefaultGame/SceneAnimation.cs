using System.Numerics;
using ImGuiNET;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Resources;
using SomeEngine.Runtime;

namespace DefaultGame;

/// <summary>The sample's seeded mesh motion and measurement playback for one loaded scene.</summary>
internal sealed class SceneAnimation : IDisposable
{
    private readonly World _world;
    private readonly SceneEdit _scene;
    private readonly QueryHandle _motionQuery;
    private readonly (Entity Entity, PointLight Value)[] _playbackLights;
    private readonly Material[] _playbackMaterials;
    private readonly Vector3 _initialCameraPosition;
    private readonly Vector3 _cameraOrbitTarget;
    private readonly string? _playback;
    private readonly Func<Material, float, bool>? _animateMaterial;
    private readonly SphereNameplates? _nameplates;
    private readonly IDisposable? _resources;
    private readonly Action _closed;
    private bool _animateScene;
    private bool _disposed;
    private static readonly SomeEngine.Diagnostics.TraceSite SceneTrace = new("app", "Scene.Update");
    private static readonly SomeEngine.Diagnostics.TraceSite PlaybackTrace = new("app", "Scene.Playback");
    private static readonly SomeEngine.Diagnostics.TraceSite MovingMeshesTrace = new("scene", "MovingMeshes");
    private static readonly SomeEngine.Diagnostics.TraceSite MovingLightsTrace = new("scene", "MovingLights");
    private static readonly SomeEngine.Diagnostics.TraceSite ChangingMaterialsTrace = new("scene", "ChangingMaterials");

    private SceneAnimation(World world, SceneEdit scene, bool animateScene, string? playback,
        bool worldNameplates, Func<Material, float, bool>? animateMaterial, IDisposable? resources, Action closed)
    {
        _world = world;
        _scene = scene;
        _animateScene = animateScene;
        _playback = playback;
        _animateMaterial = animateMaterial;
        _closed = closed;
        _playbackLights = Entities(world, new QueryDefinitionBuilder().Read<PointLight>())
            .Select(entity => (entity, world.Read<PointLight>(entity))).ToArray();
        _playbackMaterials = [.. scene.Materials];
        _initialCameraPosition = scene.CameraPosition;
        float cameraDistance = MathF.Max(Vector3.Distance(scene.CameraPosition,
            (scene.MeshPositionMin + scene.MeshPositionMax) * 0.5f), 1f);
        _cameraOrbitTarget = scene.CameraPosition + scene.CameraForward * cameraDistance;
        _motionQuery = world.Query(new QueryDefinitionBuilder().Read<SceneMotion>().ReadWrite<WorldTransform>());
        try
        {
            if (worldNameplates) _nameplates = new SphereNameplates(world, GetMovingMeshEntities(world));
        }
        catch (Exception failure)
        {
            try { world.ReleaseQuery(_motionQuery); }
            catch (Exception cleanup) { throw new AggregateException("Scene animation creation and cleanup failed.", failure, cleanup); }
            throw;
        }
        _resources = resources;
    }

    internal static void Install(ApplicationScene application, Func<SampleSceneOptions> getOptions,
        Func<Material, float, bool>? animateMaterial = null)
    {
        SceneAnimation? current = null;
        application.RegisterSceneComponents += SampleSceneSerialization.Register;
        application.InitializeMesh += InitializeMesh;
        SampleSceneOptions initial = getOptions();
        if (initial.WorldNameplates || initial.SurfacePanel || initial.HudOverlay) application.InterfaceEnabled = true;
        Func<World, SceneEdit, IDisposable?>? create = application.CreateSceneResources;
        application.CreateSceneResources = (world, scene) =>
        {
            IDisposable? resources = create?.Invoke(world, scene);
            try
            {
                SampleSceneOptions options = getOptions();
                if (options.WorldNameplates || options.SurfacePanel || options.HudOverlay) application.InterfaceEnabled = true;
                return current = new SceneAnimation(world, scene, options.DynamicScene, options.ScenePlayback,
                    options.WorldNameplates, animateMaterial, resources, () => current = null);
            }
            catch (Exception failure)
            {
                try { resources?.Dispose(); }
                catch (Exception cleanup) { throw new AggregateException("Sample scene creation and cleanup failed.", failure, cleanup); }
                throw;
            }
        };
        Action<ApplicationFrame>? initialize = application.Initialize;
        application.Initialize = frame =>
        {
            current?.RegisterInterface(frame);
            initialize?.Invoke(frame);
        };
        application.BeforeCameraInput += (_, seconds) => current?.BeforeCameraInput(seconds);
        application.SimulateScene += (_, seconds) => current?.SimulateScene(seconds);
        Action? drawDebugUi = application.DrawDebugUi;
        application.DrawDebugUi = () =>
        {
            drawDebugUi?.Invoke();
            if (current is null) return;
            ImGui.Separator();
            _ = ImGui.Checkbox("Animate scene", ref current._animateScene);
        };
    }

    private static void InitializeMesh(World world, Entity entity)
    {
        if (!world.Has<MeshMotion>(entity)) return;
        TransformQvvs transform = world.Read<WorldTransform>(entity).Qvvs;
        MeshMotion motion = world.Read<MeshMotion>(entity);
        if (motion.Amplitude != Vector3.Zero) world.Add(entity, SceneMotion.Create(transform, motion.Amplitude, motion.Seed));
        world.Remove<MeshMotion>(entity);
    }

    private void RegisterInterface(ApplicationFrame frame)
    {
        if (_nameplates is not null) frame.Systems.AddInterfaceSystem(_nameplates);
    }

    private void BeforeCameraInput(float seconds)
    {
        if (_playback is not null)
            Play(seconds, _playback is "materials" or "camera", _animateMaterial,
                orbitCamera: _playback == "camera");
    }

    private void SimulateScene(float seconds)
    {
        if (_animateScene && _playback is null) Update(seconds);
    }

    internal static Entity[] GetMovingMeshEntities(World world) => Entities(world,
        new QueryDefinitionBuilder().Read<SceneMotion>().Read<WorldTransform>());

    private static Entity[] Entities(World world, QueryDefinitionBuilder definition)
    {
        QueryHandle query = world.Query(definition);
        var entities = new List<Entity>();
        try
        {
            world.ExecuteQuery(query, ref entities, static (scoped QueryCursor rows, ref List<Entity> values) =>
            {
                foreach (var row in rows.Rows) values.Add(row.Entity);
            });
            return [.. entities];
        }
        finally { world.ReleaseQuery(query); }
    }

    private void Update(float elapsedSeconds)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(SceneTrace);
        ObjectDisposedException.ThrowIf(_disposed, this);
        UpdateMotionRows(elapsedSeconds);
    }

    // Reproducible input for renderer measurements; this is scene behavior, not trace policy.
    private void Play(float seconds, bool animateMaterials,
        Func<Material, float, bool>? animateMaterial = null, bool orbitCamera = false)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(PlaybackTrace);
        Update(seconds);
        if (orbitCamera)
        {
            Quaternion rotation = Quaternion.CreateFromYawPitchRoll(
                MathF.Sin(seconds * 0.31f) * 0.45f, MathF.Sin(seconds * 0.17f) * 0.1f, 0);
            _scene.SetCameraView(_cameraOrbitTarget + Vector3.Transform(
                _initialCameraPosition - _cameraOrbitTarget, rotation), _cameraOrbitTarget);
        }
        else
            _scene.SetCameraPosition(_initialCameraPosition + new Vector3(MathF.Sin(seconds) * 0.3f, MathF.Sin(seconds * 0.7f) * 0.15f, 0));
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
            static (scoped QueryCursor query, ref (float Seconds, int Count) motionState) =>
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
        if (_disposed) return;
        _disposed = true;
        var errors = new List<Exception>();
        try
        {
            try { _nameplates?.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { _world.ReleaseQuery(_motionQuery); } catch (Exception error) { errors.Add(error); }
            try { _resources?.Dispose(); } catch (Exception error) { errors.Add(error); }
        }
        finally { _closed(); }
        if (errors.Count != 0) throw new AggregateException("Some sample scene resources could not be closed.", errors);
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
