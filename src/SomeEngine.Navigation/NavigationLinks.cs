using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

/// <summary>A named port of a placed region. The region graph supplies its world-space endpoint.</summary>
public readonly record struct NavigationLinkEndpoint(int Region, int Port);

public readonly record struct NavigationLink(int Id, NavigationLinkEndpoint From, NavigationLinkEndpoint To,
    int Action, float Cost = 0, ulong RequiredCapabilities = 0, bool Bidirectional = false,
    Vector4 Parameters = default, float EntryRange = 0.05f, float ExitRange = 0.1f, bool Enabled = true);

/// <summary>Completed action-link definitions shared by the scene and the paths using them.</summary>
public sealed partial class NavigationLinkSet(ReadOnlyMemory<NavigationLink> links)
{
    public NavigationLinkSet(NavigationLink[] links) : this((ReadOnlyMemory<NavigationLink>)links) { }
    public ReadOnlyMemory<NavigationLink> Links { get; } = links;

    public NavigationLink GetLink(int id)
    {
        foreach (NavigationLink link in Links.Span)
            if (link.Id == id) return link;
        throw new KeyNotFoundException();
    }
}

/// <summary>A selected action edge and the detailed corners on each side of its handoff.</summary>
public readonly record struct NavigationPathTraversal(int Connection, int Action, int EntryCorner, int ExitCorner,
    Vector3 Entry, Vector3 Exit, Vector4 Parameters, float EntryRange, float ExitRange,
    ulong ContentVersion, NavigationLinkSet? Links = null);

public sealed partial class NavigationScene
{
    /// <summary>Replaces the scene's action-link definitions and advances changed connection versions.</summary>
    public JobHandle ScheduleLinks(NavigationLinkSet links, JobHandle dependency = default, Scheduler? scheduler = null)
    {
        scheduler ??= JobSystem.Default;
        return scheduler.Schedule(new ApplyLinks(this, links),
            [JobResourceAccess.Write(scheduler.GetResourceToken(this)), NavigationJobAccess.Read(scheduler, links.Links)], dependency);
    }

    private readonly struct ApplyLinks(NavigationScene scene, NavigationLinkSet links) : IJob
    {
        public void Execute()
        {
            NavigationSceneContent current = scene.Content;
            var connections = new List<NavigationSceneConnection>();
            foreach (NavigationSceneConnection connection in current.Connections.Span)
                if (connection.Links is null) connections.Add(connection);
            foreach (NavigationLink link in links.Links.Span)
            {
                if (!link.Enabled) continue;
                ulong version = (ulong)current.ContentVersion + 1;
                foreach (NavigationSceneConnection previous in current.Connections.Span)
                    if (previous.Id == link.Id && previous.Links is { } previousLinks && previousLinks.GetLink(link.Id) == link)
                    {
                        version = previous.ContentVersion;
                        break;
                    }
                connections.Add(new(link.Id, link.From.Region, link.From.Port, link.To.Region, link.To.Port,
                    link.Cost, link.RequiredCapabilities, link.Action, link.Bidirectional, version, links));
            }
            scene.ApplyCompleted(new(current.Regions, connections.ToArray(), current.Tiles));
        }
    }
}
