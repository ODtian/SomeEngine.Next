using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.RenderGraph;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Owns only the double-buffered HiZ and camera state required by Cluster culling.
/// </summary>
internal sealed class ClusterCullingHistory : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly uint _viewCount;
    private readonly Texture[] _hiZ = new Texture[2];
    private readonly TextureBoundaryState[][] _hiZEndpoints = new TextureBoundaryState[2][];
    private readonly bool[] _slotContentsAvailable = new bool[2];
    private bool _disposed;
    private readonly bool[] _hasPreviousView;
    private readonly bool[] _usePreviousView;
    private readonly Matrix4x4[] _previousViews;
    private readonly Matrix4x4[] _previousProjections;
    private readonly Matrix4x4[] _pendingViews;
    private readonly Matrix4x4[] _pendingProjections;
    private readonly Entity[] _previousViewEntities;
    private readonly Entity[] _pendingViewEntities;
    private bool _pending;
    private int _pendingViewCount;
    private int _writeIndex;
    private int _width;
    private int _height;
    private int _hiZMips;

    internal ClusterCullingHistory(
        IGraphicsBackend backend,
        Device device,
        uint viewCount)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentOutOfRangeException.ThrowIfZero(viewCount);
        _viewCount = viewCount;
        int count = checked((int)viewCount);
        _hasPreviousView = new bool[count];
        _usePreviousView = new bool[count];
        _previousViews = new Matrix4x4[count];
        _previousProjections = new Matrix4x4[count];
        _pendingViews = new Matrix4x4[count];
        _pendingProjections = new Matrix4x4[count];
        _previousViewEntities = new Entity[count];
        _pendingViewEntities = new Entity[count];
    }

    internal int HiZMipCount => _hiZMips;
    internal Texture PreviousHiZ => _hiZ[PreviousIndex];
    internal Texture CurrentHiZ => _hiZ[_writeIndex];
    internal bool PreviousContentsAvailable => _slotContentsAvailable[PreviousIndex];
    internal bool CurrentContentsAvailable => _slotContentsAvailable[_writeIndex];
    internal bool RequiresInitialization => !PreviousContentsAvailable;
    internal ReadOnlySpan<TextureBoundaryState> PreviousHiZEndpoints =>
        _hiZEndpoints[PreviousIndex];
    internal ReadOnlySpan<TextureBoundaryState> CurrentHiZEndpoints =>
        _hiZEndpoints[_writeIndex];

    private int PreviousIndex => 1 - _writeIndex;

    /// <summary>
    /// Selects the pending write slot for this frame. Does not submit GPU work: resize only allocates
    /// textures, and camera-cut only invalidates published content while preserving slot readiness.
    /// </summary>
    internal void Prepare(
        int width,
        int height,
        ReadOnlySpan<RenderView> views,
        ReadOnlySpan<Entity> identities)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending)
            throw new InvalidOperationException("The previous Cluster history frame is still pending.");
        if (views.IsEmpty || views.Length > _hasPreviousView.Length)
            throw new ArgumentOutOfRangeException(nameof(views));
        if (identities.Length != views.Length)
            throw new ArgumentException("Each history view requires its corresponding identity.", nameof(identities));
        EnsureDimensions(width, height);
        for (int index = 0; index < views.Length; index++)
        {
            ref readonly RenderView view = ref views[index];
            _usePreviousView[index] = _hasPreviousView[index] && !view.CameraCut &&
                identities[index] != Entity.Null && identities[index] == _previousViewEntities[index];

            _pendingViews[index] = view.View;
            _pendingProjections[index] = view.Projection;
            _pendingViewEntities[index] = identities[index];
        }
        for (int index = views.Length; index < _hasPreviousView.Length; index++)
            _usePreviousView[index] = false;
        _pendingViewCount = views.Length;
        _pending = true;
    }

    internal bool TryGetPrevious(
        int viewIndex,
        out Matrix4x4 view,
        out Matrix4x4 projection)
    {
        if ((uint)viewIndex >= (uint)_hasPreviousView.Length ||
            !(_pending ? _usePreviousView[viewIndex] : _hasPreviousView[viewIndex]))
        {
            view = default;
            projection = default;
            return false;
        }
        view = _previousViews[viewIndex];
        projection = _previousProjections[viewIndex];
        return true;
    }

    internal void EnsureDimensions(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (_width == width && _height == height)
            return;

        int hiZMips = CalculateMipCount(width, height);
        var hiZ = new Texture[2];
        var hiZEndpoints = new TextureBoundaryState[2][];
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
                    _viewCount,
                    1,
                    Format.R32Float,
                    TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopySource,
                    label: $"Cluster HiZ history {index}"));

            }

            for (int index = 0; index < 2; index++)
                hiZEndpoints[index] = [InitialEndpoint(hiZ[index], TextureAspects.Color)];
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            for (int index = 1; index >= 0; index--)
                TryDispose(hiZ[index], ref cleanupFailures);
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
        hiZEndpoints.CopyTo(_hiZEndpoints, 0);
        _slotContentsAvailable[0] = false;
        _slotContentsAvailable[1] = false;
        _width = width;
        _height = height;
        _hiZMips = hiZMips;
        _writeIndex = 0;
        Array.Clear(_hasPreviousView);
        Array.Clear(_previousViews);
        Array.Clear(_previousProjections);
    }

    /// <summary>
    /// Retains actual resource states and advances camera history only after its final pass.
    /// </summary>
    internal void FinishFrame(
        RenderGraphFrameResult result,
        GraphTextureId previousHiZ,
        GraphTextureId currentHiZ,
        bool currentHiZWritten)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_width == 0)
            throw new InvalidOperationException("Cluster history was not initialized.");
        if (!_pending)
            throw new InvalidOperationException("No authored Cluster history frame is waiting for commit.");
        int writeIndex = _writeIndex;
        int previousIndex = PreviousIndex;
        ReadOnlySpan<TextureBoundaryState> previousStates = result.GetTextureStates(previousHiZ);
        ReadOnlySpan<TextureBoundaryState> currentStates = result.GetTextureStates(currentHiZ);
        if (!previousStates.IsEmpty)
            _hiZEndpoints[previousIndex] = previousStates.ToArray();
        if (!currentStates.IsEmpty)
            _hiZEndpoints[writeIndex] = currentStates.ToArray();
        if (!currentHiZWritten)
        {
            if (!result.SubmittedCompletions.IsEmpty) _slotContentsAvailable[writeIndex] = false;
            ClearPending();
            return;
        }
        _slotContentsAvailable[previousIndex] = true;
        _slotContentsAvailable[writeIndex] = true;
        for (int index = 0; index < _pendingViewCount; index++)
        {
            _previousViews[index] = _pendingViews[index];
            _previousProjections[index] = _pendingProjections[index];
            _previousViewEntities[index] = _pendingViewEntities[index];
            _hasPreviousView[index] = _pendingViewEntities[index] != Entity.Null;
        }
        for (int index = _pendingViewCount; index < _hasPreviousView.Length; index++)
            _hasPreviousView[index] = false;
        if (currentHiZWritten)
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
        Array.Clear(_pendingViews);
        Array.Clear(_pendingProjections);
        Array.Clear(_pendingViewEntities);
        Array.Clear(_usePreviousView);
        _pendingViewCount = 0;
        _pending = false;
    }

    private static TextureBoundaryState InitialEndpoint(Texture texture, TextureAspects aspects) =>
        new(
            FullRange(texture, aspects),
            texture.InitialSync,
            texture.InitialAccess,
            texture.InitialLayout,
            ResourceContentState.Undefined);

    private static TextureSubresourceRange FullRange(Texture texture, TextureAspects aspects) =>
        new(0, texture.Info.MipLevelCount, 0, texture.Info.ArrayLayerCount, aspects);

    private Texture Create(in TextureDesc description)
        => _backend.CreateTexture(_device, description);

    internal static int CalculateMipCount(int width, int height)
        => 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));

    private void DestroyTextures()
    {
        for (int index = 0; index < 2; index++)
            Destroy(_hiZ[index]);
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
        Array.Clear(_hiZEndpoints);
        _slotContentsAvailable[0] = false;
        _slotContentsAvailable[1] = false;
        ClearPending();
        Array.Clear(_hasPreviousView);
        Array.Clear(_previousViews);
        Array.Clear(_previousProjections);
        _disposed = true;
    }
}
