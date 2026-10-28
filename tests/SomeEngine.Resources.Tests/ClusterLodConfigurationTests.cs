using System.Numerics;
using SomeEngine.Geometry.Cluster;
using ClusterMeshlet = SomeEngine.Geometry.Cluster.ClusterMeshBuilder.ClusterMeshlet;

namespace SomeEngine.Resources.Tests;

public sealed class ClusterLodConfigurationTests
{
    [Fact]
    public void LowerSimplificationRatioProducesFewerTrianglesAtTheFirstLod()
    {
        var (positions, indices) = CreateGrid(32);
        ClusterLodConfig config = ClusterLodConfig.GetDefault() with { SimplifyFallbackSloppy = false };
        List<ClusterMeshlet> coarse = [], fine = [];
        ClusterMeshBuilder.BuildLod(config with { SimplifyRatio = .25f }, positions, indices,
            new uint[positions.Length], coarse, []);
        ClusterMeshBuilder.BuildLod(config with { SimplifyRatio = .75f }, positions, indices,
            new uint[positions.Length], fine, []);

        Assert.Contains(coarse, cluster => cluster.Level == 1);
        Assert.True(coarse.Where(cluster => cluster.Level == 1).Sum(cluster => cluster.IndicesCount)
            < fine.Where(cluster => cluster.Level == 1).Sum(cluster => cluster.IndicesCount));
    }

    [Fact]
    public void SimplificationThresholdControlsWhichReducedGeometryGetsALod()
    {
        var (positions, indices) = CreateGrid(32);
        ClusterLodConfig config = ClusterLodConfig.GetDefault() with { SimplifyFallbackSloppy = false };
        List<ClusterMeshlet> accepted = [], retained = [];
        ClusterMeshBuilder.BuildLod(config, positions, indices, new uint[positions.Length], accepted, []);
        ClusterMeshBuilder.BuildLod(config with { SimplifyThreshold = .1f }, positions, indices,
            new uint[positions.Length], retained, []);

        Assert.Contains(accepted, cluster => cluster.Level > 0);
        Assert.All(retained, cluster => Assert.Equal(0, cluster.Level));
        Assert.Equal(indices.Length, retained.Sum(cluster => cluster.IndicesCount));
    }

    [Fact]
    public void SmallerPartitionsRetainMoreTrianglesAcrossTheFirstLod()
    {
        var (positions, indices) = CreateGrid(32);
        ClusterLodConfig config = ClusterLodConfig.GetDefault() with { SimplifyFallbackSloppy = false };
        List<ClusterMeshlet> small = [], large = [];
        ClusterMeshBuilder.BuildLod(config with { PartitionSize = 4 }, positions, indices,
            new uint[positions.Length], small, []);
        ClusterMeshBuilder.BuildLod(config with { PartitionSize = 16 }, positions, indices,
            new uint[positions.Length], large, []);

        int smallTriangles = small.Where(cluster => cluster.Level == 1 || cluster.Level == 0 && cluster.ParentGroupId < 0)
            .Sum(cluster => cluster.IndicesCount);
        int largeTriangles = large.Where(cluster => cluster.Level == 1 || cluster.Level == 0 && cluster.ParentGroupId < 0)
            .Sum(cluster => cluster.IndicesCount);
        Assert.True(smallTriangles > largeTriangles);
    }

    [Fact]
    public void SpatialPartitionsCombineDisconnectedSurfacesIntoCoarserGeometry()
    {
        var (gridPositions, gridIndices) = CreateGrid(5);
        var positions = Enumerable.Range(0, 24).SelectMany(grid => gridPositions.Select(position =>
            position + new Vector3(grid * 20, 0, 0))).ToArray();
        var indices = Enumerable.Range(0, 24).SelectMany(grid => gridIndices.Select(vertex =>
            vertex + (uint)(grid * gridPositions.Length))).ToArray();
        ClusterLodConfig config = ClusterLodConfig.GetDefault() with { PartitionSize = 4, SimplifyFallbackSloppy = false };
        List<ClusterMeshlet> spatial = [], connected = [];
        ClusterMeshBuilder.BuildLod(config, positions, indices, new uint[positions.Length], spatial, []);
        ClusterMeshBuilder.BuildLod(config with { PartitionSpatial = false }, positions, indices,
            new uint[positions.Length], connected, []);

        Assert.Contains(spatial, cluster => cluster.Level > 0);
        Assert.All(connected, cluster => Assert.Equal(0, cluster.Level));
        Assert.Equal(indices.Length, connected.Sum(cluster => cluster.IndicesCount));
    }

