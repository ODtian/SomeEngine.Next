using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationRegionBuildTests
{
    [Fact]
    public void DensePortsKeepTheirDirectedLocalCostsAndEdgeOrder()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var mesh = new NavigationMesh([new(0, 0, 0), new(10, 0, 0), new(10, 0, 10), new(0, 0, 10)],
            [0, 1, 2, 3], [new(0, 4, 0, 0)], []);
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1);
        NavigationRegionPort[] ports = Enumerable.Range(0, 12)
            .Select(index => new NavigationRegionPort(index, default, new(1 + index % 4 * 2, 0, 1 + index / 4 * 3))).ToArray();
        NavigationRegionGraph graph = NavigationRegionBuilder.Schedule(mesh, profile, new NavigationPolicy(), ports,
            scheduler: scheduler).Complete().Graph;

        Assert.Equal(ports.Length, graph.Entrances.Length);
        Assert.Equal(ports.Length * (ports.Length - 1), graph.Edges.Length);
        int edgeIndex = 0;
        for (int from = 0; from < ports.Length; from++)
        {
            NavigationRegionEntrance entrance = graph.Entrances.Span[from];
            Assert.Equal(from, entrance.Port);
            Assert.Equal(edgeIndex, entrance.EdgeStart);
            Assert.Equal(ports.Length - 1, entrance.EdgeCount);
            for (int to = 0; to < ports.Length; to++)
            {
                if (from == to) continue;
                NavigationRegionEdge edge = graph.Edges.Span[edgeIndex++];
                Assert.Equal(from, edge.From);
                Assert.Equal(to, edge.To);
                Assert.Equal(Vector3.Distance(ports[from].Position, ports[to].Position), edge.Cost, 4);
            }
        }
    }
}