using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Navigation;

/// <summary>The current action handoff and its monotonic request sequence, independent of the action consumer's execution state.</summary>
public readonly record struct NavigationTraversalIntent(NavigationTraversalRequest Request, ulong Sequence);

public static partial class NavigationComponents
{
    public static NavigationTraversalIntent CapturePathTraversal<TSpace, TState>(World world, Entity entity)
        where TSpace : class, INavigationSpace<TSpace> where TState : class =>
        new(world.Read<NavigationTraversalRequest>(entity), world.Read<NavigationRequest<TSpace, TState>>(entity).Data.TraversalSequence);

    public static NavigationTraversalIntent CaptureFlowTraversal(World world, Entity entity) =>
        new(world.Read<NavigationTraversalRequest>(entity), world.Read<NavigationFlowFollowState>(entity).TraversalSequence);

    /// <summary>Restores an action handoff; the next search starts at actual feedback after the consumer finishes at its exit.</summary>
    public static void RestorePathTraversal<TSpace, TState>(World world, Entity entity, NavigationTraversalIntent intent,
        NavigationLinkSet? links = null)
        where TSpace : class, INavigationSpace<TSpace> where TState : class
    {
        NavigationRequestData<TSpace, TState> data = world.Read<NavigationRequest<TSpace, TState>>(entity).Data;
        data.TraversalSequence = intent.Sequence;
        data.Version = intent.Request.PathVersion;
        data.Active = false;
        RestoreTraversal(world, entity, intent, links);
    }

    public static void RestoreFlowTraversal(World world, Entity entity, NavigationTraversalIntent intent, NavigationLinkSet? links = null)
    {
        world.Replace(entity, new NavigationFlowFollowState { TraversalSequence = intent.Sequence });
        RestoreTraversal(world, entity, intent, links);
    }

    private static void RestoreTraversal(World world, Entity entity, NavigationTraversalIntent intent, NavigationLinkSet? links)
    {
        world.Replace(entity, intent.Request);
        if (links is null || intent.Request.Status is not (NavigationTraversalStatus.Waiting or NavigationTraversalStatus.Requested or NavigationTraversalStatus.Executing)) return;
        foreach (NavigationLink link in links.Links.Span)
        {
            if (link.Id != intent.Request.Connection) continue;
            NavigationPassage passage = links.GetPassage(link.Id);
            world.Replace(entity, new NavigationPassageUse(passage, link.Capacity, intent.Request.ConnectionVersion));
            if (intent.Request.Status is NavigationTraversalStatus.Requested or NavigationTraversalStatus.Executing)
                passage.RestoreUser(new(entity, intent.Request.RequestId), link.Capacity, intent.Request.ConnectionVersion);
            break;
        }
    }
}
