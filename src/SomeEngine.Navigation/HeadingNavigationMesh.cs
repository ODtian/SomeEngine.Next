using System.Numerics;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Navigation;

/// <summary>A continuous interval around one channel heading, in the NavigationHeading convention.</summary>
public readonly record struct HeadingNavigationInterval(int Channel, float Minimum, float Maximum)
{
    public float Center => (Minimum + Maximum) * 0.5f;
    public bool Contains(float heading) => MathF.Abs(NavigationHeading.Difference(heading, Center)) <= (Maximum - Minimum) * 0.5f + 0.000001f;
}

/// <summary>One convex polygon's uniform feasible channels and directed action ranges. Index matches Geometry.Polygons.</summary>
public readonly record struct HeadingNavigationRegion(
    int HeadingStart, int HeadingCount, int TranslationStart, int TranslationCount, int RotationStart, int RotationCount,
    int NodeStart = 0, int NodeCount = 0);

public readonly record struct HeadingNavigationTranslation(int ToRegion, int Channel, Vector3 Left, Vector3 Right, int Portal);
public readonly record struct HeadingNavigationRotation(int FromChannel, int ToChannel, float HeadingChange);
public enum HeadingNavigationAction { Translation, Rotation }
public readonly record struct HeadingNavigationNode(int Region, int Channel, Vector3 Position, int ConnectionStart, int ConnectionCount);
public readonly record struct HeadingNavigationEdge(int ToNode, HeadingNavigationAction Action, int Portal = -1);

/// <summary>Boundary-position/channel states with translation and in-place rotation edges.</summary>
public sealed class HeadingNavigationGraph(ReadOnlyMemory<HeadingNavigationNode> nodes, ReadOnlyMemory<HeadingNavigationEdge> edges)
{
    public ReadOnlyMemory<HeadingNavigationNode> Nodes { get; } = nodes;
    public ReadOnlyMemory<HeadingNavigationEdge> Edges { get; } = edges;
}

/// <summary>Completed convex regions, continuous heading permissions and their own navigation graph.</summary>
public sealed partial class HeadingNavigationMesh : INavigationSpace<HeadingNavigationMesh>
{
    public HeadingNavigationMesh(NavigationMesh geometry, int channelCount,
        ReadOnlyMemory<HeadingNavigationRegion> regions, ReadOnlyMemory<HeadingNavigationInterval> headings,
        ReadOnlyMemory<HeadingNavigationTranslation> translations, ReadOnlyMemory<HeadingNavigationRotation> rotations,
        HeadingNavigationGraph graph, HeadingNavigationRegionGraph? regionGraph = null,
        ReadOnlyMemory<HeadingNavigationMeshTile> tiles = default)
    {
        Geometry = geometry;
        ChannelCount = channelCount;
        Regions = regions;
        Headings = headings;
        Translations = translations;
        Rotations = rotations;
        Graph = graph;
        RegionGraph = regionGraph;
        Tiles = tiles;
    }

    public NavigationMesh Geometry { get; }
    public int ChannelCount { get; }
    public ReadOnlyMemory<HeadingNavigationRegion> Regions { get; }
    public ReadOnlyMemory<HeadingNavigationInterval> Headings { get; }
    public ReadOnlyMemory<HeadingNavigationTranslation> Translations { get; }
    public ReadOnlyMemory<HeadingNavigationRotation> Rotations { get; }
    public HeadingNavigationGraph Graph { get; }
    public HeadingNavigationRegionGraph? RegionGraph { get; }
    public ReadOnlyMemory<HeadingNavigationMeshTile> Tiles { get; }
    public ulong ContentVersion => Geometry.ContentVersion;

    public HeadingNavigationInterval GetChannel(int channel)
    {
        float width = MathF.Tau / ChannelCount;
        return new(channel, (channel - 0.5f) * width, (channel + 0.5f) * width);
    }

    public ReadOnlyMemory<HeadingNavigationInterval> GetAllowedHeadings(int region)
    {
        HeadingNavigationRegion description = Regions.Span[region];
        return Headings.Slice(description.HeadingStart, description.HeadingCount);
    }