    [Fact]
    public void SortedPartitionsArrangeCoarseSurfacesFromLeftToRight()
    {
        var (gridPositions, gridIndices) = CreateGrid(5);
        var random = new Random(62);
        int[] order = Enumerable.Range(0, 24).OrderBy(_ => random.Next()).ToArray();
        var positions = order.SelectMany(grid => gridPositions.Select(position => position + new Vector3(grid * 20, 0, 0))).ToArray();
        var indices = Enumerable.Range(0, order.Length).SelectMany(grid => gridIndices.Select(vertex =>
            vertex + (uint)(grid * gridPositions.Length))).ToArray();
        ClusterLodConfig config = ClusterLodConfig.GetDefault() with
            { ClusterSpatial = false, PartitionSize = 4, SimplifyFallbackSloppy = false };
        List<ClusterMeshlet> sorted = [];
        ClusterMeshBuilder.BuildLod(config with { PartitionSort = true }, positions, indices,
            new uint[positions.Length], sorted, []);

        float[] sortedCenters = sorted.Where(cluster => cluster.Level == 1).Select(cluster => cluster.SelfLodCenter.X).Distinct().ToArray();
        Assert.True(sortedCenters.Length > 1);
        Assert.Equal(sortedCenters.Order().ToArray(), sortedCenters);
    }

    [Fact]
    public void SloppyFallbackProducesCoarserGeometryAndRetainsSourceLockedVertices()
    {
        var (positions, indices) = CreateTriangleNetwork();
        byte[] locks = new byte[positions.Length];
        Array.Fill(locks, ClusterMeshBuilder.SourceVertexLockBit, 0, 4);
        ClusterLodConfig config = ClusterLodConfig.GetDefault() with { SimplifyRatio = .1f, SimplifyPermissive = false };
        List<ClusterMeshlet> reduced = [], retained = [];
        ClusterMeshBuilder.BuildLod(config, positions, indices, new uint[positions.Length], reduced, [], locks);
        ClusterMeshBuilder.BuildLod(config with { SimplifyFallbackSloppy = false }, positions, indices,
            new uint[positions.Length], retained, [], locks);

        Assert.Contains(reduced, cluster => cluster.Level > 0);
        int reducedTriangles = reduced.Where(cluster => cluster.Level == 1 || cluster.Level == 0 && cluster.ParentGroupId < 0)
            .Sum(cluster => cluster.IndicesCount) / 3;
        int retainedTriangles = retained.Where(cluster => cluster.Level == 1 || cluster.Level == 0 && cluster.ParentGroupId < 0)
            .Sum(cluster => cluster.IndicesCount) / 3;
        Assert.True(reducedTriangles < retainedTriangles, $"Sloppy triangles: {reducedTriangles}; QEM triangles: {retainedTriangles}.");
        for (uint vertex = 0; vertex < 4; vertex++)
            Assert.Contains(reduced, cluster => cluster.Level == 1 && cluster.LocalVertices.Contains(vertex));
    }

    [Fact]
    public void SloppyErrorFactorScalesTheFirstLodTransitionError()
    {
        var (positions, indices) = CreateTriangleNetwork();
        ClusterLodConfig config = ClusterLodConfig.GetDefault() with { SimplifyRatio = .1f, SimplifyPermissive = false };
        List<ClusterMeshlet> low = [], high = [];
        ClusterMeshBuilder.BuildLod(config with { SimplifyErrorFactorSloppy = 1 }, positions, indices,
            new uint[positions.Length], low, []);
        ClusterMeshBuilder.BuildLod(config with { SimplifyErrorFactorSloppy = 3 }, positions, indices,
            new uint[positions.Length], high, []);

        float lowError = low.Where(cluster => cluster.Level == 1).Max(cluster => cluster.Error);
        float highError = high.Where(cluster => cluster.Level == 1).Max(cluster => cluster.Error);
        Assert.True(lowError > 0);
        Assert.Equal(lowError * 3, highError, 5);
    }

