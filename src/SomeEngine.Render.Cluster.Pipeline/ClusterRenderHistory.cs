using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Owns the double-buffered temporal state for the full Cluster algorithm: textures, rotation,
/// camera state, resize/camera-cut content invalidation, and per-slot GPU readiness.
/// </summary>
internal sealed class ClusterRenderHistory : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Texture[] _hiZ = new Texture[2];
    private readonly Texture[] _scene = new Texture[2];
    private readonly Texture[] _motion = new Texture[2];
    private readonly Texture[] _depth = new Texture[2];
    private readonly QueueCompletion[][] _readiness = [[], []];
    private readonly GraphResourceUsage[] _hiZStates = new GraphResourceUsage[2];
    private readonly GraphResourceUsage[] _sceneStates = new GraphResourceUsage[2];
    private readonly GraphResourceUsage[] _motionStates = new GraphResourceUsage[2];
    private readonly GraphResourceUsage[] _depthStates = new GraphResourceUsage[2];
    private readonly bool[] _slotContentsAvailable = new bool[2];
    private bool _disposed;
    private bool _hasPreviousView;
    private bool _pending;
    private int _writeIndex;
    private int _width;
    private int _height;
    private int _hiZMips;
    private Matrix4x4 _previousView;
    private Matrix4x4 _previousProjection;
    private Matrix4x4 _pendingView;
    private Matrix4x4 _pendingProjection;

    internal ClusterRenderHistory(IGraphicsBackend backend, Device device)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    /// <summary>True when a prior committed camera exists and content has not been cut/resized away.</summary>
    internal bool HasPreviousFrame => _hasPreviousView;
    internal int HiZMipCount => _hiZMips;
    internal Matrix4x4 PreviousView => _previousView;
    internal Matrix4x4 PreviousProjection => _previousProjection;
    internal Texture PreviousHiZ => _hiZ[PreviousIndex];
    internal Texture CurrentHiZ => _hiZ[_writeIndex];
    internal Texture PreviousScene => _scene[PreviousIndex];
    internal Texture CurrentScene => _scene[_writeIndex];
    internal Texture PreviousMotion => _motion[PreviousIndex];
    internal Texture CurrentMotion => _motion[_writeIndex];
    internal Texture PreviousDepth => _depth[PreviousIndex];
    internal Texture CurrentDepth => _depth[_writeIndex];
    internal QueueCompletion[] PreviousReadiness => _readiness[PreviousIndex];
    internal QueueCompletion[] CurrentReadiness => _readiness[_writeIndex];
    internal bool PreviousContentsAvailable => _slotContentsAvailable[PreviousIndex];
    internal bool CurrentContentsAvailable => _slotContentsAvailable[_writeIndex];
    internal bool RequiresInitialization => !PreviousContentsAvailable;
    internal GraphResourceUsage PreviousHiZState => _hiZStates[PreviousIndex];
    internal GraphResourceUsage CurrentHiZState => _hiZStates[_writeIndex];
    internal GraphResourceUsage PreviousSceneState => _sceneStates[PreviousIndex];
    internal GraphResourceUsage CurrentSceneState => _sceneStates[_writeIndex];
    internal GraphResourceUsage PreviousMotionState => _motionStates[PreviousIndex];
    internal GraphResourceUsage CurrentMotionState => _motionStates[_writeIndex];
    internal GraphResourceUsage PreviousDepthState => _depthStates[PreviousIndex];
    internal GraphResourceUsage CurrentDepthState => _depthStates[_writeIndex];

    private int PreviousIndex => 1 - _writeIndex;

    /// <summary>
    /// Selects the pending write slot for this frame. Does not submit GPU work: resize only allocates
    /// textures, and camera-cut only invalidates published content while preserving slot readiness.
    /// </summary>
    internal bool Prepare(
        int width,
        int height,
        in RenderView view)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending)
            throw new InvalidOperationException("The previous Cluster history frame is still pending.");
        EnsureDimensions(width, height);
        if (view.CameraCut)
        {
            _hasPreviousView = false;
            _previousView = default;
            _previousProjection = default;
        }

        _pendingView = view.View;
        _pendingProjection = view.Projection;
        _pending = true;
        return HasPreviousFrame;
    }

    private void EnsureDimensions(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (_width == width && _height == height)
            return;

        int hiZMips = CalculateMipCount(width, height);
        var hiZ = new Texture[2];
        var scene = new Texture[2];
        var motion = new Texture[2];
        var depth = new Texture[2];
        var created = new List<Texture>();
        try
        {
            for (int index = 0; index < 2; index++)
            {
                hiZ[index] = Create(new TextureDesc(
                    TextureDimension.Texture2D,
                    checked((uint)width),
                    checked((uint)height),
                    1,
                    checked((uint)hiZMips),
                    1,
                    1,
                    Format.R32Float,
                    TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopySource,
                    label: $"Cluster HiZ history {index}"));
                created.Add(hiZ[index]);

                scene[index] = Create(new TextureDesc(
                    TextureDimension.Texture2D,
                    checked((uint)width),
                    checked((uint)height),
                    1,
                    1,
                    1,
                    1,
                    Format.R16G16B16A16Float,
                    TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopyDestination,
                    label: $"Cluster scene history {index}"));
                created.Add(scene[index]);

                motion[index] = Create(new TextureDesc(
                    TextureDimension.Texture2D,
                    checked((uint)width),
                    checked((uint)height),
                    1,
                    1,
                    1,
                    1,
                    Format.R16G16Float,
                    TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopyDestination,
                    label: $"Cluster motion history {index}"));
                created.Add(motion[index]);

                depth[index] = Create(new TextureDesc(
                    TextureDimension.Texture2D,
                    checked((uint)width),
                    checked((uint)height),
                    1,
                    1,
                    1,
                    1,
                    Format.D32Float,
                    TextureUsages.Sampled | TextureUsages.DepthStencilAttachment | TextureUsages.CopyDestination,
                    label: $"Cluster depth history {index}"));
                created.Add(depth[index]);
            }
        }
        catch
        {
            foreach (Texture handle in created)
            {
                handle.Dispose();
            }
            throw;
        }

        DestroyTextures();
        hiZ.CopyTo(_hiZ, 0);
        scene.CopyTo(_scene, 0);
        motion.CopyTo(_motion, 0);
        depth.CopyTo(_depth, 0);
        _readiness[0] = [];
        _readiness[1] = [];
        Array.Fill(_hiZStates, GraphResourceUsage.Common);
        Array.Fill(_sceneStates, GraphResourceUsage.Common);
        Array.Fill(_motionStates, GraphResourceUsage.Common);
        Array.Fill(_depthStates, GraphResourceUsage.Common);
        _slotContentsAvailable[0] = false;
        _slotContentsAvailable[1] = false;
        _width = width;
        _height = height;
        _hiZMips = hiZMips;
        _writeIndex = 0;
        _hasPreviousView = false;
        _previousView = default;
        _previousProjection = default;
    }

    /// <summary>
    /// Publishes camera state and rotates slots only after a successful graph execute. Stores the
    /// frame completions as the physical readiness of both slots touched by the graph.
    /// </summary>
    internal void Commit(ReadOnlySpan<QueueCompletion> completions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_width == 0)
            throw new InvalidOperationException("Cluster history was not initialized.");
        if (!_pending)
            throw new InvalidOperationException("No authored Cluster history frame is waiting for commit.");
        QueueCompletion[] fences = completions.ToArray();
        int previousIndex = PreviousIndex;
        _readiness[_writeIndex] = fences;
        _readiness[previousIndex] = fences.ToArray();
        PublishSlot(_writeIndex);
        PublishSlot(previousIndex);
        _previousView = _pendingView;
        _previousProjection = _pendingProjection;
        _hasPreviousView = true;
        _writeIndex = 1 - _writeIndex;
        ClearPending();
    }

    /// <summary>Drops pending camera without rotating slots or publishing readiness.</summary>
    internal void Discard()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending)
            ClearPending();
    }

    private void ClearPending()
    {
        _pendingView = default;
        _pendingProjection = default;
        _pending = false;
    }

    private void PublishSlot(int slot)
    {
        _hiZStates[slot] = GraphResourceUsage.ShaderResource;
        _sceneStates[slot] = GraphResourceUsage.ShaderResource;
        _motionStates[slot] = GraphResourceUsage.ShaderResource;
        _depthStates[slot] = GraphResourceUsage.ShaderResource;
        _slotContentsAvailable[slot] = true;
    }

    private Texture Create(in TextureDesc description)
        => _backend.CreateTexture(_device, description);

    internal static int CalculateMipCount(int width, int height)
        => 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));

    private void DestroyTextures()
    {
        for (int index = 0; index < 2; index++)
        {
            Destroy(_hiZ[index]);
            Destroy(_scene[index]);
            Destroy(_motion[index]);
            Destroy(_depth[index]);
        }
    }

    private static void Destroy(Texture? handle)
    {
        handle?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        DestroyTextures();
        Array.Clear(_hiZ);
        Array.Clear(_scene);
        Array.Clear(_motion);
        Array.Clear(_depth);
        Array.Clear(_readiness);
        Array.Fill(_hiZStates, GraphResourceUsage.Common);
        Array.Fill(_sceneStates, GraphResourceUsage.Common);
        Array.Fill(_motionStates, GraphResourceUsage.Common);
        Array.Fill(_depthStates, GraphResourceUsage.Common);
        _slotContentsAvailable[0] = false;
        _slotContentsAvailable[1] = false;
        ClearPending();
        _previousView = default;
        _previousProjection = default;
        _hasPreviousView = false;
        _disposed = true;
    }
}
