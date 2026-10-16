using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Resources;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Shadows;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Runtime;

/// <summary>A finite scene run. Frames are zero based; actions at the same frame retain their order.</summary>
public sealed class SceneScript
{
    public SceneScript(string contentDirectory, int frameCount, IEnumerable<SceneAction> actions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        ArgumentNullException.ThrowIfNull(actions);
        ContentDirectory = Path.GetFullPath(contentDirectory);
        FrameCount = frameCount;
        SceneAction[] ordered = actions.OrderBy(action => action.Frame).ToArray();
        foreach (SceneAction action in ordered)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(action.Frame);
            if (action.Frame >= frameCount) throw new ArgumentOutOfRangeException(nameof(actions));
            ArgumentNullException.ThrowIfNull(action.Execute);
        }
        Actions = Array.AsReadOnly(ordered);
    }

    public ClusterShadingPath? ShadingPath { get; init; }
    public IReadOnlyList<DebugViewSettings>? DebugViews { get; init; }
    public uint GeometryMemoryBytes { get; init; } = 256u * 1024 * 1024;
    /// <summary>Maximum hierarchy, candidate and deferred entries retained by the geometry pipeline.</summary>
    public uint ClusterWorkCapacity { get; init; } = 1_048_576;
    public uint BlendTriangleCapacity { get; init; } = ClusterPipelineOptions.DefaultBlendTriangleCapacity;
    public uint BlendReferenceCapacity { get; init; } = 1_048_576;
    public Action<ClusterBlendMetrics>? BlendFrameCompleted { get; init; }
    public Func<ApplicationRendering, IApplicationColorFeature>? CreateColorFeature { get; init; }
    public bool WaitForFrames { get; init; } = true;
    public float RasterEdgePixels { get; init; } = 4;
    public bool UseCameraHistory { get; init; } = true;
    public VirtualShadowMapSettings ShadowSettings { get; init; } = new();
    public TemporalResolveSettings TemporalSettings { get; init; } = TemporalResolveSettings.Default;

    public string ContentDirectory { get; }
    public int FrameCount { get; }
    public IReadOnlyList<SceneAction> Actions { get; }
}

public sealed record SceneAction(int Frame, Action<SceneEdit> Execute);

/// <summary>Packed, top-down RGBA8 pixels retained independently of the graphics device.</summary>
public sealed record SceneImage(int Frame, int Width, int Height, ReadOnlyMemory<byte> Pixels);

/// <summary>Scene operations available during a script action, before extraction of that frame.</summary>
public sealed class SceneEdit
{
    private readonly RuntimeScene _scene;
    private readonly NativeWindow _window;
    private readonly List<Action<SceneImage>> _images = [];
    private readonly List<Action<SceneLinearImage>> _linearImages = [];
    private bool _active;

    internal SceneEdit(RuntimeScene scene, NativeWindow window) => (_scene, _window) = (scene, window);
    internal IReadOnlyList<Action<SceneImage>> Images => _images;
    internal IReadOnlyList<Action<SceneLinearImage>> LinearImages => _linearImages;
    internal ClusterShadingPath? ShadingPath { get; private set; }
    internal DebugViewSettings[]? DebugViews { get; private set; }
    internal bool FrameCanceled { get; private set; }
    internal bool CameraCut { get; private set; }
    internal bool ShadowMapRefreshRequested { get; private set; }
    internal void Begin()
    {
        ShadingPath = null;
        DebugViews = null;
        _images.Clear();
        _linearImages.Clear();
        FrameCanceled = CameraCut = ShadowMapRefreshRequested = false;
        _active = true;
    }
    internal void End() => _active = false;

    public IReadOnlyList<Entity> MeshEntities { get { RequireAction(); return _scene.GetMeshEntities(); } }
    public Matrix4x4 CameraView { get { RequireAction(); return _scene.Camera.View; } }

    public void SetShadingPath(ClusterShadingPath path)
    {
        RequireAction();
        if (path is not (ClusterShadingPath.Deferred or ClusterShadingPath.Forward))
            throw new ArgumentOutOfRangeException(nameof(path));
        ShadingPath = path;
    }

    /// <summary>Replaces the independent debug previews. An empty list removes their render work.</summary>
    public void SetDebugViews(params DebugViewSettings[] views)
    {
        RequireAction();
        ArgumentNullException.ThrowIfNull(views);
        DebugViews = views.ToArray();
    }

