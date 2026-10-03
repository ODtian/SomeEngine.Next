using System.Numerics;
using SomeEngine.Render.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Retains camera matrices and selects the two graph textures used by Cluster culling.
/// </summary>
internal sealed class ClusterCullingHistory
{
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

    internal ClusterCullingHistory(uint viewCount)
    {
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

    internal int WriteIndex => _writeIndex;
    internal bool RequiresInitialization { get; private set; } = true;

    internal void Prepare(
        ReadOnlySpan<RenderView> views,
        ReadOnlySpan<Entity> identities)
    {
        if (_pending)
            throw new InvalidOperationException("The previous Cluster history frame is still pending.");
        if (views.IsEmpty || views.Length > _hasPreviousView.Length)
            throw new ArgumentOutOfRangeException(nameof(views));
        if (identities.Length != views.Length)
            throw new ArgumentException("Each history view requires its corresponding identity.", nameof(identities));
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

    internal void FinishFrame(bool currentHiZWritten)
    {
        if (!currentHiZWritten) { ClearPending(); return; }
        RequiresInitialization = false;
        for (int index = 0; index < _pendingViewCount; index++)
        {
            _previousViews[index] = _pendingViews[index];
            _previousProjections[index] = _pendingProjections[index];
            _previousViewEntities[index] = _pendingViewEntities[index];
            _hasPreviousView[index] = _pendingViewEntities[index] != Entity.Null;
        }
        for (int index = _pendingViewCount; index < _hasPreviousView.Length; index++)
            _hasPreviousView[index] = false;
        _writeIndex = 1 - _writeIndex;
        ClearPending();
    }

    /// <summary>Discards the staged camera matrices.</summary>
    internal void Discard()
    {
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

    internal static int CalculateMipCount(int width, int height)
        => 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
}
