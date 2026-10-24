namespace SomeEngine.Navigation;

public readonly record struct NavigationAreaCost(int Area, float Multiplier);
public readonly record struct NavigationActionCost(int Action, float Cost);

/// <summary>Completed traversal costs. Positive infinity marks an unavailable area or action.</summary>
public sealed class NavigationPolicy(
    ReadOnlyMemory<NavigationAreaCost> areaCosts = default,
    ReadOnlyMemory<NavigationActionCost> actionCosts = default,
    float defaultAreaCost = 1f,
    ulong allowedCapabilities = ulong.MaxValue,
    NavigationCrowdPolicy? crowd = null)
{
    public NavigationPolicy(NavigationAreaCost[] areaCosts, NavigationActionCost[]? actionCosts = null,
        float defaultAreaCost = 1f, ulong allowedCapabilities = ulong.MaxValue, NavigationCrowdPolicy? crowd = null)
        : this((ReadOnlyMemory<NavigationAreaCost>)areaCosts,
            actionCosts is null ? ReadOnlyMemory<NavigationActionCost>.Empty : actionCosts,
            defaultAreaCost, allowedCapabilities, crowd) { }

    public ReadOnlyMemory<NavigationAreaCost> AreaCosts { get; } = areaCosts;
    public ReadOnlyMemory<NavigationActionCost> ActionCosts { get; } = actionCosts;
    public float DefaultAreaCost { get; } = defaultAreaCost;
    public ulong AllowedCapabilities { get; } = allowedCapabilities;
    public NavigationCrowdPolicy? Crowd { get; } = crowd;

    public float GetAreaCost(int area)
    {
        foreach (NavigationAreaCost cost in AreaCosts.Span)
            if (cost.Area == area)
                return cost.Multiplier;
        return DefaultAreaCost;
    }

    public float GetActionCost(int action)
    {
        foreach (NavigationActionCost cost in ActionCosts.Span)
            if (cost.Action == action)
                return cost.Cost;
        return 0;
    }
}
