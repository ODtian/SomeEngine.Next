using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

/// <summary>A named port of a placed region. The region graph supplies its world-space endpoint.</summary>
public readonly record struct NavigationLinkEndpoint(int Region, int Port);

public readonly record struct NavigationLink(int Id, NavigationLinkEndpoint From, NavigationLinkEndpoint To,
    int Action, float Cost = 0, ulong RequiredCapabilities = 0, bool Bidirectional = false,
    Vector4 Parameters = default, float EntryRange = 0.05f, float ExitRange = 0.1f, bool Enabled = true,
    int Capacity = int.MaxValue);

/// <summary>Completed action-link definitions shared by the scene and the paths using them.</summary>
public sealed partial class NavigationLinkSet
{
    private readonly Dictionary<int, NavigationPassage> _passages;

    public NavigationLinkSet(ReadOnlyMemory<NavigationLink> links)
    {
        Links = links;
        _passages = new();
        foreach (NavigationLink link in links.Span) _passages.Add(link.Id, new(link.Capacity));
    }
    public NavigationLinkSet(NavigationLink[] links) : this((ReadOnlyMemory<NavigationLink>)links) { }
    private NavigationLinkSet(ReadOnlyMemory<NavigationLink> links, Dictionary<int, NavigationPassage> passages)
    {
        Links = links;
        _passages = passages;
    }
    public ReadOnlyMemory<NavigationLink> Links { get; }
    public NavigationPassage GetPassage(int connection) => _passages[connection];

    internal NavigationLinkSet SharePassages(NavigationLinkSet? previous)
    {
        if (previous is null) return this;
        var passages = new Dictionary<int, NavigationPassage>(_passages);
        foreach (NavigationLink link in Links.Span)
            if (previous._passages.TryGetValue(link.Id, out var passage)) passages[link.Id] = passage;
        return new(Links, passages);
    }

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
        return scheduler.Schedule(new ApplyLinks(this, links, scheduler),
            [JobResourceAccess.Write(scheduler.GetResourceToken(this)), NavigationJobAccess.Read(scheduler, links.Links)], dependency);
    }

    private readonly struct ApplyLinks(NavigationScene scene, NavigationLinkSet links, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            NavigationSceneContent current = scene.Content;
            NavigationLinkSet installed = links.SharePassages(current.Links);
            var connections = new List<NavigationSceneConnection>();
            foreach (NavigationSceneConnection connection in current.Connections.Span)
                if (connection.Links is null) connections.Add(connection);
            foreach (NavigationLink link in links.Links.Span)
            {
                ulong version = (ulong)current.ContentVersion + 1;
                foreach (NavigationSceneConnection previous in current.Connections.Span)
                    if (previous.Id == link.Id && previous.Links is { } previousLinks && previousLinks.GetLink(link.Id) == link)
                    {
                        version = previous.ContentVersion;
                        break;
                    }
                NavigationPassage passage = installed.GetPassage(link.Id);
                scheduler.Schedule(new SetPassageCapacity(passage, link.Capacity, version),
                    [JobResourceAccess.Write(scheduler.GetResourceToken(passage))]);
                if (!link.Enabled) continue;
                connections.Add(new(link.Id, link.From.Region, link.From.Port, link.To.Region, link.To.Port,
                    link.Cost, link.RequiredCapabilities, link.Action, link.Bidirectional, version, installed));
            }
            scene.ApplyCompleted(new(current.Regions, connections.ToArray(), current.Tiles, links: installed));
        }
    }

    private readonly struct SetPassageCapacity(NavigationPassage passage, int capacity, ulong version) : IJob
    {
        public void Execute() => passage.SetCapacity(capacity, version);
    }
}