    public ReadOnlyMemory<HeadingNavigationInterval> GetAllowedHeadings(Vector3 position)
    {
        int nearest = Geometry.FindPolygon(position);
        if (nearest < 0) return ReadOnlyMemory<HeadingNavigationInterval>.Empty;
        Geometry.TrySampleHeight(nearest, position, out float support);
        var allowed = new Dictionary<int, HeadingNavigationInterval>();
        for (int region = 0; region < Regions.Length; region++)
            if (Geometry.TrySampleHeight(region, position, out float height) && MathF.Abs(height - support) <= 0.0001f)
                foreach (HeadingNavigationInterval interval in GetAllowedHeadings(region).Span) allowed[interval.Channel] = interval;
        return allowed.Values.ToArray();
    }

    /// <summary>Queries a containing convex region on the closest support layer with the requested heading.</summary>
    public int FindRegion(Vector3 position, float? heading = null)
    {
        int nearest = Geometry.FindPolygon(position);
        if (nearest < 0) return -1;
        Geometry.TrySampleHeight(nearest, position, out float support);
        for (int region = 0; region < Regions.Length; region++)
        {
            if (!Geometry.TrySampleHeight(region, position, out float height) || MathF.Abs(height - support) > 0.0001f) continue;
            bool feasible = !heading.HasValue;
            foreach (HeadingNavigationInterval interval in GetAllowedHeadings(region).Span)
                feasible |= heading.HasValue && interval.Contains(heading.Value);
            if (feasible) return region;
        }
        return -1;
    }

    public bool IsPoseFeasible(Vector3 position, float heading)
    {
        foreach (HeadingNavigationInterval interval in GetAllowedHeadings(position).Span)
            if (interval.Contains(heading)) return true;
        return false;
    }

    /// <summary>Checks the entire signed angular motion at this position; positive turns toward +X.</summary>
    public bool IsRotationFeasible(Vector3 position, float startHeading, float headingChange)
    {
        ReadOnlySpan<HeadingNavigationInterval> allowed = GetAllowedHeadings(position).Span;
        if (!IsPoseFeasible(position, startHeading)) return false;
        if (headingChange == 0) return true;
        float current = MathF.Min(startHeading, startHeading + headingChange);
        float finish = MathF.Max(startHeading, startHeading + headingChange);
        while (current < finish - 0.000001f)
        {
            float next = current;
            foreach (HeadingNavigationInterval interval in allowed)
            {
                float center = current - NavigationHeading.Difference(current, interval.Center);
                float half = (interval.Maximum - interval.Minimum) * 0.5f;
                if (center - half <= current + 0.000001f && center + half > current + 0.000001f)
                    next = MathF.Max(next, center + half);
            }
            if (next == current) return false;
            current = next;
        }
        return true;
    }

    public HeadingNavigationTranslation[] GetTranslationConnections(Vector3 position, float heading)
    {
        int index = FindRegion(position, heading);
        if (index < 0) return [];
        HeadingNavigationRegion region = Regions.Span[index];
        var result = new List<HeadingNavigationTranslation>();
        foreach (HeadingNavigationTranslation connection in Translations.Span.Slice(region.TranslationStart, region.TranslationCount))
            if (GetChannel(connection.Channel).Contains(heading)) result.Add(connection);
        return result.ToArray();
    }

    public HeadingNavigationRotation[] GetRotationConnections(Vector3 position, float heading)
    {
        int index = FindRegion(position, heading);
        if (index < 0) return [];
        HeadingNavigationRegion region = Regions.Span[index];
        var result = new List<HeadingNavigationRotation>();
        foreach (HeadingNavigationRotation connection in Rotations.Span.Slice(region.RotationStart, region.RotationCount))
            if (GetChannel(connection.FromChannel).Contains(heading)) result.Add(connection);
        return result.ToArray();
    }

    public static bool TryGetHeight(HeadingNavigationMesh space, Vector3 position, out float height) =>
        NavigationMesh.TryGetHeight(space.Geometry, position, out height);

    public static void CollectResources(HeadingNavigationMesh space, ref JobResourceAccessBuilder resources)
    {
        NavigationMesh.CollectResources(space.Geometry, ref resources);
        resources.Read(space.Regions);
        resources.Read(space.Headings);
        resources.Read(space.Translations);
        resources.Read(space.Rotations);
        resources.Read(space.Graph.Nodes);
        resources.Read(space.Graph.Edges);
    }
}
