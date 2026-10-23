using System.Numerics;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public readonly record struct HeadingNavigationFlowPort(int Id, Vector3 Position);
public sealed record HeadingNavigationFlowRegion(int Id, HeadingNavigationMesh Mesh, ReadOnlyMemory<HeadingNavigationFlowPort> Ports);
public readonly record struct HeadingNavigationFlowNode(int Region, int Polygon, int Channel, NavigationPose Pose);
public enum NavigationFlowAction : byte { Move, Rotate, Traversal, Arrived, Unreachable }
public readonly record struct HeadingNavigationFlowTransition(NavigationFlowAction Action, NavigationPathTraversal? Traversal = null);
public readonly record struct NavigationFlowSample(NavigationFlowAction Action, NavigationPose Target,
    int State = -1, int Edge = -1, NavigationPathTraversal? Traversal = null);

/// <summary>The heading representation's directed states and action choices, with a cost graph for shared integration.</summary>
public sealed class HeadingNavigationFlowGraph
{
    private readonly Dictionary<int, HeadingNavigationFlowRegion> _regions;

    internal HeadingNavigationFlowGraph(ReadOnlyMemory<HeadingNavigationFlowRegion> regions, NavigationLinkSet links,
        ReadOnlyMemory<HeadingNavigationFlowNode> nodes, ReadOnlyMemory<HeadingNavigationFlowTransition> transitions,
        NavigationCosts costs)
    {
        Regions = regions;
        Links = links;
        Nodes = nodes;
        Transitions = transitions;
        Costs = costs;
        _regions = regions.ToArray().ToDictionary(region => region.Id);
    }

    public ReadOnlyMemory<HeadingNavigationFlowRegion> Regions { get; }
    public NavigationLinkSet Links { get; }
    public ReadOnlyMemory<HeadingNavigationFlowNode> Nodes { get; }
    public ReadOnlyMemory<HeadingNavigationFlowTransition> Transitions { get; }
    public NavigationCosts Costs { get; }

    internal HeadingNavigationFlowGraph SharePassages(HeadingNavigationFlowGraph? previous)
    {
        if (previous is null || ReferenceEquals(Links, previous.Links)) return this;
        NavigationLinkSet links = Links.SharePassages(previous.Links);
        var transitions = Transitions.ToArray();
        for (int index = 0; index < transitions.Length; index++)
            if (transitions[index].Traversal is { Links: not null } traversal)
                transitions[index] = transitions[index] with { Traversal = traversal with { Links = links } };
        return new(Regions, links, Nodes, transitions, Costs);
    }

    internal NavigationTileCoordinate? FindTile(Vector3 position)
    {
        foreach (HeadingNavigationFlowRegion region in Regions.Span)
            if (region.Mesh.FindRegion(position) >= 0) return new(region.Id, 0);
        return null;
    }

    internal bool CanApproach(int state, NavigationPose pose)
    {
        HeadingNavigationFlowNode node = Nodes.Span[state];
        HeadingNavigationMesh mesh = _regions[node.Region].Mesh;
        return mesh.GetChannel(node.Channel).Contains(pose.Heading) &&
            mesh.Geometry.TrySampleHeight(node.Polygon, pose.Position, out float height) && MathF.Abs(height - pose.Position.Y) < 0.1f;
    }

    internal float TargetCost(int state, NavigationFlowGoal goal, NavigationProfile profile, NavigationPolicy policy)
    {
        HeadingNavigationFlowNode node = Nodes.Span[state];
        HeadingNavigationMesh mesh = _regions[node.Region].Mesh;
        if (!mesh.Geometry.TrySampleHeight(node.Polygon, goal.Position, out float height) ||
            MathF.Abs(height - goal.Position.Y) > 0.1f ||
            goal.Heading is { } heading && !mesh.GetChannel(node.Channel).Contains(heading)) return float.PositiveInfinity;
        float distance = MathF.Max(0, Vector3.Distance(node.Pose.Position, goal.Position) - goal.ArrivalRange);
        float rotation = goal.Heading is { } target ? MathF.Abs(NavigationHeading.Difference(target, node.Pose.Heading)) : 0;
        return distance / profile.MaximumSpeed * policy.GetAreaCost(mesh.Geometry.Polygons.Span[node.Polygon].Area) +
            rotation / profile.MaximumAngularSpeed;
    }

