using System.Numerics;

namespace SomeEngine.Navigation;

internal static class HeadingNavigationGraphBuilder
{
    internal static HeadingNavigationMesh Create(NavigationMesh geometry, BigInteger[] allowed, int channelCount)
    {
        float width = MathF.Tau / channelCount;
        var headings = new List<HeadingNavigationInterval>();
        var translations = new List<HeadingNavigationTranslation>();
        var rotations = new List<HeadingNavigationRotation>();
        var regions = new HeadingNavigationRegion[geometry.Polygons.Length];
        var headingRanges = new Dictionary<BigInteger, (int Start, int Count)>();
        for (int region = 0; region < regions.Length; region++)
        {
            BigInteger channels = allowed[region];
            if (!headingRanges.TryGetValue(channels, out var range))
            {
                int start = headings.Count;
                for (int channel = 0; channel < channelCount; channel++)
                    if (Contains(channels, channel)) headings.Add(new(channel, (channel - 0.5f) * width, (channel + 0.5f) * width));
                range = (start, headings.Count - start);
                headingRanges.Add(channels, range);
            }
            int translationStart = translations.Count, rotationStart = rotations.Count;
            NavigationPolygon polygon = geometry.Polygons.Span[region];
            for (int portalIndex = polygon.PortalStart; portalIndex < polygon.PortalStart + polygon.PortalCount; portalIndex++)
            {
                NavigationPortal portal = geometry.Portals.Span[portalIndex];
                BigInteger common = channels & allowed[portal.ToPolygon];
                for (int channel = 0; channel < channelCount; channel++)
                    if (Contains(common, channel)) translations.Add(new(portal.ToPolygon, channel, portal.Left, portal.Right, portalIndex));
            }
            for (int channel = 0; channel < channelCount; channel++)
            {
                if (!Contains(channels, channel)) continue;
                int next = (channel + 1) % channelCount, previous = (channel + channelCount - 1) % channelCount;
                if (Contains(channels, next)) rotations.Add(new(channel, next, width));
                if (Contains(channels, previous)) rotations.Add(new(channel, previous, -width));
            }
            regions[region] = new(range.Start, range.Count, translationStart, translations.Count - translationStart,
                rotationStart, rotations.Count - rotationStart);
        }
        HeadingNavigationGraph graph = Build(geometry, regions, headings, translations, rotations);
        return new(geometry, channelCount, regions, headings.ToArray(), translations.ToArray(), rotations.ToArray(), graph);
    }

    private static bool Contains(BigInteger channels, int channel) => (channels & (BigInteger.One << channel)) != BigInteger.Zero;

    private static HeadingNavigationGraph Build(NavigationMesh geometry, HeadingNavigationRegion[] regions,
        List<HeadingNavigationInterval> headings, List<HeadingNavigationTranslation> translations, List<HeadingNavigationRotation> rotations)
    {
        var points = new Vector3[regions.Length][];
        var nodes = new List<HeadingNavigationNode>();
        var nodeByPose = new Dictionary<(int Region, int Channel, Vector3 Position), int>();
        for (int region = 0; region < regions.Length; region++)
        {
            NavigationPolygon polygon = geometry.Polygons.Span[region];
            var boundary = new List<Vector3>();
            for (int edge = 0; edge < polygon.VertexCount; edge++)
            {
                int first = geometry.PolygonVertices.Span[polygon.VertexStart + edge];
                int second = geometry.PolygonVertices.Span[polygon.VertexStart + (edge + 1) % polygon.VertexCount];
                boundary.Add((geometry.Positions.Span[first] + geometry.Positions.Span[second]) * 0.5f);
            }
            foreach (NavigationPortal portal in geometry.Portals.Span.Slice(polygon.PortalStart, polygon.PortalCount))
            {
                Vector3 point = (portal.Left + portal.Right) * 0.5f;
                if (!boundary.Contains(point)) boundary.Add(point);
            }
            points[region] = boundary.ToArray();
            int nodeStart = nodes.Count;
            HeadingNavigationRegion data = regions[region];
            for (int index = data.HeadingStart; index < data.HeadingStart + data.HeadingCount; index++)
            foreach (Vector3 point in points[region])
            {
                int channel = headings[index].Channel;
                nodeByPose.Add((region, channel, point), nodes.Count);
                nodes.Add(new(region, channel, point, 0, 0));
            }
            regions[region] = data with { NodeStart = nodeStart, NodeCount = nodes.Count - nodeStart };
        }
        var outgoing = new List<HeadingNavigationEdge>[nodes.Count];
        for (int index = 0; index < outgoing.Length; index++) outgoing[index] = [];
        for (int region = 0; region < regions.Length; region++)
        {
            HeadingNavigationRegion data = regions[region];
            for (int index = data.HeadingStart; index < data.HeadingStart + data.HeadingCount; index++)
            {
                int channel = headings[index].Channel;
                foreach (Vector3 from in points[region])
                foreach (Vector3 to in points[region])
                    if (from != to) outgoing[nodeByPose[(region, channel, from)]].Add(new(nodeByPose[(region, channel, to)], HeadingNavigationAction.Translation));
            }
            for (int index = data.TranslationStart; index < data.TranslationStart + data.TranslationCount; index++)
            {
                HeadingNavigationTranslation connection = translations[index];
                Vector3 point = (connection.Left + connection.Right) * 0.5f;
                int from = nodeByPose[(region, connection.Channel, point)];
                int to = nodeByPose[(connection.ToRegion, connection.Channel, point)];
                outgoing[from].Add(new(to, HeadingNavigationAction.Translation, connection.Portal));
            }
            for (int index = data.RotationStart; index < data.RotationStart + data.RotationCount; index++)
            {
                HeadingNavigationRotation connection = rotations[index];
                foreach (Vector3 point in points[region])
                    outgoing[nodeByPose[(region, connection.FromChannel, point)]].Add(
                        new(nodeByPose[(region, connection.ToChannel, point)], HeadingNavigationAction.Rotation));
            }
        }
        var edges = new List<HeadingNavigationEdge>();
        for (int index = 0; index < nodes.Count; index++)
        {
            nodes[index] = nodes[index] with { ConnectionStart = edges.Count, ConnectionCount = outgoing[index].Count };
            edges.AddRange(outgoing[index]);
        }
        return new(nodes.ToArray(), edges.ToArray());
    }
}
