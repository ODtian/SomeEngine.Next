using SomeEngine.ECS.Entities;

namespace SomeEngine.Render.Shadows;

public readonly record struct VirtualShadowLocalLight(VirtualShadowLightKind Kind, Entity Entity, uint DirectIndex = 0);

/// <summary>Schedules previously rendered distant lights by their successful update age.</summary>
public sealed class VirtualShadowDistantLightUpdates
{
    private readonly Dictionary<VirtualShadowLocalLight, uint> _lastScheduled = [];
    private uint _renderedFrame;

    public VirtualShadowLocalLight[] Select(ReadOnlySpan<VirtualShadowLocalLight> cachedLights, int maximumUpdates)
    {
        var candidates = cachedLights.ToArray();
        uint frame = _renderedFrame + 1u;
        Array.Sort(candidates, (left, right) =>
            (frame - _lastScheduled.GetValueOrDefault(right)).CompareTo(frame - _lastScheduled.GetValueOrDefault(left)));
        int count = maximumUpdates < 0 ? candidates.Length : Math.Min(maximumUpdates, candidates.Length);
        return candidates.AsSpan(0, count).ToArray();
    }

    public void CompleteFrame(ReadOnlySpan<VirtualShadowLocalLight> activeLights, ReadOnlySpan<VirtualShadowLocalLight> updatedLights)
    {
        _renderedFrame++;
        var active = new HashSet<VirtualShadowLocalLight>(activeLights.ToArray());
        foreach (VirtualShadowLocalLight light in _lastScheduled.Keys.ToArray())
            if (!active.Contains(light)) _lastScheduled.Remove(light);
        foreach (VirtualShadowLocalLight light in updatedLights) _lastScheduled[light] = _renderedFrame;
    }
}