    internal int FindState(NavigationFlowField field, NavigationPose pose)
    {
        int selected = -1;
        float best = float.PositiveInfinity;
        for (int state = 0; state < Nodes.Length; state++)
        {
            if (!CanApproach(state, pose)) continue;
            HeadingNavigationFlowNode node = Nodes.Span[state];
            float distance = Vector3.Distance(node.Pose.Position, pose.Position);
            float cost = field.CumulativeCosts.Span[state] + distance / field.Input.Profile.MaximumSpeed +
                MathF.Abs(NavigationHeading.Difference(node.Pose.Heading, pose.Heading)) / field.Input.Profile.MaximumAngularSpeed;
            if (selected >= 0 && cost >= best) continue;
            selected = state;
            best = cost;
        }
        return selected;
    }

    internal NavigationFlowSample Sample(NavigationFlowField field, NavigationPose pose, ref int state)
    {
        if (state < 0 || !float.IsFinite(field.CumulativeCosts.Span[state])) return new(NavigationFlowAction.Unreachable, pose);
        while (true)
        {
            HeadingNavigationFlowNode node = Nodes.Span[state];
            int edge = field.SuccessorEdges.Span[state];
            if (edge < 0)
            {
                NavigationFlowGoal goal = field.Input.Goals.Span[field.GoalIndices.Span[state]];
                float distance = Vector3.Distance(pose.Position, goal.Position);
                float targetHeading = distance <= goal.ArrivalRange + 0.0001f ? goal.Heading ?? node.Pose.Heading : node.Pose.Heading;
                var target = new NavigationPose(goal.Position, targetHeading);
                if (MathF.Abs(NavigationHeading.Difference(targetHeading, pose.Heading)) > 0.001f)
                    return new(NavigationFlowAction.Rotate, new(pose.Position, targetHeading), state);
                return new(distance <= goal.ArrivalRange + 0.0001f ? NavigationFlowAction.Arrived : NavigationFlowAction.Move,
                    target, state);
            }
            if (MathF.Abs(NavigationHeading.Difference(node.Pose.Heading, pose.Heading)) > 0.001f)
                return new(NavigationFlowAction.Rotate, new(pose.Position, node.Pose.Heading), state);
            if (Vector3.DistanceSquared(pose.Position, node.Pose.Position) > 0.000001f)
                return new(NavigationFlowAction.Move, node.Pose, state);
            int destination = Costs.Grid.Edges.Span[edge].ToCell;
            NavigationPose next = Nodes.Span[destination].Pose;
            HeadingNavigationFlowTransition transition = Transitions.Span[edge];
            if (transition.Action == NavigationFlowAction.Traversal)
                return new(transition.Action, next, state, edge, transition.Traversal);
            if (Vector3.DistanceSquared(pose.Position, next.Position) <= 0.000001f &&
                (transition.Action == NavigationFlowAction.Move || MathF.Abs(NavigationHeading.Difference(next.Heading, pose.Heading)) <= 0.001f))
            {
                state = destination;
                continue;
            }
            return new(transition.Action, transition.Action == NavigationFlowAction.Move ? next with { Heading = node.Pose.Heading } : next, state, edge);
        }
    }

    internal static void CollectResources(HeadingNavigationFlowGraph graph, ref JobResourceAccessBuilder resources)
    {
        resources.Read(graph.Regions);
        resources.Read(graph.Nodes);
        resources.Read(graph.Transitions);
        resources.Read(graph.Links.Links);
        foreach (HeadingNavigationFlowRegion region in graph.Regions.Span)
        {
            HeadingNavigationMesh.CollectResources(region.Mesh, ref resources);
            resources.Read(region.Ports);
        }
    }

    internal void AddReadAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        accesses.Add(NavigationJobAccess.Read(scheduler, Regions));
        accesses.Add(NavigationJobAccess.Read(scheduler, Nodes));
        accesses.Add(NavigationJobAccess.Read(scheduler, Transitions));
        accesses.Add(NavigationJobAccess.Read(scheduler, Links.Links));
        foreach (HeadingNavigationFlowRegion region in Regions.Span)
        {
            region.Mesh.AddReadAccesses(scheduler, accesses);
            accesses.Add(NavigationJobAccess.Read(scheduler, region.Ports));
        }
    }
}

public sealed class HeadingNavigationFlowBuildState
{
    public HeadingNavigationFlowGraph Graph { get; internal set; } = null!;
}

