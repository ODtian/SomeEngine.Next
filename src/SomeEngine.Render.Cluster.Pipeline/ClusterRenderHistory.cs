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
    private readonly TextureBoundaryState[] _hiZEndpoints = new TextureBoundaryState[2];
    private readonly TextureBoundaryState[] _sceneEndpoints = new TextureBoundaryState[2];
    private readonly TextureBoundaryState[] _motionEndpoints = new TextureBoundaryState[2];
    private readonly TextureBoundaryState[] _depthEndpoints = new TextureBoundaryState[2];
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
    internal bool PreviousContentsAvailable => _slotContentsAvailable[PreviousIndex];
    internal bool CurrentContentsAvailable => _slotContentsAvailable[_writeIndex];
    internal bool RequiresInitialization => !PreviousContentsAvailable;
    internal ReadOnlySpan<TextureBoundaryState> PreviousHiZEndpoints =>
        _hiZEndpoints.AsSpan(PreviousIndex, 1);
    internal ReadOnlySpan<TextureBoundaryState> CurrentHiZEndpoints =>
        _hiZEndpoints.AsSpan(_writeIndex, 1);
    internal ReadOnlySpan<TextureBoundaryState> PreviousSceneEndpoints =>
        _sceneEndpoints.AsSpan(PreviousIndex, 1);
    internal ReadOnlySpan<TextureBoundaryState> CurrentSceneEndpoints =>
        _sceneEndpoints.AsSpan(_writeIndex, 1);
    internal ReadOnlySpan<TextureBoundaryState> PreviousMotionEndpoints =>
        _motionEndpoints.AsSpan(PreviousIndex, 1);
    internal ReadOnlySpan<TextureBoundaryState> CurrentMotionEndpoints =>
        _motionEndpoints.AsSpan(_writeIndex, 1);
    internal ReadOnlySpan<TextureBoundaryState> PreviousDepthEndpoints =>
        _depthEndpoints.AsSpan(PreviousIndex, 1);
    internal ReadOnlySpan<TextureBoundaryState> CurrentDepthEndpoints =>
        _depthEndpoints.AsSpan(_writeIndex, 1);

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
        var hiZEndpoints = new TextureBoundaryState[2];
        var sceneEndpoints = new TextureBoundaryState[2];
        var motionEndpoints = new TextureBoundaryState[2];
        var depthEndpoints = new TextureBoundaryState[2];
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
            }

            for (int index = 0; index < 2; index++)
            {
                hiZEndpoints[index] = InitialEndpoint(hiZ[index], TextureAspects.Color);
                sceneEndpoints[index] = InitialEndpoint(scene[index], TextureAspects.Color);
                motionEndpoints[index] = InitialEndpoint(motion[index], TextureAspects.Color);
                depthEndpoints[index] = InitialEndpoint(depth[index], TextureAspects.Depth);
            }
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            for (int index = 1; index >= 0; index--)
            {
                TryDispose(depth[index], ref cleanupFailures);
                TryDispose(motion[index], ref cleanupFailures);
                TryDispose(scene[index], ref cleanupFailures);
                TryDispose(hiZ[index], ref cleanupFailures);
            }
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    "Cluster history resize failed and cleanup also reported failures.",
                    cleanupFailures);
            }
            throw;
        }

        DestroyTextures();
        hiZ.CopyTo(_hiZ, 0);
        scene.CopyTo(_scene, 0);
        motion.CopyTo(_motion, 0);
        depth.CopyTo(_depth, 0);
        hiZEndpoints.CopyTo(_hiZEndpoints, 0);
        sceneEndpoints.CopyTo(_sceneEndpoints, 0);
        motionEndpoints.CopyTo(_motionEndpoints, 0);
        depthEndpoints.CopyTo(_depthEndpoints, 0);
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
        QueueCompletion graphicsCompletion = FindGraphicsCompletion(completions);
        int writeIndex = _writeIndex;
        int previousIndex = PreviousIndex;
        TextureBoundaryState writeHiZ = ShaderReadEndpoint(
            _hiZ[writeIndex], TextureAspects.Color, graphicsCompletion);
        TextureBoundaryState writeScene = ShaderReadEndpoint(
            _scene[writeIndex], TextureAspects.Color, graphicsCompletion);
        TextureBoundaryState writeMotion = ShaderReadEndpoint(
            _motion[writeIndex], TextureAspects.Color, graphicsCompletion);
        TextureBoundaryState writeDepth = ShaderReadEndpoint(
            _depth[writeIndex], TextureAspects.Depth, graphicsCompletion);
        TextureBoundaryState previousHiZ = ShaderReadEndpoint(
            _hiZ[previousIndex], TextureAspects.Color, graphicsCompletion);
        TextureBoundaryState previousScene = ShaderReadEndpoint(
            _scene[previousIndex], TextureAspects.Color, graphicsCompletion);
        TextureBoundaryState previousMotion = ShaderReadEndpoint(
            _motion[previousIndex], TextureAspects.Color, graphicsCompletion);
        TextureBoundaryState previousDepth = ShaderReadEndpoint(
            _depth[previousIndex], TextureAspects.Depth, graphicsCompletion);

        _hiZEndpoints[writeIndex] = writeHiZ;
        _sceneEndpoints[writeIndex] = writeScene;
        _motionEndpoints[writeIndex] = writeMotion;
        _depthEndpoints[writeIndex] = writeDepth;
        _hiZEndpoints[previousIndex] = previousHiZ;
        _sceneEndpoints[previousIndex] = previousScene;
        _motionEndpoints[previousIndex] = previousMotion;
        _depthEndpoints[previousIndex] = previousDepth;
        _slotContentsAvailable[writeIndex] = true;
        _slotContentsAvailable[previousIndex] = true;
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

    private static TextureBoundaryState InitialEndpoint(Texture texture, TextureAspects aspects) =>
        new(
            FullRange(texture, aspects),
            texture.InitialSync,
            texture.InitialAccess,
            texture.InitialLayout,
            ResourceContentState.Undefined);

    private static TextureBoundaryState ShaderReadEndpoint(
        Texture texture,
        TextureAspects aspects,
        QueueCompletion completion) =>
        new(
            FullRange(texture, aspects),
            PipelineSync.AllShading,
            ResourceAccess.ShaderResource,
            TextureLayout.ShaderResource,
            ResourceContentState.Defined,
            completion.Queue,
            completion);

    private static TextureSubresourceRange FullRange(Texture texture, TextureAspects aspects) =>
        new(0, texture.Info.MipLevelCount, 0, texture.Info.ArrayLayerCount, aspects);

    private static QueueCompletion FindGraphicsCompletion(ReadOnlySpan<QueueCompletion> completions)
    {
        foreach (ref readonly QueueCompletion completion in completions)
        {
            if (completion.Queue.Type == QueueType.Graphics)
                return completion;
        }
        throw new InvalidOperationException("Cluster history requires a Graphics Queue completion.");
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

    private static void TryDispose(IDisposable? value, ref List<Exception>? failures)
    {
        if (value is null)
            return;
        try { value.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
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
        Array.Clear(_hiZEndpoints);
        Array.Clear(_sceneEndpoints);
        Array.Clear(_motionEndpoints);
        Array.Clear(_depthEndpoints);
        _slotContentsAvailable[0] = false;
        _slotContentsAvailable[1] = false;
        ClearPending();
        _previousView = default;
        _previousProjection = default;
        _hasPreviousView = false;
        _disposed = true;
    }
}