    public void SetOutputSize(int width, int height)
    {
        RequireAction();
        _window.SetClientSize(width, height);
    }

    public void SetCameraTransform(Vector3 position, Quaternion rotation)
    {
        RequireAction();
        _scene.SetCameraTransform(position, rotation);
    }

    /// <summary>Moves the entire scene by changing its double precision origin in engine world units.</summary>
    public void SetWorldOrigin(WorldOrigin origin)
    {
        RequireAction();
        _scene.SetWorldOrigin(origin);
        CameraCut = true;
    }

    public Material GetMaterial(Entity entity, int index = 0)
    {
        RequireAction();
        return _scene.GetMaterial(entity, index);
    }

    public Mesh GetMesh(Entity entity)
    {
        RequireAction();
        return _scene.GetMesh(entity);
    }

    public void ReplaceMaterial(Entity entity, Material material, int index = 0)
    {
        RequireAction();
        ArgumentNullException.ThrowIfNull(material);
        _scene.ReplaceMaterial(entity, material, index);
    }

    /// <summary>Adds an instance of the source entity's mesh and materials at the supplied transform.</summary>
    public Entity AddEntity(Entity source, TransformQvvs transform)
    {
        RequireAction();
        return _scene.AddEntity(source, transform);
    }

    /// <summary>Adds caller-supplied, ready geometry and materials to the Game world.</summary>
    public Entity AddEntity(Mesh mesh, IReadOnlyList<Material> materials, TransformQvvs transform) =>
        AddEntity(mesh, materials, transform, 0);

    /// <summary>Adds ready geometry with a conservative world-space radius for the material's deformation.</summary>
    public Entity AddEntity(Mesh mesh, IReadOnlyList<Material> materials, TransformQvvs transform, float boundsExpansion)
    {
        RequireAction();
        return _scene.AddEntity(new MeshInstance { Mesh = mesh, BoundsExpansion = boundsExpansion },
            materials.Select(material => new MeshMaterialBinding(material)).ToArray(), transform);
    }

    /// <summary>Projects a material layer inside the transformed [-1, 1] box.</summary>
    public Entity AddMaterialDecal(Material material, TransformQvvs transform, int order = 0)
    {
        RequireAction();
        return _scene.AddMaterialDecal(material, transform, order);
    }

    public void SetMaterialDecal(Entity entity, Material material, int order = 0)
    {
        RequireAction();
        _scene.SetMaterialDecal(entity, material, order);
    }

    public void SetEntityTransform(Entity entity, TransformQvvs transform)
    {
        RequireAction();
        _scene.SetEntityTransform(entity, transform);
    }

    public void SetXray(Entity entity, Xray effect)
    {
        RequireAction();
        _scene.SetXray(entity, effect);
    }

    public void RemoveXray(Entity entity)
    {
        RequireAction();
        _scene.RemoveXray(entity);
    }

    public IReadOnlyList<Entity> PointLights { get { RequireAction(); return _scene.GetPointLights(); } }

    public void SetPointLight(Entity entity, PointLight light)
    {
        RequireAction();
        _scene.SetPointLight(entity, light);
    }

    public void SetDirectionalLight(Entity entity, DirectionalLight light)
    {
        RequireAction();
        _scene.SetDirectionalLight(entity, light);
    }

    /// <summary>Records this frame and discards it before GPU submission.</summary>
    public void CancelFrame() { RequireAction(); FrameCanceled = true; }
    public void ResetCameraHistory() { RequireAction(); CameraCut = true; }
    public void RefreshShadowMaps() { RequireAction(); ShadowMapRefreshRequested = true; }

    public void DeleteEntity(Entity entity)
    {
        RequireAction();
        _scene.DeleteEntity(entity);
    }

    public void CaptureImage(Action<SceneImage> receive)
    {
        RequireAction();
        ArgumentNullException.ThrowIfNull(receive);
        _images.Add(receive);
    }

    /// <summary>Retains this submitted frame's linear material color and its matching scene inputs.</summary>
    public void CaptureLinearImage(Action<SceneLinearImage> receive)
    {
        RequireAction();
        ArgumentNullException.ThrowIfNull(receive);
        _linearImages.Add(receive);
    }

    private void RequireAction()
    {
        if (!_active) throw new InvalidOperationException("Scene editing requires an executing script action.");
    }
}