/// <summary>Projects completed heading-region graphs and typed links into directed integration ranges.</summary>
public static class HeadingNavigationFlowBuilder
{
    public static NavigationBuild<HeadingNavigationFlowBuildState> Schedule(ReadOnlyMemory<HeadingNavigationFlowRegion> regions,
        NavigationLinkSet links, NavigationProfile profile, NavigationPolicy policy, ulong version = 1,
        JobHandle dependency = default, Scheduler? scheduler = null)
    {
        scheduler ??= JobSystem.Default;
        var state = new HeadingNavigationFlowBuildState();
        var accesses = new List<JobResourceAccess>
        {
            NavigationJobAccess.Read(scheduler, regions), NavigationJobAccess.Read(scheduler, links.Links),
            NavigationJobAccess.Read(scheduler, profile.Footprint), NavigationJobAccess.Read(scheduler, policy.AreaCosts),
            NavigationJobAccess.Read(scheduler, policy.ActionCosts), JobResourceAccess.Write(scheduler.GetResourceToken(state)),
        };
        foreach (HeadingNavigationFlowRegion region in regions.Span)
        {
            region.Mesh.AddReadAccesses(scheduler, accesses);
            accesses.Add(NavigationJobAccess.Read(scheduler, region.Ports));
        }
        return new(state, scheduler.Schedule(new Build(state, regions, links, profile, policy, version), accesses.ToArray(), dependency));
    }

