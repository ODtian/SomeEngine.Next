using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Resources;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Shadows;

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

    public uint GeometryMemoryBytes { get; init; } = 256u * 1024 * 1024;
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
public sealed record SceneImage(int Frame, int Width, int Height, ReadOnlyMemory<byte> Pixels)
{
    /// <summary>Saves the packed pixels as a top-down 32-bit bitmap.</summary>
    public void SaveBmp(string path)
    {
        using var writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write));
        int rowBytes = checked(Width * 4);
        int imageBytes = checked(rowBytes * Height);
        writer.Write((ushort)0x4D42);
        writer.Write(checked(54 + imageBytes));
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(Width);
        writer.Write(-Height);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(imageBytes);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        byte[] row = new byte[rowBytes];
        ReadOnlySpan<byte> pixels = Pixels.Span;
        for (int y = 0; y < Height; y++)
        {
            int offset = y * rowBytes;
            for (int x = 0; x < rowBytes; x += 4)
            {
                row[x] = pixels[offset + x + 2];
                row[x + 1] = pixels[offset + x + 1];
                row[x + 2] = pixels[offset + x];
                row[x + 3] = pixels[offset + x + 3];
            }
            writer.Write(row);
        }
    }
}

/// <summary>Scene operations available during a frame action.</summary>
public sealed class SceneEdit
{
    private readonly RuntimeScene _scene;
    private readonly NativeWindow _window;
    private readonly List<Action<SceneImage>> _images = [];
    private bool _active;

    internal SceneEdit(RuntimeScene scene, NativeWindow window) => (_scene, _window) = (scene, window);
    internal IReadOnlyList<Action<SceneImage>> Images => _images;
    internal bool FrameCanceled { get; private set; }
    internal bool CameraCut { get; private set; }
    internal bool ShadowMapRefreshRequested { get; private set; }
    internal void Begin()
    {
        _images.Clear();
        FrameCanceled = CameraCut = ShadowMapRefreshRequested = false;
        _active = true;
    }
    internal void End() => _active = false;

    public IReadOnlyList<Entity> MeshEntities { get { RequireAction(); return _scene.GetMeshEntities(); } }

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

    public Material GetMaterial(Entity entity, int index = 0)
    {
        RequireAction();
        return _scene.GetMaterial(entity, index);
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

    public void SetEntityTransform(Entity entity, TransformQvvs transform)
    {
        RequireAction();
        _scene.SetEntityTransform(entity, transform);
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

    private void RequireAction()
    {
        if (!_active) throw new InvalidOperationException("Scene editing requires an executing frame action.");
    }
}
