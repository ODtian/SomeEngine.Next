using System.Text.Json;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph.Diagnostics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class RenderGraphSnapshotJsonTests
{
    [Fact]
    public void SnapshotJsonPreservesItsWireNamesValuesAndCompleteRoundTrip()
    {
        RenderGraphSnapshot snapshot = CreateSnapshot();

        string json = RenderGraphSnapshotJsonSerializer.Serialize(snapshot);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(
            ["queryPools", "shaderTables", "version", "structureVersion", "passes", "buffers",
                "textures", "accesses", "dependencies", "barriers", "statistics"],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Contains(json.Split('\n'), line => line.TrimEnd('\r') == "  \"version\": 3,");
        Assert.Equal(ulong.MaxValue, root.GetProperty("structureVersion").GetUInt64());
        Assert.Equal("pipelineStatistics", root.GetProperty("queryPools")[0].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("shaderTables")[0].GetProperty("label").ValueKind);

        JsonElement pass = root.GetProperty("passes")[0];
        Assert.Equal("compute", pass.GetProperty("kind").GetString());
        Assert.Equal("compute", pass.GetProperty("queue").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("passes")[1].GetProperty("queue").ValueKind);
        JsonElement buffer = root.GetProperty("buffers")[0];
        Assert.Equal("graphOwned", buffer.GetProperty("ownership").GetString());
        Assert.Equal("perFrame", buffer.GetProperty("lifetime").GetString());
        Assert.Equal(JsonValueKind.Null, buffer.GetProperty("label").ValueKind);
        Assert.Equal(9_007_199_254_740_993UL, buffer.GetProperty("size").GetUInt64());
        Assert.Equal("r8G8B8A8UNormSrgb", root.GetProperty("textures")[0].GetProperty("format").GetString());

        JsonElement bufferAccess = root.GetProperty("accesses")[0];
        Assert.Equal("buffer", bufferAccess.GetProperty("targetKind").GetString());
        Assert.Equal("readWrite", bufferAccess.GetProperty("mode").GetString());
        Assert.Equal("partial", bufferAccess.GetProperty("coverage").GetString());
        Assert.Equal("computeShading, copy", bufferAccess.GetProperty("sync").GetString());
        Assert.Equal("unorderedAccess, shaderResource", bufferAccess.GetProperty("resourceAccess").GetString());
        Assert.Equal("defined", bufferAccess.GetProperty("resultContents").GetString());
        Assert.Equal("countBoundedRanges", bufferAccess.GetProperty("bufferContentScope").GetString());
        Assert.False(bufferAccess.GetProperty("bufferRange").GetProperty("isWhole").GetBoolean());
        JsonElement textureAccess = root.GetProperty("accesses")[1];
        Assert.Equal("depthStencilRead", textureAccess.GetProperty("textureLayout").GetString());
        Assert.Equal("depth, stencil", textureAccess.GetProperty("textureRange").GetProperty("aspects").GetString());
        Assert.Equal(JsonValueKind.Null, textureAccess.GetProperty("resultContents").ValueKind);
        Assert.Equal("physical", root.GetProperty("dependencies")[0].GetProperty("kind").GetString());
        Assert.Equal("queueAcquire", root.GetProperty("barriers")[0].GetProperty("kind").GetString());
        Assert.Equal("end", root.GetProperty("barriers")[0].GetProperty("phase").GetString());
        JsonElement statistics = root.GetProperty("statistics");
        Assert.True(statistics.GetProperty("analysisReused").GetBoolean());
        Assert.Equal(7, statistics.GetProperty("analyzedPassCount").GetInt32());
        Assert.Equal(11, statistics.GetProperty("rebuiltRowCount").GetInt32());
        Assert.Equal(160UL, statistics.GetProperty("transientUnusedBytes").GetUInt64());

        RenderGraphSnapshot restored = RenderGraphSnapshotJsonSerializer.Deserialize(json);

        Assert.Equal(snapshot.Version, restored.Version);
        Assert.Equal(snapshot.StructureVersion, restored.StructureVersion);
        Assert.Equal(snapshot.Passes.ToArray(), restored.Passes.ToArray());
        Assert.Equal(snapshot.Buffers.ToArray(), restored.Buffers.ToArray());
        Assert.Equal(snapshot.Textures.ToArray(), restored.Textures.ToArray());
        Assert.Equal(snapshot.Accesses.ToArray(), restored.Accesses.ToArray());
        Assert.Equal(snapshot.Dependencies.ToArray(), restored.Dependencies.ToArray());
        Assert.Equal(snapshot.Barriers.ToArray(), restored.Barriers.ToArray());
        Assert.Equal(snapshot.QueryPools.ToArray(), restored.QueryPools.ToArray());
        Assert.Equal(snapshot.ShaderTables.ToArray(), restored.ShaderTables.ToArray());
        Assert.Equal(snapshot.Statistics, restored.Statistics);
    }

    [Fact]
    public void SnapshotJsonReadsExistingEnumSpellingsNumbersAndOmittedOptionalRows()
    {
        const string json = """
            {
              "version": 3,
              "structureVersion": 9007199254740993,
              "passes": [{
                "ordinal": 0, "slot": 17, "generation": 2, "label": "captured",
                "kind": "CoMpUtE", "enabled": false, "live": false,
                "declarationOrdinal": 4, "scheduledOrdinal": -1,
                "queue": { "type": 254, "index": 5, "nodeIndex": 7 }
              }],
              "statistics": { "declaredPassCount": 1 }
            }
            """;

        RenderGraphSnapshot snapshot = RenderGraphSnapshotJsonSerializer.Deserialize(json);

        Assert.Equal(9_007_199_254_740_993UL, snapshot.StructureVersion);
        RenderGraphSnapshot.Pass pass = Assert.Single(snapshot.Passes);
        Assert.Equal(GraphPassKind.Compute, pass.Kind);
        Assert.Equal(new RenderGraphSnapshot.QueueInfo((QueueType)254, 5, 7), pass.Queue);
        Assert.Empty(snapshot.Buffers);
        Assert.Empty(snapshot.Textures);
        Assert.Empty(snapshot.Accesses);
        Assert.Empty(snapshot.Dependencies);
        Assert.Empty(snapshot.Barriers);
        Assert.Empty(snapshot.QueryPools);
        Assert.Empty(snapshot.ShaderTables);
        Assert.False(snapshot.QueryPools.IsDefault);
        Assert.False(snapshot.ShaderTables.IsDefault);
        using JsonDocument encoded = JsonDocument.Parse(RenderGraphSnapshotJsonSerializer.Serialize(snapshot));
        JsonElement encodedPass = encoded.RootElement.GetProperty("passes")[0];
        Assert.Equal("compute", encodedPass.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Number, encodedPass.GetProperty("queue").GetProperty("type").ValueKind);
        Assert.Equal(254, encodedPass.GetProperty("queue").GetProperty("type").GetInt32());
    }

    [Theory]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"Version\":3}")]
    [InlineData("{\"version\":3,\"statistics\":{\"declaredPassCount\":1}}")]
    [InlineData("{\"version\":3,\"passes\":[{\"ordinal\":1,\"scheduledOrdinal\":-1}],\"statistics\":{\"declaredPassCount\":1}}")]
    [InlineData("{\"version\":3,\"accesses\":[{\"pass\":0,\"targetOrdinal\":0}],\"statistics\":{\"accessCount\":1}}")]
    public void SnapshotJsonRejectsInvalidVersionsRowsReferencesAndCounts(string json)
    {
        Assert.Throws<JsonException>(() => RenderGraphSnapshotJsonSerializer.Deserialize(json));
    }

    [Fact]
    public void SnapshotJsonRejectsNullOrBlankInputs()
    {
        Assert.Throws<ArgumentNullException>(() => RenderGraphSnapshotJsonSerializer.Serialize(null!));
        Assert.Throws<ArgumentNullException>(() => RenderGraphSnapshotJsonSerializer.Deserialize(null!));
        Assert.Throws<ArgumentException>(() => RenderGraphSnapshotJsonSerializer.Deserialize(" \t\r\n"));
        Assert.Throws<JsonException>(() => RenderGraphSnapshotJsonSerializer.Deserialize("null"));
    }

    private static RenderGraphSnapshot CreateSnapshot() => new(
        RenderGraphSnapshot.CurrentVersion,
        ulong.MaxValue,
        [
            new(0, 17, 2, "compute <A>", GraphPassKind.Compute, true, true, 4, 0,
                new RenderGraphSnapshot.QueueInfo(QueueType.Compute, 2, 3)),
            new(1, 23, 5, "culled", GraphPassKind.General, false, false, 9, -1, null),
        ],
        [new(0, 31, 7, null, RenderGraphResourceOwnership.GraphOwned, RenderGraphResourceLifetime.PerFrame,
            9_007_199_254_740_993UL, 64, 128, 0, 0)],
        [new(0, 37, 11, "depth", RenderGraphResourceOwnership.CallerOwned, RenderGraphResourceLifetime.Persistent,
            640, 360, Format.R8G8B8A8UNormSrgb, 256, 512, 0, 0)],
        [
            new(0, GraphAccessTargetKind.Buffer, 0, GraphAccessMode.ReadWrite, WriteCoverage.Partial,
                PipelineSync.ComputeShading | PipelineSync.Copy, ResourceAccess.UnorderedAccess | ResourceAccess.ShaderResource,
                new BufferRange(64, 128), default, TextureLayout.Undefined, ResourceContentState.Defined,
                BufferContentScope.CountBoundedRanges, new QueryRange(2, 3)),
            new(0, GraphAccessTargetKind.Texture, 0, GraphAccessMode.Read, WriteCoverage.Complete,
                PipelineSync.PixelShading, ResourceAccess.DepthStencilRead, default,
                new TextureSubresourceRange(1, 2, 3, 4, TextureAspects.Depth | TextureAspects.Stencil),
                TextureLayout.DepthStencilRead, null),
        ],
        [new(0, 1, RenderGraphDependencyKind.Physical)],
        [new(0, RenderGraphBarrierKind.QueueAcquire, BarrierPhase.End)],
        new RenderGraphStatistics(2, 1, 1, 1, 1, 2, 1, 1, 1, 128, 96, 256, 512)
            { AnalysisReused = true, AnalyzedPassCount = 7, RebuiltRowCount = 11 },
        [new(0, 41, 13, "queries", 8, QueryType.PipelineStatistics)],
        [new(0, 43, 17, null)]);
}