    private readonly struct Build(HeadingNavigationFlowBuildState result, ReadOnlyMemory<HeadingNavigationFlowRegion> regions,
        NavigationLinkSet links, NavigationProfile profile, NavigationPolicy policy, ulong version) : IJob
    {
        public void Execute()
        {
            NavigationProfile shape = profile;
            NavigationPolicy costPolicy = policy;
            NavigationLinkSet definitions = links;
            ulong contentVersion = version;
            var nodes = new List<HeadingNavigationFlowNode>();
            var edges = new List<List<(NavigationGridEdge Edge, HeadingNavigationFlowTransition Choice)>>();
            var ports = new Dictionary<NavigationLinkEndpoint, int[]>();
            var regionById = regions.ToArray().ToDictionary(region => region.Id);
            var ranges = new List<NavigationGridTileRange>();
            Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
            ulong capabilities = profile.Capabilities & policy.AllowedCapabilities;
            foreach (HeadingNavigationFlowRegion region in regions.Span)
            {
                HeadingNavigationMesh mesh = region.Mesh;
                minimum = Vector3.Min(minimum, mesh.Geometry.Bounds.Minimum);
                maximum = Vector3.Max(maximum, mesh.Geometry.Bounds.Maximum);
                int first = nodes.Count;
                foreach (HeadingNavigationNode node in mesh.Graph.Nodes.Span)
                    Add(new(region.Id, node.Region, node.Channel, new(node.Position, mesh.GetChannel(node.Channel).Center)));
                for (int local = 0; local < mesh.Graph.Nodes.Length; local++)
                {
                    HeadingNavigationNode node = mesh.Graph.Nodes.Span[local];
                    foreach (HeadingNavigationEdge edge in mesh.Graph.Edges.Span.Slice(node.ConnectionStart, node.ConnectionCount))
                    {
                        NavigationPortal portal = edge.Portal >= 0 ? mesh.Geometry.Portals.Span[edge.Portal] : default;
                        NavigationFlowAction action = edge.Action == HeadingNavigationAction.Rotation ? NavigationFlowAction.Rotate : NavigationFlowAction.Move;
                        Connect(first + local, first + edge.ToNode, action, portal.Cost, portal.RequiredCapabilities, portal.Action);
                    }
                }
                foreach (HeadingNavigationFlowPort port in region.Ports.Span)
                {
                    int polygon = mesh.FindRegion(port.Position);
                    var attached = new List<int>();
                    foreach (HeadingNavigationInterval channel in mesh.GetAllowedHeadings(polygon).Span)
                    {
                        int found = -1;
                        for (int index = first; index < nodes.Count; index++)
                            if (nodes[index].Polygon == polygon && nodes[index].Channel == channel.Channel &&
                                nodes[index].Pose.Position == port.Position) { found = index; break; }
                        if (found < 0)
                        {
                            found = Add(new(region.Id, polygon, channel.Channel, new(port.Position, channel.Center)));
                            for (int other = first; other < found; other++)
                                if (nodes[other].Polygon == polygon && nodes[other].Channel == channel.Channel)
                                {
                                    Connect(found, other, NavigationFlowAction.Move);
                                    Connect(other, found, NavigationFlowAction.Move);
                                }
                        }
                        attached.Add(found);
                    }
                    HeadingNavigationRegion description = mesh.Regions.Span[polygon];
                    foreach (HeadingNavigationRotation rotation in mesh.Rotations.Span.Slice(description.RotationStart, description.RotationCount))
                    {
                        int from = attached.First(index => nodes[index].Channel == rotation.FromChannel);
                        int to = attached.First(index => nodes[index].Channel == rotation.ToChannel);
                        Connect(from, to, NavigationFlowAction.Rotate);
                    }
                    ports.Add(new(region.Id, port.Id), attached.ToArray());
                }
                ranges.Add(new(new(region.Id, 0), first, nodes.Count - first, mesh.ContentVersion, true));
            }
            foreach (NavigationLink link in links.Links.Span)
            {
                ConnectLink(link.From, link.To, link);
                if (link.Bidirectional) ConnectLink(link.To, link.From, link);
            }
            var cells = new NavigationGridCell[nodes.Count];
            var allEdges = new List<NavigationGridEdge>();
            var choices = new List<HeadingNavigationFlowTransition>();
            for (int index = 0; index < nodes.Count; index++)
            {
                HeadingNavigationFlowNode node = nodes[index];
                NavigationPolygon polygon = regionById[node.Region].Mesh.Geometry.Polygons.Span[node.Polygon];
                float baseCost = (polygon.RequiredCapabilities & ~capabilities) == 0 ? 1 : float.PositiveInfinity;
                cells[index] = new(node.Pose.Position, allEdges.Count, edges[index].Count, polygon.Area, baseCost,
                    Tile: new(node.Region, 0));
                foreach (var edge in edges[index]) { allEdges.Add(edge.Edge); choices.Add(edge.Choice); }
            }
            var grid = new NavigationGrid(new(minimum, maximum), 1, cells, allEdges.ToArray(),
                NavigationGridShape.FromProfile(profile, NavigationGridFootprintMode.Point), version, tileRanges: ranges.ToArray());
            NavigationCosts costs = NavigationCostSystem.Create(grid, policy, version);
            result.Graph = new(regions, links, nodes.ToArray(), choices.ToArray(), costs);

            int Add(HeadingNavigationFlowNode node) { int index = nodes.Count; nodes.Add(node); edges.Add([]); return index; }
            void Connect(int from, int to, NavigationFlowAction action, float extra = 0, ulong required = 0, int actionId = 0,
                NavigationPathTraversal? traversal = null)
            {
                HeadingNavigationFlowNode first = nodes[from], last = nodes[to];
                float cost = action switch
                {
                    NavigationFlowAction.Rotate => MathF.Abs(NavigationHeading.Difference(last.Pose.Heading, first.Pose.Heading)) / shape.MaximumAngularSpeed,
                    NavigationFlowAction.Traversal => 0,
                    _ => Vector3.Distance(first.Pose.Position, last.Pose.Position) / shape.MaximumSpeed *
                        costPolicy.GetAreaCost(regionById[first.Region].Mesh.Geometry.Polygons.Span[first.Polygon].Area),
                };
                edges[from].Add((new(to, cost + extra + costPolicy.GetActionCost(actionId), required, actionId, true), new(action, traversal)));
            }
            void ConnectLink(NavigationLinkEndpoint from, NavigationLinkEndpoint to, NavigationLink link)
            {
                foreach (int first in ports[from])
                foreach (int last in ports[to])
                {
                    HeadingNavigationFlowNode source = nodes[first], destination = nodes[last];
                    HeadingNavigationMesh targetMesh = regionById[destination.Region].Mesh;
                    if (!targetMesh.GetChannel(destination.Channel).Contains(source.Pose.Heading)) continue;
                    NavigationFlowAction action = link.Action == 0 ? NavigationFlowAction.Move : NavigationFlowAction.Traversal;
                    NavigationPathTraversal? traversal = action == NavigationFlowAction.Traversal
                        ? new(link.Id, link.Action, 0, 0, source.Pose.Position, destination.Pose.Position,
                            link.Parameters, link.EntryRange, link.ExitRange, contentVersion, definitions) : null;
                    Connect(first, last, action, link.Enabled ? link.Cost : float.PositiveInfinity,
                        link.RequiredCapabilities, link.Action, traversal);
                }
            }
        }
    }
}