    [Fact]
    public void SloppyFallbackPreservesTheMaterialOfEveryTriangleVertex()
    {
        var (sourcePositions, sourceIndices) = CreateTriangleNetwork();
        Vector3[] positions = [.. sourcePositions, .. sourcePositions.Select(position => position + new Vector3(.05f))];
        uint[] indices = [.. sourceIndices, .. sourceIndices.Select(vertex => vertex + (uint)sourcePositions.Length)];
        uint[] materials = [.. Enumerable.Repeat(17u, sourcePositions.Length), .. Enumerable.Repeat(1024u, sourcePositions.Length)];
        ClusterLodConfig config = ClusterLodConfig.GetDefault() with
            { PartitionSize = 64, SimplifyRatio = .1f, SimplifyPermissive = false };
        List<ClusterMeshlet> clusters = [];
        ClusterMeshBuilder.BuildLod(config, positions, indices, materials, clusters, []);

        ClusterMeshlet[] coarse = clusters.Where(cluster => cluster.Level == 1).ToArray();
        Assert.NotEmpty(coarse);
        Assert.Equal(new uint[] { 17, 1024 }, coarse.SelectMany(cluster => cluster.LocalVertices)
            .Select(vertex => materials[vertex]).Distinct().Order().ToArray());
        foreach (ClusterMeshlet cluster in coarse)
            for (int triangle = 0; triangle < cluster.LocalIndices.Length; triangle += 3)
            {
                uint material = materials[cluster.LocalVertices[cluster.LocalIndices[triangle]]];
                Assert.Equal(material, materials[cluster.LocalVertices[cluster.LocalIndices[triangle + 1]]]);
                Assert.Equal(material, materials[cluster.LocalVertices[cluster.LocalIndices[triangle + 2]]]);
            }
    }

    [Fact]
    public void EdgeErrorLimitReducesTheLodTransitionError()
    {
        var (positions, indices) = CreateTriangleNetwork();
        ClusterLodConfig config = ClusterLodConfig.GetDefault();
        List<ClusterMeshlet> limited = [], full = [];
        ClusterMeshBuilder.BuildLod(config with { SimplifyErrorEdgeLimit = .01f }, positions, indices,
            new uint[positions.Length], limited, []);
        ClusterMeshBuilder.BuildLod(config, positions, indices, new uint[positions.Length], full, []);

        Assert.True(limited.Where(cluster => cluster.Level == 1).Max(cluster => cluster.Error)
            < full.Where(cluster => cluster.Level == 1).Max(cluster => cluster.Error));
    }

    [Fact]
    public void RegularizationChangesCoarseTrianglesForANonuniformSurface()
    {
        var (positions, indices) = CreateGrid(32);
        for (int vertex = 0; vertex < positions.Length; vertex++)
            positions[vertex].X *= positions[vertex].X * .1f;
        ClusterLodConfig config = ClusterLodConfig.GetDefault() with { SimplifyFallbackSloppy = false };
        List<ClusterMeshlet> regular = [], detailed = [];
        ClusterMeshBuilder.BuildLod(config with { SimplifyRegularize = true }, positions, indices,
            new uint[positions.Length], regular, []);
        ClusterMeshBuilder.BuildLod(config, positions, indices, new uint[positions.Length], detailed, []);

        uint[] regularVertices = regular.Where(cluster => cluster.Level == 1).SelectMany(cluster => cluster.LocalVertices).Distinct().Order().ToArray();
        uint[] detailedVertices = detailed.Where(cluster => cluster.Level == 1).SelectMany(cluster => cluster.LocalVertices).Distinct().Order().ToArray();
        Assert.True(regularVertices.Except(detailedVertices).Any() || detailedVertices.Except(regularVertices).Any());
    }

    private static (Vector3[] Positions, uint[] Indices) CreateGrid(int size)
    {
        var positions = new Vector3[(size + 1) * (size + 1)];
        var indices = new List<uint>();
        for (int y = 0; y <= size; y++)
            for (int x = 0; x <= size; x++)
                positions[y * (size + 1) + x] = new(x, y, MathF.Sin(x * .3f) * MathF.Cos(y * .4f));
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                uint first = (uint)(y * (size + 1) + x), next = first + (uint)size + 1;
                indices.AddRange([first, first + 1, next, first + 1, next + 1, next]);
            }
        return (positions, indices.ToArray());
    }

    private static (Vector3[] Positions, uint[] Indices) CreateTriangleNetwork()
    {
        var positions = new List<Vector3>();
        var indices = new List<uint>();
        var random = new Random(591);
        for (int vertex = 0; vertex < 16; vertex++)
            positions.Add(new(random.NextSingle() * 16, random.NextSingle() * 16, random.NextSingle() * 16));
        // Every edge belongs to several overlapping surfaces, so topology constrains QEM reduction.
        for (uint first = 0; first < positions.Count; first++)
            for (uint second = first + 1; second < positions.Count; second++)
                for (uint third = second + 1; third < positions.Count; third++)
                    indices.AddRange([first, second, third, first, third, second]);
        return (positions.ToArray(), indices.ToArray());
    }
}
