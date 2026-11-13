using SomeEngine.ECS.Entities;

using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Features.Shadows;

public readonly record struct VirtualShadowLocalLight(VirtualShadowLightKind Kind, Entity Entity, uint DirectIndex = 0);

/// <summary>Schedules previously rendered distant lights by their successful update age.</summary>
public sealed class VirtualShadowDistantLightUpdates
{
    private readonly Dictionary<VirtualShadowLocalLight, uint> _lastScheduled = [];
    private readonly List<VirtualShadowLocalLight> _candidates = [];
    private readonly HashSet<VirtualShadowLocalLight> _activeLights = [];
    private readonly Comparison<VirtualShadowLocalLight> _ageComparison;
    private uint _renderedFrame;

    public VirtualShadowDistantLightUpdates() => _ageComparison = CompareUpdateAge;

    public VirtualShadowLocalLight[] Select(ReadOnlySpan<VirtualShadowLocalLight> cachedLights, int maximumUpdates)
    {
        _candidates.Clear();
        foreach (VirtualShadowLocalLight light in cachedLights) _candidates.Add(light);
        int count = SortCandidates(maximumUpdates);
        var result = new VirtualShadowLocalLight[count];
        _candidates.CopyTo(0, result, 0, count);
        return result;
    }

    internal void WriteUpdates(HashSet<VirtualShadowLocalLight> cachedLights, int maximumUpdates,
        HashSet<VirtualShadowLocalLight> destination)
    {
        _candidates.Clear();
        _candidates.AddRange(cachedLights);
        int count = SortCandidates(maximumUpdates);
        for (int index = 0; index < count; index++) destination.Add(_candidates[index]);
    }

    private int SortCandidates(int maximumUpdates)
    {
        _candidates.Sort(_ageComparison);
        return maximumUpdates < 0 ? _candidates.Count : Math.Min(maximumUpdates, _candidates.Count);
    }

    private int CompareUpdateAge(VirtualShadowLocalLight left, VirtualShadowLocalLight right)
    {
        uint frame = _renderedFrame + 1u;
        return (frame - _lastScheduled.GetValueOrDefault(right)).CompareTo(frame - _lastScheduled.GetValueOrDefault(left));
    }

    public void CompleteFrame(ReadOnlySpan<VirtualShadowLocalLight> activeLights, ReadOnlySpan<VirtualShadowLocalLight> updatedLights)
    {
        _renderedFrame++;
        _activeLights.Clear();
        foreach (VirtualShadowLocalLight light in activeLights) _activeLights.Add(light);
        RemoveDepartedLights();
        foreach (VirtualShadowLocalLight light in updatedLights) _lastScheduled[light] = _renderedFrame;
    }

    internal void CompleteFrame<TView>(Dictionary<VirtualShadowLocalLight, TView> activeLights,
        HashSet<VirtualShadowLocalLight> updatedLights)
    {
        _renderedFrame++;
        _activeLights.Clear();
        foreach (var light in activeLights) _activeLights.Add(light.Key);
        RemoveDepartedLights();
        foreach (VirtualShadowLocalLight light in updatedLights) _lastScheduled[light] = _renderedFrame;
    }

    private void RemoveDepartedLights()
    {
        foreach (VirtualShadowLocalLight light in _lastScheduled.Keys)
            if (!_activeLights.Contains(light)) _lastScheduled.Remove(light);
    }
}
